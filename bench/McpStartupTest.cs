using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;

namespace RemoteControl
{
    internal static class McpStartupTest
    {
        private static int checks;
        private static void Check(bool value,string label) { if(!value)throw new Exception(label);checks++;Console.WriteLine("PASS "+label); }
        private static int FreePort() { var p=new TcpListener(IPAddress.Loopback,0);p.Start();int n=((IPEndPoint)p.LocalEndpoint).Port;p.Stop();return n; }
        private static bool Answers(int port)
        {
            try {
                using(var c=new TcpClient()) {
                    var ar=c.BeginConnect(IPAddress.Loopback,port,null,null);if(!ar.AsyncWaitHandle.WaitOne(200))return false;c.EndConnect(ar);
                    c.ReceiveTimeout=1000;var s=c.GetStream();byte[] b=Encoding.ASCII.GetBytes("GET /mcp HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n");s.Write(b,0,b.Length);
                    return new StreamReader(s).ReadLine().Contains("401");
                }
            } catch {return false;}
        }
        private static void WaitFor(Func<bool> test,string label) {var w=Stopwatch.StartNew();while(!test()&&w.ElapsedMilliseconds<5000)Thread.Sleep(25);Check(test(),label);Console.WriteLine("elapsed_ms="+w.ElapsedMilliseconds);}
        public sealed class FakeTask
        {
            public int State {get;set;}
            public int Starts;
            public object Run(object args) { Starts++;State=4;return new object(); }
        }
        private static void GatewayModes()
        {
            foreach(int state in new[]{0,1,2,4}) {
                var fake=new FakeTask{State=state};
                Check(McpGatewayStartup.StartReadyTask(fake)==null&&fake.Starts==0,"task state "+state+" is not restarted");
            }
            var ready=new FakeTask{State=3};
            Check(McpGatewayStartup.StartReadyTask(ready)!=null&&ready.Starts==1,"ready task is started immediately");
            Check(McpGatewayStartup.StartReadyTask(ready)==null&&ready.Starts==1,"running task is never started twice");
            var starts=new List<string>();Action<string> task=delegate(string n){starts.Add("task:"+n);},service=delegate(string n){starts.Add("service:"+n);};
            McpGatewayStartup.EnsureConfigured(false,true,"ddns",true,false,task,service);
            McpGatewayStartup.EnsureConfigured(true,false,"ddns",true,false,task,service);
            Check(starts.Count==0,"disabled MCP or missing password never starts gateways");
            foreach(string mode in new[]{"ddns","private","tailscale","off"}) {
                starts.Clear();McpGatewayStartup.EnsureConfigured(true,true,mode,false,false,task,service);
                string expected=mode=="ddns"?"task:RemoteControlWebMcp,service:RemoteControlWebHttps":mode=="private"?"task:RemoteControlPrivateMcp":mode=="tailscale"?"task:RemoteControlTailscaleMcp":"";
                Check(string.Join(",",starts.ToArray())==expected,"starts only configured gateway: "+mode);
            }
            starts.Clear();McpGatewayStartup.EnsureConfigured(true,true,"off",true,false,task,service);
            Check(string.Join(",",starts.ToArray())=="task:RemoteControlLanWebMcpGateway,task:RemoteControlLanWebMcpHttps","independent LAN HTTPS is restored");
            starts.Clear();McpGatewayStartup.EnsureConfigured(true,true,"ddns",true,true,task,service);
            Check(string.Join(",",starts.ToArray())=="task:RemoteControlWebMcp,task:RemoteControlLanWebMcpGateway,service:RemoteControlWebHttps","shared HTTPS starts once without competing LAN task");
        }
        public static int Main(string[] args)
        {
            try {
                string root=Path.GetFullPath(args[0]);Directory.CreateDirectory(root);
                typeof(Config).GetField("Path0",BindingFlags.Static|BindingFlags.NonPublic).SetValue(null,Path.Combine(root,"settings.txt"));
                Config.EnsureWritable();Config.Reload();
                int mcpPort=FreePort();
                Config.SetMany(new Dictionary<string,string>{{"pwhash","fixture-password-stamp"},{"mcp_enabled","1"},{"mcp_port",mcpPort.ToString()},{"mcp_roots",root},{"extauto","0"}});
                var occupied=new TcpListener(IPAddress.Any,0);occupied.Start();int screenPort=((IPEndPoint)occupied.LocalEndpoint).Port;
                var server=new Server();
                try {
                    var watch=Stopwatch.StartNew();Check(!server.Start(screenPort,true),"occupied screen port is reported");
                    Check(Answers(mcpPort),"MCP starts even when screen port is occupied");
                    Console.WriteLine("startup_ms="+watch.ElapsedMilliseconds);
                    var bindings=System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
                    Check(Array.Exists(bindings,delegate(IPEndPoint endpoint){return endpoint.Port==mcpPort&&endpoint.Address.Equals(IPAddress.Loopback);}) &&
                          !Array.Exists(bindings,delegate(IPEndPoint endpoint){return endpoint.Port==mcpPort&&!IPAddress.IsLoopback(endpoint.Address);}),"internal backend listens only on localhost");
                    object first=typeof(Server).GetField("mcp",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(server);
                    var start=typeof(Server).GetMethod("StartMcp",BindingFlags.Instance|BindingFlags.NonPublic);
                    if(start!=null){start.Invoke(server,null);Check(object.ReferenceEquals(first,typeof(Server).GetField("mcp",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(server)),"repeated startup reuses the MCP host");}
                    Config.Set("mcp_enabled","0");WaitFor(delegate{return ((McpHost)first).Port==0;},"MCP off setting closes the port");
                    Config.Set("mcp_enabled","1");WaitFor(delegate{return ((McpHost)first).Port==mcpPort&&Answers(mcpPort);},"MCP on setting starts without screen restart");
                } finally {server.Stop();occupied.Stop();}
                Check(!Answers(mcpPort),"Stop closes MCP");
                Config.Set("pwhash","");server=new Server();
                try {server.Start(FreePort(),true);Check(!Answers(mcpPort),"no password never opens MCP");}finally{server.Stop();}
                int retryPort=FreePort();var blocker=new TcpListener(IPAddress.Loopback,retryPort);blocker.ExclusiveAddressUse=true;blocker.Start();
                var options=new McpOptions { Enabled=true,Port=retryPort,Workspaces=new[]{root} };
                using(var host=new McpHost(delegate{return options;},delegate(string a,string b){return false;},Console.WriteLine,Path.Combine(root,"sessions.bin"))) {
                    host.Start();Check(host.Port==0&&host.Note.Contains("실패"),"MCP port conflict remains retryable");
                    blocker.Stop();WaitFor(delegate{return host.Port==retryPort&&Answers(retryPort);},"MCP automatically recovers after port release");
                }
                GatewayModes();
                var woke = new AutoResetEvent(false);
                TunnelGuard.Enable(delegate{return 1;},delegate{woke.Set();return false;},delegate(string m){});
                Check(woke.WaitOne(1000),"tunnel startup begins without five-second delay");
                TunnelGuard.Disable();
                while(woke.WaitOne(0)){}
                TunnelGuard.Enable(delegate{return 1;},delegate{woke.Set();return false;},delegate(string m){});
                Check(woke.WaitOne(1000),"re-enabling wakes the existing tunnel worker");
                TunnelGuard.Disable();
                Console.WriteLine("ALL "+checks+" MCP STARTUP CHECKS PASSED");return 0;
            }catch(Exception ex){Console.WriteLine(ex);return 1;}
        }
    }
}


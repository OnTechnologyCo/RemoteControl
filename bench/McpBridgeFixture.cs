using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Collections.Generic;
namespace RemoteControl
{
    internal static class McpBridgeFixture
    {
        private static int FreePort(){var l=new TcpListener(IPAddress.Loopback,0);l.Start();int p=((IPEndPoint)l.LocalEndpoint).Port;l.Stop();return p;}
        [STAThread] public static int Main(string[] args)
        {
            if(args.Length>0&&args[0]=="--mcp-job")return McpJobRunner.Run(args);
            if(args.Length!=1)return 64;
            string root=Path.GetFullPath(args[0]);Directory.CreateDirectory(root);
            typeof(Config).GetField("Path0",BindingFlags.NonPublic|BindingFlags.Static).SetValue(null,Path.Combine(root,"settings.txt"));
            Config.EnsureWritable();Config.User="fixture";Config.SetPassword("fixture-only");
            int port=FreePort(),mcpPort=FreePort();
            Config.Set("mcp_port",mcpPort.ToString());Config.Set("mcp_enabled","1");Config.Set("mcp_roots",root);
            Config.Set("mcp_commands","1");Config.Set("mcp_screen","1");
            McpHost host=null;Server server=null;
            Action start=delegate {
                host=new McpHost(McpOptions.Load,Config.CheckLogin,delegate(string line){Console.Error.WriteLine(line);},Path.Combine(root,"sessions.bin"));
                host.Start();if(host.Port!=mcpPort)throw new Exception(host.Note);
                server=new Server();typeof(Server).GetField("mcp",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(server,host);
                // Exercise the real accept/HTTP handlers without exposing synthetic credentials on a LAN NIC.
                var listener=new TcpListener(IPAddress.Loopback,port);listener.Start();
                typeof(Server).GetField("listener",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(server,listener);
                typeof(Server).GetField("port",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(server,port);
                Server captured=server;
                new System.Threading.Thread(delegate(){typeof(Server).GetMethod("AcceptLoop",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(captured,null);}){IsBackground=true}.Start();
            };
            try {
                start();Console.WriteLine(McpJson.Write(new{port=port,mcpPort=mcpPort,root=root,pid=System.Diagnostics.Process.GetCurrentProcess().Id}));Console.Out.Flush();
                string line;while(!string.IsNullOrEmpty(line=Console.ReadLine())) {
                    var command=McpJson.Read(line);string op=Json.Str(command,"op");
                    if(op=="peer") {
                        int id=Json.Int(command,"id",1);var peers=Bridge.List();peers.RemoveAll(delegate(Peer p){return p.Id==id;});
                        peers.Add(new Peer{Id=id,Name="fixture-"+id,Host="127.0.0.1",Port=Json.Int(command,"port",0),User="fixture"});
                        Config.Set("bridgen",Math.Max(id,Json.Int(new Dictionary<string,object>{{"n",Config.Get("bridgen")}},"n",0)).ToString());
                        Bridge.Save(peers);Bridge.SetPassword(id,McpJson.Str(command,"password","fixture-only"));
                    }
                    else if(op=="set")Config.Set(Json.Str(command,"key"),Json.Str(command,"value"));
                    else if(op=="dropauth")Auth.DropAll();
                    else if(op=="restart"){server.Stop();Auth.DropAll();start();}
                    else throw new ArgumentException("Unknown fixture command");
                    host.Sync();Console.WriteLine("synced");Console.Out.Flush();
                }
            } finally {if(server!=null)server.Stop();}
            return 0;
        }
    }
}


using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using RemoteControl;

internal static class TailscaleMcpTest
{
    private static int count;
    private const BindingFlags Hidden=BindingFlags.Static|BindingFlags.NonPublic;
    private static void Check(bool value,string name){if(!value)throw new Exception(name);count++;Console.WriteLine("PASS "+name);}
    private static void Set(string field,object value){typeof(TailscaleMcp).GetField(field,Hidden).SetValue(null,value);}
    private static void Healthy(string address){typeof(TailscaleMcp).GetMethod("ApplyProbeResult",Hidden).Invoke(null,new object[]{typeof(TailscaleMcp).GetField("epoch",Hidden).GetValue(null),address,true});}
    private static void Update(){typeof(TailscaleMcp).GetMethod("Update",Hidden).Invoke(null,null);}
    private static void Idle(){var until=DateTime.UtcNow.AddSeconds(5);while((int)typeof(TailscaleMcp).GetField("notifying",Hidden).GetValue(null)!=0){if(DateTime.UtcNow>until)throw new Exception("Notification did not finish");Thread.Sleep(10);}}
    [STAThread] private static void Main(string[] args)
    {
        string root=Path.GetFullPath(args[0]);Directory.CreateDirectory(root);
        string settings=Path.Combine(root,"settings.txt"),active=Path.Combine(root,"active.json");
        typeof(Config).GetField("Path0",Hidden).SetValue(null,settings);Config.EnsureWritable();Config.Reload();
        Set("StateDir",root);File.WriteAllText(active,"{}");
        Config.SetMany(new Dictionary<string,string>{{"pwhash","fixture"},{"mcp_enabled","1"},{"mcp_web_mode","ddns"},{"mcp_web_url","https://manual.example.test:59443"},{"mcp_ts_port","8443"}});
        string address="https://fixture.example.ts.net:8443";
        Check(TailscaleMcp.Normalize("https://fixture.example.ts.net:443",443)=="https://fixture.example.ts.net","Tailscale default HTTPS URL omits 443");
        TailscaleMcp.Address(address);var psi=new ProcessStartInfo();TailscaleMcp.ConfigureProcess(psi);TailscaleMcp.Tick();
        Check(!TailscaleMcp.Enabled&&TailscaleMcp.CurrentUrl==""&&!psi.EnvironmentVariables.ContainsKey("RC_MCP_BACKEND"),"Manual MCP mode ignores the running Tailscale screen tunnel");
        Check(Config.Get("mcp_web_url")=="https://manual.example.test:59443"&&File.ReadAllText(active)=="{}","Manual URL and handoff unchanged");
        Config.Set("mcp_web_mode","tailscale");TailscaleMcp.Address(address);Set("nextProbe",DateTime.UtcNow.AddMinutes(1));Update();
        Check(File.ReadAllText(active).Contains(address)&&Config.Get("mcp_web_url")==address,"Explicit automatic mode publishes the generated URL");
        Notify.Block.Reset();Healthy(address);
        Check(Notify.Entered.WaitOne(3000),"Automatic notice begins after successful public probe");
        Set("nextHeartbeat",DateTime.MinValue);var elapsed=Stopwatch.StartNew();Update();
        Check(elapsed.ElapsedMilliseconds<1000,"Slow Telegram request does not block the OAuth heartbeat");
        Notify.Block.Set();Idle();
        Check(Notify.Sent.Count==1&&Config.Get("tg_mcp_url")==Notify.ChatId+"|"+address,"Successful automatic notice recorded");
        Healthy(address);Idle();Check(Notify.Sent.Count==1,"Unchanged address is not repeatedly sent");
        string plugin=Path.Combine(root,"plugins","RemoteControl_WebPlugin.zip");
        using(var zip=new ZipArchive(File.OpenRead(plugin),ZipArchiveMode.Read))using(var reader=new StreamReader(zip.GetEntry("remotecontrol-web/mcp.json").Open()))Check(reader.ReadToEnd().Contains(address+"/mcp"),"Generated plugin uses the public MCP port");
        address="https://renamed.example.ts.net:8443";TailscaleMcp.Address(address);Notify.Success=false;Healthy(address);Idle();
        Check(!Config.Get("tg_mcp_url").EndsWith(address),"Failed notification is not marked sent");
        Notify.Success=true;Healthy(address);Idle();Check(Config.Get("tg_mcp_url").EndsWith(address),"Failed notification can be retried");
        byte[] prior=File.ReadAllBytes(plugin);
        // Simulate another process applying manual mode while this process has cached old settings.
        string disk=File.ReadAllText(settings).Replace("mcp_web_mode=tailscale","mcp_web_mode=ddns").Replace("mcp_web_url=https://fixture.example.ts.net:8443","mcp_web_url=https://manual.example.test:59443");
        File.WriteAllText(settings,disk);
        TailscaleMcp.Address("https://late.example.ts.net:8443");Healthy("https://late.example.ts.net:8443");Idle();
        Check(Convert.ToBase64String(prior)==Convert.ToBase64String(File.ReadAllBytes(plugin)),"Late automatic completion cannot overwrite a manual plugin");
        Check(Config.Get("mcp_web_mode")=="ddns"&&Config.Get("mcp_web_url")=="https://manual.example.test:59443","Conditional save reads the latest manual choice from disk");
        var body=Notify.CurrentAddresses();Check(body.Contains("manual.example.test:59443/mcp")&&!body.Contains("late.example"),"Manual address notification uses the manually configured MCP URL");
        body=Notify.ConnectionAddresses("https://screen.example.ts.net",address,true);
        Check(body.Contains("443")&&body.Contains("8443")&&body.Contains("/mcp")&&body.Contains("ID"),"Notification contains screen/MCP URLs, ports and login guidance");
        body=Notify.ConnectionAddresses("https://user:secret@example.test","file:///C:/secret",false);
        Check(!body.Contains("secret"),"Notification excludes credential-bearing and non-web URLs");
        Application.EnableVisualStyles();
        using(var form=new NotifyForm()){
            form.ShowInTaskbar=false;form.Opacity=0;form.Show();Application.DoEvents();
            var button=(Button)form.Controls.Find("SendConnectionAddresses",true)[0];int before=Notify.Sent.Count;button.PerformClick();
            var until=DateTime.UtcNow.AddSeconds(3);while(!button.Enabled&&DateTime.UtcNow<until){Application.DoEvents();Thread.Sleep(10);}
            Check(button.Enabled&&Notify.Sent.Count==before+1&&Notify.Sent[Notify.Sent.Count-1].Contains("manual.example.test:59443/mcp"),"Notification popup button sends current addresses instead of the test message");
            using(var bitmap=new System.Drawing.Bitmap(form.Width,form.Height)){form.DrawToBitmap(bitmap,form.ClientRectangle);bitmap.Save(Path.Combine(root,"notify.png"));}
        }
        Config.SetMany(new Dictionary<string,string>{{"mcp_web_mode","tailscale"},{"mcp_ts_port","443"}});
        TailscaleMcp.Address("https://default.example.ts.net:443");Set("nextProbe",DateTime.UtcNow.AddMinutes(1));Update();Healthy(TailscaleMcp.CurrentUrl);Idle();
        Check(Config.Get("mcp_web_url")=="https://default.example.ts.net"&&File.ReadAllText(active).Contains("https://default.example.ts.net"),"Default 443 address handoff and settings are canonical");
        using(var zip=new ZipArchive(File.OpenRead(plugin),ZipArchiveMode.Read))using(var reader=new StreamReader(zip.GetEntry("remotecontrol-web/mcp.json").Open()))Check(reader.ReadToEnd().Contains("https://default.example.ts.net/mcp"),"Default 443 plugin URL omits the port");
        Console.WriteLine("ALL "+count+" CHECKS PASSED; no live settings or Telegram messages changed.");
    }
}
namespace RemoteControl
{
    internal static class Auth {internal static string MakeHash(string s){return s;}internal static bool VerifyHash(string a,string b){return a==b;}internal static void DropAll(){}}
    internal static class MainForm {internal const string Version="1.8.10";}
    internal sealed class McpOptions {internal int Port=59331;internal static McpOptions Load(){return new McpOptions();}}
    internal static class PrivateMcp {internal static string CurrentUrl="";internal static bool Ready=false;}
    internal static class Tunnel {internal static bool IsAlive(){return true;}internal static bool IsOpening{get{return false;}}}
    internal static class RemoteTunnel {internal static string Provider="tailscale",CurrentUrl="https://screen.example.ts.net";}
    internal sealed class TestStatus {internal string Url="",McpWebUrl="";internal bool McpWebReady=false;}
    internal static class Agent {internal static TestStatus ReadStatus(){return null;}}
    internal static class Https {internal static void EnsureTls(){}}
    internal static partial class Notify
    {
        internal static readonly ManualResetEvent Entered=new ManualResetEvent(false),Block=new ManualResetEvent(true);
        internal static readonly List<string> Sent=new List<string>();
        internal static string ChatId="fixture-chat",LastError="Fixture delivery failed";
        internal static bool Success=true;
        internal static bool IsConfigured{get{return ChatId.Length>0;}}
        internal static bool HasBot{get{return false;}}
        internal static bool Send(string body){Entered.Set();Block.WaitOne();lock(Sent)Sent.Add(body);return Success;}
        internal static void SaveChat(string chat){ChatId=chat;}
        internal static void TurnOff(){ChatId="";}
        internal static string FindChatId(out string who){who="fixture";return ChatId;}
        internal static string BotName(){return "fixture";}
        private static string Now(){return "fixture-time";}
    }
}

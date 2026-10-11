using System;
using System.Collections.Generic;
using RemoteControl;

internal static class NotifyMessagesTest
{
    private static int count;
    private static void Check(bool ok,string name){if(!ok)throw new Exception(name);count++;Console.WriteLine("PASS "+name);}
    private static void Main()
    {
        Config.Set("user","operator-01");Config.Set("sshname","검사 PC A");
        Config.Set("sshuser","relay-user");Config.Set("sshhost","relay.example.test");Config.Set("sshport","39333");
        Config.Set("pwhash","PASSWORD_SENTINEL");Config.Set("sshaccesskey","ACCESS_KEY_SENTINEL");
        string changed=Notify.AddressChanged("https://old.example.test/","https://new.example.test/");
        Check(changed.Contains("PC: "+Environment.MachineName)&&changed.Contains("장비명: 검사 PC A"),"Changed address identifies computer and configured device");
        Check(changed.Contains("원격제어 ID: operator-01")&&changed.Contains("터널 계정: relay-user")&&changed.Contains("relay.example.test:39333"),"Remote login and relay account are distinguished");
        Check(changed.Contains("이전 주소: https://old.example.test/")&&changed.Contains("변경 주소: https://new.example.test/")&&!changed.Contains(".test//"),"Both addresses have one trailing slash");
        Check(!changed.Contains("PASSWORD_SENTINEL")&&!changed.Contains("ACCESS_KEY_SENTINEL"),"Passwords and access keys are excluded");
        Check(Notify.AddressChanged("","https://new.example.test").Contains("(없음)"),"Missing previous address is explicit");
        Check(!Notify.AddressChanged("https://user:SECRET@old.example.test","file:///SECRET").Contains("SECRET"),"Credential-bearing and non-web addresses are excluded");
        Check(Notify.WentDown(5,"fixture").Contains("원격제어 ID: operator-01")&&Notify.CameBack("https://new.example.test/").Contains("터널 계정: relay-user"),"Outage and recovery include the same device context");
        string body=Notify.ConnectionAddresses("https://screen.example.test","https://new.example.test",true,"https://old.example.test");
        Check(body.Contains("MCP 주소가 바뀌었습니다")&&body.Contains("이전 MCP 주소: https://old.example.test/mcp")&&body.Contains("https://new.example.test/mcp"),"MCP change includes previous and new endpoints");
        Check(body.Contains("원격제어 ID: operator-01")&&body.Contains("터널 계정: relay-user"),"MCP notices identify PC accounts");
        Check(!Notify.ConnectionAddresses("https://screen.example.test","https://new.example.test",true,"https://new.example.test/").Contains("바뀌었습니다"),"Equivalent MCP URL is not labeled changed");
        Config.Set("tg_mcp_url","chat-1|https://old.example.test");
        Check(Notify.PreviousMcpAddress("chat-1")=="https://old.example.test"&&Notify.PreviousMcpAddress("chat-2")==""&&Notify.PreviousMcpAddress("")=="","Previous MCP address belongs to the same recipient");
        RemoteTunnel.Provider="tailscale";
        body=Notify.AddressChanged("https://old.example.test","https://new.example.test");
        Check(body.Contains("Tailscale")&&!body.Contains("relay-user")&&!body.Contains("relay.example.test"),"Tailscale notice omits stale SSH settings");
        RemoteTunnel.Provider="private";Config.Set("user","");Config.Set("sshport","");Config.Set("sshhost","2001:db8::1");
        body=Notify.ConnectionIdentity();
        Check(body.Contains("원격제어 ID: admin")&&body.Contains("[2001:db8::1]:2222"),"Defaults and IPv6 server formatting");
        Config.Set("sshname","검사\r\nPC: forged");
        body=Notify.ConnectionIdentity();
        Check(!body.Contains("\nPC: forged"),"Control characters cannot forge notification fields");
        Config.Set("sshname",new string('A',10000));
        Check(Notify.ConnectionIdentity().Length<700,"Oversized labels are bounded for Telegram");
        Console.WriteLine("ALL "+count+" CHECKS PASSED; no Telegram messages sent.");
    }
}
namespace RemoteControl
{
    internal static class Config
    {
        private static readonly Dictionary<string,string> values=new Dictionary<string,string>();
        internal static string Get(string key){string value;return values.TryGetValue(key,out value)?value:"";}
        internal static string[] GetMany(string[] keys){return Array.ConvertAll(keys,Get);}
        internal static void Set(string key,string value){values[key]=value;}
        internal static void Reload(){}
    }
    internal static class RemoteTunnel {internal static string Provider="private",CurrentUrl="";}
    internal static class PrivateMcp {internal static string CurrentUrl="";internal static bool Ready=false;}
    internal static class TailscaleMcp {internal static string CurrentUrl="";internal static bool Ready=false;}
    internal sealed class TestStatus {internal string Url="",McpWebUrl="";internal bool McpWebReady=false;}
    internal static class Agent {internal static TestStatus ReadStatus(){return null;}}
}

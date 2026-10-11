using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace RemoteControl
{
    internal static class PrivateMcp
    {
        private static readonly object gate=new object();
        private static volatile string url="",note="";
        private static volatile bool ready;
        private static int epoch,working,notifying;
        private static DateTime nextHeartbeat=DateTime.MinValue,nextProbe=DateTime.MinValue;
        internal static string CurrentUrl {get{return url;}}
        internal static string Note {get{return note;}}
        internal static bool Ready {get{return ready;}}
        internal static bool Enabled {get{return Config.Get("mcp_web_mode")=="private" && Config.Get("mcp_enabled")=="1" && Config.HasPassword;}}
        private static readonly string StateDir=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"RemoteControlPrivateMcp");
        private static volatile bool configured;
        internal static string Normalize(string address)
        {
            if(!SshTunnel.ValidUrl(address)||!address.StartsWith("https://",StringComparison.Ordinal)||new Uri(address).Host.IndexOf('.')<0)throw new ArgumentException("프라이빗 MCP 주소가 올바르지 않습니다.");
            return new Uri(address).GetLeftPart(UriPartial.Authority);
        }
        internal static string RequestFields()
        {
            configured=false;
            if(!Enabled)return "";
            try {
                var c=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(Path.Combine(StateDir,"gateway.json"),Encoding.UTF8));
                int local=Convert.ToInt32(c["listenPort"]);string key=Convert.ToString(c["proxyKey"]);
                if(local<1024||local>65535||local==McpOptions.Load().Port||local==Config.Port||!System.Text.RegularExpressions.Regex.IsMatch(key,@"\A[A-Za-z0-9_-]{43}\z"))throw new ArgumentException();
                configured=true;note="프라이빗 MCP 주소 생성 대기 중";
                return ",\"mcpTarget\":\"127.0.0.1:"+local+"\",\"mcpProxyKey\":\""+key+"\"";
            }catch{note="웹 MCP 설정에서 프라이빗 터널 자동 연결을 적용하세요. 서비스 모드가 필요합니다.";return "";}
        }
        internal static void Address(string value)
        {
            if(!Enabled || !configured)return;
            try {
                string valid=Normalize(value);
                lock(gate){url=valid;ready=false;epoch++;nextHeartbeat=nextProbe=DateTime.MinValue;note="MCP 공개 주소 확인 중";}
            }catch{Error("프라이빗 터널 MCP 주소를 확인하지 못했습니다.");}
        }
        internal static void Error(string message){lock(gate){ready=false;url="";note=message;epoch++;}}
        internal static void Stop(){lock(gate){url="";ready=false;note="";configured=false;epoch++;}}
        internal static void Tick()
        {
            if(!Enabled || RemoteTunnel.Provider!="private"){if(url.Length>0)Stop();return;}
            if(!SshTunnel.IsAlive()){if(!SshTunnel.IsOpening && url.Length>0)Stop();return;}
            if(url.Length==0 || Interlocked.CompareExchange(ref working,1,0)!=0)return;
            ThreadPool.QueueUserWorkItem(delegate {
                try{Update();}catch(Exception){lock(gate){if(Enabled&&url.Length>0){note="MCP 자동 연결 준비 실패. 웹 MCP 설정과 설치 경로를 확인하세요.";ready=false;}}}
                finally{Interlocked.Exchange(ref working,0);}
            });
        }
        private static bool Current(int generation,string address){return generation==epoch && address==url && Enabled && RemoteTunnel.Provider=="private" && SshTunnel.IsAlive();}
        private static void Update()
        {
            string address;int generation;
            lock(gate){address=url;generation=epoch;}
            if(!Current(generation,address))return;
            if(DateTime.UtcNow>=nextHeartbeat){
                var active=new{publicUrl=address,lanPort=McpOptions.Load().Port,expiresAt=(long)(DateTime.UtcNow.AddSeconds(40)-new DateTime(1970,1,1)).TotalMilliseconds};
                string path=Path.Combine(StateDir,"active.json"),temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
                try{File.WriteAllText(temp,new JavaScriptSerializer().Serialize(active),new UTF8Encoding(false));lock(gate){if(!Current(generation,address))return;File.Replace(temp,path,null);}}finally{if(File.Exists(temp))File.Delete(temp);}
                lock(gate){
                    if(!Current(generation,address))return;
                    nextHeartbeat=DateTime.UtcNow.AddSeconds(10);
                    if(Config.Get("mcp_web_url")!=address&&!Config.SetManyIf("mcp_web_mode","private",new Dictionary<string,string>{{"mcp_web_url",address},{"mcp_private_url",address}},null))return;
                }
            }
            if(DateTime.UtcNow<nextProbe)return;
            nextProbe=DateTime.UtcNow.AddSeconds(15);
            bool healthy=Probe(address);
            ApplyProbeResult(generation,address,healthy);
        }
        private static void ApplyProbeResult(int generation,string address,bool healthy)
        {
            lock(gate){
                if(!Current(generation,address))return;
                ready=healthy;
                if(!healthy){note="MCP 주소 확인 중 · 연결 준비 전";return;}
                string folder=Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),"plugins");
                if(Config.Get("mcp_private_plugin_url")!=address || !File.Exists(Path.Combine(folder,"RemoteControl_WebPlugin.zip"))){
                    if(!Config.SetManyIf("mcp_web_mode","private",new Dictionary<string,string>{{"mcp_private_plugin_url",address}},delegate{
                        RemoteControlPackages.WebPlugin.Write(Path.Combine(folder,"RemoteControl_WebPlugin.zip"),address,MainForm.Version);
                    }))return;
                }
                note="MCP 접속 확인됨 · ChatGPT / Claude에서 로그인하세요";
                nextProbe=DateTime.UtcNow.AddSeconds(60);
            }
            // 텔레그램 재시도가 오래 걸려도 주소 heartbeat/OAuth 연결은 유지한다.
            string chat=Notify.ChatId,sent=chat+"|"+address;
            if(!Notify.IsConfigured||Config.Get("tg_mcp_url")==sent||!Current(generation,address)||Interlocked.CompareExchange(ref notifying,1,0)!=0)return;
            ThreadPool.QueueUserWorkItem(delegate{
                try{
                    if(!Current(generation,address)||chat!=Notify.ChatId)return;
                    bool delivered=Notify.Send(Notify.ConnectionAddresses(RemoteTunnel.CurrentUrl,address,true,Notify.PreviousMcpAddress(chat)));
                    lock(gate){
                        if(!Current(generation,address)||chat!=Notify.ChatId)return;
                        if(delivered)Config.SetManyIf("mcp_web_mode","private",new Dictionary<string,string>{{"tg_mcp_url",sent}},null);
                        else{note+=" · 텔레그램 발송 재시도 대기";nextProbe=DateTime.UtcNow.AddSeconds(15);}
                    }
                }catch{}finally{Interlocked.Exchange(ref notifying,0);}
            });
        }
        internal static bool Probe(string address)
        {
            try {
                Https.EnsureTls();
                var request=(HttpWebRequest)WebRequest.Create(address+"/.well-known/oauth-protected-resource");
                request.Proxy=null;request.Timeout=8000;request.ReadWriteTimeout=8000;request.AllowAutoRedirect=false;
                using(var response=(HttpWebResponse)request.GetResponse())using(var stream=response.GetResponseStream()){
                    if(response.StatusCode!=HttpStatusCode.OK)return false;
                    byte[] buffer=new byte[32769];int total=0,n;
                    while(total<buffer.Length && (n=stream.Read(buffer,total,buffer.Length-total))>0)total+=n;
                    if(total==buffer.Length)return false;
                    var value=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(Encoding.UTF8.GetString(buffer,0,total));
                    return Convert.ToString(value["resource"])==address+"/mcp";
                }
            }catch{return false;}
        }
    }
}

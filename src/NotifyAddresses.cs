using System;
using System.Text;

namespace RemoteControl
{
    internal static partial class Notify
    {
        // 서비스는 SYSTEM으로 실행되므로 실행 계정 대신 실제 접속 ID와 터널 계정을 구분한다.
        internal static string ConnectionIdentity()
        {
            string[] values=Config.GetMany(new[]{"user","sshname","sshuser","sshhost","sshport"});
            string provider=RemoteTunnel.Provider;
            string text="PC: "+NoticeValue(Environment.MachineName,"알 수 없음")+
                "\n원격제어 ID: "+NoticeValue(values[0],"admin")+
                "\n연결 방식: "+(provider=="private"?"SSHTunnel":provider=="tailscale"?"Tailscale":"사용 안 함");
            if(provider=="private"){
                string host=NoticeValue(values[3],"미설정");
                if(host.IndexOf(':')>=0&&!host.StartsWith("[",StringComparison.Ordinal))host="["+host+"]";
                text+="\n장비명: "+NoticeValue(values[1],Environment.MachineName+" 원격제어")+
                    "\n터널 계정: "+NoticeValue(values[2],"미설정")+
                    "\n터널 서버: "+host+":"+NoticeValue(values[4],"2222");
            }
            return text;
        }
        private static string NoticeValue(string value,string fallback)
        {
            var clean=new StringBuilder();
            foreach(char c in (value??"").Trim()){
                // 설정의 개행으로 다른 PC/계정 행을 위조하지 못하게 한 줄로 제한한다.
                clean.Append(char.IsControl(c)?' ':c);
                if(clean.Length>=160)break;
            }
            string result=clean.ToString().Trim();
            return result.Length==0?fallback:result;
        }
        internal static string NoticeAddress(string value)
        {
            Uri uri;
            if(string.IsNullOrWhiteSpace(value))return "(없음)";
            return SafeAddress(value,out uri)?uri.GetLeftPart(UriPartial.Authority)+"/":"(올바른 웹 주소 없음)";
        }
        internal static string PreviousMcpAddress(string chat)
        {
            string last=Config.Get("tg_mcp_url"),prefix=chat+"|";
            // 새 수신자에게 다른 수신자의 이전 주소를 끼워 보내지 않는다.
            return !string.IsNullOrEmpty(chat)&&last.StartsWith(prefix,StringComparison.Ordinal)?last.Substring(prefix.Length):"";
        }
        internal static string ConnectionAddresses(string screenUrl,string mcpUrl,bool verified,string previousMcpUrl="")
        {
            Uri screen,mcp,previous;
            string screenLine=SafeAddress(screenUrl,out screen)
                ? screen.GetLeftPart(UriPartial.Authority)+"/\n화면 포트: "+screen.Port : "연결되지 않음";
            string mcpLine=SafeAddress(mcpUrl,out mcp)
                ? mcp.GetLeftPart(UriPartial.Authority)+"/mcp\nMCP HTTPS 포트: "+mcp.Port+"\n상태: "+(verified?"접속 확인됨 · 앱 로그인 필요":"설정된 주소 · 접속 확인 전") : "아직 준비되지 않음";
            bool changed=mcp!=null&&SafeAddress(previousMcpUrl,out previous)&&previous.GetLeftPart(UriPartial.Authority)!=mcp.GetLeftPart(UriPartial.Authority);
            string prior=changed?"\n이전 MCP 주소: "+NoticeAddress(previousMcpUrl).TrimEnd('/')+"/mcp\n":"";
            return (changed?"⚠ 원격제어 MCP 주소가 바뀌었습니다":"🔗 원격제어 접속 주소")+"\n"+ConnectionIdentity()+"\n"+prior+
                "\n화면 원격제어\n"+screenLine+"\n\n웹 ChatGPT / Claude MCP\n"+mcpLine+
                "\n\nMCP 주소를 앱에 등록하고 연결 화면에서 원격제어 ID·비밀번호로 로그인하세요.\n"+Now();
        }
        private static bool SafeAddress(string value,out Uri uri)
        {
            Uri parsed;
            bool valid=Uri.TryCreate(value,UriKind.Absolute,out parsed)&&(parsed.Scheme=="https"||parsed.Scheme=="http")&&parsed.UserInfo.Length==0;
            uri=valid?parsed:null;return valid;
        }
        internal static string CurrentAddresses()
        {
            return CurrentAddressesWithScreen(null);
        }
        internal static string CurrentAddressesWithScreen(string overrideScreenUrl)
        {
            Config.Reload();var state=Agent.ReadStatus();
            string screen=state!=null?state.Url:RemoteTunnel.CurrentUrl;
            if(overrideScreenUrl!=null)screen=overrideScreenUrl;
            string mcp=state!=null?state.McpWebUrl:(Config.Get("mcp_web_mode")=="private"?PrivateMcp.CurrentUrl:TailscaleMcp.CurrentUrl);
            bool verified=state!=null?state.McpWebReady:(Config.Get("mcp_web_mode")=="private"?PrivateMcp.Ready:TailscaleMcp.Ready);
            if(Config.Get("mcp_web_mode")!="tailscale"&&Config.Get("mcp_web_mode")!="private"){mcp=Config.Get("mcp_web_url");verified=false;}
            return ConnectionAddresses(screen,mcp,verified);
        }
    }
}

using System;

namespace RemoteControl
{
    internal static partial class Notify
    {
        internal static string ConnectionAddresses(string screenUrl,string mcpUrl,bool verified)
        {
            Uri screen,mcp;
            string screenLine=SafeAddress(screenUrl,out screen)
                ? screen.GetLeftPart(UriPartial.Authority)+"/\n화면 포트: "+screen.Port : "연결되지 않음";
            string mcpLine=SafeAddress(mcpUrl,out mcp)
                ? mcp.GetLeftPart(UriPartial.Authority)+"/mcp\nMCP HTTPS 포트: "+mcp.Port+"\n상태: "+(verified?"접속 확인됨 · 앱 로그인 필요":"설정된 주소 · 접속 확인 전") : "아직 준비되지 않음";
            return "🔗 원격제어 접속 주소\nPC: "+Environment.MachineName+"\n\n화면 원격제어\n"+screenLine+
                "\n\n웹 ChatGPT / Claude MCP\n"+mcpLine+
                "\n\nMCP 주소를 앱에 등록하고 연결 화면에서 원격제어 ID·비밀번호로 로그인하세요.\n"+Now();
        }
        private static bool SafeAddress(string value,out Uri uri)
        {return Uri.TryCreate(value,UriKind.Absolute,out uri)&&(uri.Scheme=="https"||uri.Scheme=="http")&&uri.UserInfo.Length==0;}
        internal static string CurrentAddresses()
        {
            Config.Reload();var state=Agent.ReadStatus();
            string screen=state!=null?state.Url:RemoteTunnel.CurrentUrl;
            string mcp=state!=null?state.McpWebUrl:(Config.Get("mcp_web_mode")=="private"?PrivateMcp.CurrentUrl:TailscaleMcp.CurrentUrl);
            bool verified=state!=null?state.McpWebReady:(Config.Get("mcp_web_mode")=="private"?PrivateMcp.Ready:TailscaleMcp.Ready);
            if(Config.Get("mcp_web_mode")!="tailscale"&&Config.Get("mcp_web_mode")!="private"){mcp=Config.Get("mcp_web_url");verified=false;}
            return ConnectionAddresses(screen,mcp,verified);
        }

    }
}

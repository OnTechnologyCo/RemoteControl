// HTTPS/OAuth 웹 중계가 호출하는 내부 처리기. 외부 NIC에는 바인딩하지 않는다.
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace RemoteControl
{
    internal sealed class McpHost : IDisposable
    {
        private readonly Func<McpOptions> settings;
        private readonly Func<string,string,bool> login;
        private readonly Action<string> log;
        private readonly McpTools tools;
        private readonly McpSessions sessions;
        private readonly object gate = new object();
        private readonly HashSet<TcpClient> clients = new HashSet<TcpClient>();
        private TcpListener listener;
        private Timer poll;
        private bool stopped;
        private string credentials;
        internal volatile int Port;
        internal volatile string Note = "MCP 꺼짐";
        internal McpHost(Func<McpOptions> read, Func<string,string,bool> checkLogin, Action<string> logger, string sessionPath = null)
        { settings=read; login=checkLogin; log=logger; sessions=new McpSessions(sessionPath); tools=new McpTools(sessionPath==null?null:Path.Combine(Path.GetDirectoryName(sessionPath),"transfer-state")); }
        internal void Start() { Sync(); poll=new Timer(delegate { Sync(); },null,2000,2000); }
        internal void Sync()
        {
            lock(gate)
            {
                if(stopped)return;
                try {
                    McpOptions o=settings();
                    if(credentials!=null&&credentials!=o.CredentialStamp) { sessions.Clear(o.CredentialStamp); Auth.DropAll(); } credentials=o.CredentialStamp;
                    int want=o.Enabled?o.Port:0;
                    if(want==Port)return;
                    if(listener!=null)listener.Stop(); listener=null; Port=0;
                    if(want==0) { Note="MCP 꺼짐";return; }
                    var accept=new TcpListener(IPAddress.Loopback,want); accept.ExclusiveAddressUse=true; accept.Start(); Interop.MakeHandleNonInheritable(accept.Server.Handle);
                    listener=accept; Port=want; Note="Web MCP 내부 처리 준비 · 포트 "+want;
                    var thread=new Thread(delegate(){ Accept(accept); }){IsBackground=true};thread.Start();
                    log(Note);
                } catch(Exception ex) { Note="MCP 시작 실패: "+ex.Message; }
            }
        }
        private void Accept(TcpListener accept)
        {
            while(true)
            {
                TcpClient client; try {client=accept.AcceptTcpClient();}catch{return;}
                lock(gate) { if(stopped||clients.Count>=32) {client.Close();continue;}clients.Add(client); }
                new Thread(delegate(){ try{ Handle(client); }catch{}finally{client.Close();lock(gate)clients.Remove(client);} }){IsBackground=true}.Start();
            }
        }
        private void Handle(TcpClient client)
        {
            Interop.MakeHandleNonInheritable(client.Client.Handle);client.NoDelay=true;
            NetworkStream stream=client.GetStream();stream.ReadTimeout=30000;stream.WriteTimeout=30000;
            McpOptions options=settings(); var peer=((IPEndPoint)client.Client.RemoteEndPoint).Address;
            if(!options.Enabled||!IPAddress.IsLoopback(peer)) {Reply(stream,"403 Forbidden",new{error="Web MCP 사용 설정을 확인하세요."});return;}
            byte[] prefix; string header=Server.ReadHead(stream,out prefix); if(string.IsNullOrEmpty(header))return;
            string[] lines=header.Split(new[]{"\r\n"},StringSplitOptions.None), request=lines[0].Split(' ');
            if(request.Length!=3){Reply(stream,"400 Bad Request",new{error="Invalid HTTP request"});return;}
            string method=request[0],target=request[1],path=target.Split('?')[0];
            // 내부 HTTPS/OAuth 게이트웨이만 직접 접속한다.
            if(!string.IsNullOrEmpty(Server.Header(lines,"Origin"))||!string.IsNullOrEmpty(Server.Header(lines,"X-Forwarded-For"))||!string.IsNullOrEmpty(Server.Header(lines,"Forwarded")))
            {Reply(stream,"403 Forbidden",new{error="Web MCP HTTPS 주소로 연결하세요."});return;}
            if(!string.IsNullOrEmpty(Server.Header(lines,"Transfer-Encoding"))) {Reply(stream,"400 Bad Request",new{error="Content-Length required"});return;}
            long length=0; string lengthText=Server.Header(lines,"Content-Length");
            if(!string.IsNullOrEmpty(lengthText)&&(!long.TryParse(lengthText,out length)||length<0)) {Reply(stream,"400 Bad Request",new{error="Invalid length"});return;}
            string ip=peer.ToString();
            if(path=="/login"&&method=="POST")
            {
                if(length>8192){Reply(stream,"413 Payload Too Large",new{error="Login body too large"});return;}
                int locked=Auth.LockedFor(ip);if(locked>0){Reply(stream,"429 Too Many Requests",new{error="로그인 시도가 너무 많습니다.",retry_after=locked});return;}
                Dictionary<string,object> value;try{value=McpJson.Read(Encoding.UTF8.GetString(Read(stream,prefix,length,8192)));}catch{Reply(stream,"400 Bad Request",new{error="Invalid login JSON"});return;}
                if(value==null){Reply(stream,"400 Bad Request",new{error="Invalid login JSON"});return;}
                string user=Json.Str(value,"user"), password=Json.Str(value,"password");
                if(!login(user,password)){Auth.NoteFailure(ip);Reply(stream,"401 Unauthorized",new{error="Remote ID/PW가 맞지 않습니다."});return;}
                Auth.NoteSuccess(ip);Session session=sessions.Create(user,ip,options.CredentialStamp);
                Reply(stream,"200 OK",new{ok=true,hostname=Environment.MachineName,version=MainForm.Version},"Set-Cookie: rcmcpid="+session.Token+"; Path=/; HttpOnly; SameSite=Strict\r\n");return;
            }
            string token=Cookie(Server.Header(lines,"Cookie"),"rcmcpid"); Session authenticated=sessions.Find(token,options.CredentialStamp);
            if(authenticated==null||authenticated.Ip!=ip){Reply(stream,"401 Unauthorized",new{error="Remote ID/PW 로그인이 필요합니다."});return;}
            if(path=="/logout"&&method=="POST"){sessions.Drop(token,options.CredentialStamp);Reply(stream,"200 OK",new{ok=true});return;}
            if(path=="/transfer-download"&&method=="GET")
            {try{tools.Transfers.SendDownload(stream,Query(target,"id"),token,options,Server.Header(lines,"Range"));}catch(Exception ex){Reply(stream,"400 Bad Request",new{error=ex.Message});}return;}
            if(path!="/mcp"){Reply(stream,"404 Not Found",new{error="Not found"});return;}
            if(method=="GET"||method=="DELETE"){Reply(stream,"405 Method Not Allowed",new{error="Stateless MCP: use POST"},"Allow: POST\r\n");return;}
            if(method!="POST"){Reply(stream,"405 Method Not Allowed",new{error="Use POST"});return;}
            if(length>2*1024*1024){Reply(stream,"413 Payload Too Large",new{error="JSON body limit: 2MiB"});return;}
            object response; Dictionary<string,object> rpc;
            try {rpc=McpJson.Read(Encoding.UTF8.GetString(Read(stream,prefix,length,2*1024*1024)));}
            catch {Reply(stream,"200 OK",McpJson.Error(null,-32700,"Invalid JSON"));return;}
            if(rpc==null){Reply(stream,"200 OK",McpJson.Error(null,-32600,"Invalid request"));return;}
            object id=McpJson.Id(rpc);string call=Json.Str(rpc,"method");var args=McpJson.Obj(rpc,"params");
            if(Json.Str(rpc,"jsonrpc")!="2.0"||string.IsNullOrEmpty(call)){Reply(stream,"200 OK",McpJson.Error(id,-32600,"Invalid JSON-RPC request"));return;}
            if(call.StartsWith("notifications/",StringComparison.Ordinal)){Server.Send(stream,"202 Accepted","application/json",new byte[0],"Cache-Control: no-store\r\n");return;}
            if(id==null){Reply(stream,"200 OK",McpJson.Error(null,-32600,"Requests require an id"));return;}
            switch(call)
            {
                case "initialize":
                    string protocol=Json.Str(args,"protocolVersion");if(Array.IndexOf(new[]{"2025-11-25","2025-06-18","2025-03-26","2024-11-05"},protocol)<0)protocol="2025-11-25";
                    response=McpJson.Result(id,new{protocolVersion=protocol,capabilities=new{tools=new{listChanged=false}},serverInfo=new{name="RemoteControl Web backend",version=MainForm.Version},instructions="Use pc_get_status to verify the remote hostname before changes. File paths are on the remote PC. Workspace directories are default working locations, not access restrictions. Any absolute local path may be used subject to the remote Windows account permissions and path validation. Never infer permission denial from a path being outside a workspace; check the actual tool response. NOT_FOUND and INVALID_ARGUMENT do not mean ACCESS_DENIED. File and folder transfers use pc_begin_upload, pc_write_upload_chunk, pc_finish_upload, pc_prepare_download and pc_read_download_chunk with SHA-256 validation. Commands run with the remote agent's Windows rights. Keep output concise; use logs and screenshot to verify a deployment."+McpBridge.Instructions});break;
                case "ping":response=McpJson.Result(id,new{});break;
                case "tools/list":response=McpJson.Result(id,new{tools=McpTools.List()});break;
                case "resources/list":response=McpJson.Result(id,new{resources=new object[0]});break;
                case "resources/templates/list":response=McpJson.Result(id,new{resourceTemplates=new object[0]});break;
                case "prompts/list":response=McpJson.Result(id,new{prompts=new object[0]});break;
                case "tools/call":
                    string name=Json.Str(args,"name");var clock=System.Diagnostics.Stopwatch.StartNew();bool success=false;
                    try {response=McpJson.Result(id,tools.Call(name,McpJson.Obj(args,"arguments"),options,authenticated.User,authenticated.Token));success=true;}
                    catch(Exception ex){response=McpJson.Result(id,McpJson.ToolError(ex));}
                    log("MCP "+McpJson.Write(new{tool=name,user=authenticated.User,ip=ip,ok=success,ms=clock.ElapsedMilliseconds}));break;
                default:response=McpJson.Error(id,-32601,"Method not found");break;
            }
            Reply(stream,"200 OK",response);
        }
        internal object BridgeCall(Dictionary<string,object> request,Session session)
        {
            Sync();McpOptions options=settings();
            if(stopped||!options.Enabled)throw new McpCapabilityDisabledException("하위 PC의 MCP 사용 설정이 꺼져 있습니다.");
            if(Auth.Find(session.Token)!=session)throw new UnauthorizedAccessException("하위 PC 로그인이 만료되었습니다.");
            string owner=McpTools.Required(request,"owner");
            if(!System.Text.RegularExpressions.Regex.IsMatch(owner,@"\A[0-9a-f]{64}\z"))throw new ArgumentException("잘못된 중계 소유자입니다.");
            string operation=Json.Str(request,"operation");
            if(operation=="list")return McpJson.Text(new{hostname=Environment.MachineName,version=MainForm.Version,tools=McpTools.List(false)});
            if(operation!="call")throw new ArgumentException("지원하지 않는 중계 요청입니다.");
            string name=McpTools.Required(request,"tool");McpBridge.CheckTool(name,options);
            if(name!="pc_get_status"&&!string.Equals(McpTools.Required(request,"expected_hostname"),Environment.MachineName,StringComparison.OrdinalIgnoreCase))
                throw new McpBridgeException("BRIDGE_HOSTNAME_MISMATCH","하위 PC 이름이 확인한 대상과 다릅니다. 실행하지 않았습니다.");
            string isolatedOwner="bridge-"+McpTools.Hash(Encoding.UTF8.GetBytes(McpJson.Write(new{owner,user=session.User,stamp=options.CredentialStamp})));
            var clock=System.Diagnostics.Stopwatch.StartNew();bool success=false;
            try {object result=tools.Call(name,McpJson.Obj(request,"arguments"),options,isolatedOwner,isolatedOwner);success=true;return result;}
            finally {log("MCP bridge "+McpJson.Write(new{tool=name,user=session.User,ok=success,ms=clock.ElapsedMilliseconds}));}
        }
        private static byte[] Read(NetworkStream stream,byte[] prefix,long length,int max)
        {
            if(length<0||length>max)throw new IOException("Body limit");byte[] bytes=new byte[(int)length];int read=(int)Math.Min(length,prefix.Length);Buffer.BlockCopy(prefix,0,bytes,0,read);
            while(read<bytes.Length){int n=stream.Read(bytes,read,bytes.Length-read);if(n<=0)throw new EndOfStreamException();read+=n;}return bytes;
        }
        private static string Cookie(string cookies,string name)
        {foreach(string part in (cookies??"").Split(';')){string p=part.Trim();if(p.StartsWith(name+"=",StringComparison.Ordinal))return p.Substring(name.Length+1);}return "";}
        internal static string Query(string target,string name)
        {int start=target.IndexOf('?');if(start<0)return "";foreach(string pair in target.Substring(start+1).Split('&')){int eq=pair.IndexOf('=');if(eq>0&&Uri.UnescapeDataString(pair.Substring(0,eq))==name)return Uri.UnescapeDataString(pair.Substring(eq+1));}return "";}
        private static void Reply(NetworkStream stream,string status,object body,string extra="")
        {Server.Send(stream,status,"application/json; charset=utf-8",Encoding.UTF8.GetBytes(McpJson.Write(body)),"Cache-Control: no-store\r\n"+extra);}
        public void Dispose()
        {
            lock(gate){stopped=true;if(poll!=null)poll.Dispose();if(listener!=null)listener.Stop();listener=null;Port=0;foreach(var client in clients)try{client.Close();}catch{}}
            tools.Dispose();
        }
    }
}



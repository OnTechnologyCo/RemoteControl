// 전용 LAN 포트만 사용한다. 브라우저/터널 포트로 /mcp를 중계하지 않는다.
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
                    var accept=new TcpListener(IPAddress.Any,want); accept.Start(); Interop.MakeHandleNonInheritable(accept.Server.Handle);
                    listener=accept; Port=want; Note="MCP 대기 중 · 포트 "+want;
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
            if(!options.Enabled||!options.Accepts(peer)) {Reply(stream,"403 Forbidden",new{error="MCP 사용 또는 허용 IP 설정을 확인하세요."});return;}
            byte[] prefix; string header=Server.ReadHead(stream,out prefix); if(string.IsNullOrEmpty(header))return;
            string[] lines=header.Split(new[]{"\r\n"},StringSplitOptions.None), request=lines[0].Split(' ');
            if(request.Length!=3){Reply(stream,"400 Bad Request",new{error="Invalid HTTP request"});return;}
            string method=request[0],target=request[1],path=target.Split('?')[0];
            // 브라우저 출처 및 외부 프록시를 통한 호출은 받지 않는다. 로컬 stdio 연결기가 직접 접속한다.
            if(!string.IsNullOrEmpty(Server.Header(lines,"Origin"))||!string.IsNullOrEmpty(Server.Header(lines,"X-Forwarded-For"))||!string.IsNullOrEmpty(Server.Header(lines,"Forwarded")))
            {Reply(stream,"403 Forbidden",new{error="MCP는 LLM PC에서 전용 LAN 주소로 직접 연결하세요."});return;}
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
            if(path=="/upload"&&method=="POST") {Upload(stream,prefix,length,target,options);return;}
            if(path=="/transfer-download"&&method=="GET")
            {try{tools.Transfers.SendDownload(stream,Query(target,"id"),token,options,Server.Header(lines,"Range"));}catch(Exception ex){Reply(stream,"400 Bad Request",new{error=ex.Message});}return;}
            if(path=="/download"&&method=="GET")
            {
                try { string file=McpPath.Check(Query(target,"path"),options);Files.Send(stream,lines,file,false); }
                catch(Exception ex){Reply(stream,"400 Bad Request",new{error=ex.Message});}return;
            }
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
                    response=McpJson.Result(id,new{protocolVersion=protocol,capabilities=new{tools=new{listChanged=false}},serverInfo=new{name="RemoteControl LAN",version=MainForm.Version},instructions="Use pc_get_status to verify the remote hostname before changes. File paths are on the remote PC. File and folder transfers use pc_begin_upload, pc_write_upload_chunk, pc_finish_upload, pc_prepare_download and pc_read_download_chunk with SHA-256 validation. Commands run with the remote agent's Windows rights. Keep output concise; use logs and screenshot to verify a deployment."});break;
                case "ping":response=McpJson.Result(id,new{});break;
                case "tools/list":response=McpJson.Result(id,new{tools=McpTools.List()});break;
                case "resources/list":response=McpJson.Result(id,new{resources=new object[0]});break;
                case "resources/templates/list":response=McpJson.Result(id,new{resourceTemplates=new object[0]});break;
                case "prompts/list":response=McpJson.Result(id,new{prompts=new object[0]});break;
                case "tools/call":
                    string name=Json.Str(args,"name");var clock=System.Diagnostics.Stopwatch.StartNew();bool success=false;
                    try {response=McpJson.Result(id,tools.Call(name,McpJson.Obj(args,"arguments"),options,authenticated.User,authenticated.Token));success=true;}
                    catch(Exception ex){response=McpJson.Result(id,McpJson.ToolError(ex.Message));}
                    log("MCP "+McpJson.Write(new{tool=name,user=authenticated.User,ip=ip,ok=success,ms=clock.ElapsedMilliseconds}));break;
                default:response=McpJson.Error(id,-32601,"Method not found");break;
            }
            Reply(stream,"200 OK",response);
        }
        private static void Upload(NetworkStream stream,byte[] prefix,long length,string target,McpOptions options)
        {
            string temp=null;
            try {
                if(length>4L*1024*1024*1024)throw new IOException("업로드는 4GiB까지입니다.");
                string path=McpPath.Check(Query(target,"path"),options),expected=Query(target,"expected_sha256"),sha=Query(target,"sha256");
                if(!System.Text.RegularExpressions.Regex.IsMatch(sha,"^[a-fA-F0-9]{64}$"))throw new ArgumentException("업로드 SHA-256이 필요합니다.");
                McpTools.CheckHash(path,expected);temp=path+".mcp-upload-"+Guid.NewGuid().ToString("N")+".tmp";
                using(var file=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None,256*1024))
                {
                    int pre=(int)Math.Min(length,prefix.Length);file.Write(prefix,0,pre);long left=length-pre;byte[] buffer=new byte[256*1024];
                    while(left>0){int n=stream.Read(buffer,0,(int)Math.Min(buffer.Length,left));if(n<=0)throw new EndOfStreamException("업로드가 중간에 끊겼습니다.");file.Write(buffer,0,n);left-=n;}
                }
                if(!string.Equals(McpTools.FileHash(temp),sha,StringComparison.OrdinalIgnoreCase))throw new IOException("업로드 SHA-256이 일치하지 않습니다.");
                McpTools.Commit(temp,path,expected,options);temp=null;Reply(stream,"200 OK",McpTools.Info(path));
            }catch(Exception ex){Reply(stream,"400 Bad Request",new{error=ex.Message});}
            finally{if(temp!=null)try{File.Delete(temp);}catch{}}
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

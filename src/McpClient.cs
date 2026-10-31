// LLM PC에서 실행하는 stdio 연결기. 대상 PC에는 인터넷/OAuth/Node 설치가 필요 없다.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace RemoteControl
{
    internal sealed class McpClient : IDisposable
    {
        private readonly Uri address;
        private readonly string user, password;
        private CookieContainer cookies = new CookieContainer();
        private bool loggedIn;
        internal McpClient(string host, string port, string login, string secret)
        {
            IPAddress ip; int n;
            if(!IPAddress.TryParse(host,out ip)||ip.AddressFamily!=System.Net.Sockets.AddressFamily.InterNetwork ||
                !int.TryParse(port,out n)||n<1||n>65535)throw new ArgumentException("대상 IPv4 주소와 MCP 포트를 지정하세요.");
            if(string.IsNullOrWhiteSpace(login)||string.IsNullOrEmpty(secret))throw new ArgumentException("Remote 로그인 ID/PW를 설정하세요.");
            address=new Uri("http://"+ip+":"+n+"/");user=login;password=secret;
        }
        internal static int Run(string[] args)
        {
            McpClient client=null;
            try {
                // winexe는 콘솔이 없어 InputEncoding setter가 실패한다. 상속된 파이프를 직접 UTF-8로 연다.
                Console.SetIn(new StreamReader(Console.OpenStandardInput(),new UTF8Encoding(false),false));
                Console.SetOut(new StreamWriter(Console.OpenStandardOutput(),new UTF8Encoding(false)){AutoFlush=true});
                Console.SetError(new StreamWriter(Console.OpenStandardError(),new UTF8Encoding(false)){AutoFlush=true});
                string profile="default"; for(int i=1;i<args.Length;i++)if(args[i]=="--profile"&&i+1<args.Length)profile=args[++i];
                string host=Environment.GetEnvironmentVariable("RC_MCP_HOST"),port=Environment.GetEnvironmentVariable("RC_MCP_PORT")??"5901";
                string user=Environment.GetEnvironmentVariable("RC_MCP_USER"),password=Environment.GetEnvironmentVariable("RC_MCP_PASSWORD");
                if(string.IsNullOrEmpty(host)) {var p=McpProfile.Load(profile);host=Json.Str(p,"host");port=McpJson.Str(p,"port","5901");user=Json.Str(p,"user");password=Json.Str(p,"password");}
                client=new McpClient(host,port,user,password);
                string line;while((line=Console.ReadLine())!=null)
                {
                    if(string.IsNullOrWhiteSpace(line))continue;
                    object id=null;object response;
                    try { if(line.Length>2*1024*1024)throw new IOException("MCP request too large");var rpc=McpJson.Read(line);if(rpc==null)throw new ArgumentException("Invalid JSON-RPC");id=McpJson.Id(rpc);response=client.Dispatch(rpc); }
                    catch(Exception ex) {response=McpJson.Error(id,-32000,ex.Message);}
                    if(response!=null) {Console.WriteLine(McpJson.Write(response));Console.Out.Flush();}
                }
                return 0;
            }catch(Exception ex){Console.Error.WriteLine("RemoteControl MCP: "+ex.Message);return 1;}
            finally{if(client!=null)client.Dispose();}
        }
        internal object Dispatch(Dictionary<string,object> rpc)
        {
            string method=Json.Str(rpc,"method")??"";object id=McpJson.Id(rpc);
            if(Json.Str(rpc,"jsonrpc")!="2.0"||method.Length==0)return McpJson.Error(id,-32600,"Invalid JSON-RPC request");
            if(method.StartsWith("notifications/",StringComparison.Ordinal))return null;
            if(id==null)return McpJson.Error(null,-32600,"Requests require an id");
            if(method=="tools/call")
            {
                var p=McpJson.Obj(rpc,"params");string name=Json.Str(p,"name");var a=McpJson.Obj(p,"arguments");
                if(name=="pc_upload_file"||name=="pc_download_file")
                {try{return McpJson.Result(id,name=="pc_upload_file"?Upload(a):Download(a));}catch(Exception ex){return McpJson.Result(id,McpJson.ToolError(ex));}}
            }
            var response=Remote(rpc);
            if(method=="tools/list"&&response.ContainsKey("result"))
            {
                var result=McpJson.Obj(response,"result");var list=new List<object>(); object items;
                if(result.TryGetValue("tools",out items))foreach(object item in (IEnumerable)items)list.Add(item);
                list.Add(TransferTool(true));list.Add(TransferTool(false));result["tools"]=list.ToArray();
            }
            return response;
        }
        private static object TransferTool(bool upload)
        {
            var props=new Dictionary<string,object>();props["local_path"]=new{type="string",description="Absolute file path on the LLM PC"};props["remote_path"]=new{type="string",description="Absolute local file path on the target PC; may be outside configured workspaces"};
            if(upload)props["expected_sha256"]=new{type="string",description="Current REMOTE file hash, required to replace an existing file. A remote backup is retained."};
            return new{name=upload?"pc_upload_file":"pc_download_file",description=upload?
                "Stream a compiled binary or other file from the LLM PC to the remote PC, up to 4GiB. Verifies SHA-256; never sends binary data through model context. Create the destination folder first.":
                "Stream a remote file to the LLM PC and verify SHA-256. Refuses to overwrite an existing local file. Paths refer to different PCs.",
                inputSchema=new{type="object",properties=props,required=new[]{"local_path","remote_path"},additionalProperties=false},
                annotations=new{readOnlyHint=false,destructiveHint=upload,idempotentHint=false,openWorldHint=true}};
        }
        private Dictionary<string,object> Remote(object rpc)
        {byte[] bytes=Encoding.UTF8.GetBytes(McpJson.Write(rpc));using(var response=Authorized("mcp","POST",bytes.Length,"application/json",delegate(Stream s){s.Write(bytes,0,bytes.Length);}))return ReadJson(response);}
        private object Upload(Dictionary<string,object> a)
        {
            string local=McpPath.Absolute(McpTools.Required(a,"local_path")),remote=McpTools.Required(a,"remote_path"),expected=Json.Str(a,"expected_sha256")??"";
            using(var f=new FileStream(local,FileMode.Open,FileAccess.Read,FileShare.Read,256*1024))
            {
                if(f.Length>4L*1024*1024*1024)throw new IOException("업로드는 4GiB까지입니다.");
                string hash;using(var sha=SHA256.Create())hash=BitConverter.ToString(sha.ComputeHash(f)).Replace("-","").ToLowerInvariant();
                string query="upload?path="+Uri.EscapeDataString(remote)+"&sha256="+hash+"&expected_sha256="+Uri.EscapeDataString(expected);
                using(var response=Authorized(query,"POST",f.Length,"application/octet-stream",delegate(Stream s){f.Position=0;f.CopyTo(s,256*1024);}))
                {var info=ReadJson(response);if(Json.Str(info,"sha256")!=hash)throw new IOException("대상 파일 SHA-256 확인 실패");return McpJson.Text(info);}
            }
        }
        private object Download(Dictionary<string,object> a)
        {
            string local=McpPath.Absolute(McpTools.Required(a,"local_path")),remote=McpTools.Required(a,"remote_path");
            if(File.Exists(local))throw new IOException("로컬 파일이 이미 있습니다. 다른 저장 이름을 지정하세요.");
            var reply=Remote(new{jsonrpc="2.0",id="download-info",method="tools/call",@params=new{name="pc_get_file_info",arguments=new{path=remote}}});
            var result=McpJson.Obj(reply,"result");if(Json.Bool(result,"isError",false))return result;if(reply.ContainsKey("error"))throw new IOException(RemoteError(reply));
            Dictionary<string,object> info=null;foreach(var c in (IEnumerable)result["content"]) {var item=c as Dictionary<string,object>;if(item!=null&&Json.Str(item,"type")=="text"){info=McpJson.Read(Json.Str(item,"text"));break;}}
            if(info==null)throw new IOException("파일 정보가 없습니다.");
            string temp=local+".mcp-download-"+Guid.NewGuid().ToString("N")+".tmp";
            try {
                using(var response=Authorized("download?path="+Uri.EscapeDataString(remote),"GET",0,null,null))
                using(var output=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None,256*1024))response.GetResponseStream().CopyTo(output,256*1024);
                if(!string.Equals(McpTools.FileHash(temp),Json.Str(info,"sha256"),StringComparison.OrdinalIgnoreCase))throw new IOException("다운로드 중 파일이 변경되었거나 SHA-256이 다릅니다. 다시 확인하세요.");
                File.Move(temp,local);return McpJson.Text(McpTools.Info(local));
            }finally{if(File.Exists(temp))File.Delete(temp);}
        }
        private static string RemoteError(Dictionary<string,object> reply)
        {
            var result=McpJson.Obj(reply,"result"); object content;
            if(result.TryGetValue("content",out content) && content is IEnumerable)
                foreach(var value in (IEnumerable)content) { var item=value as Dictionary<string,object>; if(item!=null && Json.Str(item,"type")=="text") return Json.Str(item,"text"); }
            string message=Json.Str(McpJson.Obj(reply,"error"),"message");
            return string.IsNullOrEmpty(message)?"원격 파일 정보를 읽지 못했습니다. 서버 응답과 경로를 확인하세요.":message;
        }
        private HttpWebRequest Request(string path,string method)
        {
            var request=(HttpWebRequest)WebRequest.Create(new Uri(address,path));request.Method=method;request.Proxy=null;request.AllowAutoRedirect=false;
            request.CookieContainer=cookies;request.KeepAlive=false;request.Timeout=60000;request.ReadWriteTimeout=60000;
            request.AllowWriteStreamBuffering=false;request.ServicePoint.Expect100Continue=false;
            request.Accept="application/json, text/event-stream";return request;
        }
        private HttpWebResponse Authorized(string path,string method,long size,string type,Action<Stream> write)
        {
            for(int attempt=0;attempt<2;attempt++)
            {
                if(!loggedIn)Login();var request=Request(path,method);if(type!=null)request.ContentType=type;
                if(method=="POST")request.ContentLength=size;
                try {if(write!=null)using(var body=request.GetRequestStream())write(body);return (HttpWebResponse)request.GetResponse();}
                catch(WebException ex) {var response=ex.Response as HttpWebResponse;if(response!=null&&response.StatusCode==HttpStatusCode.Unauthorized&&attempt==0){response.Dispose();loggedIn=false;continue;}throw Explain(ex);}
            }
            throw new IOException("Remote 로그인 갱신에 실패했습니다.");
        }
        internal void Login()
        {
            cookies=new CookieContainer();var request=Request("login","POST");request.ContentType="application/json";
            byte[] bytes=Encoding.UTF8.GetBytes(McpJson.Write(new{user=user,password=password}));request.ContentLength=bytes.Length;
            try {using(var body=request.GetRequestStream())body.Write(bytes,0,bytes.Length);using(var response=(HttpWebResponse)request.GetResponse())ReadJson(response);loggedIn=true;}
            catch(WebException ex){throw Explain(ex);}
        }
        private static Dictionary<string,object> ReadJson(HttpWebResponse response)
        {using(var reader=new StreamReader(response.GetResponseStream(),Encoding.UTF8))return McpJson.Read(reader.ReadToEnd());}
        private static Exception Explain(WebException ex)
        {
            var response=ex.Response as HttpWebResponse;
            if(response==null)return new IOException("MCP 대상 PC에 연결하지 못했습니다. IP·MCP 포트·허용 IP·방화벽을 확인하세요. 전송 중 실패한 작업은 결과를 먼저 확인한 뒤 다시 요청하세요.",ex);
            using(response) {string detail="";try{detail=Json.Str(ReadJson(response),"error");}catch{}return new IOException("MCP HTTP "+(int)response.StatusCode+": "+detail);}
        }
        public void Dispose()
        {if(!loggedIn)return;try{var request=Request("logout","POST");request.Timeout=2000;request.ContentLength=0;using(var response=request.GetResponse()){} }catch{}loggedIn=false;}
    }
    internal static class McpProfile
    {
        private static string Location(string name)
        {
            if(!System.Text.RegularExpressions.Regex.IsMatch(name??"","^[a-zA-Z0-9_-]{1,64}$"))throw new ArgumentException("연결 이름은 영문·숫자·_- 1~64자입니다.");
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"RemoteControl","mcp-clients",name+".json");
        }
        internal static Dictionary<string,object> Load(string name)
        {
            string path=Location(name);if(!File.Exists(path))throw new IOException("LLM PC에서 RemoteControl.exe --mcp-config로 연결 설정을 먼저 저장하세요.");
            var data=McpJson.Read(File.ReadAllText(path,Encoding.UTF8));
            data["password"]=Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(Json.Str(data,"password")),null,DataProtectionScope.CurrentUser));return data;
        }
        internal static void Save(string name,string host,string port,string user,string password)
        {
            string path=Location(name);Directory.CreateDirectory(Path.GetDirectoryName(path));
            var data=new{host=host,port=port,user=user,password=Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(password),null,DataProtectionScope.CurrentUser))};
            File.WriteAllText(path,McpJson.Write(data),new UTF8Encoding(false));
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace RemoteControl
{
    internal sealed class McpBridgeException : IOException
    {
        internal readonly string Code;
        internal McpBridgeException(string code, string message) : base(message) { Code=code; }
    }

    // The existing authenticated bridge port carries a fixed protocol, never an arbitrary URL.
    internal sealed class McpBridge
    {
        private sealed class Login { internal string Token; internal DateTime Until; }
        private readonly Dictionary<string,Login> logins=new Dictionary<string,Login>();
        internal const string Instructions=" For bridge PCs, first call pc_list_bridge_pcs, then pc_bridge_list_tools with bridge_id and target_revision. Call pc_get_status through pc_bridge_call to verify the child hostname; supply expected_hostname on subsequent child calls. Always retain that bridge_id and target_revision for job and transfer IDs. Local pc_* tools address this PC only. Child browser transfer links and nested bridge relaying are not supported; use the chunk transfer tools through pc_bridge_call.";

        private static string Revision(Peer p)
        {
            return McpTools.Hash(Encoding.UTF8.GetBytes(McpJson.Write(new{p.Id,p.Name,p.Host,p.Port,p.User,
                password=Config.Get("bridge"+p.Id+"p"),machinePassword=Config.Get("bridge"+p.Id+"pm")})));
        }
        private static Peer Target(Dictionary<string,object> args)
        {
            Peer p=Bridge.Find(Json.Int(args,"bridge_id",0));
            if(p==null)throw new McpBridgeException("BRIDGE_NOT_FOUND","등록된 브릿지 PC가 없습니다.");
            if(!string.Equals(Revision(p),McpTools.Required(args,"target_revision"),StringComparison.Ordinal))
                throw new McpBridgeException("BRIDGE_TARGET_CHANGED","브릿지 설정이 변경되었습니다. PC 목록과 대상 상태를 다시 확인하세요.");
            if(Uri.CheckHostName(p.Host)==UriHostNameType.Unknown||p.Host.IndexOfAny(new[]{'\r','\n','/','\\','@'})>=0)
                throw new ArgumentException("브릿지 주소 형식이 잘못되었습니다.");
            return p;
        }
        internal static object List()
        {
            var peers=new List<object>();
            foreach(Peer p in Bridge.List())peers.Add(new{bridge_id=p.Id,name=p.Name,address=p.Addr,user=p.User,target_revision=Revision(p),
                password_saved=Bridge.HasPassword(p.Id)});
            return McpJson.Text(new{pcs=peers.ToArray(),instructions=Instructions});
        }
        internal static void CheckTool(string name,McpOptions options)
        {
            bool found=false;
            foreach(object tool in McpTools.List(false))if(Json.Str(McpJson.Read(McpJson.Write(tool)),"name")==name){found=true;break;}
            if(!found)throw new ArgumentException("중계할 수 없는 도구입니다. 하위 PC의 기본 MCP 도구만 지정하세요.");
            if(!options.AllowCommands&&(name=="pc_start_process"||name=="pc_launch_program"||name=="pc_stop_process"))
                throw new McpCapabilityDisabledException("MCP 명령 실행이 설정에서 꺼져 있습니다.");
            if(!options.AllowScreen&&name=="pc_capture_screen")throw new McpCapabilityDisabledException("MCP 화면 확인이 설정에서 꺼져 있습니다.");
        }
        internal object Call(string operation,Dictionary<string,object> args,McpOptions options,string user,string token)
        {
            Peer p=Target(args);string revision=Revision(p),name=Json.Str(args,"tool_name");
            bool listing=operation=="pc_bridge_list_tools";
            if(!listing) {
                CheckTool(name,options);
                if(name!="pc_get_status")McpTools.Required(args,"expected_hostname");
                object value;if(args.TryGetValue("arguments",out value)&&!(value is Dictionary<string,object>))throw new ArgumentException("arguments는 JSON 객체여야 합니다.");
            }
            if(string.IsNullOrEmpty(token))throw new UnauthorizedAccessException("인증된 MCP 연결이 필요합니다.");
            // Stable across child web-session renewal, isolated between parent MCP sessions and targets.
            string owner=McpTools.Hash(Encoding.UTF8.GetBytes(McpJson.Write(new{token,user,revision})));
            var request=new{protocol=1,operation=listing?"list":"call",owner=owner,tool=name,
                expected_hostname=Json.Str(args,"expected_hostname"),arguments=McpJson.Obj(args,"arguments")};
            Dictionary<string,object> response=Send(p,revision,request,args);
            var target=new{bridge_id=p.Id,name=p.Name,target_revision=revision,hostname=Json.Str(response,"hostname"),version=Json.Str(response,"version")};
            object result;if(!response.TryGetValue("result",out result)||!(result is Dictionary<string,object>))
                throw new McpBridgeException("BRIDGE_PROTOCOL_ERROR","하위 PC의 MCP 응답 형식이 잘못되었습니다.");
            var toolResult=(Dictionary<string,object>)result;
            toolResult["_meta"]=new{bridge_target=target};
            return toolResult;
        }
        private Dictionary<string,object> Send(Peer p,string revision,object body,Dictionary<string,object> args)
        {
            byte[] bytes=Encoding.UTF8.GetBytes(McpJson.Write(body));
            if(bytes.Length>2*1024*1024)throw new ArgumentException("브릿지 요청은 2MiB까지입니다.");
            Login auth;
            lock(logins) {
                if(!logins.TryGetValue(revision,out auth)||auth.Until<=DateTime.UtcNow) {
                    string why,code;string value=Bridge.Login(p,"",out why,out code);
                    if(string.IsNullOrEmpty(value))throw new McpBridgeException(code,why);
                    if(!Regex.IsMatch(value,@"\A[A-Za-z0-9_-]{20,256}\z"))throw new McpBridgeException("BRIDGE_PROTOCOL_ERROR","하위 PC의 세션 형식이 잘못되었습니다.");
                    auth=new Login{Token=value,Until=DateTime.UtcNow.AddMinutes(20)};
                    if(logins.Count>=64)logins.Clear();logins[revision]=auth;
                }
            }
            Target(args); // Settings may have changed while the child was authenticating.
            string error;TcpClient client=Bridge.Dial(p.Host,p.Port,5000,out error);
            if(client==null)throw new McpBridgeException("BRIDGE_OFFLINE","하위 PC에 접속하지 못했습니다: "+error);
            using(client) {
                try {
                    NetworkStream stream=client.GetStream();stream.ReadTimeout=30000;stream.WriteTimeout=30000;
                    string host=p.Host.IndexOf(':')>=0?"["+p.Host+"]":p.Host;
                    byte[] head=Encoding.ASCII.GetBytes("POST /bridge/mcp HTTP/1.1\r\nHost: "+host+":"+p.Port+
                        "\r\nCookie: rcsid="+auth.Token+"\r\nX-RC-Bridge-MCP: 1\r\nContent-Type: application/json\r\nContent-Length: "+bytes.Length+"\r\nConnection: close\r\n\r\n");
                    stream.Write(head,0,head.Length);stream.Write(bytes,0,bytes.Length);stream.Flush();
                    byte[] prefix;string[] lines=Server.ReadHead(stream,out prefix).Split(new[]{"\r\n"},StringSplitOptions.None);
                    string[] status=lines[0].Split(' ');
                    if(status.Length<2)throw new IOException("응답 헤더가 없습니다.");
                    if(status[1]=="401") {
                        lock(logins)logins.Remove(revision);
                        throw new McpBridgeException("BRIDGE_AUTH_EXPIRED","하위 PC 인증이 만료되었습니다. 상태 확인부터 다시 진행하세요. 요청은 재전송하지 않았습니다.");
                    }
                    if(status[1]!="200")throw new McpBridgeException("BRIDGE_UNAVAILABLE","하위 PC가 MCP 중계를 거부했습니다 (HTTP "+status[1]+"). 버전과 MCP 사용 설정을 확인하세요.");
                    if(!(Server.Header(lines,"Content-Type")??"").StartsWith("application/json",StringComparison.OrdinalIgnoreCase))
                        throw new McpBridgeException("BRIDGE_UNSUPPORTED","하위 PC가 MCP 중계를 지원하지 않습니다. 하위 PC도 업데이트하세요.");
                    long length;
                    if(!long.TryParse(Server.Header(lines,"Content-Length"),out length)||length<0||length>16*1024*1024||!string.IsNullOrEmpty(Server.Header(lines,"Transfer-Encoding")))
                        throw new IOException("잘못된 응답 길이입니다.");
                    var response=McpJson.Read(Encoding.UTF8.GetString(Read(stream,prefix,(int)length)));
                    if(response==null||Json.Int(response,"protocol",0)!=1)throw new IOException("잘못된 중계 응답입니다.");
                    return response;
                } catch(McpBridgeException){throw;}
                catch(Exception) {
                    throw new McpBridgeException("BRIDGE_RESULT_UNKNOWN","하위 PC 응답이 끊겼거나 올바르지 않습니다. 실행 여부를 확인할 수 없어 자동 재전송하지 않았습니다. 상태/파일/작업을 조회한 뒤 계속하세요.");
                }
            }
        }
        internal static byte[] Read(NetworkStream stream,byte[] prefix,int length)
        {
            byte[] bytes=new byte[length];int at=Math.Min(length,prefix.Length);Buffer.BlockCopy(prefix,0,bytes,0,at);
            while(at<length){int n=stream.Read(bytes,at,length-at);if(n<=0)throw new EndOfStreamException();at+=n;}return bytes;
        }
        internal static void Receive(NetworkStream stream,string method,string[] headers,byte[] prefix,Session session,McpHost host)
        {
            string status="200 OK";object result;
            if(session==null){Reply(stream,"401 Unauthorized",new{error="Remote login required"});return;}
            if(method!="POST"){Reply(stream,"405 Method Not Allowed",new{error="POST required"});return;}
            if(Server.Header(headers,"X-RC-Bridge-MCP")!="1"||!string.IsNullOrEmpty(Server.Header(headers,"Origin"))||
                !string.IsNullOrEmpty(Server.Header(headers,"Sec-Fetch-Site"))||!string.IsNullOrEmpty(Server.Header(headers,"Transfer-Encoding"))||
                !(Server.Header(headers,"Content-Type")??"").StartsWith("application/json",StringComparison.OrdinalIgnoreCase))
            {Reply(stream,"403 Forbidden",new{error="Authenticated bridge request required"});return;}
            int length;
            if(!int.TryParse(Server.Header(headers,"Content-Length"),out length)||length<1||length>2*1024*1024)
            {Reply(stream,"413 Payload Too Large",new{error="Body limit: 2MiB"});return;}
            if(host==null){Reply(stream,"503 Service Unavailable",new{error="MCP unavailable"});return;}
            try {
                var request=McpJson.Read(Encoding.UTF8.GetString(Read(stream,prefix,length)));
                if(request==null||Json.Int(request,"protocol",0)!=1)throw new ArgumentException("지원하지 않는 브릿지 프로토콜입니다.");
                result=host.BridgeCall(request,session);
            } catch(Exception ex){result=McpJson.ToolError(ex);}
            Reply(stream,status,new{protocol=1,hostname=Environment.MachineName,version=MainForm.Version,result=result});
        }
        private static void Reply(NetworkStream stream,string status,object value)
        {Server.Send(stream,status,"application/json; charset=utf-8",Encoding.UTF8.GetBytes(McpJson.Write(value)),"Cache-Control: no-store\r\n");}
    }
}


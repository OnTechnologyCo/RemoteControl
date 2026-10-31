using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Windows.Forms;

namespace RemoteControl
{
    internal sealed class McpTools : IDisposable
    {
        private readonly McpProcesses processes = new McpProcesses();
        internal readonly McpTransfers Transfers;
        internal McpTools(string transferDirectory = null) { Transfers = new McpTransfers(transferDirectory); }
        internal static readonly object FileGate = new object();
        private static object Str(string description) { return new { type = "string", description = description }; }
        private static object Num(string description) { return new { type = "integer", description = description, minimum = 0 }; }
        private static object Flag(string description) { return new { type = "boolean", description = description }; }
        private static object Tool(string name, string description, bool read, string[] required, params object[] pairs)
        {
            var properties = new Dictionary<string, object>(); for (int i = 0; i < pairs.Length; i += 2) properties[(string)pairs[i]] = pairs[i + 1];
            return new { name = "pc_" + name, description = description, inputSchema = new { type = "object", properties = properties, required = required, additionalProperties = false },
                annotations = new { readOnlyHint = read, destructiveHint = !read, idempotentHint = read, openWorldHint = !read } };
        }
        internal static object[] List()
        {
            var none = new string[0];
            return new[] {
                Tool("get_status", "Verify the target PC hostname, execution identity and enabled MCP settings before changes. workspace_directories are default locations, not access boundaries; paths outside them are supported under the Windows account permissions.", true, none),
                Tool("list_directory", "List a bounded page at any absolute local directory on the remote PC. Configured workspaces do not restrict access.", true, new[]{"path"}, "path",Str("Absolute remote Windows folder"),"offset",Num("Zero-based entry offset"),"limit",Num("1..500, default 100")),
                Tool("get_file_info", "Read file metadata and SHA-256 for deployment verification.", true, new[]{"path"}, "path",Str("Absolute remote file")),
                Tool("read_file", "Read bounded UTF-8 text lines and the SHA-256 required for edits.", true, new[]{"path"}, "path",Str("Absolute remote file"),"offset",Num("Zero-based line offset"),"limit",Num("1..2000, default 200")),
                Tool("write_file", "Create or replace a UTF-8 text file. Replacing requires its current SHA-256; a backup is retained.", false,new[]{"path","content"},"path",Str("Absolute remote file"),"content",Str("UTF-8 text, maximum 1MiB"),"expected_sha256",Str("Current hash, required when the file exists")),
                Tool("edit_file", "Replace exactly one matching text block after verifying expected_sha256.",false,new[]{"path","old_text","new_text","expected_sha256"},"path",Str("Absolute remote file"),"old_text",Str("Unique text to replace"),"new_text",Str("Replacement"),"expected_sha256",Str("Current SHA-256")),
                Tool("create_directory", "Create a directory at any absolute local path on the remote PC, including outside the configured workspaces.",false,new[]{"path"},"path",Str("Absolute remote folder")),
                Tool("search_files", "Search filenames recursively; skips junctions, node_modules and .git. Bounded to 10000 entries.",true,new[]{"path","query"},"path",Str("Remote root folder"),"query",Str("Filename substring"),"limit",Num("1..200, default 50")),
                Tool("start_process", "Run PowerShell on the REMOTE PC, returning a tracked session and output. Commands use the agent's Windows rights. Workspaces do not restrict commands or cwd; omitted cwd uses the first workspace. For a GUI use pc_launch_program. Maximum runtime is one hour.",false,new[]{"command"},"command",Str("PowerShell script, at most 16000 characters"),"cwd",Str("Absolute local working directory on the remote PC; may be outside workspaces. Defaults to the first workspace."),"timeout_ms",Num("Initial output wait, 0..10000, default 1000")),
                Tool("launch_program", "Launch an executable at an absolute local path on the remote desktop and keep its process session tracked. Set visible=true to show its window. Maximum runtime is one hour; stopping the session closes its descendants.",false,new[]{"path"},"path",Str("Absolute local executable path on the remote PC; may be outside workspaces"),"arguments",Str("Windows command-line arguments"),"visible",Flag("Show the application window, default false")),
                Tool("read_process_output", "Read output from an MCP-owned session. Use next_offset to continue without repeating logs.",true,new[]{"session_id"},"session_id",Str("ID returned by start/launch"),"offset",Num("Absolute character offset"),"limit",Num("1..65536, default 12000")),
                Tool("list_sessions", "List jobs started by this Remote login; does not confuse them with MCP connections.",true,none),
                Tool("stop_process", "Stop only the selected MCP-owned process job and descendants. Does not kill programs by name.",false,new[]{"session_id"},"session_id",Str("Owned job ID")),
                Tool("list_processes", "Inspect current PC processes, PID, memory and available window titles. Read-only.",true,none),
                Tool("read_event_log", "Read recent Windows Application or System events for diagnosis.",true,none,"log",Str("Application or System"),"limit",Num("1..100, default 20")),
                Tool("capture_screen", "Capture the selected remote monitor to verify the running program. Returns a JPEG and original/display dimensions. Does not change resolution or inject input.",true,none,"monitor",Num("Zero-based monitor index"),"max_width",Num("320..3840, default 1920")),
                Tool("begin_upload", "Begin resumable binary file or folder upload. Supply each file size and complete SHA-256. Files remain temporary until finish. Folder target must be new.",false,new[]{"path","kind","files"},"path",Str("Remote target file or new folder"),"kind",Str("file or folder"),"files",new{type="array",items=new{type="object",properties=new{path=Str("Relative path; empty for a single file"),size=Num("File bytes, at most 4GiB"),sha256=Str("Complete file SHA-256"),directory=Flag("Empty directory entry")},required=new[]{"path","size"},additionalProperties=false}},"expected_sha256",Str("Current target hash required to replace a file"),"request_id",Str("Optional 32 lowercase hex ID; retain to recover a lost begin response")),
                Tool("write_upload_chunk", "Write up to 256KiB at the confirmed next offset. Verify SHA-256. After an ambiguous failure query transfer status before continuing.",false,new[]{"transfer_id","offset","data_base64","sha256"},"transfer_id",Str("Upload ID"),"relative_path",Str("Manifest file path; empty for single file"),"offset",Num("Confirmed byte offset"),"data_base64",Str("Base64 encoded chunk"),"sha256",Str("Chunk SHA-256")),
                Tool("finish_upload", "Verify every complete SHA-256 and commit the upload. Existing file replacement keeps a backup; folders are committed together.",false,new[]{"transfer_id"},"transfer_id",Str("Upload ID")),
                Tool("get_transfer_status", "Read upload offsets or folder download preparation status. Authentication owns each transfer.",true,new[]{"transfer_id"},"transfer_id",Str("Transfer ID")),
                Tool("cancel_transfer", "Cancel this connection's transfer and remove only its temporary data.",false,new[]{"transfer_id"},"transfer_id",Str("Transfer ID")),
                Tool("prepare_download", "Prepare a binary file or folder ZIP for streamed or chunked download. Folder preparation is asynchronous; poll transfer status.",false,new[]{"path","kind"},"path",Str("Remote file or folder"),"kind",Str("file or folder")),
                Tool("read_download_chunk", "Read up to 256KiB of a prepared file or folder ZIP. Verify chunk and complete SHA-256 when saving the bytes.",true,new[]{"transfer_id"},"transfer_id",Str("Prepared download ID"),"offset",Num("Byte offset"),"limit",Num("1..262144, default 262144"))
            };
        }
        internal object Call(string name, Dictionary<string, object> a, McpOptions options, string user, string token = null)
        {
            if(name=="pc_begin_upload"||name=="pc_write_upload_chunk"||name=="pc_finish_upload"||name=="pc_get_transfer_status"||name=="pc_cancel_transfer"||name=="pc_prepare_download"||name=="pc_read_download_chunk")return McpJson.Text(Transfers.Call(name,a,options,token));
            string path;
            switch (name)
            {
                case "pc_get_status": return McpJson.Text(new { hostname = Environment.MachineName, version = MainForm.Version,
                    transport = "streamable-http", pid = Process.GetCurrentProcess().Id, identity = WindowsIdentity.GetCurrent().Name,
                    workspace_directories = options.Workspaces, default_working_directory = options.Workspaces[0],
                    workspaces_restrict_access = false, filesystem_access = "all_local_paths_with_windows_account_permissions",
                    path_requirements = "absolute_local_windows_paths; file_tools_do_not_follow_junctions_or_symlinks",
                    access_note = "Workspace directories are default locations, not access restrictions. Do not report permission denial without an actual access-denied response. Missing files, invalid paths and disabled capabilities are different errors.", allow_commands = options.AllowCommands, allow_screen = options.AllowScreen, mcp_port = options.Port });
                case "pc_list_directory":
                    path = McpPath.Check(Required(a,"path"));
                    var entries = new DirectoryInfo(path).GetFileSystemInfos(); Array.Sort(entries, delegate(FileSystemInfo x, FileSystemInfo y) { return StringComparer.OrdinalIgnoreCase.Compare(x.Name,y.Name); });
                    int offset = Bound(a,"offset",0,0,int.MaxValue), limit = Bound(a,"limit",100,1,500); var list = new List<object>();
                    for (int i = offset; i < entries.Length && list.Count < limit; i++) { var f = entries[i]; list.Add(new { name=f.Name, directory=(f.Attributes&FileAttributes.Directory)!=0, link=(f.Attributes&FileAttributes.ReparsePoint)!=0, modified=f.LastWriteTimeUtc.ToString("o") }); }
                    return McpJson.Text(new { path=path, entries=list.ToArray(), next_offset=(long)offset+list.Count, total=entries.Length });
                case "pc_get_file_info": return McpJson.Text(Info(McpPath.Check(Required(a,"path"))));
                case "pc_read_file":
                    path=McpPath.Check(Required(a,"path")); byte[] data=ReadTextBytes(path); string[] lines=Decode(data).Split('\n');
                    int from=Bound(a,"offset",0,0,int.MaxValue), count=Bound(a,"limit",200,1,2000), end=Math.Min(lines.Length,from>int.MaxValue-count?int.MaxValue:from+count);
                    var output=new StringBuilder(); int at=from; for(;at<end;at++) { if(output.Length+lines[at].Length>65536) break; output.AppendLine(lines[at].TrimEnd('\r')); }
                    return McpJson.Text(new { path=path, content=output.ToString(),sha256=Hash(data),next_offset=at,total_lines=lines.Length,truncated=at<lines.Length });
                case "pc_write_file":
                    path=McpPath.Check(Required(a,"path")); return McpJson.Text(Write(path,Encoding.UTF8.GetBytes(TextArgument(a,"content")),Json.Str(a,"expected_sha256"),options));
                case "pc_edit_file":
                    path=McpPath.Check(Required(a,"path"));
                    lock(FileGate) {
                        byte[] old=ReadTextBytes(path); CheckHash(path,Json.Str(a,"expected_sha256")); string text=Decode(old), find=Required(a,"old_text"); int pos=text.IndexOf(find,StringComparison.Ordinal);
                        if(pos<0 || text.IndexOf(find,pos+find.Length,StringComparison.Ordinal)>=0) throw new ArgumentException("old_text는 정확히 한 곳에서만 일치해야 합니다.");
                        return McpJson.Text(Write(path,Encoding.UTF8.GetBytes(text.Substring(0,pos)+TextArgument(a,"new_text")+text.Substring(pos+find.Length)),Json.Str(a,"expected_sha256"),options));
                    }
                case "pc_create_directory": path=McpPath.Check(Required(a,"path")); Directory.CreateDirectory(path); McpPath.Check(path); return McpJson.Text(new{path=path,created=true});
                case "pc_search_files": return McpJson.Text(Search(McpPath.Check(Required(a,"path")),Required(a,"query"),Bound(a,"limit",50,1,200),options));
                case "pc_start_process":
                    Commands(options); path=McpPath.Check(McpJson.Str(a,"cwd",options.Workspaces[0]));
                    return McpJson.Text(processes.Start(user,Required(a,"command"),path,Bound(a,"timeout_ms",1000,0,10000)));
                case "pc_launch_program":
                    Commands(options); path=McpPath.Check(Required(a,"path")); if(!File.Exists(path)) throw new FileNotFoundException("실행 파일이 없습니다.");
                    string args=Json.Str(a,"arguments")??""; if(args.Length>16000) throw new ArgumentException("인자가 너무 깁니다.");
                    string cmd="$p=Start-Process -FilePath "+Literal(path)+(args.Length>0?" -ArgumentList "+Literal(args):"")+" -WorkingDirectory "+Literal(Path.GetDirectoryName(path))+" -WindowStyle "+(Json.Bool(a,"visible",false)?"Normal":"Hidden")+" -PassThru; $p.WaitForExit(); exit $p.ExitCode";
                    return McpJson.Text(processes.Start(user,cmd,Path.GetDirectoryName(path),1000));
                case "pc_read_process_output": return McpJson.Text(processes.Read(user,Required(a,"session_id"),Bound(a,"offset",0,0,int.MaxValue),Bound(a,"limit",12000,1,65536)));
                case "pc_list_sessions": return McpJson.Text(processes.List(user));
                case "pc_stop_process": Commands(options); return McpJson.Text(processes.Stop(user,Required(a,"session_id")));
                case "pc_list_processes":
                    var ps=new List<object>(); foreach(var p in Process.GetProcesses()) using(p) { try { ps.Add(new{pid=p.Id,name=p.ProcessName,working_set=p.WorkingSet64,window=p.MainWindowTitle}); }catch{} if(ps.Count>=1000)break; }
                    return McpJson.Text(new{processes=ps.ToArray()});
                case "pc_read_event_log":
                    string log=McpJson.Str(a,"log","Application"); if(log!="Application"&&log!="System")throw new ArgumentException("Application 또는 System 로그만 읽습니다.");
                    var events=new List<object>(); int take=Bound(a,"limit",20,1,100);
                    using(var ev=new EventLog(log)) for(int i=ev.Entries.Count-1;i>=0&&events.Count<take;i--) { var e=ev.Entries[i]; string msg=e.Message; events.Add(new{time=e.TimeGenerated.ToString("o"),source=e.Source,type=e.EntryType.ToString(),id=e.InstanceId,message=msg.Length>2000?msg.Substring(0,2000):msg}); }
                    return McpJson.Text(new{log=log,events=events.ToArray()});
                case "pc_capture_screen":
                    if(!options.AllowScreen)throw new McpCapabilityDisabledException("MCP 화면 확인이 설정에서 꺼져 있습니다.");
                    return Screenshot(Bound(a,"monitor",0,0,31),Bound(a,"max_width",1920,320,3840));
                default: throw new ArgumentException("알 수 없는 MCP 도구입니다: "+name);
            }
        }
        internal static string Required(Dictionary<string,object> a,string name) { string value=Json.Str(a,name); if(string.IsNullOrWhiteSpace(value))throw new ArgumentException(name+" 값이 필요합니다."); return value; }
        private static string TextArgument(Dictionary<string,object> a,string name) { object value; if(!a.TryGetValue(name,out value)||!(value is string))throw new ArgumentException(name+" 문자열이 필요합니다."); return (string)value; }
        private static int Bound(Dictionary<string,object> a,string name,int fallback,int min,int max) { int v=Json.Int(a,name,fallback); if(v<min||v>max)throw new ArgumentException(name+" 값 범위 오류"); return v; }
        private static void Commands(McpOptions o) { if(!o.AllowCommands)throw new McpCapabilityDisabledException("MCP 명령 실행이 설정에서 꺼져 있습니다."); }
        private static string Literal(string text) { return "'"+text.Replace("'","''")+"'"; }
        internal static string Hash(byte[] bytes) { using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-","").ToLowerInvariant(); }
        internal static string FileHash(string path) { using(var f=File.OpenRead(path))using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(f)).Replace("-","").ToLowerInvariant(); }
        internal static object Info(string path) { var f=new FileInfo(path); return new{path=path,size=f.Length,modified=f.LastWriteTimeUtc.ToString("o"),sha256=FileHash(path)}; }
        private static byte[] ReadTextBytes(string path) { if(new FileInfo(path).Length>2*1024*1024)throw new IOException("텍스트 읽기는 2MiB까지입니다. 큰 파일은 다운로드하세요.");return File.ReadAllBytes(path); }
        private static string Decode(byte[] b) { int skip=b.Length>=3&&b[0]==239&&b[1]==187&&b[2]==191?3:0;return new UTF8Encoding(false,true).GetString(b,skip,b.Length-skip); }
        internal static void CheckHash(string path,string expected)
        {
            if(File.Exists(path)) { if(string.IsNullOrEmpty(expected)||!string.Equals(FileHash(path),expected,StringComparison.OrdinalIgnoreCase))throw new IOException("파일이 이미 있거나 변경되었습니다. 현재 SHA-256을 확인한 뒤 다시 요청하세요."); }
            else if(!string.IsNullOrEmpty(expected))throw new IOException("교체할 원본 파일이 없습니다.");
        }
        private static object Write(string path,byte[] bytes,string expected,McpOptions options)
        {
            if(bytes.Length>1024*1024)throw new IOException("텍스트 쓰기는 1MiB까지입니다. 바이너리/큰 파일은 업로드하세요.");
            lock(FileGate) {
                CheckHash(path,expected); string temp=path+".mcp-"+Guid.NewGuid().ToString("N")+".tmp";
                try { File.WriteAllBytes(temp,bytes); Commit(temp,path,expected,options); return Info(path); }
                finally { if(File.Exists(temp))File.Delete(temp); }
            }
        }
        internal static void Commit(string temp,string path,string expected,McpOptions options)
        {
            lock(FileGate) { McpPath.Check(path); CheckHash(path,expected); if(File.Exists(path))File.Replace(temp,path,path+".mcp-backup-"+DateTime.UtcNow.ToString("yyyyMMddHHmmssfff")+"-"+Guid.NewGuid().ToString("N").Substring(0,6));else File.Move(temp,path); }
        }
        private static object Search(string root,string query,int limit,McpOptions options)
        {
            var queue=new Queue<string>(); queue.Enqueue(root); var found=new List<string>(); int scanned=0;
            while(queue.Count>0&&scanned<10000&&found.Count<limit) {
                string dir=queue.Dequeue(); FileSystemInfo[] all; try { McpPath.Check(dir); all=new DirectoryInfo(dir).GetFileSystemInfos(); }catch{continue;}
                foreach(var f in all) { if(++scanned>10000||found.Count>=limit)break; if(f.Name.IndexOf(query,StringComparison.OrdinalIgnoreCase)>=0)found.Add(f.FullName);
                    if((f.Attributes&FileAttributes.Directory)!=0&&(f.Attributes&FileAttributes.ReparsePoint)==0&&f.Name!="node_modules"&&f.Name!=".git")queue.Enqueue(f.FullName); }
            }
            return new{paths=found.ToArray(),scanned=scanned,truncated=scanned>=10000||found.Count>=limit};
        }
        private static object Screenshot(int monitor,int maxWidth)
        {
            Screen[] screens=Screen.AllScreens; if(monitor>=screens.Length)throw new ArgumentException("모니터 번호가 없습니다.");
            Rectangle bounds=screens[monitor].Bounds;
            using(var source=new ScreenSource(bounds)) {
                source.ScalePercent=Math.Max(30,Math.Min(100,maxWidth*100/bounds.Width));
                byte[] jpeg=source.Snapshot();
                return new{content=new object[]{new{type="text",text=McpJson.Write(new{monitor=monitor,original_width=bounds.Width,original_height=bounds.Height,x=bounds.X,y=bounds.Y,width=source.FrameWidth,height=source.FrameHeight})},new{type="image",mimeType="image/jpeg",data=Convert.ToBase64String(jpeg)}}};
            }
        }
        public void Dispose() { processes.Dispose(); }
    }
}

// 웹/로컬 MCP가 같은 전송 경로를 쓴다. 완성되지 않은 데이터는 대상 이름으로 노출하지 않는다.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace RemoteControl
{
    internal sealed class McpTransfers
    {
        internal const int ChunkSize = 256 * 1024;
        internal sealed class Item
        { public string Path {get;set;} public long Size {get;set;} public string Sha256 {get;set;} public bool Directory {get;set;} }
        internal sealed class State
        {
            public string Id {get;set;} public string Owner {get;set;} public string Kind {get;set;} public string Path {get;set;}
            public string Stage {get;set;} public string Expected {get;set;} public string Status {get;set;} public string Error {get;set;}
            public long Expires {get;set;} public long Size {get;set;} public long Modified {get;set;} public string Sha256 {get;set;}
            public List<Item> Files {get;set;}
        }
        private readonly string directory;
        private readonly object gate = new object();
        private readonly Dictionary<string,State> active = new Dictionary<string,State>();
        internal McpTransfers(string folder)
        { directory=folder ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"RemoteControl","mcp-transfers"); }
        private static string Hash(byte[] bytes) { using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-","").ToLowerInvariant(); }
        private static string Owner(string token) { if(string.IsNullOrEmpty(token))throw new UnauthorizedAccessException("인증된 MCP 연결이 필요합니다.");return Hash(Encoding.UTF8.GetBytes(token)); }
        private string Metadata(string id) { if(!Regex.IsMatch(id??"","^[a-f0-9]{32}$"))throw new ArgumentException("전송 ID가 올바르지 않습니다.");return System.IO.Path.Combine(directory,id+".json"); }
        private static JavaScriptSerializer JsonSerializer() {return new JavaScriptSerializer{MaxJsonLength=2*1024*1024,RecursionLimit=32};}
        private void Save(State row)
        {
            Directory.CreateDirectory(directory);string path=Metadata(row.Id),temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
            try {File.WriteAllText(temp,JsonSerializer().Serialize(row),new UTF8Encoding(false));if(File.Exists(path))File.Replace(temp,path,null);else File.Move(temp,path);}
            finally {if(File.Exists(temp))File.Delete(temp);}
        }
        private State Load(string id,string token,McpOptions options)
        {
            State row;
            if(!active.TryGetValue(id,out row))
            {
                string file=Metadata(id);if(!File.Exists(file))throw new IOException("전송을 찾을 수 없습니다.");
                if(new FileInfo(file).Length>2*1024*1024)throw new IOException("전송 정보가 너무 큽니다.");
                row=JsonSerializer().Deserialize<State>(File.ReadAllText(file));
                if(row.Status=="preparing") {row.Status="failed";row.Error="폴더 준비 중 서버가 재시작됐습니다. 다운로드를 다시 요청하세요.";Save(row);}
                active[id]=row;
            }
            if(row.Id!=id||row.Owner!=Owner(token))throw new UnauthorizedAccessException("다른 연결의 전송입니다.");
            if(row.Expires<DateTime.UtcNow.Ticks)throw new IOException("전송이 만료됐습니다. 다시 요청하세요.");
            McpPath.Check(row.Path,options);McpPath.Check(row.Stage,options);
            string suffix=row.Kind=="folder-upload"?".dir":row.Kind=="folder-download"?".zip":".tmp";
            if(row.Stage!=row.Path+".mcp-transfer-"+row.Id+suffix)throw new IOException("전송 임시 경로가 올바르지 않습니다.");
            return row;
        }
        private static long Number(Dictionary<string,object> a,string key,long fallback)
        {object value;if(!a.TryGetValue(key,out value))return fallback;double n=Convert.ToDouble(value);if(n<0||n>8L*1024*1024*1024||n!=Math.Truncate(n))throw new ArgumentException(key+" 값이 올바르지 않습니다.");return (long)n;}
        private static string Relative(string value)
        {
            if(string.IsNullOrEmpty(value))return "";value=value.Replace('\\','/');
            foreach(string part in value.Split('/'))if(part.Length==0||part=="."||part==".."||part.IndexOf(':')>=0)throw new ArgumentException("폴더 안 상대 경로가 올바르지 않습니다.");
            return value;
        }
        private static string ItemPath(State row,Item item,McpOptions options)
        {return McpPath.Check(row.Kind=="file-upload"?row.Stage:System.IO.Path.Combine(row.Stage,item.Path.Replace('/','\\')),options);}
        private static Item FindItem(State row,string relative)
        {relative=Relative(relative);foreach(var item in row.Files)if(item.Path==relative&&!item.Directory)return item;throw new IOException("목록에 없는 파일입니다.");}
        private static object View(State row,McpOptions options)
        {
            var files=new List<object>();
            if(row.Files!=null)foreach(var item in row.Files){string p=ItemPath(row,item,options);long received=row.Status=="completed"?item.Size:File.Exists(p)?new FileInfo(p).Length:0;files.Add(new{path=item.Path,size=item.Size,received=received,sha256=item.Sha256,directory=item.Directory});}
            return new{transfer_id=row.Id,path=row.Path,kind=row.Kind,status=row.Status,error=row.Error,size=row.Size,sha256=row.Sha256,files=files,chunk_size=ChunkSize};
        }
        internal object Call(string name,Dictionary<string,object> a,McpOptions options,string token)
        {
            lock(gate)
            {
                if(name=="pc_begin_upload")return Begin(a,options,token);
                if(name=="pc_prepare_download")return Prepare(a,options,token);
                State row=Load(McpTools.Required(a,"transfer_id"),token,options);
                if(name=="pc_get_transfer_status")return View(row,options);
                if(name=="pc_cancel_transfer") {row.Status="canceled";Save(row);Cleanup(row,options);return View(row,options);}
                if(name=="pc_write_upload_chunk")return Write(row,a,options);
                if(name=="pc_finish_upload")return Finish(row,options);
                if(name=="pc_read_download_chunk")return Read(row,a,options);
                throw new ArgumentException("알 수 없는 전송 기능입니다.");
            }
        }
        private object Begin(Dictionary<string,object> a,McpOptions options,string token)
        {
            string path=McpPath.Check(McpTools.Required(a,"path"),options),kind=McpTools.Required(a,"kind");
            if(kind!="file"&&kind!="folder")throw new ArgumentException("kind는 file 또는 folder입니다.");
            string id=Json.Str(a,"request_id");if(string.IsNullOrEmpty(id))id=Guid.NewGuid().ToString("N");Metadata(id);
            var items=new List<Item>();object values;if(!a.TryGetValue("files",out values)||!(values is IEnumerable))throw new ArgumentException("파일 목록과 SHA-256이 필요합니다.");
            var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);long total=0;
            foreach(object value in (IEnumerable)values)
            {
                var item=value as Dictionary<string,object>;if(item==null)throw new ArgumentException("파일 목록 형식 오류");
                string relative=Relative(Json.Str(item,"path")??"");bool dir=Json.Bool(item,"directory",false);long size=Number(item,"size",-1);string sha=(Json.Str(item,"sha256")??"").ToLowerInvariant();
                if(items.Count>=10000||!names.Add(relative)||size<0||size>4L*1024*1024*1024||(!dir&&!Regex.IsMatch(sha,"^[a-f0-9]{64}$"))||(dir&&size!=0))throw new ArgumentException("파일 수·크기·중복 이름·SHA-256을 확인하세요.");
                if(kind=="file"&&(relative.Length!=0||dir))throw new ArgumentException("파일 업로드의 상대 경로는 빈 문자열입니다.");
                if(kind=="folder"&&relative.Length==0)throw new ArgumentException("폴더 안 상대 경로가 필요합니다.");
                total+=size;if(total>8L*1024*1024*1024)throw new IOException("폴더 전송은 8GiB까지입니다.");
                items.Add(new Item{Path=relative,Size=size,Sha256=sha,Directory=dir});
            }
            if(kind=="file"&&items.Count!=1)throw new ArgumentException("전송할 파일 목록이 필요합니다.");
            string expected=Json.Str(a,"expected_sha256")??"";
            if(File.Exists(Metadata(id))) {State existing=Load(id,token,options);if(existing.Path!=path||existing.Kind!=kind+"-upload"||existing.Expected!=expected||JsonSerializer().Serialize(existing.Files)!=JsonSerializer().Serialize(items))throw new IOException("이 전송 ID의 파일 목록이 다릅니다.");return View(existing,options);}
            Capacity();if(!Directory.Exists(System.IO.Path.GetDirectoryName(path)))throw new IOException("대상 부모 폴더가 없습니다. 먼저 작업 폴더를 만드세요.");
            if(kind=="file") {if(Directory.Exists(path))throw new IOException("대상이 폴더입니다.");McpTools.CheckHash(path,expected);}
            else if(File.Exists(path)||Directory.Exists(path))throw new IOException("폴더 업로드는 새 폴더 경로를 지정하세요. 기존 폴더를 덮어쓰지 않습니다.");
            var row=new State{Id=id,Owner=Owner(token),Kind=kind+"-upload",Path=path,Expected=expected,Status="uploading",Expires=DateTime.UtcNow.AddHours(24).Ticks,Files=items,Size=total};
            row.Stage=path+".mcp-transfer-"+id+(kind=="folder"?".dir":".tmp");McpPath.Check(row.Stage,options);
            try {
                if(kind=="folder")Directory.CreateDirectory(row.Stage);
                foreach(var item in items){string p=ItemPath(row,item,options);if(item.Directory)Directory.CreateDirectory(p);else{Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p));McpPath.Check(p,options);using(var f=new FileStream(p,FileMode.CreateNew,FileAccess.Write)) {}}}
                Save(row);active[id]=row;return View(row,options);
            }catch{Cleanup(row,options);throw;}
        }
        private void Capacity()
        {
            Directory.CreateDirectory(directory);int count=0;
            foreach(string file in Directory.GetFiles(directory,"*.json")) {State row=JsonSerializer().Deserialize<State>(File.ReadAllText(file));if(row.Expires>DateTime.UtcNow.Ticks&&(row.Status=="uploading"||row.Status=="preparing"||row.Status=="ready"))count++;}
            if(count>=32)throw new IOException("진행 중인 전송이 많습니다. 완료하거나 취소하세요.");
        }
        private object Write(State row,Dictionary<string,object> a,McpOptions options)
        {
            if(row.Status!="uploading")throw new IOException("업로드 중인 전송이 아닙니다.");
            Item item=FindItem(row,Json.Str(a,"relative_path")??"");long offset=Number(a,"offset",-1);
            string encoded=McpTools.Required(a,"data_base64");if(encoded.Length>4*((ChunkSize+2)/3))throw new IOException("분할 데이터가 256KiB를 넘습니다.");
            byte[] data=Convert.FromBase64String(encoded);string sha=McpTools.Required(a,"sha256");
            if(data.Length==0||data.Length>ChunkSize||offset<0||offset+data.Length>item.Size||!string.Equals(Hash(data),sha,StringComparison.OrdinalIgnoreCase))throw new IOException("데이터 크기·위치·SHA-256이 올바르지 않습니다.");
            string path=ItemPath(row,item,options);
            using(var file=new FileStream(path,FileMode.Open,FileAccess.ReadWrite,FileShare.None))
            {
                if(offset<file.Length) {if(offset+data.Length>file.Length)throw new IOException("분할 데이터 위치가 겹칩니다.");file.Position=offset;byte[] prior=new byte[data.Length];int got=0;while(got<prior.Length){int n=file.Read(prior,got,prior.Length-got);if(n==0)throw new EndOfStreamException();got+=n;}if(Hash(prior)!=Hash(data))throw new IOException("이미 받은 데이터와 다릅니다.");}
                else {if(offset!=file.Length)throw new IOException("다음 위치는 "+file.Length+"입니다. 전송 상태를 확인하세요.");file.Position=offset;file.Write(data,0,data.Length);file.Flush(true);}
                return new{transfer_id=row.Id,relative_path=item.Path,next_offset=file.Length,sha256=Hash(data)};
            }
        }
        private object Finish(State row,McpOptions options)
        {
            if(row.Status=="completed")return View(row,options);
            if(row.Status!="uploading")throw new IOException("업로드 중인 전송이 아닙니다.");
            foreach(var item in row.Files)if(!item.Directory){string p=ItemPath(row,item,options);if(new FileInfo(p).Length!=item.Size||!string.Equals(McpTools.FileHash(p),item.Sha256,StringComparison.OrdinalIgnoreCase))throw new IOException("완성 파일 크기·SHA-256이 다릅니다: "+item.Path);}
            if(row.Kind=="file-upload")McpTools.Commit(row.Stage,row.Path,row.Expected,options);
            else lock(McpTools.FileGate){McpPath.Check(row.Path,options);if(Directory.Exists(row.Path)||File.Exists(row.Path))throw new IOException("대상 폴더가 이미 생겼습니다.");Directory.Move(row.Stage,row.Path);}
            row.Status="completed";row.Sha256=row.Kind=="file-upload"?row.Files[0].Sha256:null;Save(row);return View(row,options);
        }
        private object Prepare(Dictionary<string,object> a,McpOptions options,string token)
        {
            string path=McpPath.Check(McpTools.Required(a,"path"),options),kind=McpTools.Required(a,"kind");Capacity();
            if(kind!="file"&&kind!="folder")throw new ArgumentException("kind는 file 또는 folder입니다.");
            var row=new State{Id=Guid.NewGuid().ToString("N"),Owner=Owner(token),Kind=kind+"-download",Path=path,Expires=DateTime.UtcNow.AddHours(1).Ticks,Status="preparing"};
            row.Stage=path+".mcp-transfer-"+row.Id+(kind=="folder"?".zip":".tmp");McpPath.Check(row.Stage,options);
            if(kind=="file") {if(!File.Exists(path))throw new IOException("파일이 없습니다.");if(new FileInfo(path).Length>4L*1024*1024*1024)throw new IOException("파일 다운로드는 4GiB까지입니다.");Save(row);active[row.Id]=row;ThreadPool.QueueUserWorkItem(delegate{try{SetReadyFile(row,options);}catch(Exception ex){lock(gate){if(row.Status!="canceled"){row.Status="failed";row.Error=ex.Message;}Save(row);}}});}
            else {if(!Directory.Exists(path)||path==System.IO.Path.GetPathRoot(path))throw new IOException("받을 폴더 경로를 확인하세요.");Save(row);active[row.Id]=row;ThreadPool.QueueUserWorkItem(delegate{BuildZip(row,options);});}
            return View(row,options);
        }
        private void SetReadyFile(State row,McpOptions options)
        { string path=McpPath.Check(row.Path,options);var result=new State();SetReady(result,path);lock(gate){if(row.Status=="canceled")return;row.Sha256=result.Sha256;row.Size=result.Size;row.Modified=result.Modified;row.Status="ready";Save(row);} }
        private static void SetReady(State row,string path)
        {using(var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read)){using(var sha=SHA256.Create())row.Sha256=BitConverter.ToString(sha.ComputeHash(file)).Replace("-","").ToLowerInvariant();row.Size=file.Length;row.Modified=File.GetLastWriteTimeUtc(path).Ticks;}row.Status="ready";}
        private void BuildZip(State row,McpOptions options)
        {
            try {
                var queue=new Queue<string>();queue.Enqueue(row.Path);var entries=new List<string>();long total=0;
                while(queue.Count>0){string dir=queue.Dequeue();McpPath.Check(dir,options);foreach(string path in Directory.GetFileSystemEntries(dir)){McpPath.Check(path,options);if(++total>10000)throw new IOException("폴더에 항목이 10000개를 넘습니다.");entries.Add(path);if(Directory.Exists(path))queue.Enqueue(path);}}
                long bytes=0;
                using(var file=new FileStream(row.Stage,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                using(var zip=new ZipArchive(file,ZipArchiveMode.Create))foreach(string path in entries)
                {
                    lock(gate){if(row.Status=="canceled")throw new OperationCanceledException();}
                    McpPath.Check(path,options);string relative=path.Substring(row.Path.Length+1).Replace('\\','/');
                    if(Directory.Exists(path)){zip.CreateEntry(relative+"/");continue;}
                    using(var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read)) {bytes+=input.Length;if(bytes>8L*1024*1024*1024)throw new IOException("폴더 다운로드는 8GiB까지입니다.");var entry=zip.CreateEntry(relative,CompressionLevel.NoCompression);using(var output=entry.Open())input.CopyTo(output,ChunkSize);}
                }
                lock(gate){if(row.Status=="canceled"){Cleanup(row,options);return;}SetReady(row,row.Stage);Save(row);}
            }catch(Exception ex){lock(gate){if(row.Status!="canceled"){row.Status="failed";row.Error=ex.Message;}Save(row);Cleanup(row,options);}}
        }
        private static string DownloadPath(State row,McpOptions options)
        {
            if(row.Status!="ready"||!row.Kind.EndsWith("-download"))throw new IOException("다운로드 준비가 완료되지 않았습니다.");
            string path=McpPath.Check(row.Kind=="folder-download"?row.Stage:row.Path,options);var info=new FileInfo(path);
            if(info.Length!=row.Size||info.LastWriteTimeUtc.Ticks!=row.Modified)throw new IOException("다운로드 원본이 변경됐습니다. 다시 요청하세요.");return path;
        }
        private static object Read(State row,Dictionary<string,object> a,McpOptions options)
        {
            string path=DownloadPath(row,options);long offset=Number(a,"offset",0),limit=Number(a,"limit",ChunkSize);if(limit<1||limit>ChunkSize||offset>row.Size)throw new ArgumentException("다운로드 분할 범위가 올바르지 않습니다.");
            byte[] data=new byte[(int)Math.Min(limit,row.Size-offset)];using(var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read)){file.Position=offset;int got=0;while(got<data.Length){int n=file.Read(data,got,data.Length-got);if(n==0)throw new EndOfStreamException();got+=n;}}
            return new{transfer_id=row.Id,offset=offset,next_offset=offset+data.Length,size=row.Size,sha256=row.Sha256,chunk_sha256=Hash(data),data_base64=Convert.ToBase64String(data),eof=offset+data.Length==row.Size};
        }
        internal void SendDownload(Stream stream,string id,string token,McpOptions options,string range)
        {
            State row;string path;lock(gate){row=Load(id,token,options);path=DownloadPath(row,options);}
            using(var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
            {
                using(var sha=SHA256.Create())if(!string.Equals(BitConverter.ToString(sha.ComputeHash(file)).Replace("-",""),row.Sha256,StringComparison.OrdinalIgnoreCase))throw new IOException("다운로드 원본이 변경됐습니다.");
                long start=0,end=file.Length-1;bool partial=!string.IsNullOrEmpty(range);
                if(partial){var m=Regex.Match(range,"^bytes=([0-9]+)-([0-9]*)$");if(!m.Success||!long.TryParse(m.Groups[1].Value,out start)||start>=file.Length||start<0||(!string.IsNullOrEmpty(m.Groups[2].Value)&&(!long.TryParse(m.Groups[2].Value,out end)||end<start||end>=file.Length)))throw new ArgumentException("다운로드 Range가 올바르지 않습니다.");}
                long length=file.Length==0?0:end-start+1;string name=System.IO.Path.GetFileName(row.Path)+(row.Kind=="folder-download"?".zip":"");
                string head="HTTP/1.1 "+(partial?"206 Partial Content":"200 OK")+"\r\nContent-Type: application/octet-stream\r\nContent-Length: "+length+"\r\nContent-Disposition: attachment; filename=\"download\"; filename*=UTF-8''"+Uri.EscapeDataString(name)+"\r\nCache-Control: no-store\r\nAccept-Ranges: bytes\r\nX-Content-SHA256: "+row.Sha256+"\r\n"+(partial?"Content-Range: bytes "+start+"-"+end+"/"+file.Length+"\r\n":"")+"Connection: close\r\n\r\n";
                byte[] header=Encoding.ASCII.GetBytes(head);stream.Write(header,0,header.Length);file.Position=start;byte[] buffer=new byte[ChunkSize];
                while(length>0){int n=file.Read(buffer,0,(int)Math.Min(buffer.Length,length));if(n==0)throw new EndOfStreamException();stream.Write(buffer,0,n);length-=n;}
            }
        }
        private static void Cleanup(State row,McpOptions options)
        {try{McpPath.Check(row.Stage,options);if(File.Exists(row.Stage))File.Delete(row.Stage);if(Directory.Exists(row.Stage))Directory.Delete(row.Stage,true);}catch{}}
    }
}

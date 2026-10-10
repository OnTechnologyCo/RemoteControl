// 상대 PC 의 파일을 보고, 받고, 올린다.
//
// 왜 WebSocket 이 아니라 HTTP 인가.
//   화면 스트림과 같은 소켓으로 파일을 나르면 큰 파일을 받는 동안 화면이 밀린다.
//   이 프로젝트의 1번 합격 기준이 지연이다(`요구사항.md`). 소켓을 나눠야 한다.
//   게다가 HTTP 라야 브라우저의 기본 다운로드 창이 뜨고, 이어받기가 되고,
//   메모리에 다 담지 않고 몇 GB 짜리도 받을 수 있다. WebSocket 으로 받으면
//   전부 메모리에 쌓았다가 Blob 으로 넘겨야 해서 큰 파일에서 통째로 죽는다.
//
// 흐르는 방식은 옆 프로젝트(FileSharer)의 ServeFile 을 가져와 다듬었다.
// 암호화(CTR)와 공유 DB 부분은 여기서 필요 없어 걷어냈다.
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace RemoteControl
{
    internal static class Files
    {
        private const int Chunk = 256 * 1024;

        // ---------------------------------------------------------------- 목록

        /// <summary>
        /// 폴더 하나를 JSON 으로. 경로가 비면 드라이브 목록을 준다.
        ///
        /// 폴더를 먼저, 그 다음 파일. 각각 이름순. 탐색기와 같은 차례라야
        /// 사람이 눈으로 찾을 때 헤매지 않는다.
        /// </summary>
        internal static string List(string path)
        {
            StringBuilder sb = new StringBuilder(8192);

            if (string.IsNullOrEmpty(path))
            {
                sb.Append("{\"path\":\"\",\"up\":null,\"items\":[");

                bool first = true;
                foreach (DriveInfo d in SafeDrives())
                {
                    if (!first) sb.Append(',');
                    first = false;

                    string label = d.Name;
                    try { if (d.IsReady && !string.IsNullOrEmpty(d.VolumeLabel)) label = d.Name + "  " + d.VolumeLabel; }
                    catch { }

                    sb.Append("{\"n\":\"").Append(Json.Escape(label))
                      .Append("\",\"p\":\"").Append(Json.Escape(d.Name))
                      .Append("\",\"d\":true,\"s\":0,\"t\":0}");
                }

                sb.Append("]}");
                return sb.ToString();
            }

            string full;
            try { full = Path.GetFullPath(path); }
            catch { return Fail("경로가 올바르지 않습니다."); }

            if (!Directory.Exists(full)) return Fail("폴더가 없습니다: " + full);

            // 위로 갈 곳. 드라이브 뿌리면 null 이 아니라 "" 를 줘서 드라이브 목록으로 돌아가게 한다.
            string up;
            try
            {
                DirectoryInfo par = Directory.GetParent(full);
                up = par == null ? "" : par.FullName;
            }
            catch { up = ""; }

            sb.Append("{\"path\":\"").Append(Json.Escape(full))
              .Append("\",\"up\":\"").Append(Json.Escape(up)).Append("\",\"items\":[");

            bool comma = false;

            try
            {
                foreach (string d in Directory.GetDirectories(full))
                {
                    DirectoryInfo di;
                    try { di = new DirectoryInfo(d); }
                    catch { continue; }

                    if (comma) sb.Append(',');
                    comma = true;

                    sb.Append("{\"n\":\"").Append(Json.Escape(di.Name))
                      .Append("\",\"p\":\"").Append(Json.Escape(di.FullName))
                      .Append("\",\"d\":true,\"s\":0,\"t\":").Append(Unix(di.LastWriteTimeUtc)).Append('}');
                }

                foreach (string f in Directory.GetFiles(full))
                {
                    FileInfo fi;
                    try { fi = new FileInfo(f); }
                    catch { continue; }

                    if (comma) sb.Append(',');
                    comma = true;

                    sb.Append("{\"n\":\"").Append(Json.Escape(fi.Name))
                      .Append("\",\"p\":\"").Append(Json.Escape(fi.FullName))
                      .Append("\",\"d\":false,\"s\":").Append(fi.Length)
                      .Append(",\"t\":").Append(Unix(fi.LastWriteTimeUtc)).Append('}');
                }
            }
            catch (UnauthorizedAccessException)
            {
                return Fail("이 폴더를 읽을 권한이 없습니다.");
            }
            catch (Exception ex)
            {
                return Fail(ex.Message);
            }

            sb.Append("]}");
            return sb.ToString();
        }

        /// <summary>
        /// 준비 안 된 드라이브에서 터지지 않게 감싼다.
        /// 카드리더나 연결 끊긴 네트워크 드라이브가 있으면 DriveInfo 접근 자체가 예외를 낸다.
        /// </summary>
        private static List<DriveInfo> SafeDrives()
        {
            List<DriveInfo> r = new List<DriveInfo>();

            try
            {
                foreach (DriveInfo d in DriveInfo.GetDrives())
                {
                    try { if (d.IsReady) r.Add(d); }
                    catch { }
                }
            }
            catch { }

            return r;
        }

        private static string Fail(string why)
        {
            return "{\"err\":\"" + Json.Escape(why) + "\"}";
        }

        private static long Unix(DateTime utc)
        {
            long t = (long)(utc - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
            return t < 0 ? 0 : t;
        }

        // ---------------------------------------------------------------- 파일 하나 내보내기

        /// <summary>
        /// 파일 하나를 HTTP 로 내보낸다. Range 이어받기를 지원한다.
        ///
        /// 이어받기가 왜 필요한가 — 외부망 터널은 오래 붙들고 있으면 조용히 끊긴다.
        /// Range 가 없으면 90% 에서 끊겨도 처음부터 다시 받아야 한다.
        /// (FileSharer 에서 그대로 겪었다)
        /// </summary>
        internal static void Send(Stream to, string[] reqLines, string path, bool head)
        {
            FileInfo fi;
            try { fi = new FileInfo(Path.GetFullPath(path)); }
            catch { Text(to, "400 Bad Request", "경로가 올바르지 않습니다."); return; }

            if (!fi.Exists) { Text(to, "404 Not Found", "파일이 없습니다."); return; }

            long total;
            try { total = fi.Length; }
            catch { Text(to, "404 Not Found", "파일을 읽을 수 없습니다."); return; }

            long start = 0, end = total - 1;
            bool partial = false;

            string range = Header(reqLines, "Range");
            if (!string.IsNullOrEmpty(range) && total > 0)
            {
                if (TryRange(range, total, out start, out end)) partial = true;
                else
                {
                    Raw(to, "HTTP/1.1 416 Range Not Satisfiable\r\nContent-Range: bytes */" + total +
                            "\r\nContent-Length: 0\r\nAccept-Ranges: bytes\r\nConnection: close\r\n\r\n");
                    return;
                }
            }

            long length = total == 0 ? 0 : end - start + 1;

            StringBuilder h = new StringBuilder();
            h.Append(partial ? "HTTP/1.1 206 Partial Content\r\n" : "HTTP/1.1 200 OK\r\n");
            h.Append("Content-Type: application/octet-stream\r\n");
            h.Append("Content-Length: ").Append(length).Append("\r\n");
            h.Append("Accept-Ranges: bytes\r\n");
            if (partial) h.Append("Content-Range: bytes ").Append(start).Append('-').Append(end).Append('/').Append(total).Append("\r\n");

            // 한글 이름을 머리글에 날것으로 넣으면 클라이언트마다 다르게 깨진다.
            // ASCII 예비 이름과 RFC 5987 값을 같이 준다.
            h.Append("Content-Disposition: attachment; filename=\"").Append(HeaderSafe(fi.Name))
             .Append("\"; filename*=UTF-8''").Append(Uri.EscapeDataString(fi.Name)).Append("\r\n");

            h.Append("Last-Modified: ").Append(fi.LastWriteTimeUtc.ToString("R")).Append("\r\n");
            h.Append("Cache-Control: no-store\r\nConnection: close\r\n\r\n");

            Raw(to, h.ToString());
            if (head) return;

            try
            {
                using (FileStream fs = new FileStream(fi.FullName, FileMode.Open, FileAccess.Read,
                                                      FileShare.ReadWrite | FileShare.Delete,
                                                      Chunk, FileOptions.SequentialScan))
                {
                    if (start > 0) fs.Seek(start, SeekOrigin.Begin);

                    byte[] buf = new byte[Chunk];
                    long left = length;

                    while (left > 0)
                    {
                        int n = fs.Read(buf, 0, (int)Math.Min(Chunk, left));
                        if (n <= 0) break;

                        to.Write(buf, 0, n);
                        left -= n;
                    }
                }
            }
            catch { }        // 중간에 끊겨도 그만이다. 브라우저가 Range 로 이어받는다
        }

        // ---------------------------------------------------------------- 폴더 통째로 (tar)

        /// <summary>
        /// 폴더를 tar 로 묶어 흘려보낸다. 압축하지 않는다 (Tar.cs 머리말 참고).
        /// 크기를 미리 셈해 Content-Length 를 정확히 준다 - 브라우저가 진행률을 그린다.
        /// </summary>
        internal static void SendTar(Stream to, string path, bool head)
        {
            string full;
            try { full = Path.GetFullPath(path); }
            catch { Text(to, "400 Bad Request", "경로가 올바르지 않습니다."); return; }

            if (!Directory.Exists(full)) { Text(to, "404 Not Found", "폴더가 없습니다."); return; }

            long total;
            List<TarEntry> plan;

            try { plan = Tar.Plan(full, out total); }
            catch (Exception ex) { Text(to, "500 Internal Server Error", "폴더를 훑지 못했습니다: " + ex.Message); return; }

            string name = Path.GetFileName(full.TrimEnd('\\', '/'));
            if (string.IsNullOrEmpty(name)) name = full.Replace(":", "").Replace("\\", "");
            if (string.IsNullOrEmpty(name)) name = "files";

            SendTarStream(to, plan, total, name, head);
        }

        private static void SendTarStream(Stream to, List<TarEntry> plan, long total, string name, bool head)
        {
            if (!name.EndsWith(".tar", StringComparison.OrdinalIgnoreCase)) name += ".tar";

            StringBuilder h = new StringBuilder();
            h.Append("HTTP/1.1 200 OK\r\n");
            h.Append("Content-Type: application/x-tar\r\n");
            h.Append("Content-Length: ").Append(total).Append("\r\n");

            // 흘려보내며 만드는 것이라 중간부터는 못 준다. 시늉만 하면 브라우저가
            // 이어받기를 시도했다가 엉뚱한 위치를 받아 조용히 깨진 파일이 나온다.
            h.Append("Accept-Ranges: none\r\n");

            h.Append("Content-Disposition: attachment; filename=\"").Append(HeaderSafe(name))
             .Append("\"; filename*=UTF-8''").Append(Uri.EscapeDataString(name)).Append("\r\n");

            h.Append("X-File-Count: ").Append(plan.Count).Append("\r\n");
            h.Append("Cache-Control: no-store\r\nConnection: close\r\n\r\n");

            Raw(to, h.ToString());
            if (head) return;

            try { Tar.Write(to, plan); }
            catch { }
        }

        // ---------------------------------------------------------------- 고른 것 묶어 받기

        // 고른 경로 목록을 잠깐 들고 있는다.
        //
        // 왜 표를 끊어 주나 - 경로를 전부 주소에 실으면 파일을 몇십 개만 골라도 주소가
        // 수 KB 가 된다. 우리 서버는 머리글을 16KB 까지만 읽고, 그보다 앞서 브라우저와
        // 중계기가 먼저 자른다. 그래서 목록은 본문으로 한 번 보내 두고(POST /fs/pack),
        // 받는 것은 짧은 표로만 부른다(GET /fs/tar?id=...).
        // 브라우저의 기본 다운로드 창은 GET 으로만 뜨기 때문에 두 단계로 나눠야 한다.
        private sealed class Pack { public List<string> Paths; public string Name; public DateTime Until; }

        private static readonly Dictionary<string, Pack> packs = new Dictionary<string, Pack>();
        private static readonly object packGate = new object();

        internal const int MaxPick = 5000;

        /// <summary>고른 목록을 맡아 두고 표를 준다.</summary>
        internal static string Keep(List<string> paths, string name)
        {
            string id = Guid.NewGuid().ToString("N");

            lock (packGate)
            {
                // 오래된 것은 버린다. 안 버리면 고르기만 하고 안 받은 목록이 계속 쌓인다.
                List<string> dead = new List<string>();
                foreach (KeyValuePair<string, Pack> kv in packs)
                    if (kv.Value.Until < DateTime.UtcNow) dead.Add(kv.Key);

                foreach (string k in dead) packs.Remove(k);

                Pack p = new Pack();
                p.Paths = paths;
                p.Name = name;

                // 넉넉히 둔다. 고르고 나서 다른 일을 하다 받는 경우도 있고,
                // 받다 끊겨 다시 누르는 경우도 있다.
                p.Until = DateTime.UtcNow.AddMinutes(30);

                packs[id] = p;
            }

            return id;
        }

        /// <summary>맡아 둔 목록을 tar 로 내보낸다.</summary>
        internal static void SendPack(Stream to, string id, bool head)
        {
            Pack p = null;

            lock (packGate)
            {
                Pack got;
                if (packs.TryGetValue(id, out got) && got.Until > DateTime.UtcNow) p = got;
            }

            if (p == null)
            {
                Text(to, "410 Gone", "고른 목록이 만료되었습니다. 다시 골라 주세요.");
                return;
            }

            long total;
            List<TarEntry> plan;

            try { plan = Tar.PlanMany(p.Paths, out total); }
            catch (Exception ex) { Text(to, "500 Internal Server Error", "묶지 못했습니다: " + ex.Message); return; }

            SendTarStream(to, plan, total, p.Name, head);
        }

        // ---------------------------------------------------------------- 올려받기

        /// <summary>
        /// 올라오는 본문을 그대로 파일에 쓴다.
        ///
        /// 통째로 메모리에 담지 않는다. 몇 GB 짜리를 올리면 그 자리에서 죽는다.
        /// 머리글을 읽을 때 딸려 온 앞부분(preRead)을 먼저 쓰고 나머지를 이어 읽는다.
        ///
        /// 임시 이름으로 받아 다 받은 뒤에 제 이름으로 바꾼다. 중간에 끊겼을 때
        /// 반쯤 받은 파일이 멀쩡한 이름을 달고 남아 있으면, 그게 온전한 줄 알고 쓴다.
        /// </summary>
        internal static string Receive(Stream from, byte[] preRead, long length, string dir, string name)
        {
            if (length < 0) return "크기를 알 수 없습니다.";

            string folder;
            try { folder = Path.GetFullPath(dir); }
            catch { return "폴더 경로가 올바르지 않습니다."; }

            if (!Directory.Exists(folder)) return "폴더가 없습니다: " + folder;

            string safe = SafeName(name);
            if (safe.Length == 0) return "파일 이름이 올바르지 않습니다.";

            string target = Path.Combine(folder, safe);

            // 이미 있으면 덮어쓰지 않고 이름을 비켜 준다. 원격에서 남의 파일을 말없이
            // 덮어쓰면 되돌릴 방법이 없다.
            target = Unique(target);

            string tmp = target + ".올리는중";

            try
            {
                using (FileStream fs = new FileStream(tmp, FileMode.Create, FileAccess.Write,
                                                      FileShare.None, Chunk, FileOptions.SequentialScan))
                {
                    long left = length;

                    if (preRead != null && preRead.Length > 0)
                    {
                        int n = (int)Math.Min(preRead.Length, left);
                        fs.Write(preRead, 0, n);
                        left -= n;
                    }

                    byte[] buf = new byte[Chunk];

                    while (left > 0)
                    {
                        int n = from.Read(buf, 0, (int)Math.Min(Chunk, left));
                        if (n <= 0) break;

                        fs.Write(buf, 0, n);
                        left -= n;
                    }

                    if (left > 0)
                    {
                        fs.Close();
                        try { File.Delete(tmp); } catch { }
                        return "전송이 중간에 끊겼습니다. 다시 올려 주세요.";
                    }
                }

                File.Move(tmp, target);
                return "";
            }
            catch (UnauthorizedAccessException)
            {
                try { File.Delete(tmp); } catch { }
                return "이 폴더에 쓸 권한이 없습니다.";
            }
            catch (Exception ex)
            {
                try { File.Delete(tmp); } catch { }
                return ex.Message;
            }
        }

        /// <summary>
        /// multipart/form-data 로 올라온 파일을 받는다.
        ///
        /// 왜 이게 필요한가 — 파일을 본문에 날것으로 실어 보내면(POST + File 객체)
        /// 백신·DLP 같은 보안 제품이 "정체를 알 수 없는 이진 업로드" 로 보고 막는 일이 있다.
        /// 실제로 그렇게 막혔다. multipart 는 웹에서 파일을 올리는 표준 모양이라
        /// 그런 제품들이 알아보고 통과시킨다.
        ///
        /// 통째로 메모리에 담지 않는다. 경계 표시를 미끄러지듯 찾으며 그대로 흘려 쓴다.
        /// </summary>
        internal static string ReceiveMultipart(Stream from, byte[] preRead, long length,
                                                string dir, string name, string boundary)
        {
            if (length <= 0) return "크기를 알 수 없습니다.";
            if (string.IsNullOrEmpty(boundary)) return "boundary 가 없습니다.";

            string folder;
            try { folder = Path.GetFullPath(dir); }
            catch { return "폴더 경로가 올바르지 않습니다."; }

            if (!Directory.Exists(folder)) return "폴더가 없습니다: " + folder;

            string safe = SafeName(name);
            if (safe.Length == 0) return "파일 이름이 올바르지 않습니다.";

            string target = Unique(Path.Combine(folder, safe));
            string tmp = target + ".올리는중";

            // 끝 표시는 "\r\n--boundary". 앞의 \r\n 은 내용이 아니라 구분자의 일부다.
            byte[] mark = Encoding.ASCII.GetBytes("\r\n--" + boundary);

            Feeder src = new Feeder(from, preRead, length);

            try
            {
                // 첫 줄(--boundary)과 파트 머리글을 건너뛴다. 빈 줄이 나오면 거기부터 내용이다.
                // 이름은 주소(n=)로 이미 받았으므로 머리글에서 캐낼 것이 없다.
                if (!src.SkipPast(Encoding.ASCII.GetBytes("\r\n\r\n")))
                    return "올라온 자료의 모양이 올바르지 않습니다.";

                using (FileStream fs = new FileStream(tmp, FileMode.Create, FileAccess.Write,
                                                      FileShare.None, Chunk, FileOptions.SequentialScan))
                {
                    if (!src.CopyUntil(mark, fs))
                    {
                        fs.Close();
                        try { File.Delete(tmp); } catch { }
                        return "전송이 중간에 끊겼습니다. 다시 올려 주세요.";
                    }
                }

                File.Move(tmp, target);
                return "";
            }
            catch (UnauthorizedAccessException)
            {
                try { File.Delete(tmp); } catch { }
                return "이 폴더에 쓸 권한이 없습니다.";
            }
            catch (Exception ex)
            {
                try { File.Delete(tmp); } catch { }
                return ex.Message;
            }
        }

        /// <summary>
        /// 머리글을 읽을 때 딸려 온 몫과 소켓을 하나로 이어 읽는다.
        /// Content-Length 만큼만 읽고 멈춘다 - 더 읽으면 다음 요청을 먹어 버린다.
        /// </summary>
        private sealed class Feeder
        {
            private readonly Stream sock;
            private byte[] buf = new byte[Chunk * 2];
            private int start, end;
            private long left;

            internal Feeder(Stream s, byte[] pre, long length)
            {
                sock = s;
                left = length;

                if (pre != null && pre.Length > 0)
                {
                    int n = (int)Math.Min(pre.Length, left);
                    if (n > buf.Length) buf = new byte[n + Chunk];

                    Buffer.BlockCopy(pre, 0, buf, 0, n);
                    end = n;
                    left -= n;
                }
            }

            private int Have { get { return end - start; } }

            /// <summary>버퍼에 최소 want 바이트가 있게 한다. 더 못 채우면 false.</summary>
            private bool Fill(int want)
            {
                while (Have < want)
                {
                    if (left <= 0) return false;

                    if (start > 0)
                    {
                        Buffer.BlockCopy(buf, start, buf, 0, Have);
                        end -= start;
                        start = 0;
                    }

                    if (end == buf.Length)
                    {
                        byte[] bigger = new byte[buf.Length * 2];
                        Buffer.BlockCopy(buf, 0, bigger, 0, end);
                        buf = bigger;
                    }

                    int n;
                    try { n = sock.Read(buf, end, (int)Math.Min(buf.Length - end, left)); }
                    catch { return false; }
                    if (n <= 0) return false;

                    end += n;
                    left -= n;
                }
                return true;
            }

            /// <summary>표시가 나올 때까지 버리고, 그 뒤로 옮긴다.</summary>
            internal bool SkipPast(byte[] mark)
            {
                while (true)
                {
                    if (!Fill(mark.Length)) return false;

                    int at = Find(mark);
                    if (at >= 0) { start = at + mark.Length; return true; }

                    // 표시가 걸쳐 있을 수 있으니 끝자락은 남긴다.
                    start = end - (mark.Length - 1);
                }
            }

            /// <summary>표시가 나올 때까지 그대로 흘려 쓴다.</summary>
            internal bool CopyUntil(byte[] mark, Stream to)
            {
                while (true)
                {
                    if (!Fill(mark.Length))
                    {
                        // 더 못 읽는다. 남은 것에 표시가 있으면 거기까지가 내용이다.
                        int last = Find(mark);
                        if (last >= 0) { to.Write(buf, start, last - start); return true; }
                        return false;
                    }

                    int at = Find(mark);
                    if (at >= 0)
                    {
                        to.Write(buf, start, at - start);
                        start = at + mark.Length;
                        return true;
                    }

                    // 표시가 두 조각에 걸쳐 있을 수 있다. 끝자락 (표시 길이 - 1) 은 남겨 둔다.
                    int keep = mark.Length - 1;
                    int give = Have - keep;

                    if (give > 0) { to.Write(buf, start, give); start += give; }
                    else if (!Fill(Have + 1)) return false;      // 더 받아야 판단할 수 있다
                }
            }

            private int Find(byte[] mark)
            {
                int limit = end - mark.Length;

                for (int i = start; i <= limit; i++)
                {
                    int j = 0;
                    while (j < mark.Length && buf[i + j] == mark[j]) j++;
                    if (j == mark.Length) return i;
                }
                return -1;
            }
        }

        /// <summary>Content-Type 에서 boundary 를 꺼낸다.</summary>
        internal static string Boundary(string contentType)
        {
            if (string.IsNullOrEmpty(contentType)) return "";

            int at = contentType.IndexOf("boundary=", StringComparison.OrdinalIgnoreCase);
            if (at < 0) return "";

            string v = contentType.Substring(at + 9).Trim();

            int semi = v.IndexOf(';');
            if (semi >= 0) v = v.Substring(0, semi).Trim();

            return v.Trim('"');
        }

        // 폴더 올리기의 상대 경로는 파일 이름처럼 잘라 버리면 안 된다.
        // SYSTEM으로 받으므로 '..', 장치 이름, junction을 통해 선택한 폴더 밖으로 나가지 않게 한다.
        internal static string UploadDirectory(string dir, string relative, out string folder)
        {
            folder = "";
            try
            {
                string root = Path.GetFullPath(dir);
                if (!Directory.Exists(root)) return "올릴 폴더가 없습니다.";
                string[] parts = (relative ?? "").Split('/');
                if (relative == null || relative.Length == 0 || parts.Length > 64 || relative.Length > 2048)
                    return "폴더 경로가 올바르지 않습니다.";
                // 먼저 전체 경로를 검사한다. 뒤쪽이 잘못됐는데 앞쪽만 만들어 두지 않는다.
                foreach (string part in parts)
                {
                    if (part.Length == 0 || part == "." || part == ".." || part.Length > 200 ||
                        part != part.Trim() || part.EndsWith(".") || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                        return "폴더 이름이 올바르지 않습니다: " + part;
                    string stem = part.Split('.')[0].TrimEnd().ToUpperInvariant();
                    if (stem == "CON" || stem == "PRN" || stem == "AUX" || stem == "NUL" || stem == "CONIN$" || stem == "CONOUT$" ||
                        (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && "123456789¹²³".IndexOf(stem[3]) >= 0))
                        return "Windows에서 사용할 수 없는 폴더 이름입니다: " + part;
                }
                string current = root;
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    return "연결된 폴더에는 폴더 전송을 할 수 없습니다.";
                foreach (string part in parts)
                {
                    current = Path.Combine(current, part);
                    if (File.Exists(current)) return "같은 이름의 파일이 있어 폴더를 만들 수 없습니다: " + part;
                    if (!Directory.Exists(current)) Directory.CreateDirectory(current);
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                        return "연결된 하위 폴더에는 전송할 수 없습니다: " + part;
                }
                folder = current;
                return "";
            }
            catch (Exception ex) { return ex.Message; }
        }

        internal static string MakeDir(string dir, string name)
        {
            string folder;
            try { folder = Path.GetFullPath(dir); }
            catch { return "폴더 경로가 올바르지 않습니다."; }

            if (!Directory.Exists(folder)) return "폴더가 없습니다.";

            string safe = SafeName(name);
            if (safe.Length == 0) return "이름이 올바르지 않습니다.";

            try { Directory.CreateDirectory(Path.Combine(folder, safe)); return ""; }
            catch (Exception ex) { return ex.Message; }
        }

        /// <summary>
        /// 브라우저가 보낸 이름에서 경로 부분을 떼어 낸다.
        ///
        /// 이걸 안 하면 이름에 "..\..\Windows\System32\..." 를 넣어 아무 데나 쓸 수 있다.
        /// 대리 프로세스는 SYSTEM 으로 돌기 때문에 실제로 써진다.
        /// </summary>
        private static string SafeName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";

            string s = name.Replace('/', '\\');
            int cut = s.LastIndexOf('\\');
            if (cut >= 0) s = s.Substring(cut + 1);

            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');

            s = s.Trim().TrimEnd('.');
            if (s == "." || s == "..") return "";

            return s.Length > 200 ? s.Substring(0, 200) : s;
        }

        private static string Unique(string target)
        {
            if (!File.Exists(target) && !Directory.Exists(target)) return target;

            string dir = Path.GetDirectoryName(target);
            string stem = Path.GetFileNameWithoutExtension(target);
            string ext = Path.GetExtension(target);

            for (int i = 2; i < 10000; i++)
            {
                string t = Path.Combine(dir, stem + " (" + i + ")" + ext);
                if (!File.Exists(t) && !Directory.Exists(t)) return t;
            }

            return target + "." + DateTime.Now.Ticks;
        }

        // ---------------------------------------------------------------- 클립보드

        /// <summary>
        /// 이 PC 의 클립보드를 읽는다.
        ///
        /// 두 가지를 지켜야 한다.
        ///  1. 클립보드는 STA 스레드에서만 제대로 열린다. 서버 스레드는 MTA 라서
        ///     그냥 부르면 ThreadStateException 이 난다. 전용 스레드를 하나 세운다.
        ///  2. 지금 떠 있는 데스크톱에 붙어야 한다. 잠금 화면(Winlogon)으로 넘어간 뒤에
        ///     Default 에 묶인 채 부르면 빈 값이 나온다. InputInjector 와 같은 이유다.
        /// </summary>
        internal static string ClipGet()
        {
            string got = "";

            Sta(delegate
            {
                Desktops.Sync();

                try
                {
                    if (System.Windows.Forms.Clipboard.ContainsText())
                        got = System.Windows.Forms.Clipboard.GetText() ?? "";
                }
                catch { }
            });

            return got;
        }

        internal static bool ClipSet(string text)
        {
            bool ok = false;

            Sta(delegate
            {
                Desktops.Sync();

                try
                {
                    // 빈 문자열을 SetText 에 넣으면 예외가 난다. 비우는 것은 Clear 로.
                    if (string.IsNullOrEmpty(text)) System.Windows.Forms.Clipboard.Clear();
                    else System.Windows.Forms.Clipboard.SetText(text);

                    ok = true;
                }
                catch { }
            });

            return ok;
        }

        /// <summary>
        /// STA 스레드에서 한 번 돌리고 끝날 때까지 기다린다.
        ///
        /// 다른 프로그램이 클립보드를 붙들고 있으면 열리지 않는다. 그때 무한정 기다리면
        /// 화면 연결을 다루는 스레드가 통째로 멈춘다. 제한 시간을 둔다.
        /// </summary>
        private static void Sta(ThreadStart body)
        {
            Thread t = new Thread(body);
            t.IsBackground = true;
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join(3000);
        }

        // ---------------------------------------------------------------- 도우미

        private static string HeaderSafe(string name)
        {
            if (string.IsNullOrEmpty(name)) return "download";

            StringBuilder sb = new StringBuilder(name.Length);
            foreach (char c in name)
                sb.Append(c < 32 || c > 126 || c == '"' || c == '\\' ? '_' : c);

            string s = sb.ToString().Trim();
            return s.Length == 0 ? "download" : s;
        }

        /// <summary>"bytes=0-1023" / "bytes=1024-" / "bytes=-500" 을 푼다. 첫 구간만 쓴다.</summary>
        private static bool TryRange(string header, long total, out long start, out long end)
        {
            start = 0;
            end = total - 1;
            if (total <= 0) return false;

            string v = header.Trim();
            if (!v.StartsWith("bytes", StringComparison.OrdinalIgnoreCase)) return false;

            v = v.Substring(5).TrimStart();
            if (v.Length == 0 || v[0] != '=') return false;
            v = v.Substring(1).Trim();

            int comma = v.IndexOf(',');
            if (comma >= 0) v = v.Substring(0, comma).Trim();

            int dash = v.IndexOf('-');
            if (dash < 0) return false;

            string a = v.Substring(0, dash).Trim();
            string b = v.Substring(dash + 1).Trim();

            if (a.Length == 0)
            {
                long n;
                if (!long.TryParse(b, out n) || n <= 0) return false;
                if (n > total) n = total;

                start = total - n;
                end = total - 1;
                return true;
            }

            if (!long.TryParse(a, out start) || start < 0 || start >= total) return false;

            if (b.Length == 0) end = total - 1;
            else if (!long.TryParse(b, out end)) return false;

            if (end >= total) end = total - 1;
            return end >= start;
        }

        private static string Header(string[] lines, string name)
        {
            if (lines == null) return null;

            for (int i = 1; i < lines.Length; i++)
            {
                if (string.IsNullOrEmpty(lines[i])) break;

                int c = lines[i].IndexOf(':');
                if (c <= 0) continue;

                if (string.Compare(lines[i].Substring(0, c).Trim(), name, StringComparison.OrdinalIgnoreCase) == 0)
                    return lines[i].Substring(c + 1).Trim();
            }
            return null;
        }

        private static void Raw(Stream s, string text)
        {
            byte[] b = Encoding.UTF8.GetBytes(text);
            try { s.Write(b, 0, b.Length); s.Flush(); }
            catch { }
        }

        private static void Text(Stream s, string status, string body)
        {
            byte[] b = Encoding.UTF8.GetBytes(body);

            Raw(s, "HTTP/1.1 " + status + "\r\nContent-Type: text/plain; charset=utf-8\r\n" +
                   "Content-Length: " + b.Length + "\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");

            try { s.Write(b, 0, b.Length); s.Flush(); }
            catch { }
        }
    }
}

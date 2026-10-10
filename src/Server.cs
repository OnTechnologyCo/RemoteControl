// HTTP + WebSocket 서버.
//
// 구조는 옆 프로젝트(FileSharer)와 같다. TcpListener 로 직접 받고, 접속마다 전용
// 스레드를 준다. 다운로드처럼 오래 붙잡는 연결이 있어서 ThreadPool 에 태우면
// 워커가 금세 고갈되기 때문이다. 원격제어는 연결이 더 오래 유지되므로 더욱 그렇다.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace RemoteControl
{
    internal sealed class Server
    {
        private TcpListener listener;
        private Thread acceptThread;
        private McpHost mcp;
        internal string McpNote { get { return mcp == null ? "MCP 꺼짐" : mcp.Note; } }

        private int port;
        private int activeConns;
        private const int MaxConns = 24;

        /// <summary>지금 화면을 보고 있는 사람 수. 대상 PC 에 표시하기 위해 센다.</summary>
        internal int Viewers;

        /// <summary>이 PC 를 문으로 삼아 하위 PC 에 붙어 있는 사람 수. 이 화면을 보는 것은 아니다.</summary>
        internal int Relays;

        internal event Action<string> Notice;      // 접속/해제 알림 (UI 가 받아 표시)

        internal int Port { get { return port; } }
        internal string LastError = "";

        internal bool Start(int p, bool startMcp = false)
        {
            try
            {
                listener = new TcpListener(IPAddress.Any, p);
                listener.Start();
                port = p;

                // 자식 프로세스(터널)가 이 소켓을 물려받으면, 본체가 죽어도 포트가 안 풀린다.
                Interop.MakeHandleNonInheritable(listener.Server.Handle);

                acceptThread = new Thread(AcceptLoop) { IsBackground = true };
                acceptThread.Start();

                LastError = "";
                if (startMcp && mcp == null)
                {
                    mcp = new McpHost(delegate { var o=McpOptions.Load(); o.Enabled &= Config.HasPassword; return o; }, Config.CheckLogin, SvcLog.Write);
                    mcp.Start();
                }
                return true;
            }
            catch (Exception ex)
            {
                listener = null;
                LastError = ex.Message;
                return false;
            }
        }

        internal void Stop()
        {
            if (mcp != null) { mcp.Dispose(); mcp = null; }
            try
            {
                TcpListener l = listener;
                listener = null;
                if (l != null) l.Stop();
            }
            catch { }
        }

        private void AcceptLoop()
        {
            while (listener != null)
            {
                TcpClient c;
                try { c = listener.AcceptTcpClient(); }
                catch { break; }

                if (Interlocked.Increment(ref activeConns) > MaxConns)
                {
                    Interlocked.Decrement(ref activeConns);
                    try { c.Close(); } catch { }
                    continue;
                }

                TcpClient captured = c;
                Thread t = new Thread(delegate ()
                {
                    try { Handle(captured); }
                    catch { }
                    finally { Interlocked.Decrement(ref activeConns); }
                }, 512 * 1024);

                t.IsBackground = true;
                t.Start();
            }
        }

        // ---------------------------------------------------------------- 요청 처리

        private void Handle(TcpClient client)
        {
            using (client)
            {
                try
                {
                    Interop.MakeHandleNonInheritable(client.Client.Handle);
                    client.NoDelay = true;                 // 조작 지연을 줄이려면 필수
                    client.SendBufferSize = 256 * 1024;
                    client.SendTimeout = 30000;
                }
                catch { }

                NetworkStream stream = client.GetStream();
                stream.ReadTimeout = 20000;

                byte[] preRead;
                string head = ReadHead(stream, out preRead);
                if (string.IsNullOrEmpty(head)) return;

                string[] lines = head.Split(new string[] { "\r\n" }, StringSplitOptions.None);
                string[] req = lines[0].Split(' ');
                if (req.Length < 2) return;

                string method = req[0];
                string path = req[1];
                int q = path.IndexOf('?');
                string query = q >= 0 ? path.Substring(q + 1) : "";
                if (q >= 0) path = path.Substring(0, q);

                string ip = ClientIp(client, lines);
                string token = Auth.TokenFromCookie(Header(lines, "Cookie"));
                Session sess = Auth.Find(token);

                switch (path)
                {
                    // 지킴이가 공개 주소로 한 바퀴 돌아 여기까지 오는지 본다.
                    // 로그인 화면(2.3KB)을 대신 받아도 되지만, 1분마다 그만큼 실어
                    // 나르는 것은 낭비다. 로그인 화면이 이미 "이 주소에 무언가 있다" 를
                    // 알려 주므로 이 두 글자로 더 새는 것은 없다.
                    case "/ping":
                        Send(stream, "200 OK", "text/plain",
                             Encoding.UTF8.GetBytes("ok"), "Cache-Control: no-store\r\n");
                        return;

                    case "/login":
                        if (method == "POST") { DoLogin(stream, lines, ip, preRead); return; }
                        SendPage(stream, LoginPage(null, client));
                        return;

                    case "/logout":
                        Auth.Drop(token);
                        Send(stream, "302 Found", "text/html",
                             Encoding.UTF8.GetBytes(""), "Location: /\r\nSet-Cookie: rcsid=; Path=/; Max-Age=0\r\n");
                        return;

                    // ---- 앱으로 설치되게 하는 것들 (PWA) ----
                    //
                    // 로그인 없이 내준다. 브라우저는 manifest 와 서비스 워커를 가져갈 때
                    // 쿠키를 붙이지 않기 때문이다 (규격이 그렇다). 로그인을 걸면 설치가
                    // 아예 제안되지 않는다. 대신 여기에는 비밀이 없다 - 프로그램 이름과
                    // 아이콘뿐이고, 그건 로그인 화면에 이미 크게 적혀 있다.
                    case "/manifest.webmanifest":
                        Send(stream, "200 OK", "application/manifest+json; charset=utf-8",
                             Encoding.UTF8.GetBytes(Pwa.Manifest), "Cache-Control: no-cache\r\n");
                        return;

                    case "/sw.js":
                        // 서비스 워커는 절대 캐시하지 않는다. 한 번 낡은 것이 물리면
                        // 새 판을 올려도 옛것이 계속 돌아, 고쳤는데 안 고쳐지는 고장이 된다.
                        Send(stream, "200 OK", "application/javascript; charset=utf-8",
                             Encoding.UTF8.GetBytes(Pwa.ServiceWorker), "Cache-Control: no-store\r\n");
                        return;

                    case "/icon-192.png":
                        SendIcon(stream, Pwa.Icon(192, false));
                        return;

                    case "/icon-512.png":
                        SendIcon(stream, Pwa.Icon(512, false));
                        return;

                    case "/icon-mask.png":
                        SendIcon(stream, Pwa.Icon(512, true));
                        return;

                    case "/favicon.ico":
                        SendIcon(stream, Pwa.Icon(64, false));
                        return;
                }

                // 여기부터는 로그인이 필요하다.
                if (sess == null)
                {
                    if (path == "/ws") return;   // 인증 없는 WebSocket 은 조용히 끊는다
                    SendPage(stream, LoginPage(null, client));
                    return;
                }

                switch (path)
                {
                    case "/":
                    case "/index.html":
                        SendPage(stream, Res("viewer.html"));
                        return;

                    case "/viewer.js":
                        Send(stream, "200 OK", "application/javascript; charset=utf-8",
                             Encoding.UTF8.GetBytes(Res("viewer.js")), "Cache-Control: no-store\r\n");
                        return;

                    // ---- 파일 ----
                    //
                    // 화면(WebSocket)과 다른 소켓으로 간다. 같은 소켓을 쓰면 큰 파일을
                    // 받는 동안 화면이 밀린다 - 이 프로젝트의 1번 기준이 지연이다.
                    //
                    // pc 가 붙어 있으면 하위 PC 것이다. 브릿지가 그대로 넘겨준다.
                    case "/fs/list":
                    case "/fs/get":
                    case "/fs/tar":
                    case "/fs/pack":
                    case "/fs/put":
                    case "/fs/mkdir":
                    {
                        int pc = QInt(query, "pc", 0);

                        if (pc > 0) { DoFsBridge(stream, lines, sess, ip, pc, path, query, method, preRead); return; }

                        DoFs(stream, lines, sess, ip, path, query, method, preRead);
                        return;
                    }

                    // 어느 PC 를 볼지 고르는 창이 이걸 받아 채운다.
                    case "/peers":
                        Send(stream, "200 OK", "application/json; charset=utf-8",
                             Encoding.UTF8.GetBytes(PeerList()), "Cache-Control: no-store\r\n");
                        return;

                    case "/ws":
                    {
                        // pc=0 이거나 없으면 이 PC 다. 그 외에는 하위 PC 로 중계한다.
                        int pc = QInt(query, "pc", 0);

                        if (pc > 0) DoBridge(stream, lines, sess, ip, pc, preRead);
                        else DoWebSocket(stream, lines, sess, ip);
                        return;
                    }
                }

                Send(stream, "404 Not Found", "text/plain", Encoding.UTF8.GetBytes("Not Found"), "");
            }
        }

        // ---------------------------------------------------------------- 로그인

        private void DoLogin(NetworkStream stream, string[] lines, string ip, byte[] preRead)
        {
            int len = 0;
            int.TryParse(Header(lines, "Content-Length") ?? "0", out len);
            if (len < 0 || len > 8192) len = 0;

            string body = "";
            if (len > 0)
            {
                byte[] b = new byte[len];
                int got = 0;

                // 머리글을 읽을 때 이미 딸려 온 몫을 먼저 채운다.
                if (preRead != null && preRead.Length > 0)
                {
                    got = Math.Min(len, preRead.Length);
                    Buffer.BlockCopy(preRead, 0, b, 0, got);
                }

                while (got < len)
                {
                    int r;
                    try { r = stream.Read(b, got, len - got); }
                    catch { break; }
                    if (r <= 0) break;
                    got += r;
                }
                body = Encoding.UTF8.GetString(b, 0, got);
            }

            var form = ParseForm(body);
            string user = form.ContainsKey("u") ? form["u"] : "";
            string pass = form.ContainsKey("p") ? form["p"] : "";

            int locked = Auth.LockedFor(ip);
            if (locked > 0)
            {
                SendPage(stream, LoginPage(
                    string.Format("시도가 너무 많습니다. {0}초 뒤에 다시 시도해 주세요.", locked), null));
                return;
            }

            if (!Config.CheckLogin(user, pass))
            {
                Auth.NoteFailure(ip);
                Notify(string.Format("로그인 실패 - {0} ({1})", user, ip));
                SendPage(stream, LoginPage("아이디 또는 비밀번호가 맞지 않습니다.", null));
                return;
            }

            Auth.NoteSuccess(ip);
            Session s = Auth.CreateSession(user, ip);
            Notify(string.Format("로그인 - {0} ({1})", user, ip));

            // HttpOnly: 스크립트가 토큰을 못 읽게. SameSite=Lax: 다른 사이트에서 끌어다 쓰지 못하게.
            string extra = "Location: /\r\nSet-Cookie: rcsid=" + s.Token +
                           "; Path=/; HttpOnly; SameSite=Lax; Max-Age=" +
                           (int)Auth.SessionLifetime.TotalSeconds + "\r\n";

            Send(stream, "302 Found", "text/html", Encoding.UTF8.GetBytes(""), extra);
        }

        private string LoginPage(string error, TcpClient client)
        {
            string html = Res("login.html");

            string err = string.IsNullOrEmpty(error)
                ? ""
                : "<div class='err'>" + WebUtility.HtmlEncode(error) + "</div>";

            return html.Replace("%ERROR%", err)
                       .Replace("%HOST%", WebUtility.HtmlEncode(Environment.MachineName));
        }

        private static Dictionary<string, string> ParseForm(string body)
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(body)) return d;

            foreach (string pair in body.Split('&'))
            {
                int eq = pair.IndexOf('=');
                if (eq <= 0) continue;

                string k = Uri.UnescapeDataString(pair.Substring(0, eq).Replace('+', ' '));
                string v = Uri.UnescapeDataString(pair.Substring(eq + 1).Replace('+', ' '));
                d[k] = v;
            }
            return d;
        }

        // ---------------------------------------------------------------- 파일

        private void DoFs(NetworkStream stream, string[] lines, Session sess, string ip,
                          string path, string query, string method, byte[] preRead)
        {
            string p = QStr(query, "p");
            bool head = method == "HEAD";

            switch (path)
            {
                case "/fs/list":
                    Send(stream, "200 OK", "application/json; charset=utf-8",
                         Encoding.UTF8.GetBytes(Files.List(p)), "Cache-Control: no-store\r\n");
                    return;

                case "/fs/get":
                    // 큰 파일은 몇 분씩 걸린다. 20초 제한이 걸린 채로 두면 중간에 끊긴다.
                    stream.WriteTimeout = Timeout.Infinite;
                    Notify(string.Format("파일 받기 - {0} ({1}) {2}", sess.User, ip, p));
                    Files.Send(stream, lines, p, head);
                    return;

                case "/fs/tar":
                {
                    stream.WriteTimeout = Timeout.Infinite;

                    // id 가 있으면 앞서 골라 맡겨 둔 목록이다. 없으면 폴더 하나 통째로.
                    string id = QStr(query, "id");

                    if (!string.IsNullOrEmpty(id))
                    {
                        Notify(string.Format("고른 것 받기 - {0} ({1})", sess.User, ip));
                        Files.SendPack(stream, id, head);
                    }
                    else
                    {
                        Notify(string.Format("폴더 받기 - {0} ({1}) {2}", sess.User, ip, p));
                        Files.SendTar(stream, p, head);
                    }
                    return;
                }

                // 고른 목록을 맡아 두고 짧은 표를 준다.
                // 경로를 전부 주소에 실으면 몇십 개만 골라도 주소가 잘린다.
                case "/fs/pack":
                {
                    int len = 0;
                    int.TryParse(Header(lines, "Content-Length") ?? "0", out len);

                    string body = ReadBody(stream, preRead, len, 2 * 1024 * 1024);

                    List<string> paths = new List<string>();
                    foreach (string line in body.Split('\n'))
                    {
                        string t = line.Trim('\r', ' ', '\t');
                        if (t.Length > 0) paths.Add(t);
                        if (paths.Count >= Files.MaxPick) break;
                    }

                    if (paths.Count == 0) { SendResult(stream, "고른 것이 없습니다."); return; }

                    string id = Files.Keep(paths, QStr(query, "n"));

                    Send(stream, "200 OK", "application/json; charset=utf-8",
                         Encoding.UTF8.GetBytes("{\"ok\":true,\"id\":\"" + id + "\",\"n\":" + paths.Count + "}"),
                         "Cache-Control: no-store\r\n");
                    return;
                }

                case "/fs/mkdir":
                {
                    string relative = QStr(query, "r");
                    if (relative.Length > 0)
                    {
                        if (method != "POST") { SendResult(stream, "폴더 전송은 POST 요청이 필요합니다."); return; }
                        string folder;
                        string error = Files.UploadDirectory(p, relative, out folder);
                        if (error.Length > 0) { SendResult(stream, error); return; }
                        // 옛 하위 PC는 r을 무시한다. 지원 여부 확인 없이 올리면 파일들이 한 폴더로 섞인다.
                        Send(stream, "200 OK", "application/json; charset=utf-8",
                            Encoding.UTF8.GetBytes("{\"ok\":true,\"folders\":1}"), "Cache-Control: no-store\r\n");
                        return;
                    }
                    string why = Files.MakeDir(p, QStr(query, "n"));
                    SendResult(stream, why);
                    return;
                }

                case "/fs/put":
                {
                    if (method != "POST") { SendResult(stream, "파일 전송은 POST 요청이 필요합니다."); return; }
                    string relative = QStr(query, "r");
                    if (relative.Length > 0)
                    {
                        string folder;
                        string error = Files.UploadDirectory(p, relative, out folder);
                        if (error.Length > 0) { SendResult(stream, error); return; }
                        p = folder;
                    }
                    long len = 0;
                    long.TryParse(Header(lines, "Content-Length") ?? "0", out len);

                    // 올리는 데도 오래 걸린다. 읽기 제한을 풀어 준다.
                    stream.ReadTimeout = 15000; // ping/ACK도 끊겼으면 눌린 입력을 무한히 남기지 않는다.

                    string name = QStr(query, "n");

                    // 보통은 multipart 로 온다 (웹에서 파일을 올리는 표준 모양).
                    // 본문에 날것으로 실어 보내는 방식도 계속 받아 준다 - 옛 화면이나
                    // curl 로 올릴 때 쓸 수 있다.
                    string bnd = Files.Boundary(Header(lines, "Content-Type"));

                    string why = string.IsNullOrEmpty(bnd)
                        ? Files.Receive(stream, preRead, len, p, name)
                        : Files.ReceiveMultipart(stream, preRead, len, p, name, bnd);

                    Notify(string.IsNullOrEmpty(why)
                        ? string.Format("파일 올림 - {0} ({1}) {2} → {3}", sess.User, ip, name, p)
                        : string.Format("파일 올리기 실패 - {0} ({1}) {2}: {3}", sess.User, ip, name, why));

                    SendResult(stream, why);
                    return;
                }
            }
        }

        /// <summary>본문을 끝까지 읽는다. 머리글을 읽을 때 딸려 온 몫을 먼저 채운다.</summary>
        private static string ReadBody(NetworkStream s, byte[] preRead, int len, int max)
        {
            if (len <= 0 || len > max) return "";

            byte[] b = new byte[len];
            int got = 0;

            if (preRead != null && preRead.Length > 0)
            {
                got = Math.Min(len, preRead.Length);
                Buffer.BlockCopy(preRead, 0, b, 0, got);
            }

            while (got < len)
            {
                int n;
                try { n = s.Read(b, got, len - got); }
                catch { break; }
                if (n <= 0) break;
                got += n;
            }

            return Encoding.UTF8.GetString(b, 0, got);
        }

        private static void SendResult(NetworkStream s, string why)
        {
            string json = string.IsNullOrEmpty(why)
                ? "{\"ok\":true}"
                : "{\"ok\":false,\"err\":\"" + Json.Escape(why) + "\"}";

            Send(s, "200 OK", "application/json; charset=utf-8",
                 Encoding.UTF8.GetBytes(json), "Cache-Control: no-store\r\n");
        }

        /// <summary>하위 PC 의 파일을 다루려면 그쪽 /fs/* 로 넘겨 준다.</summary>
        private void DoFsBridge(NetworkStream stream, string[] lines, Session sess, string ip,
                                int pc, string path, string query, string method, byte[] preRead)
        {
            Peer peer = Bridge.Find(pc);
            if (peer == null)
            {
                Send(stream, "404 Not Found", "application/json; charset=utf-8",
                     Encoding.UTF8.GetBytes("{\"err\":\"목록에 없는 PC 입니다.\"}"), "");
                return;
            }

            long len = 0;
            long.TryParse(Header(lines, "Content-Length") ?? "0", out len);

            // 파일이 오가는 동안은 제한을 풀어 둔다. 몇 분씩 걸린다.
            stream.ReadTimeout = 15000; // ping/ACK도 끊겼으면 눌린 입력을 무한히 남기지 않는다.
            stream.WriteTimeout = Timeout.Infinite;

            string why;
            if (!Bridge.Http(stream, peer, method, path, StripPc(query),
                             Header(lines, "Range"), Header(lines, "Content-Type"),
                             len, preRead, ip, out why))
            {
                Send(stream, "502 Bad Gateway", "application/json; charset=utf-8",
                     Encoding.UTF8.GetBytes("{\"err\":\"" + Json.Escape(why) + "\"}"), "");
            }
        }

        /// <summary>
        /// 하위 PC 에 넘길 때 pc= 는 떼어 낸다.
        /// 안 떼면 그쪽이 그걸 보고 또 자기 하위 PC 로 넘기려 든다 (1단만 지원한다).
        /// </summary>
        private static string StripPc(string query)
        {
            if (string.IsNullOrEmpty(query)) return "";

            StringBuilder sb = new StringBuilder();

            foreach (string pair in query.Split('&'))
            {
                int eq = pair.IndexOf('=');
                if (eq > 0 && pair.Substring(0, eq) == "pc") continue;
                if (pair.Length == 0) continue;

                if (sb.Length > 0) sb.Append('&');
                sb.Append(pair);
            }

            return sb.ToString();
        }

        private static string QStr(string query, string key)
        {
            if (string.IsNullOrEmpty(query)) return "";

            foreach (string pair in query.Split('&'))
            {
                int eq = pair.IndexOf('=');
                if (eq <= 0 || pair.Substring(0, eq) != key) continue;

                try { return Uri.UnescapeDataString(pair.Substring(eq + 1).Replace('+', ' ')); }
                catch { return ""; }
            }
            return "";
        }

        // ---------------------------------------------------------------- 브릿지

        /// <summary>고를 수 있는 PC 목록. 이 PC 가 늘 맨 앞이다.</summary>
        private static string PeerList()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("{\"self\":\"").Append(Json.Escape(Environment.MachineName)).Append("\",\"peers\":[");

            bool first = true;

            foreach (Peer p in Bridge.List())
            {
                if (!first) sb.Append(',');
                first = false;

                sb.Append("{\"id\":").Append(p.Id)
                  .Append(",\"name\":\"").Append(Json.Escape(p.Name))
                  .Append("\",\"addr\":\"").Append(Json.Escape(p.Addr))
                  // 비밀번호가 없으면 붙을 수 없다. 눌러 보고 실패하는 것보다
                  // 처음부터 못 누르게 하는 편이 낫다.
                  .Append("\",\"pw\":").Append(Bridge.HasPassword(p.Id) ? "true" : "false")
                  .Append('}');
            }

            sb.Append("]}");
            return sb.ToString();
        }

        /// <summary>
        /// 브라우저를 하위 PC 에 이어 준다.
        ///
        /// 실패하면 우리가 대신 핸드셰이크를 마치고 이유를 한 줄 보낸 뒤 끊는다.
        /// 그냥 끊어 버리면 브라우저는 "연결 실패" 만 알 뿐 왜인지 알 방법이 없다 -
        /// 하위 PC 의 기록은 원격에서 볼 수 없으니, 여기서 못 알려 주면 끝이다.
        ///
        /// 순서가 중요하다. Bridge.Relay 는 하위 PC 와 101 을 주고받기 전까지
        /// 브라우저 쪽에 한 글자도 쓰지 않는다. 그래서 실패했을 때 이쪽에서 처음부터
        /// 핸드셰이크를 할 수 있다. (반대로 먼저 핸드셰이크해 두면, 하위 PC 가 보낸
        /// 101 을 그대로 흘려보낼 수 없어 프레임을 뜯어 고쳐야 한다)
        /// </summary>
        private void DoBridge(NetworkStream stream, string[] lines, Session sess, string ip,
                              int pc, byte[] preRead)
        {
            string key = Header(lines, "Sec-WebSocket-Key");
            if (string.IsNullOrEmpty(key)) return;

            Peer p = Bridge.Find(pc);
            if (p == null) { Refuse(stream, key, "그 번호(" + pc + ")의 PC 가 목록에 없습니다."); return; }

            stream.ReadTimeout = 15000; // ping/ACK도 끊겼으면 눌린 입력을 무한히 남기지 않는다.      // 화면이 안 바뀌면 한참 조용하다

            Interlocked.Increment(ref Relays);
            Notify(string.Format("브릿지 연결 - {0} → {1} ({2})", sess.User, p.Name, ip));

            try
            {
                string why;
                if (!Bridge.Relay(stream, p, key, preRead, ip, out why))
                    Refuse(stream, key, why);
            }
            catch { }
            finally
            {
                Interlocked.Decrement(ref Relays);
                Notify(string.Format("브릿지 연결 종료 - {0} → {1} ({2})", sess.User, p.Name, ip));
            }
        }

        /// <summary>핸드셰이크만 해 주고 이유를 알린 뒤 끊는다.</summary>
        private static void Refuse(NetworkStream stream, string key, string why)
        {
            try
            {
                byte[] hs = Encoding.ASCII.GetBytes(WebSocketConn.BuildHandshake(key));
                stream.Write(hs, 0, hs.Length);
                stream.Flush();

                WebSocketConn ws = new WebSocketConn(stream);
                ws.SendText("{\"t\":\"fail\",\"s\":\"" + Json.Escape(why ?? "붙지 못했습니다.") + "\"}");

                // 바로 닫으면 마지막 프레임이 밀려 나가기 전에 소켓이 끊길 수 있다.
                Thread.Sleep(120);
                ws.Close();
            }
            catch { }
        }

        private static int QInt(string query, string key, int def)
        {
            if (string.IsNullOrEmpty(query)) return def;

            foreach (string pair in query.Split('&'))
            {
                int eq = pair.IndexOf('=');
                if (eq <= 0 || pair.Substring(0, eq) != key) continue;

                int v;
                return int.TryParse(pair.Substring(eq + 1), out v) ? v : def;
            }
            return def;
        }

        // ---------------------------------------------------------------- WebSocket

        private void DoWebSocket(NetworkStream stream, string[] lines, Session sess, string ip)
        {
            string key = Header(lines, "Sec-WebSocket-Key");
            if (string.IsNullOrEmpty(key)) return;

            byte[] hs = Encoding.ASCII.GetBytes(WebSocketConn.BuildHandshake(key));
            stream.Write(hs, 0, hs.Length);
            stream.Flush();

            // 조작 명령을 기다리느라 막히면 안 된다. 읽기 제한을 없애고
            // 살아 있는지는 클라이언트가 보내는 ping 으로 판단한다.
            stream.ReadTimeout = 15000; // ping/ACK도 끊겼으면 눌린 입력을 무한히 남기지 않는다.

            WebSocketConn ws = new WebSocketConn(stream);
            Interlocked.Increment(ref Viewers);
            Notify(string.Format("화면 연결 - {0} ({1})", sess.User, ip));

            int screenIndex = 0;
            InputInjector input = new InputInjector();
            StreamCommands output = new StreamCommands();

            // 브라우저가 보낸 이동 명령 번호. 화면을 보내는 쪽과 명령을 받는 쪽이
            // 다른 스레드라 한 칸짜리 배열로 주고받는다.
            //
            // 이게 왜 필요한가.
            //   커서 위치를 브라우저에도 알려 주는데, 그 값은 회선을 건너오는 동안
            //   이미 과거가 된다. 손가락을 빠르게 밀면 브라우저 쪽이 늘 앞서 있어서,
            //   그 과거 값을 그대로 받아들이면 커서가 계속 뒤로 끌려간다.
            //   실제로 "커서가 제자리로 돌아가서 제어가 안 된다" 는 증상이 났다.
            //   그래서 "네가 마지막으로 보낸 명령까지 반영했다" 는 번호를 같이 준다.
            //   브라우저는 번호가 최신일 때만 그 위치를 받아들인다.
            int[] moveSeq = new int[1];
            ScreenSource src = null;
            Thread sender = null;
            bool running = true;

            try
            {
                src = new ScreenSource(ScreenBounds(screenIndex));
                SendInit(ws, src, screenIndex, output);

                ScreenSource srcRef = src;

                // 보내는 쪽은 따로 돈다. 받는 쪽(조작)이 화면 전송에 막히면
                // 클릭이 늦게 들어가 "정확하지 않다" 는 느낌을 준다.
                sender = new Thread(delegate ()
                {
                    try { StreamLoop(ws, ref running, delegate () { return srcRef; }, moveSeq, output); }
                    catch { }
                    finally { running = false; input.Dispose(); ws.Abort(); }
                }, 1024 * 1024);
                sender.IsBackground = true;
                sender.Start();

                // 받는 쪽: 조작 명령
                while (running && !ws.Closed)
                {
                    WsMessage m = ws.Receive();
                    if (m == null) break;
                    if (m.Op != WsOp.Text) continue;

                    var o = Json.Parse(m.Text);
                    string t = Json.Str(o, "t");

                    InputViewport map = output.Mapping;
                    bool staleView = Json.Int(o, "view", map.View) != map.View;
                    if (staleView && (t == "move" || t == "wheel" || (t == "btn" && Json.Bool(o, "d", true)))) continue;

                    switch (t)
                    {
                        case "ack":
                            output.Flow.Ack(Json.Int(o, "n", 0));
                            break;
                        case "probeack":
                            output.Flow.ProbeAck(Json.Int(o, "n", 0));
                            break;
                        case "stream":
                            bool automatic = Json.Bool(o, "auto", true);
                            int maxTile = Json.Int(o, "tile", 128) == 1024 ? 1024 : 128;
                            bool probe = Json.Bool(o, "probe", false);
                            output.Post(delegate {
                                output.AutoQuality = automatic; output.MaxTileSize = maxTile;
                                if (probe) ws.SendText("{\"t\":\"probe\",\"n\":" + output.Flow.Probe() + "}");
                                output.Flow.Enable(); // 설정/RTT 요청보다 화면 전송이 먼저 시작되는 경합을 막는다.
                            });
                            break;
                        case "release":
                            input.Release();
                            break;
                        case "viewpause":
                            bool pause = Json.Bool(o, "v", false);
                            output.Post(delegate { output.Paused = pause; if (!pause) src.InvalidateAll(); });
                            break;
                        // 브라우저가 보내는 좌표는 "받은 그림" 기준이다.
                        // 축소해서 보내는 중이면 그만큼 되돌려야 실제 화면 좌표가 된다.
                        case "move":
                            InputInjector.MoveTo(map.X(Json.Int(o, "x", 0)),
                                                 map.Y(Json.Int(o, "y", 0)));
                            System.Threading.Interlocked.Exchange(ref moveSeq[0], Json.Int(o, "n", 0));
                            break;

                        case "btn":
                            input.Button(map.X(Json.Int(o, "x", 0)),
                                                 map.Y(Json.Int(o, "y", 0)),
                                                 Json.Int(o, "b", 0), Json.Bool(o, "d", true));
                            break;

                        case "wheel":
                            InputInjector.Wheel(map.X(Json.Int(o, "x", 0)),
                                                map.Y(Json.Int(o, "y", 0)),
                                                Json.Int(o, "d", 0), Json.Bool(o, "h", false));
                            break;

                        case "scale":
                            int scale = Json.Int(o, "s", 100);
                            output.Post(delegate {
                                src.ScalePercent = scale; output.Tiles.Clear();
                                output.View++; SendInit(ws, src, screenIndex, output);
                            });
                            break;

                        // 브라우저가 확대·축소하는 동안에는 받은 조각을 버린다.
                        // 끝나면 이걸 보내온다. 전부 다시 보내 한 번에 새로 그리게 한다.
                        case "refresh":
                            output.Post(delegate { src.InvalidateAll(); });
                            break;

                        // 원격에서 이 PC 의 화면 크기를 바꾼다.
                        // 모니터 없는 PC 는 이게 없으면 작은 화면에 갇힌다.
                        case "screen":
                        {
                            int nw = Json.Int(o, "w", 0), nh = Json.Int(o, "h", 0);
                            if (nw <= 0 || nh <= 0) break;

                            output.Post(delegate {
                            // 기억해 둔다. 원격 데스크톱을 끊는 등으로 크기가 무너져도
                            // 대리 프로세스가 이 값으로 되돌린다.
                            Config.Set("screenw", nw.ToString());
                            Config.Set("screenh", nh.ToString());

                            string why;

                            if (Display.Apply(nw, nh, out why))
                            {
                                Notify("화면 크기를 " + nw + "x" + nh + " 로 바꿨습니다 (원격 요청).");

                                // 크기가 바뀌면 잡을 영역도 달라진다. 다시 잡고 알려 준다.
                                Thread.Sleep(900);      // 윈도우가 새 크기를 적용할 틈
                                src.SetArea(ScreenBounds(screenIndex));
                                output.Tiles.Clear();
                                output.View++; SendInit(ws, src, screenIndex, output);
                            }
                            else
                            {
                                Notify("화면 크기 " + nw + "x" + nh + " 실패: " + why);

                                // 브라우저에도 이유를 알려 준다. 원격에서는 기록을 볼 수 없다.
                                // 이 PC 가 실제로 받아 주는 목록도 같이 보낸다 - 그걸 봐야
                                // "드라이버 한계라 소프트웨어로는 못 늘린다" 는 걸 알 수 있다.
                                string info = nw + "x" + nh + " 로 바꾸지 못했습니다.<br>" + why;

                                try { info += "<br><br>이 PC 가 받아 주는 크기<br>" + Display.Report(); }
                                catch { }

                                if (Display.LooksHeadless())
                                    info += "<br><br>모니터가 연결되어 있지 않아 윈도우가 기본 화면 장치로 " +
                                            "잡고 있습니다. 이 상태에서는 소프트웨어로 크기를 늘릴 수 없습니다. " +
                                            "HDMI 더미 플러그를 꽂거나 가상 디스플레이 드라이버를 설치해야 합니다.";

                                ws.SendText("{\"t\":\"note\",\"s\":\"" + Json.Escape(info) + "\"}");
                            }

                            });

                            break;
                        }

                        case "key":
                            input.Key((ushort)Json.Int(o, "vk", 0), Json.Bool(o, "d", true));
                            break;

                        case "text":
                            InputInjector.Text(Json.Str(o, "s"));
                            break;

                        case "quality":
                            int quality = Json.Int(o, "q", 70);
                            output.Post(delegate { output.AutoQuality = quality == 0; src.Quality = quality == 0 ? 70 : quality; });
                            break;

                        case "nextScreen":
                            output.Post(delegate {
                                screenIndex = (screenIndex + 1) % Math.Max(1, Screen.AllScreens.Length);
                                src.SetArea(ScreenBounds(screenIndex)); output.Tiles.Clear();
                                output.View++; SendInit(ws, src, screenIndex, output);
                            });
                            break;

                        // 원격 PC 의 클립보드를 가져온다.
                        // 화면으로는 글자를 옮길 수 없다 - 눈으로 읽어 손으로 다시 치는 수밖에
                        // 없었다. 긴 주소나 로그를 옮길 때 이게 제일 답답한 부분이었다.
                        case "clipget":
                        {
                            output.Post(delegate {
                            string got = Files.ClipGet();
                            ws.SendText("{\"t\":\"clip\",\"s\":\"" + Json.Escape(got) + "\"}");
                            Notify(string.Format("클립보드 가져감 - {0} ({1}) {2}자", sess.User, ip, got.Length));
                            });
                            break;
                        }

                        // 이쪽 클립보드를 원격 PC 에 넣는다.
                        case "clipset":
                        {
                            string s = Json.Str(o, "s");
                            output.Post(delegate {
                            bool ok = Files.ClipSet(s);

                            ws.SendText("{\"t\":\"clipok\",\"v\":" + (ok ? "1" : "0") +
                                        ",\"n\":" + s.Length + "}");

                            Notify(ok
                                ? string.Format("클립보드 넣음 - {0} ({1}) {2}자", sess.User, ip, s.Length)
                                : string.Format("클립보드 넣기 실패 - {0} ({1})", sess.User, ip));
                            });
                            break;
                        }

                        case "ping":
                            string pong = "{\"t\":\"pong\",\"c\":" +
                                Json.Num(o, "c", 0).ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "}";
                            output.Post(delegate { ws.SendText(pong); });
                            break;
                    }
                }
            }
            catch { }
            finally
            {
                running = false;
                input.Dispose(); // 네트워크 정리를 기다리기 전에 이 접속이 누른 입력부터 해제한다.
                ws.Abort();
                if (sender != null) { try { sender.Join(500); } catch { } }
                if (src != null) src.Dispose();

                Interlocked.Decrement(ref Viewers);
                Notify(string.Format("화면 연결 종료 - {0} ({1})", sess.User, ip));
            }
        }

        private static Rectangle ScreenBounds(int i)
        {
            Screen[] all = Screen.AllScreens;
            if (all.Length == 0) return new Rectangle(0, 0, 1024, 768);
            return all[Math.Max(0, Math.Min(all.Length - 1, i))].Bounds;
        }

        private static void SendInit(WebSocketConn ws, ScreenSource src, int screenIndex, StreamCommands output)
        {
            output.Mapping = new InputViewport(src.Area, src.FrameWidth, src.FrameHeight, output.View);
            // 이 PC 가 받아 주는 화면 크기 목록도 같이 보낸다.
            // 모니터 없는 PC 는 원격에서 크기를 못 고치면 작은 화면으로 갇힌다.
            StringBuilder sizes = new StringBuilder("[");

            try
            {
                bool first = true;
                foreach (System.Drawing.Size s in Display.Choices())
                {
                    if (!first) sizes.Append(',');
                    sizes.Append('[').Append(s.Width).Append(',').Append(s.Height).Append(']');
                    first = false;
                }
            }
            catch { }

            sizes.Append(']');

            System.Drawing.Size cur = Display.Current();

            ws.SendText(string.Format(
                "{{\"t\":\"init\",\"w\":{0},\"h\":{1},\"cols\":{2},\"rows\":{3},\"screen\":{4},\"screens\":{5}," +
                "\"scale\":{6},\"way\":\"{7}\",\"sizes\":{8},\"sw\":{9},\"sh\":{10},\"flow\":1,\"view\":{11},\"tile\":{12},\"tilemax\":1024}}",
                src.FrameWidth, src.FrameHeight, src.Cols, src.Rows,
                screenIndex, Screen.AllScreens.Length, src.ScalePercent, src.Method,
                sizes, cur.Width, cur.Height, output.View, src.TileSize));
        }

        /// <summary>
        /// 화면을 계속 잡아 바뀐 타일만 보낸다.
        ///
        /// 최대 두 묶음의 디코딩 ACK로 크기를 조절한다. 캡처 주기와 남은 타일 전송을 분리한다.
        /// 안 보낸 타일은 최신 내용으로 교체하고, ACK를 기다리는 동안 캡처/압축도 쉰다.
        /// </summary>
        private static void StreamLoop(WebSocketConn ws, ref bool running, Func<ScreenSource> get, int[] moveSeq, StreamCommands output)
        {
            // 캡처는 16ms 간격이지만 전송은 ACK 즉시 재개한다. 둘을 같이 재우면
            // 빠른 LAN에서도 4K 화면의 다음 두 묶음을 보낼 때마다 16ms가 낭비된다.
            var schedule = new FrameSchedule();
            int qualityChangedAt = Environment.TickCount;
            int lastMotionAt = Environment.TickCount;
            int tileChangedAt = Environment.TickCount - 3000;

            int lastCurX = -99999, lastCurY = -99999, lastShape = 0;
            bool lastVis = false;
            string lastCaptureMethod = null;

            while (running && !ws.Closed)
            {
                int change = output.Flow.Change;

                output.Drain();
                if (output.Flow.TimedOut) break;
                if (!output.Flow.Ready || output.Paused || !output.Flow.CanSend) { output.Flow.Wait(change, 16); continue; }
                ScreenSource src = get();

                // 준비 중인 화면을 16ms마다 다시 압축하면 4K에서는 남은 타일이 굶는다.
                // 빠른 망은 먼저 다 보내고, 작은 묶음은 바로 최신 내용으로 바꾼다.
                // 큰 묶음도 100ms가 지나면 최신 화면으로 교체한다.
                if (schedule.ShouldCapture(output.Tiles.Count, output.Flow.Budget))
                {
                    schedule.StartCapture();
                    int tileSize = src.TileSize;
                    bool largeScreen = (long)src.FrameWidth * src.FrameHeight >= 3840L * 2160;
                    if (output.MaxTileSize < 1024 || !largeScreen || output.Flow.QueueLatency > 160 || output.Flow.Budget < 64 * 1024)
                        tileSize = 128;
                    else if (output.Flow.Budget >= 128 * 1024 && output.Flow.QueueLatency < 100 && Environment.TickCount - tileChangedAt >= 3000)
                        tileSize = 1024;
                    if (tileSize != src.TileSize)
                    {
                        src.TileSize = tileSize; output.Tiles.Clear(); output.View++;
                        // PC/배율은 그대로지만 타일 좌표계가 달라졌다. 브라우저도 원자적으로 비운다.
                        output.Mapping = new InputViewport(src.Area, src.FrameWidth, src.FrameHeight, output.View);
                        ws.SendText("{\"t\":\"tiles\",\"tile\":" + tileSize + ",\"cols\":" + src.Cols + ",\"rows\":" + src.Rows + ",\"view\":" + output.View + "}");
                        tileChangedAt = Environment.TickCount;
                    }
                    // 회선/디코딩이 밀리고 바뀔 조각이 많이 남아 있을 때만 화질을 줄인다.
                    // 움직임이 멎으면 다시 선명하게 보내 문서 글자가 낮은 화질로 남지 않게 한다.
                    if (output.Flow.Enabled && output.AutoQuality && Environment.TickCount - qualityChangedAt > 1500)
                    {
                        int q = output.Tiles.Count > 8 && Environment.TickCount - lastMotionAt < 1200
                            ? (output.Flow.Latency > 250 ? 40 : output.Flow.Latency > 130 ? 55 : 70)
                            : (Environment.TickCount - lastMotionAt > 1200 ? 70 : (int)src.Quality);
                        if (src.Quality != q) { src.Quality = q; output.Tiles.Clear(); qualityChangedAt = Environment.TickCount; }
                    }
                    List<TilePatch> patches;
                    try { patches = src.Grab(); }
                    catch { break; }
                    if (src.Method != lastCaptureMethod)
                    {
                        lastCaptureMethod = src.Method;
                        ws.SendText("{\"t\":\"capture\",\"way\":\"" + lastCaptureMethod + "\"}");
                    }
                    if (patches.Count > 0 && !src.LastGrabWasFull) lastMotionAt = Environment.TickCount;
                    output.Tiles.Merge(patches);

                    // 커서는 화면과 따로 보낸다.
                    // 예전에는 화면에 그려 넣어서, 마우스만 흔들어도 그 자리 타일이 계속
                    // 바뀐 것으로 잡혀 프레임마다 다시 인코딩하고 보냈다.
                    try
                    {
                        // 번호를 위치보다 먼저 읽는다.
                        // 반대로 하면 "옛 위치 + 최신 번호" 가 나갈 수 있고, 브라우저는 그걸
                        // 따라잡은 값으로 믿고 커서를 뒤로 옮긴다. 이 순서면 최악이라야
                        // "최신 위치 + 옛 번호" 라서, 브라우저가 그냥 무시하고 넘어간다.
                        int seqNow = System.Threading.Interlocked.CompareExchange(ref moveSeq[0], 0, 0);

                        CursorState cs = src.GetCursor();

                        if (cs.Png != null)
                        {
                            // 모양이 바뀌었을 때만 그림을 보낸다. 0xFFFF 는 "타일이 아니라 커서" 표시.
                            MemoryStream cm = new MemoryStream(cs.Png.Length + 16);
                            cm.WriteByte(0xFF); cm.WriteByte(0xFF);

                            int cn = cs.Png.Length;
                            cm.WriteByte((byte)(cn & 0xFF));
                            cm.WriteByte((byte)((cn >> 8) & 0xFF));
                            cm.WriteByte((byte)((cn >> 16) & 0xFF));
                            cm.WriteByte((byte)((cn >> 24) & 0xFF));

                            cm.WriteByte((byte)(cs.HotX & 0xFF)); cm.WriteByte((byte)((cs.HotX >> 8) & 0xFF));
                            cm.WriteByte((byte)(cs.HotY & 0xFF)); cm.WriteByte((byte)((cs.HotY >> 8) & 0xFF));

                            cm.Write(cs.Png, 0, cn);
                            ws.SendBinary(cm.ToArray());

                            lastShape = cs.ShapeId;
                        }

                        if (cs.X != lastCurX || cs.Y != lastCurY || cs.Visible != lastVis)
                        {
                            lastCurX = cs.X; lastCurY = cs.Y; lastVis = cs.Visible;

                            // n = 여기까지 반영한 이동 명령 번호.
                            // 브라우저는 이 번호가 자기가 마지막으로 보낸 것과 같을 때만
                            // 이 좌표를 받아들인다. 아니면 자기가 아는 위치가 더 최신이다.
                            ws.SendText("{\"t\":\"cur\",\"x\":" + cs.X + ",\"y\":" + cs.Y +
                                        ",\"v\":" + (cs.Visible ? 1 : 0) +
                                        ",\"n\":" + seqNow + "}");
                        }
                    }
                    catch { }
                }

                while (output.Tiles.Count > 0 && output.Flow.CanSend && !ws.Closed)
                {
                    byte[] packet = output.Tiles.Take(output.Flow.Budget);
                    int number = output.Flow.Begin(packet.Length);
                    ws.SendBinary(packet);
                    if (number != 0) ws.SendText("{\"t\":\"frame\",\"n\":" + number + "}");
                    if (schedule.CaptureAge >= 100) break; // ACK가 빨라도 제어 응답과 최신 화면을 굶기지 않는다.
                }

                if (output.Tiles.Count > 0 && output.Flow.CanSend) continue;
                output.Flow.Wait(change, schedule.WaitMilliseconds);
            }
        }

        // ---------------------------------------------------------------- 도우미

        private void Notify(string s)
        {
            Action<string> h = Notice;
            if (h != null) { try { h(s); } catch { } }
        }

        /// <summary>
        /// 머리글을 끝(빈 줄)까지 읽는다.
        ///
        /// 한 번의 읽기로 머리글과 본문이 같이 딸려 오는 경우가 흔하다.
        /// 그때 딸려 온 본문을 버리면, 나중에 본문을 읽으려 할 때 소켓에는 아무것도
        /// 없어서 시간 초과까지 멈춰 버린다. 로그인 POST 가 응답하지 않던 원인이었다.
        /// 그래서 남은 바이트를 함께 돌려준다.
        /// </summary>
        internal static string ReadHead(NetworkStream s, out byte[] leftover)
        {
            leftover = new byte[0];

            byte[] buf = new byte[16384];
            int filled = 0;

            while (filled < buf.Length)
            {
                int n;
                try { n = s.Read(buf, filled, buf.Length - filled); }
                catch { break; }
                if (n <= 0) break;

                int from = Math.Max(3, filled);
                filled += n;

                for (int i = from; i < filled; i++)
                {
                    if (buf[i] == '\n' && buf[i - 1] == '\r' && buf[i - 2] == '\n' && buf[i - 3] == '\r')
                    {
                        int headLen = i + 1;
                        int rest = filled - headLen;
                        if (rest > 0)
                        {
                            leftover = new byte[rest];
                            Buffer.BlockCopy(buf, headLen, leftover, 0, rest);
                        }
                        return Encoding.UTF8.GetString(buf, 0, headLen);
                    }
                }
            }
            return filled > 0 ? Encoding.UTF8.GetString(buf, 0, filled) : "";
        }

        internal static string Header(string[] lines, string name)
        {
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

        private static string ClientIp(TcpClient c, string[] lines)
        {
            string xff = Header(lines, "X-Forwarded-For");
            if (!string.IsNullOrEmpty(xff))
            {
                string first = xff.Split(',')[0].Trim();
                if (first.Length > 0) return first;
            }

            try
            {
                IPEndPoint ep = c.Client.RemoteEndPoint as IPEndPoint;
                if (ep != null)
                {
                    string s = ep.Address.ToString();
                    return s.StartsWith("::ffff:") ? s.Substring(7) : s;
                }
            }
            catch { }
            return "-";
        }

        /// <summary>
        /// 아이콘. 이건 캐시해도 된다 - 아이콘은 판이 바뀌어도 거의 그대로고,
        /// 휴대폰이 홈 화면 그림을 그릴 때마다 다시 받아 가면 낭비다.
        /// </summary>
        private static void SendIcon(NetworkStream s, byte[] png)
        {
            if (png == null || png.Length == 0)
            {
                Send(s, "404 Not Found", "text/plain", Encoding.UTF8.GetBytes("no icon"), "");
                return;
            }

            Send(s, "200 OK", "image/png", png, "Cache-Control: public, max-age=86400\r\n");
        }

        private static void SendPage(NetworkStream s, string html)
        {
            Send(s, "200 OK", "text/html; charset=utf-8", Encoding.UTF8.GetBytes(html),
                 "Cache-Control: no-store\r\nX-Frame-Options: DENY\r\n");
        }

        internal static void Send(NetworkStream s, string status, string type, byte[] body, string extra)
        {
            string h = "HTTP/1.1 " + status + "\r\n" +
                       "Content-Type: " + type + "\r\n" +
                       "Content-Length: " + body.Length + "\r\n" +
                       (extra ?? "") +
                       "Connection: close\r\n\r\n";

            byte[] hb = Encoding.UTF8.GetBytes(h);
            try
            {
                s.Write(hb, 0, hb.Length);
                if (body.Length > 0) s.Write(body, 0, body.Length);
                s.Flush();
            }
            catch { }
        }

        /// <summary>exe 안에 넣어 둔 웹 파일을 읽는다.</summary>
        private static readonly Dictionary<string, string> resCache = new Dictionary<string, string>();

        internal static string Res(string name)
        {
            lock (resCache)
            {
                string v;
                if (resCache.TryGetValue(name, out v)) return v;

                using (Stream st = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
                {
                    if (st == null) { resCache[name] = ""; return ""; }

                    using (StreamReader r = new StreamReader(st, Encoding.UTF8))
                    {
                        v = r.ReadToEnd();
                        resCache[name] = v;
                        return v;
                    }
                }
            }
        }
    }
}

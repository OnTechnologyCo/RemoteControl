// 브릿지 - 이 PC 를 문으로 삼아 같은 랜 안의 다른 PC 를 제어한다.
//
// 왜 필요한가.
//   하위 PC 는 사내망 안에만 있어 밖에서 직접 닿지 않는다. PC 마다 터널을 열어 줄 수는
//   있지만 그러면 PC 수만큼 공개 주소가 생긴다. 원격제어에서 밖으로 난 문이 늘어나는 것은
//   그 자체가 위험이다. 문은 한 개만 두고 나머지는 그 뒤에 숨긴다.
//
// 어떻게 하는가.
//   브라우저는 브릿지 PC 의 화면(viewer)을 그대로 받는다. 달라지는 것은 /ws 하나뿐이다.
//   /ws?pc=N 이 오면 브릿지가 하위 PC 에 대신 로그인해서 그쪽 /ws 를 열고,
//   그 다음부터는 두 소켓 사이로 바이트를 그대로 밀기만 한다.
//
//   프레임을 뜯어보지 않는다. 이게 핵심이다 - WebSocket 은 클라이언트→서버 방향만
//   마스킹한다. 손대지 않고 흘려보내면 브라우저가 만든 마스킹 프레임이 하위 PC 에
//   그대로 닿고(하위 PC 가 보기엔 멀쩡한 클라이언트), 하위 PC 가 보낸 비마스킹 프레임이
//   브라우저에 그대로 닿는다(브라우저가 보기엔 멀쩡한 서버). 양쪽 다 규격에 맞는다.
//   그래서 중계는 화면 프로토콜을 하나도 몰라도 되고, 프로토콜이 바뀌어도 이 파일은
//   손댈 일이 없다. 뜯어 고쳐 다시 조립하는 방식이었다면 판이 어긋날 때마다 깨졌다.
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace RemoteControl
{
    internal sealed class Peer
    {
        internal int Id;
        internal string Name = "";
        internal string Host = "";
        internal int Port = 5900;
        internal string User = "";

        internal string Addr { get { return Host + ":" + Port; } }
    }

    internal static class Bridge
    {
        private const int MaxPeers = 64;

        // ---------------------------------------------------------------- 목록

        internal static List<Peer> List()
        {
            List<Peer> r = new List<Peer>();

            int n;
            if (!int.TryParse(Config.Get("bridgen"), out n)) n = 0;
            if (n > MaxPeers) n = MaxPeers;

            for (int i = 1; i <= n; i++)
            {
                Peer p = Read(i);
                if (p != null) r.Add(p);
            }
            return r;
        }

        internal static Peer Find(int id)
        {
            if (id <= 0 || id > MaxPeers) return null;
            return Read(id);
        }

        private static Peer Read(int id)
        {
            string v = Config.Get("bridge" + id);
            if (string.IsNullOrEmpty(v)) return null;

            string[] f = v.Split('|');
            if (f.Length < 4) return null;

            int port;
            if (!int.TryParse(f[2], out port) || port <= 0 || port > 65535) return null;

            Peer p = new Peer();
            p.Id = id;
            p.Name = Un(f[0]);
            p.Host = Un(f[1]);
            p.Port = port;
            p.User = Un(f[3]);
            return p;
        }

        /// <summary>
        /// 목록을 통째로 다시 쓴다.
        ///
        /// 지워진 번호는 비워만 두고 다시 쓰지 않는다. 번호를 재사용하면 `/?pc=3` 을
        /// 즐겨찾기해 둔 사람이 어느 날 전혀 다른 PC 에 붙는다. 원격제어에서 그건
        /// 남의 PC 를 조작하게 되는 사고다.
        /// </summary>
        internal static void Save(List<Peer> peers)
        {
            int n;
            if (!int.TryParse(Config.Get("bridgen"), out n)) n = 0;

            Dictionary<int, Peer> byId = new Dictionary<int, Peer>();

            foreach (Peer p in peers)
            {
                if (p.Id <= 0)
                {
                    if (n >= MaxPeers) continue;
                    n++;
                    p.Id = n;
                }
                byId[p.Id] = p;
            }

            for (int i = 1; i <= n; i++)
            {
                Peer p;
                if (byId.TryGetValue(i, out p))
                {
                    Config.Set("bridge" + i,
                        Esc(p.Name) + "|" + Esc(p.Host) + "|" + p.Port + "|" + Esc(p.User));
                }
                else if (!string.IsNullOrEmpty(Config.Get("bridge" + i)))
                {
                    Config.Set("bridge" + i, "");
                    Config.Set("bridge" + i + "p", "");
                    Config.Set("bridge" + i + "pm", "");
                }
            }

            Config.Set("bridgen", n.ToString());
        }

        // 이름에 | 나 = 가 들어가면 설정 한 줄이 통째로 깨진다. 이름과 아이디는 감싸 둔다.
        private static string Esc(string s) { try { return Uri.EscapeDataString(s ?? ""); } catch { return ""; } }
        private static string Un(string s) { try { return Uri.UnescapeDataString(s ?? ""); } catch { return s ?? ""; } }

        // ---------------------------------------------------------------- 비밀번호

        /// <summary>
        /// 두 벌로 봉인한다. 하위 PC 에 실제로 로그인하는 것은 대리 프로세스(SYSTEM)라
        /// 내 계정으로 잠근 것은 풀지 못한다. tskey 와 똑같은 이유다 (Tunnel.cs 참고).
        /// </summary>
        internal static void SetPassword(int id, string plain)
        {
            if (id <= 0) return;

            Config.Set("bridge" + id + "p", Seal(plain, DataProtectionScope.CurrentUser));
            Config.Set("bridge" + id + "pm", Seal(plain, DataProtectionScope.LocalMachine));
        }

        internal static bool HasPassword(int id)
        {
            return id > 0 && (!string.IsNullOrEmpty(Config.Get("bridge" + id + "p")) ||
                              !string.IsNullOrEmpty(Config.Get("bridge" + id + "pm")));
        }

        internal static string GetPassword(int id)
        {
            string s = Open(Config.Get("bridge" + id + "p"), DataProtectionScope.CurrentUser);
            if (!string.IsNullOrEmpty(s)) return s;

            return Open(Config.Get("bridge" + id + "pm"), DataProtectionScope.LocalMachine);
        }

        // Tunnel.cs 에 같은 것이 있다. 그쪽은 터널 키 전용이고 가장 손대기 조심스러운
        // 파일이라, 공용으로 끌어내는 대신 여기에 따로 뒀다.
        private static string Seal(string v, DataProtectionScope scope)
        {
            if (string.IsNullOrEmpty(v)) return "";

            try { return Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(v), null, scope)); }
            catch { return ""; }
        }

        private static string Open(string enc, DataProtectionScope scope)
        {
            if (string.IsNullOrEmpty(enc)) return "";

            try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(enc), null, scope)); }
            catch { return ""; }
        }

        // ---------------------------------------------------------------- 하위 PC 에 로그인

        /// <summary>하위 PC 에 대신 로그인해 세션 토큰을 받아 온다. 실패하면 "" 와 이유.</summary>
        private static string Login(Peer p, string userIp, out string why)
        {
            why = "";

            string pass = GetPassword(p.Id);
            if (string.IsNullOrEmpty(pass))
            {
                why = "저장된 비밀번호가 없습니다. [하위 PC] 창에서 다시 넣어 주세요.";
                return "";
            }

            TcpClient c = Dial(p.Host, p.Port, 5000, out why);
            if (c == null) return "";

            using (c)
            {
                try
                {
                    NetworkStream s = c.GetStream();
                    s.ReadTimeout = 8000;
                    s.WriteTimeout = 8000;

                    byte[] body = Encoding.UTF8.GetBytes(
                        "u=" + Uri.EscapeDataString(p.User) + "&p=" + Uri.EscapeDataString(pass));

                    byte[] head = Encoding.UTF8.GetBytes(
                        "POST /login HTTP/1.1\r\n" +
                        "Host: " + p.Host + ":" + p.Port + "\r\n" +
                        "Content-Type: application/x-www-form-urlencoded\r\n" +
                        "Content-Length: " + body.Length + "\r\n" +
                        Fwd(userIp) +
                        "Connection: close\r\n\r\n");

                    s.Write(head, 0, head.Length);
                    s.Write(body, 0, body.Length);
                    s.Flush();

                    byte[] rest;
                    string resp = ReadHead(s, out rest);

                    string tok = Cookie(resp);
                    if (!string.IsNullOrEmpty(tok)) return tok;

                    // 왜 안 됐는지는 돌려받은 화면에 적혀 있다. 그대로 사람에게 옮긴다.
                    // 원격에서는 하위 PC 의 기록을 볼 수 없으니 여기서 못 알려 주면 끝이다.
                    string page = ReadRest(s, rest, 16384);

                    if (page.IndexOf("시도가 너무 많습니다", StringComparison.Ordinal) >= 0)
                        why = p.Addr + " 가 로그인 시도를 잠시 막고 있습니다. 몇 분 뒤에 다시 해 보세요.";
                    else if (page.IndexOf("맞지 않습니다", StringComparison.Ordinal) >= 0)
                        why = p.Addr + " 의 아이디 또는 비밀번호가 맞지 않습니다.";
                    else if (resp.IndexOf(" 404 ", StringComparison.Ordinal) >= 0)
                        why = p.Addr + " 는 원격제어가 아닙니다.";
                    else
                        why = p.Addr + " 에 로그인하지 못했습니다.";

                    return "";
                }
                catch (Exception ex)
                {
                    why = p.Addr + " 와 이야기하지 못했습니다 (" + ex.Message + ")";
                    return "";
                }
            }
        }

        /// <summary>
        /// 하위 PC 가 남기는 기록에 브릿지 IP 가 아니라 실제로 조작하는 사람의 IP 가
        /// 찍히게 한다. 안 그러면 하위 PC 기록에 늘 같은 랜 주소만 남아, 누가 들어왔는지
        /// 알 수 없다. 하위 PC 의 Server.ClientIp() 가 이 머리글을 먼저 본다.
        /// </summary>
        private static string Fwd(string userIp)
        {
            if (string.IsNullOrEmpty(userIp) || userIp == "-") return "";

            foreach (char ch in userIp)
                if (!char.IsLetterOrDigit(ch) && ch != '.' && ch != ':') return "";   // 머리글 주입 방지

            return "X-Forwarded-For: " + userIp + "\r\n";
        }

        private static string Cookie(string resp)
        {
            foreach (string line in resp.Split(new string[] { "\r\n" }, StringSplitOptions.None))
            {
                if (!line.StartsWith("Set-Cookie:", StringComparison.OrdinalIgnoreCase)) continue;

                int a = line.IndexOf("rcsid=", StringComparison.OrdinalIgnoreCase);
                if (a < 0) continue;

                a += 6;
                int b = line.IndexOf(';', a);
                string v = (b < 0 ? line.Substring(a) : line.Substring(a, b - a)).Trim();
                if (v.Length > 0) return v;
            }
            return "";
        }

        /// <summary>
        /// 하위 PC 에 열어 둔 세션을 접는다.
        /// 안 하면 붙었다 끊길 때마다 그쪽에 세션이 하나씩 쌓인다. 하나가 8시간을 산다.
        /// </summary>
        private static void Logout(Peer p, string token)
        {
            if (string.IsNullOrEmpty(token)) return;

            try
            {
                string why;
                TcpClient c = Dial(p.Host, p.Port, 3000, out why);
                if (c == null) return;

                using (c)
                {
                    NetworkStream s = c.GetStream();
                    s.ReadTimeout = 3000;
                    s.WriteTimeout = 3000;

                    byte[] b = Encoding.UTF8.GetBytes(
                        "GET /logout HTTP/1.1\r\n" +
                        "Host: " + p.Host + ":" + p.Port + "\r\n" +
                        "Cookie: rcsid=" + token + "\r\n" +
                        "Connection: close\r\n\r\n");

                    s.Write(b, 0, b.Length);
                    s.Flush();

                    byte[] rest;
                    ReadHead(s, out rest);
                }
            }
            catch { }
        }

        // ---------------------------------------------------------------- 확인

        /// <summary>
        /// 지금 붙을 수 있는지 미리 본다.
        ///
        /// 뷰어로 넘어간 뒤에 조용히 실패하면 사람은 "화면이 안 나온다" 만 알 뿐
        /// 왜인지 알 방법이 없다. 고르는 자리에서 이유까지 보여 주려고 따로 둔다.
        /// </summary>
        internal static bool Test(Peer p, string userIp, out string why)
        {
            string tok = Login(p, userIp, out why);
            if (string.IsNullOrEmpty(tok)) return false;

            Logout(p, tok);
            why = "";
            return true;
        }

        /// <summary>
        /// 그 주소에 원격제어가 떠 있는지 본다.
        ///
        /// 포트 번호만 보고 판단하면 안 된다. 5900 은 VNC 가 먼저 쓰던 자리라 랜을
        /// 훑으면 VNC 서버가 같이 잡힌다. 그쪽은 HTTP 가 아니라 RFB 로 말하므로
        /// /ping 이 "ok" 를 돌려주는지까지 봐야 우리 것인지 갈린다.
        /// </summary>
        internal static bool Probe(string host, int port, int ms)
        {
            string why;
            TcpClient c = Dial(host, port, ms, out why);
            if (c == null) return false;

            using (c)
            {
                try
                {
                    NetworkStream s = c.GetStream();
                    s.ReadTimeout = ms;
                    s.WriteTimeout = ms;

                    byte[] b = Encoding.UTF8.GetBytes(
                        "GET /ping HTTP/1.1\r\nHost: " + host + "\r\nConnection: close\r\n\r\n");

                    s.Write(b, 0, b.Length);
                    s.Flush();

                    byte[] rest;
                    string head = ReadHead(s, out rest);
                    if (head.IndexOf(" 200 ", StringComparison.Ordinal) < 0) return false;

                    return ReadRest(s, rest, 64).Trim() == "ok";
                }
                catch { return false; }
            }
        }

        // ---------------------------------------------------------------- 토큰 보관

        // 하위 PC 에서 받아 둔 세션. 파일 목록을 볼 때마다 새로 로그인하면 그쪽에서
        // PBKDF2 를 12만 회씩 돌린다(약 50ms). 폴더를 몇 번 오가면 그게 다 쌓인다.
        // 그래서 한 번 받은 것을 잠시 들고 쓴다.
        private sealed class Tok { public string Token; public DateTime Until; }

        private static readonly Dictionary<int, Tok> toks = new Dictionary<int, Tok>();
        private static readonly object tokGate = new object();

        // 하위 PC 의 세션은 8시간을 산다. 그보다 훨씬 짧게 잡아 두면, 저쪽이 다시 뜨거나
        // 비밀번호가 바뀌었을 때 오래 헤매지 않는다.
        private static readonly TimeSpan TokLife = TimeSpan.FromMinutes(20);

        private static string Cached(Peer p)
        {
            lock (tokGate)
            {
                Tok t;
                if (toks.TryGetValue(p.Id, out t) && t.Until > DateTime.UtcNow) return t.Token;
            }
            return "";
        }

        private static void Remember(Peer p, string token)
        {
            lock (tokGate)
            {
                Tok t = new Tok();
                t.Token = token;
                t.Until = DateTime.UtcNow + TokLife;
                toks[p.Id] = t;
            }
        }

        private static void Forget(Peer p)
        {
            lock (tokGate) { toks.Remove(p.Id); }
        }

        /// <summary>설정이 바뀌면(비밀번호·주소) 들고 있던 세션은 못 쓴다.</summary>
        internal static void DropTokens()
        {
            lock (tokGate) { toks.Clear(); }
        }

        // ---------------------------------------------------------------- HTTP 넘기기

        /// <summary>
        /// 하위 PC 로 HTTP 요청 하나를 그대로 넘기고, 응답을 그대로 돌려준다.
        ///
        /// 파일 목록·다운로드·업로드가 이걸 탄다. 응답을 뜯어보지 않고 흘려보내므로
        /// Range 이어받기도, tar 스트리밍도, 몇 GB 짜리도 그대로 통한다.
        ///
        /// 로그인은 들고 있던 세션을 먼저 써 본다. 그게 죽었으면(로그인 화면이 돌아오면)
        /// 한 번만 새로 받아 다시 시도한다. 여기서 되풀이하면 안 된다 - 비밀번호가 틀린
        /// 경우에 하위 PC 의 잠금이 걸릴 때까지 두들기게 된다.
        /// </summary>
        internal static bool Http(NetworkStream browser, Peer p, string method, string path,
                                  string query, string range, string contentType, long bodyLen,
                                  byte[] preRead, string userIp, out string why)
        {
            why = "";

            string token = Cached(p);
            bool fresh = false;

            if (string.IsNullOrEmpty(token))
            {
                token = Login(p, userIp, out why);
                if (string.IsNullOrEmpty(token)) return false;

                Remember(p, token);
                fresh = true;
            }

            bool retry;
            bool ok = HttpOnce(browser, p, method, path, query, range, contentType, bodyLen,
                               preRead, userIp, token, out retry, out why);

            if (ok || !retry || fresh) return ok;

            // 들고 있던 세션이 죽었다. 한 번만 새로 받아 다시 해 본다.
            // 본문은 이미 읽어 버렸을 수 있어 올리기(PUT)는 되풀이하지 않는다.
            Forget(p);
            if (bodyLen > 0) { why = p.Addr + " 의 세션이 끊겼습니다. 다시 해 주세요."; return false; }

            token = Login(p, userIp, out why);
            if (string.IsNullOrEmpty(token)) return false;

            Remember(p, token);

            bool dummy;
            return HttpOnce(browser, p, method, path, query, range, contentType, bodyLen,
                            preRead, userIp, token, out dummy, out why);
        }

        private static bool HttpOnce(NetworkStream browser, Peer p, string method, string path,
                                     string query, string range, string contentType, long bodyLen,
                                     byte[] preRead, string userIp, string token, out bool retry, out string why)
        {
            retry = false;
            why = "";

            TcpClient d = Dial(p.Host, p.Port, 5000, out why);
            if (d == null) return false;

            using (d)
            {
                try { d.NoDelay = true; d.SendBufferSize = 256 * 1024; }
                catch { }

                NetworkStream ds = d.GetStream();
                ds.ReadTimeout = Timeout.Infinite;      // 큰 파일은 몇 분씩 걸린다
                ds.WriteTimeout = Timeout.Infinite;

                bool wrote = false;

                try
                {
                    StringBuilder req = new StringBuilder();
                    req.Append(method).Append(' ').Append(path);
                    if (!string.IsNullOrEmpty(query)) req.Append('?').Append(query);
                    req.Append(" HTTP/1.1\r\n");

                    req.Append("Host: ").Append(p.Host).Append(':').Append(p.Port).Append("\r\n");
                    req.Append("Cookie: rcsid=").Append(token).Append("\r\n");
                    req.Append(Fwd(userIp));

                    if (!string.IsNullOrEmpty(range)) req.Append("Range: ").Append(range).Append("\r\n");

                    // multipart 는 boundary 가 Content-Type 에 들어 있다. 이걸 안 넘기면
                    // 하위 PC 가 경계를 못 찾아 파일 앞뒤에 구분자가 섞인 채로 저장된다.
                    if (!string.IsNullOrEmpty(contentType))
                        req.Append("Content-Type: ").Append(contentType.Replace("\r", "").Replace("\n", "")).Append("\r\n");

                    if (bodyLen > 0) req.Append("Content-Length: ").Append(bodyLen).Append("\r\n");

                    req.Append("Connection: close\r\n\r\n");

                    byte[] rb = Encoding.UTF8.GetBytes(req.ToString());
                    ds.Write(rb, 0, rb.Length);

                    // 올리는 본문을 그대로 흘려 넘긴다. 메모리에 담지 않는다.
                    if (bodyLen > 0)
                    {
                        long left = bodyLen;

                        if (preRead != null && preRead.Length > 0)
                        {
                            int n = (int)Math.Min(preRead.Length, left);
                            ds.Write(preRead, 0, n);
                            left -= n;
                        }

                        byte[] buf = new byte[64 * 1024];

                        while (left > 0)
                        {
                            int n = browser.Read(buf, 0, (int)Math.Min(buf.Length, left));
                            if (n <= 0) break;

                            ds.Write(buf, 0, n);
                            left -= n;
                        }
                    }

                    ds.Flush();

                    byte[] extra;
                    string resp = ReadHead(ds, out extra);

                    if (resp.Length == 0) { why = p.Addr + " 가 응답하지 않았습니다."; return false; }

                    // 세션이 죽으면 하위 PC 는 로그인 화면을 200 으로 돌려준다.
                    // 그걸 그대로 넘기면 브라우저에 로그인 HTML 이 파일로 저장된다.
                    if (resp.IndexOf("text/html", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        retry = true;
                        why = p.Addr + " 의 세션이 만료되었습니다.";
                        return false;
                    }

                    byte[] hb = Encoding.UTF8.GetBytes(resp);
                    wrote = true;

                    browser.Write(hb, 0, hb.Length);
                    if (extra.Length > 0) browser.Write(extra, 0, extra.Length);

                    byte[] pipe = new byte[64 * 1024];

                    while (true)
                    {
                        int n = ds.Read(pipe, 0, pipe.Length);
                        if (n <= 0) break;

                        browser.Write(pipe, 0, n);
                    }

                    browser.Flush();
                    return true;
                }
                catch (Exception ex)
                {
                    // 이미 응답을 흘려보내기 시작했으면 실패로 돌려주면 안 된다.
                    // 부르는 쪽이 그 위에 502 를 또 쓴다.
                    if (wrote) return true;

                    why = p.Addr + " 와의 연결이 끊겼습니다 (" + ex.Message + ")";
                    return false;
                }
            }
        }

        // ---------------------------------------------------------------- 중계

        /// <summary>
        /// 브라우저의 WebSocket 을 하위 PC 로 이어 준다.
        /// 성공하면 연결이 끝날 때까지 이 안에 머문다.
        /// </summary>
        internal static bool Relay(NetworkStream browser, Peer p, string wsKey,
                                   byte[] preRead, string userIp, out string why)
        {
            string token = Login(p, userIp, out why);
            if (string.IsNullOrEmpty(token)) return false;

            try { return Upgrade(browser, p, wsKey, preRead, userIp, token, out why); }
            finally { Logout(p, token); }
        }

        private static bool Upgrade(NetworkStream browser, Peer p, string wsKey,
                                    byte[] preRead, string userIp, string token, out string why)
        {
            why = "";

            TcpClient d = Dial(p.Host, p.Port, 5000, out why);
            if (d == null) return false;

            using (d)
            {
                try
                {
                    d.NoDelay = true;                       // 조작 지연을 줄이려면 필수
                    d.SendBufferSize = 256 * 1024;
                }
                catch { }

                NetworkStream ds = d.GetStream();
                ds.ReadTimeout = Timeout.Infinite;          // 화면이 안 바뀌면 한참 조용하다
                ds.WriteTimeout = 30000;

                // 브라우저 쪽에 한 글자라도 쓴 뒤에는 실패했다고 돌려주면 안 된다.
                // 부르는 쪽이 그걸 보고 처음부터 핸드셰이크를 다시 하기 때문이다.
                // 이미 101 을 보낸 소켓에 또 101 을 쓰면 브라우저가 통째로 어긋난다.
                bool wrote = false;

                try
                {
                    // 브라우저가 준 Sec-WebSocket-Key 를 그대로 넘긴다.
                    // 그래야 하위 PC 가 셈한 Sec-WebSocket-Accept 가 브라우저가 기다리는
                    // 값과 같아진다. 응답 머리글을 손대지 않고 통째로 흘려보낼 수 있다.
                    byte[] req = Encoding.UTF8.GetBytes(
                        "GET /ws HTTP/1.1\r\n" +
                        "Host: " + p.Host + ":" + p.Port + "\r\n" +
                        "Upgrade: websocket\r\n" +
                        "Connection: Upgrade\r\n" +
                        "Sec-WebSocket-Version: 13\r\n" +
                        "Sec-WebSocket-Key: " + wsKey + "\r\n" +
                        "Cookie: rcsid=" + token + "\r\n" +
                        Fwd(userIp) +
                        "\r\n");

                    ds.Write(req, 0, req.Length);
                    ds.Flush();

                    byte[] extra;
                    string resp = ReadHead(ds, out extra);

                    if (!resp.StartsWith("HTTP/1.1 101", StringComparison.Ordinal))
                    {
                        why = p.Addr + " 가 화면 연결을 받아 주지 않았습니다.";
                        return false;
                    }

                    byte[] rb = Encoding.UTF8.GetBytes(resp);
                    wrote = true;
                    browser.Write(rb, 0, rb.Length);
                    if (extra.Length > 0) browser.Write(extra, 0, extra.Length);
                    browser.Flush();

                    // 머리글을 읽을 때 브라우저의 첫 프레임이 딸려 왔을 수 있다.
                    // 버리면 그 조작 하나가 통째로 사라진다.
                    if (preRead != null && preRead.Length > 0)
                    {
                        ds.Write(preRead, 0, preRead.Length);
                        ds.Flush();
                    }

                    Pump(browser, ds);
                    return true;
                }
                catch (Exception ex)
                {
                    // 이미 이어 준 뒤에 끊긴 것은 실패가 아니다. 알릴 것도 없다 -
                    // 브라우저는 소켓이 닫히는 것으로 알고 스스로 다시 붙는다.
                    if (wrote) return true;

                    why = p.Addr + " 와의 연결이 끊겼습니다 (" + ex.Message + ")";
                    return false;
                }
            }
        }

        private static void Pump(NetworkStream a, NetworkStream b)
        {
            Thread t = new Thread(delegate () { Copy(b, a); }, 256 * 1024);
            t.IsBackground = true;
            t.Start();

            Copy(a, b);

            // 반대쪽은 소켓이 닫히면서 읽기에서 풀려난다. 깃발로 세우려 하면
            // 아무것도 안 오는 동안에는 계속 막혀 있어서 소용이 없다.
            try { a.Close(); } catch { }
            try { b.Close(); } catch { }
            try { t.Join(1000); } catch { }
        }

        private static void Copy(NetworkStream from, NetworkStream to)
        {
            // 화면 조각은 한 덩어리가 96KB 까지 온다. 버퍼가 그보다 작으면 나눠 읽을
            // 뿐 손해는 없지만, 너무 작으면 왕복 횟수만 늘어난다.
            byte[] buf = new byte[64 * 1024];

            try
            {
                while (true)
                {
                    int n = from.Read(buf, 0, buf.Length);
                    if (n <= 0) break;

                    to.Write(buf, 0, n);
                    to.Flush();
                }
            }
            catch { }

            try { from.Close(); } catch { }
            try { to.Close(); } catch { }
        }

        // ---------------------------------------------------------------- 도우미

        /// <summary>
        /// 제한 시간을 두고 붙는다.
        ///
        /// TcpClient.Connect 를 그냥 쓰면 꺼져 있는 PC 에 20초 넘게 매달린다.
        /// 그동안 브라우저 쪽은 아무 말도 못 듣고 기다리게 된다.
        /// </summary>
        private static TcpClient Dial(string host, int port, int ms, out string why)
        {
            why = "";
            TcpClient c = new TcpClient();

            try
            {
                IAsyncResult ar = c.BeginConnect(host, port, null, null);

                if (!ar.AsyncWaitHandle.WaitOne(ms))
                {
                    try { c.Close(); } catch { }
                    why = host + ":" + port + " 에 닿지 않습니다. 켜져 있는지, 방화벽이 막는지 확인하세요.";
                    return null;
                }

                c.EndConnect(ar);
                return c;
            }
            catch (Exception ex)
            {
                try { c.Close(); } catch { }
                why = host + ":" + port + " 에 닿지 않습니다 (" + ex.Message + ")";
                return null;
            }
        }

        /// <summary>
        /// 머리글을 끝(빈 줄)까지 읽는다. 한 번에 본문까지 딸려 오는 일이 흔해서
        /// 남은 바이트를 함께 돌려준다 (Server.ReadHead 와 같은 이유).
        /// </summary>
        private static string ReadHead(NetworkStream s, out byte[] leftover)
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

        private static string ReadRest(NetworkStream s, byte[] have, int max)
        {
            MemoryStream ms = new MemoryStream();

            if (have != null && have.Length > 0)
                ms.Write(have, 0, Math.Min(have.Length, max));

            byte[] buf = new byte[4096];

            while (ms.Length < max)
            {
                int n;
                try { n = s.Read(buf, 0, buf.Length); }
                catch { break; }
                if (n <= 0) break;

                ms.Write(buf, 0, (int)Math.Min(n, max - ms.Length));
            }

            return Encoding.UTF8.GetString(ms.ToArray());
        }

        // ---------------------------------------------------------------- 랜 훑기

        /// <summary>이 PC 가 붙어 있는 랜의 IPv4 주소들. 검색 범위를 정하는 데 쓴다.</summary>
        internal static List<string> LocalIps()
        {
            List<string> r = new List<string>();

            try
            {
                foreach (IPAddress a in Dns.GetHostAddresses(Dns.GetHostName()))
                {
                    if (a.AddressFamily != AddressFamily.InterNetwork) continue;

                    string s = a.ToString();
                    if (s.StartsWith("127.") || s.StartsWith("169.254.")) continue;

                    // Tailscale 주소(100.64~100.127)는 랜이 아니다. 훑어 봐야 남의 장비만 나온다.
                    if (s.StartsWith("100.")) continue;

                    if (!r.Contains(s)) r.Add(s);
                }
            }
            catch { }

            return r;
        }

        /// <summary>
        /// 같은 대역(/24)을 훑어 원격제어가 떠 있는 PC 를 찾는다.
        /// 254개를 하나씩 재면 몇 분이 걸린다. 한꺼번에 던지고 기다린다.
        /// </summary>
        internal static List<string> Scan(string localIp, int port, Action<int, int> progress)
        {
            List<string> found = new List<string>();

            int cut = localIp.LastIndexOf('.');
            if (cut <= 0) return found;

            string pre = localIp.Substring(0, cut + 1);

            int done = 0;
            object gate = new object();

            using (ManualResetEvent all = new ManualResetEvent(false))
            {
                int left = 254;

                for (int i = 1; i <= 254; i++)
                {
                    string host = pre + i;
                    if (host == localIp)
                    {
                        // 자기 자신은 뺀다. 브릿지가 자기를 하위 PC 로 두면 끝없이 돈다.
                        lock (gate) { done++; if (--left == 0) all.Set(); }
                        continue;
                    }

                    ThreadPool.QueueUserWorkItem(delegate (object o)
                    {
                        string h = (string)o;
                        bool ok = false;

                        try { ok = Probe(h, port, 700); }
                        catch { }

                        lock (gate)
                        {
                            if (ok) found.Add(h);
                            done++;

                            if (progress != null) { try { progress(done, 254); } catch { } }
                            if (--left == 0) all.Set();
                        }
                    }, host);
                }

                all.WaitOne(40000);
            }

            found.Sort(delegate (string a, string b)
            {
                int x, y;
                int.TryParse(a.Substring(a.LastIndexOf('.') + 1), out x);
                int.TryParse(b.Substring(b.LastIndexOf('.') + 1), out y);
                return x.CompareTo(y);
            });

            return found;
        }

        /// <summary>IP 로 PC 이름을 물어본다. 목록에 번호만 늘어놓으면 어느 PC 인지 모른다.</summary>
        internal static string NameOf(string ip)
        {
            try
            {
                string n = Dns.GetHostEntry(ip).HostName;
                if (string.IsNullOrEmpty(n)) return ip;

                int dot = n.IndexOf('.');
                return dot > 0 ? n.Substring(0, dot) : n;
            }
            catch { return ip; }
        }
    }
}

// 브릿지 중계 실물 시험.
//
//   가짜 브라우저  ──TCP──▶  Bridge.Relay  ──TCP──▶  가짜 하위 PC
//
// 하위 PC 쪽은 Server.cs 가 실제로 내보내는 응답을 그대로 흉내 낸다.
// 확인하는 것:
//   1. Sec-WebSocket-Accept 가 브라우저가 준 키에서 나온 값과 맞는가
//   2. 브라우저가 보낸 마스킹 프레임이 하위 PC 에 그대로 닿는가
//   3. 하위 PC 가 보낸 비마스킹 프레임이 브라우저에 그대로 닿는가
//   4. 로그인 실패·꺼져 있음이 이유와 함께 돌아오는가
//   5. 세션을 열었으면 반드시 /logout 으로 접는가
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
    internal static class T
    {
        private const string Guid0 = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

        private static int fails;
        private static void Ok(string what, bool cond)
        {
            Console.WriteLine((cond ? "  OK   " : "  실패 ") + what);
            if (!cond) fails++;
        }

        internal static void Main()
        {
            Console.OutputEncoding = Encoding.UTF8;

            Console.WriteLine("[1] 정상 중계");
            Normal();

            Console.WriteLine();
            Console.WriteLine("[2] 비밀번호가 틀릴 때");
            BadPassword();

            Console.WriteLine();
            Console.WriteLine("[3] 하위 PC 가 꺼져 있을 때");
            Down();

            Console.WriteLine();
            Console.WriteLine(fails == 0 ? "전부 통과" : fails + "건 실패");
            Environment.Exit(fails == 0 ? 0 : 1);
        }

        // ------------------------------------------------------------ 1. 정상

        private static void Normal()
        {
            Fake down = new Fake(true);
            down.Start();

            Peer p = new Peer();
            p.Id = 1; p.Name = "시험"; p.Host = "127.0.0.1"; p.Port = down.Port; p.User = "admin";
            Bridge.SetPassword(1, "비밀1234");

            string key = Convert.ToBase64String(Encoding.ASCII.GetBytes("0123456789abcdef"));

            TcpListener bl = new TcpListener(IPAddress.Loopback, 0);
            bl.Start();
            int bport = ((IPEndPoint)bl.LocalEndpoint).Port;

            // 브릿지 쪽 (Server.DoBridge 가 하는 일)
            string why = "x";
            bool ok = false;
            Thread bridge = new Thread(delegate ()
            {
                using (TcpClient c = bl.AcceptTcpClient())
                    ok = Bridge.Relay(c.GetStream(), p, key, null, "203.0.113.9", out why);
            });
            bridge.IsBackground = true;
            bridge.Start();

            // 브라우저 쪽
            using (TcpClient b = new TcpClient("127.0.0.1", bport))
            {
                NetworkStream bs = b.GetStream();
                bs.ReadTimeout = 5000;

                byte[] extra;
                string resp = Head(bs, out extra);

                Ok("101 을 받았다", resp.StartsWith("HTTP/1.1 101"));

                string want;
                using (SHA1 sha = SHA1.Create())
                    want = Convert.ToBase64String(sha.ComputeHash(Encoding.ASCII.GetBytes(key + Guid0)));

                Ok("Accept 가 내 키에서 나온 값과 같다  (" + want + ")",
                   resp.IndexOf(want, StringComparison.Ordinal) >= 0);

                // 브라우저 → 하위 PC (마스킹)
                bs.Write(Mask("{\"t\":\"move\",\"x\":10}"), 0, Mask("{\"t\":\"move\",\"x\":10}").Length);
                bs.Flush();

                Ok("마스킹 프레임이 하위 PC 에 그대로 닿았다",
                   down.GotFrame.WaitOne(3000) && down.Got == "{\"t\":\"move\",\"x\":10}");

                // 하위 PC → 브라우저 (비마스킹). 가짜 하위 PC 가 받으면 되돌려 준다.
                string back = ReadFrame(bs, extra);
                Ok("비마스킹 프레임이 브라우저에 그대로 닿았다  (" + back + ")", back == "ECHO:10");
            }

            bridge.Join(4000);
            Ok("중계가 성공으로 끝났다", ok);
            Ok("세션을 /logout 으로 접었다", down.LoggedOut.WaitOne(3000));

            down.Stop();
        }

        // ------------------------------------------------------------ 2. 비밀번호 틀림

        private static void BadPassword()
        {
            Fake down = new Fake(false);
            down.Start();

            Peer p = new Peer();
            p.Id = 2; p.Name = "시험2"; p.Host = "127.0.0.1"; p.Port = down.Port; p.User = "admin";
            Bridge.SetPassword(2, "틀린것");

            TcpListener bl = new TcpListener(IPAddress.Loopback, 0);
            bl.Start();
            int bport = ((IPEndPoint)bl.LocalEndpoint).Port;

            string why = "";
            bool ok = true;
            Thread bridge = new Thread(delegate ()
            {
                using (TcpClient c = bl.AcceptTcpClient())
                    ok = Bridge.Relay(c.GetStream(), p, "abc", null, "203.0.113.9", out why);
            });
            bridge.IsBackground = true;
            bridge.Start();

            using (TcpClient b = new TcpClient("127.0.0.1", bport))
            {
                bridge.Join(5000);

                Ok("실패로 돌아왔다", !ok);
                Ok("이유가 사람이 읽을 수 있다  \"" + why + "\"",
                   why.IndexOf("맞지 않습니다", StringComparison.Ordinal) >= 0);

                // 실패했으면 브라우저 쪽에는 한 글자도 쓰지 않았어야 한다.
                // 안 그러면 Server 가 이어서 하는 핸드셰이크와 겹쳐 통째로 어긋난다.
                b.Client.ReceiveTimeout = 400;
                int n = 0;
                try { n = b.Client.Receive(new byte[64]); }
                catch (SocketException) { n = -1; }

                Ok("브라우저 쪽에 아무것도 쓰지 않았다", n <= 0);
            }

            down.Stop();
        }

        // ------------------------------------------------------------ 3. 꺼져 있음

        private static void Down()
        {
            // 아무도 안 듣는 포트. 붙는 데 5초를 넘기지 않아야 한다.
            TcpListener probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int dead = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            Peer p = new Peer();
            p.Id = 3; p.Name = "꺼진PC"; p.Host = "127.0.0.1"; p.Port = dead; p.User = "admin";
            Bridge.SetPassword(3, "아무거나");

            TcpListener bl = new TcpListener(IPAddress.Loopback, 0);
            bl.Start();
            int bport = ((IPEndPoint)bl.LocalEndpoint).Port;

            string why = "";
            bool ok = true;
            int t0 = Environment.TickCount;

            Thread bridge = new Thread(delegate ()
            {
                using (TcpClient c = bl.AcceptTcpClient())
                    ok = Bridge.Relay(c.GetStream(), p, "abc", null, "203.0.113.9", out why);
            });
            bridge.IsBackground = true;
            bridge.Start();

            using (TcpClient b = new TcpClient("127.0.0.1", bport))
            {
                bridge.Join(9000);
                int spent = Environment.TickCount - t0;

                Ok("실패로 돌아왔다", !ok);
                Ok("6초 안에 끝났다  (" + spent + "ms)", spent < 6000);
                Ok("이유가 사람이 읽을 수 있다  \"" + why + "\"",
                   why.IndexOf("닿지 않습니다", StringComparison.Ordinal) >= 0);
            }
        }

        // ------------------------------------------------------------ 가짜 하위 PC

        private sealed class Fake
        {
            private readonly TcpListener l;
            private readonly bool acceptLogin;
            private volatile bool run = true;

            internal readonly ManualResetEvent GotFrame = new ManualResetEvent(false);
            internal readonly ManualResetEvent LoggedOut = new ManualResetEvent(false);
            internal string Got = "";

            internal int Port { get { return ((IPEndPoint)l.LocalEndpoint).Port; } }

            internal Fake(bool ok)
            {
                acceptLogin = ok;
                l = new TcpListener(IPAddress.Loopback, 0);
                l.Start();
            }

            internal void Start()
            {
                Thread t = new Thread(delegate ()
                {
                    while (run)
                    {
                        TcpClient c;
                        try { c = l.AcceptTcpClient(); }
                        catch { break; }

                        TcpClient cc = c;
                        Thread h = new Thread(delegate () { try { Serve(cc); } catch { } });
                        h.IsBackground = true;
                        h.Start();
                    }
                });
                t.IsBackground = true;
                t.Start();
            }

            internal void Stop() { run = false; try { l.Stop(); } catch { } }

            private void Serve(TcpClient c)
            {
                using (c)
                {
                    NetworkStream s = c.GetStream();
                    s.ReadTimeout = 8000;

                    byte[] rest;
                    string head = Head(s, out rest);
                    if (head.Length == 0) return;

                    string first = head.Split('\r')[0];

                    if (first.StartsWith("POST /login"))
                    {
                        // 브릿지가 실제로 조작하는 사람의 IP 를 넘겨 주는지 본다.
                        Ok("하위 PC 에 조작자 IP 가 전달됐다",
                            head.IndexOf("X-Forwarded-For: 203.0.113.9", StringComparison.Ordinal) >= 0);

                        Reply(s, acceptLogin
                            ? "HTTP/1.1 302 Found\r\nSet-Cookie: rcsid=TESTTOKEN; Path=/; HttpOnly\r\n" +
                              "Location: /\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"
                            : "HTTP/1.1 200 OK\r\nContent-Type: text/html\r\nContent-Length: 47\r\n" +
                              "Connection: close\r\n\r\n아이디 또는 비밀번호가 맞지 않습니다.");
                        return;
                    }

                    if (first.StartsWith("GET /logout"))
                    {
                        Ok("/logout 에 세션 쿠키를 실어 보냈다",
                            head.IndexOf("rcsid=TESTTOKEN", StringComparison.Ordinal) >= 0);

                        LoggedOut.Set();
                        Reply(s, "HTTP/1.1 302 Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                        return;
                    }

                    if (first.StartsWith("GET /ws"))
                    {
                        Ok("하위 PC 에 세션 쿠키가 실려 왔다",
                            head.IndexOf("rcsid=TESTTOKEN", StringComparison.Ordinal) >= 0);

                        string key = "";
                        foreach (string ln in head.Split(new string[] { "\r\n" }, StringSplitOptions.None))
                            if (ln.StartsWith("Sec-WebSocket-Key:")) key = ln.Substring(18).Trim();

                        string acc;
                        using (SHA1 sha = SHA1.Create())
                            acc = Convert.ToBase64String(sha.ComputeHash(Encoding.ASCII.GetBytes(key + Guid0)));

                        Reply(s, "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\n" +
                                 "Connection: Upgrade\r\nSec-WebSocket-Accept: " + acc + "\r\n\r\n");

                        s.ReadTimeout = 6000;
                        Got = ReadFrame(s, rest);
                        GotFrame.Set();

                        // 서버는 마스킹하지 않는다. 그대로 돌려보낸다.
                        int x = 0;
                        int at = Got.IndexOf("\"x\":", StringComparison.Ordinal);
                        if (at >= 0) int.TryParse(Got.Substring(at + 4).TrimEnd('}'), out x);

                        byte[] f = Plain("ECHO:" + x);
                        s.Write(f, 0, f.Length);
                        s.Flush();

                        Thread.Sleep(600);
                        return;
                    }
                }
            }

            private static void Reply(NetworkStream s, string t)
            {
                byte[] b = Encoding.UTF8.GetBytes(t);
                s.Write(b, 0, b.Length);
                s.Flush();
            }
        }

        // ------------------------------------------------------------ 프레임 도우미

        /// <summary>브라우저가 만드는 것과 같은 마스킹 텍스트 프레임.</summary>
        private static byte[] Mask(string text)
        {
            byte[] p = Encoding.UTF8.GetBytes(text);
            byte[] m = new byte[] { 0x11, 0x22, 0x33, 0x44 };

            MemoryStream ms = new MemoryStream();
            ms.WriteByte(0x81);
            ms.WriteByte((byte)(0x80 | p.Length));      // 시험 문장은 125자 미만
            ms.Write(m, 0, 4);

            for (int i = 0; i < p.Length; i++) ms.WriteByte((byte)(p[i] ^ m[i & 3]));
            return ms.ToArray();
        }

        /// <summary>서버가 만드는 것과 같은 비마스킹 텍스트 프레임.</summary>
        private static byte[] Plain(string text)
        {
            byte[] p = Encoding.UTF8.GetBytes(text);

            MemoryStream ms = new MemoryStream();
            ms.WriteByte(0x81);
            ms.WriteByte((byte)p.Length);
            ms.Write(p, 0, p.Length);
            return ms.ToArray();
        }

        private static string ReadFrame(NetworkStream s, byte[] have)
        {
            List<byte> buf = new List<byte>();
            if (have != null) buf.AddRange(have);

            byte[] t = new byte[4096];
            while (buf.Count < 2)
            {
                int n = s.Read(t, 0, t.Length);
                if (n <= 0) return "";
                for (int i = 0; i < n; i++) buf.Add(t[i]);
            }

            bool masked = (buf[1] & 0x80) != 0;
            int len = buf[1] & 0x7F;
            int need = 2 + (masked ? 4 : 0) + len;

            while (buf.Count < need)
            {
                int n = s.Read(t, 0, t.Length);
                if (n <= 0) return "";
                for (int i = 0; i < n; i++) buf.Add(t[i]);
            }

            byte[] pay = new byte[len];
            int off = 2 + (masked ? 4 : 0);
            for (int i = 0; i < len; i++)
                pay[i] = masked ? (byte)(buf[off + i] ^ buf[2 + (i & 3)]) : buf[off + i];

            return Encoding.UTF8.GetString(pay);
        }

        private static string Head(NetworkStream s, out byte[] leftover)
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
                        int hl = i + 1, rest = filled - hl;
                        if (rest > 0) { leftover = new byte[rest]; Buffer.BlockCopy(buf, hl, leftover, 0, rest); }
                        return Encoding.UTF8.GetString(buf, 0, hl);
                    }
                }
            }
            return filled > 0 ? Encoding.UTF8.GetString(buf, 0, filled) : "";
        }
    }
}

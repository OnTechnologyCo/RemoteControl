// 일회용 Go SSH 서버 + 실제 Windows ssh.exe. 사용자 설정/키/서비스에는 접근하지 않는다.
using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace RemoteControl
{
    internal static class SshTunnelTest
    {
        private static int checks;
        private static void Check(bool value, string message)
        { if (!value) throw new Exception(message + " / " + RemoteTunnel.LastError); checks++; Console.WriteLine("PASS " + message); }
        private static int FreePort() { var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); int p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p; }
        private static void Wait(Func<bool> ready, string label)
        { var t = Stopwatch.StartNew(); while (!ready() && t.ElapsedMilliseconds < 18000) Thread.Sleep(50); Check(ready(), label); }
        public static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            try { Run(args); Console.WriteLine("ALL " + checks + " CHECKS PASSED"); return 0; }
            catch (Exception ex) { Console.WriteLine(ex); return 1; }
            finally { TunnelGuard.Disable(); RemoteTunnel.Stop(); }
        }
        private static void Run(string[] args)
        {
            Config.Reset();
            Check(RemoteTunnel.Provider == "private", "fresh install defaults to private-tunnel");
            Config.Set("tskey", "existing");
            Check(RemoteTunnel.Provider == "tailscale", "existing Tailscale selection survives upgrade");
            Config.Set("tunnel", "private");
            Check(!RemoteTunnel.IsConfigured, "incomplete settings remain stopped");
            Config.Set("sshhost", "127.0.0.1"); Config.Set("sshport", args[0]); Config.Set("sshuser", "test");
            Config.Set("sshkeyfile", args[1]); Config.Set("sshknownhosts", args[2]);
            int remote = FreePort();
            Config.Set("sshremoteport", remote.ToString()); Config.Set("sshurl", "http://127.0.0.1:" + remote);
            SshSettings settings = SshSettings.Load();
            Check(settings.Validate(true) == "", "key/known_hosts paths with spaces and Korean are accepted");
            settings.Host = "-oProxyCommand=calc"; Check(settings.Validate(false).Length > 0, "SSH option injection rejected");
            settings = SshSettings.Load(); settings.Url = "http://public.example"; Check(settings.Validate(false).Length > 0, "public browser URL requires TLS");
            settings.Url = "https://public.example/remote"; Check(settings.Validate(false).Length > 0, "unsupported URL subpath rejected");
            settings = SshSettings.Load(); settings.KeyFile = @"\\server\key"; Check(settings.Validate(false).Length > 0, "UNC key path rejected");
            using (var backend = new Backend())
            {
                Config.Set("port", backend.Port.ToString());
                RemoteTunnel.SyncSettings();
                Stopwatch start = Stopwatch.StartNew();
                Check(RemoteTunnel.Start(backend.Port) != null && RemoteTunnel.IsAlive(), "real SSH reverse forward acknowledged");
                Console.WriteLine("MEASURE SSH startup " + start.ElapsedMilliseconds + " ms");
                Check(TunnelGuard.Probe(RemoteTunnel.CurrentUrl), "HTTP /ping travels through reverse SSH");
                backend.Status = 502; Check(!TunnelGuard.Probe(RemoteTunnel.CurrentUrl), "HTTP 502 is not healthy");
                backend.Status = 200; backend.Ping = "wrong"; Check(!TunnelGuard.Probe(RemoteTunnel.CurrentUrl), "wrong HTTP body is not healthy"); backend.Ping = "ok";
                byte[] payload = new byte[16 * 1024 * 1024]; new Random(17).NextBytes(payload);
                using (WebClient client = new WebClient())
                {
                    client.Proxy = null;
                    client.Headers["Content-Type"] = "multipart/form-data; boundary=test-boundary";
                    Stopwatch timer = Stopwatch.StartNew();
                    byte[] reply = client.UploadData(RemoteTunnel.CurrentUrl + "/upload", payload);
                    Check(Hash(reply) == Hash(payload), "16 MiB HTTP upload/download bytes preserved");
                    Console.WriteLine("MEASURE 16 MiB round trip " + timer.ElapsedMilliseconds + " ms");
                }
                using (var c = new TcpClient("127.0.0.1", remote))
                {
                    c.ReceiveTimeout = 5000;
                    var stream = c.GetStream();
                    Write(stream, "GET /ws HTTP/1.1\r\nHost: local\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: MDEyMzQ1Njc4OWFiY2RlZg==\r\n\r\n");
                    Check(Header(stream).StartsWith("HTTP/1.1 101"), "WebSocket handshake crosses SSH");
                    var ws = new WebSocketConn(stream);
                    byte[] frame = { 0x82, 0x84, 1, 2, 3, 4, (byte)('t' ^ 1), (byte)('e' ^ 2), (byte)('s' ^ 3), (byte)('t' ^ 4) };
                    stream.Write(frame, 0, frame.Length);
                    var message = ws.Receive();
                    Check(message != null && message.Text == "test", "masked browser frame and server frame preserved");
                }
                using (var c = new TcpClient("127.0.0.1", remote))
                {
                    c.ReceiveTimeout = 5000; var stream = c.GetStream(); Write(stream, "GET /eof HTTP/1.0\r\n\r\n");
                    c.Client.Shutdown(SocketShutdown.Send);
                    Check(Header(stream).Contains("200 OK"), "response after client EOF survives SSH half-close");
                }
                Config.Set("sshremoteport", FreePort().ToString());
                Config.Set("sshurl", "http://127.0.0.1:" + Config.Get("sshremoteport"));
                Check(RemoteTunnel.SyncSettings() && !RemoteTunnel.IsAlive(), "port change closes old SSH child");
                Check(RemoteTunnel.Start(backend.Port) != null && TunnelGuard.Probe(RemoteTunnel.CurrentUrl), "changed reverse port forwards to backend");
                RemoteTunnel.Stop();
                Config.Set("sshknownhosts", args[3]); RemoteTunnel.SyncSettings();
                Check(RemoteTunnel.Start(backend.Port) == null && RemoteTunnel.RetryBlocked, "wrong server host key blocks connection and retries");
                Config.Set("sshknownhosts", args[2]); Config.Set("sshuser", "wrong"); RemoteTunnel.SyncSettings();
                Check(RemoteTunnel.Start(backend.Port) == null && RemoteTunnel.RetryBlocked, "authentication rejection blocks retries");
                Config.Set("sshuser", "test"); RemoteTunnel.SyncSettings();
                var occupied = new TcpListener(IPAddress.Loopback, int.Parse(Config.Get("sshremoteport"))); occupied.Start();
                try { Check(RemoteTunnel.Start(backend.Port) == null && RemoteTunnel.RetryBlocked, "occupied hub port fails without false success"); }
                finally { occupied.Stop(); }
                Config.Set("tunnelrevision", "retry"); RemoteTunnel.SyncSettings();
                Check(RemoteTunnel.Start(backend.Port) != null, "explicit retry clears permanent failure");
                Config.Set("tunnel", "tailscale"); RemoteTunnel.SyncSettings();
                Check(!SshTunnel.IsAlive() && RemoteTunnel.Start(backend.Port) != null && Tunnel.Starts == 1, "provider switch stops SSH and selects Tailscale");
                Config.Set("tunnel", "off"); RemoteTunnel.SyncSettings();
                Check(!RemoteTunnel.IsAlive() && RemoteTunnel.Start(backend.Port) == null, "off mode never opens a tunnel");
                Config.Set("tunnel", "private"); Config.Set("extauto", "1"); RemoteTunnel.SyncSettings();
                TunnelGuard.Enable(delegate { return backend.Port; }, delegate { return Config.Get("extauto") == "1"; }, Console.WriteLine);
                Wait(RemoteTunnel.IsAlive, "guard restores saved start intent");
                RemoteTunnel.Stop();
                Wait(RemoteTunnel.IsAlive, "guard reconnects after SSH terminates");
                Config.Set("extauto", "0"); RemoteTunnel.SyncSettings();
                Thread.Sleep(5500);
                Check(!RemoteTunnel.IsAlive(), "manual stop cancels automatic reconnect");
                TunnelGuard.Disable();
                // 응답 없는 SSH 서버에 연결 중에도 Stop은 UI를 붙잡지 않아야 한다.
                var silent = new TcpListener(IPAddress.Loopback, 0); silent.Start();
                try
                {
                    Config.Set("sshport", ((IPEndPoint)silent.LocalEndpoint).Port.ToString()); RemoteTunnel.SyncSettings();
                    string result = "pending";
                    var thread = new Thread(delegate() { result = RemoteTunnel.Start(backend.Port); }); thread.Start();
                    using (TcpClient accepted = silent.AcceptTcpClient())
                    {
                        Stopwatch timer = Stopwatch.StartNew(); RemoteTunnel.Stop();
                        Check(timer.ElapsedMilliseconds < 500, "cancel returns within 500 ms while opening");
                        Check(thread.Join(3000) && result == null && !RemoteTunnel.IsAlive(), "canceled startup cannot publish stale success");
                    }
                }
                finally { silent.Stop(); }
            }
        }
        private static string Hash(byte[] bytes) { using (var sha = SHA256.Create()) return Convert.ToBase64String(sha.ComputeHash(bytes)); }
        private static void Write(Stream s, string text) { byte[] bytes = Encoding.ASCII.GetBytes(text); s.Write(bytes, 0, bytes.Length); }
        private static string Header(Stream s)
        {
            StringBuilder b = new StringBuilder(); int n;
            while (b.Length < 16384 && (n = s.ReadByte()) >= 0) { b.Append((char)n); if (b.ToString().EndsWith("\r\n\r\n")) break; }
            return b.ToString();
        }
        private sealed class Backend : IDisposable
        {
            private readonly TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            internal volatile int Status = 200;
            internal volatile string Ping = "ok";
            internal int Port { get { return ((IPEndPoint)listener.LocalEndpoint).Port; } }
            internal Backend()
            {
                listener.Start();
                new Thread(delegate() { try { while (true) { var c = listener.AcceptTcpClient(); ThreadPool.QueueUserWorkItem(delegate { Serve(c); }); } } catch (SocketException) { } }) { IsBackground = true }.Start();
            }
            private void Serve(TcpClient client)
            {
                try { using (client)
                {
                    client.ReceiveTimeout = 5000; client.SendTimeout = 5000;
                    var s = client.GetStream(); string h = Header(s);
                    if (h.Contains(" /ws "))
                    {
                        Write(s, WebSocketConn.BuildHandshake("MDEyMzQ1Njc4OWFiY2RlZg=="));
                        var ws = new WebSocketConn(s); var m = ws.Receive(); if (m != null) ws.Send(m.Op, m.Data); return;
                    }
                    if (h.Contains(" /eof ")) { while (s.ReadByte() >= 0) { } }
                    int length = 0;
                    foreach (string line in h.Split('\n')) if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) int.TryParse(line.Substring(15).Trim(), out length);
                    if (h.IndexOf("100-continue", StringComparison.OrdinalIgnoreCase) >= 0) Write(s, "HTTP/1.1 100 Continue\r\n\r\n");
                    byte[] body = length > 0 ? new byte[length] : Encoding.ASCII.GetBytes(Ping);
                    for (int pos = 0; pos < length;) { int n = s.Read(body, pos, length - pos); if (n == 0) throw new IOException(); pos += n; }
                    Write(s, "HTTP/1.1 " + Status + (Status == 200 ? " OK" : " Bad Gateway") + "\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n");
                    s.Write(body, 0, body.Length);
                } } catch (Exception) { }
            }
            public void Dispose() { listener.Stop(); }
        }
    }
}

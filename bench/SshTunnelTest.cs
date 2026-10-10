// 실제 SSHTunnel 1.3 서버 + 제품 내장 helper. 운영 계정과 설정은 읽지 않는다.
using System;
using System.Collections.Generic;
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
        private static void Wait(Func<bool> ready, string label)
        { var t = Stopwatch.StartNew(); while (!ready() && t.ElapsedMilliseconds < 18000) Thread.Sleep(50); Check(ready(), label); }
        public static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            try { Run(args[0]); Console.WriteLine("ALL " + checks + " CHECKS PASSED"); return 0; }
            catch (Exception ex) { Console.WriteLine(ex); return 1; }
            finally { TunnelGuard.Disable(); RemoteTunnel.Stop(); }
        }
        private static HttpWebResponse Request(string url, int gateway, string path, byte[] bytes)
        {
            var request = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + gateway + path);
            request.Proxy = null; request.Host = new Uri(url).Host; request.KeepAlive = false;
            request.Timeout = 10000; request.AllowAutoRedirect = false;
            if (bytes != null) { request.Method = "POST"; request.ContentLength = bytes.Length; request.ContentType = "application/octet-stream";
                using (var output = request.GetRequestStream()) output.Write(bytes, 0, bytes.Length); }
            return (HttpWebResponse)request.GetResponse();
        }
        private static void Run(string infoPath)
        {
            var info = Json.Parse(File.ReadAllText(infoPath)); int gateway = int.Parse(Json.Str(info, "gatewayPort"));
            var refused = new TcpListener(IPAddress.Loopback, 0); refused.Start();
            string refusedUrl = "https://127.0.0.1:" + ((IPEndPoint)refused.LocalEndpoint).Port; refused.Stop();
            bool routeFailure; string detail;
            Check(!TunnelGuard.Probe(refusedUrl, out routeFailure, out detail) && !routeFailure && !TunnelGuard.ShouldReopen("private", routeFailure), "refused HTTPS connection preserves live private tunnel URL");
            Check(TunnelGuard.ShouldReopen("tailscale", routeFailure), "Tailscale recovery policy is preserved");
            using (var probe = new Backend())
            {
                string probeUrl = "http://127.0.0.1:" + probe.Port;
                probe.Status = 404;
                Check(!TunnelGuard.Probe(probeUrl, out routeFailure, out detail) && routeFailure && TunnelGuard.ShouldReopen("private", routeFailure), "missing web route requests private tunnel recovery");
                probe.Status = 502;
                Check(!TunnelGuard.Probe(probeUrl, out routeFailure, out detail) && routeFailure, "broken upstream requests private tunnel recovery");
                probe.Status = 403;
                Check(!TunnelGuard.Probe(probeUrl, out routeFailure, out detail) && !routeFailure, "HTTPS proxy denial preserves private tunnel URL");
                probe.Status = 200;
                Check(TunnelGuard.Probe(probeUrl, out routeFailure, out detail), "public probe recognizes actual remote-control ping");
                probe.Ping = "other app";
                Check(!TunnelGuard.Probe(probeUrl, out routeFailure, out detail) && !routeFailure, "wrong proxy response is not treated as a healthy tunnel");
            }
            Config.Reset();
            Check(RemoteTunnel.Provider == "private", "fresh install selects SSHTunnel");
            Check(SshSettings.Load().Port == "2222", "SSH port defaults to 2222");
            Config.Set("tskey", "existing"); Check(RemoteTunnel.Provider == "tailscale", "existing Tailscale selection survives upgrade");
            Config.Set("tunnel", "private"); Check(!RemoteTunnel.IsConfigured, "incomplete settings do not connect");
            Config.Set("sshhost", "127.0.0.1"); Config.Set("sshport", Json.Str(info, "sshPort")); Config.Set("sshuser", "test");
            Config.Set("sshfingerprint", Json.Str(info, "fingerprint")); Config.Set("sshname", "원격제어 통합 시험");
            string issued = Json.Str(info, "accessKey");
            var changes = new Dictionary<string,string>(); SshSettings.AddKeySettings(changes, issued); Config.SetMany(changes);
            Check(Config.Get("sshaccesskey") != issued && Config.Get("sshaccesskeym") != issued && SshSettings.Load().AccessKey == issued, "AccessKey is DPAPI protected and round trips");
            string userCopy = Config.Get("sshaccesskey"); Config.Set("sshaccesskey", "invalid");
            Check(SshSettings.Load().AccessKey == issued, "service can recover key from machine DPAPI copy"); Config.Set("sshaccesskey", userCopy);
            Check(SshSettings.Load().Validate(true) == "", "issued SSHTunnel settings are accepted without OpenSSH or key files");
            var invalid = SshSettings.Load(); invalid.Host = "-oProxyCommand=calc"; Check(invalid.Validate(false).Length > 0, "invalid server address rejected");
            invalid = SshSettings.Load(); invalid.AccessKey = "not-a-key"; Check(invalid.Validate(false).Length > 0, "invalid access key rejected");
            Check(!SshTunnel.ValidUrl("https://test.example:1234") && !SshTunnel.ValidUrl("http://public.example") &&
                !SshTunnel.ValidUrl("https://test.example/path") && !SshTunnel.ValidUrl("https://test.example:443"), "public URL must be HTTPS without explicit port or subpath");
            using (var backend = new Backend())
            {
                Config.Set("port", backend.Port.ToString()); RemoteTunnel.SyncSettings();
                var startup = Stopwatch.StartNew(); string first = RemoteTunnel.Start(backend.Port);
                Check(first != null && RemoteTunnel.IsAlive() && SshTunnel.ValidUrl(first), "real SSHTunnel web API returns live port-free HTTPS URL");
                Check(new Uri(first).Host.EndsWith(".tunnel.example.test") && new Uri(first).Host.Split('.')[0].Length <= 63, "server-issued subdomain is accepted without a fixed length");
                Console.WriteLine("MEASURE SSHTunnel ready " + startup.ElapsedMilliseconds + " ms");
                using (var response = Request(first, gateway, "/ping", null))
                using (var reader = new StreamReader(response.GetResponseStream())) Check(reader.ReadToEnd() == "ok", "HTTP ping reaches local remote-control backend");
                byte[] payload = new byte[16 * 1024 * 1024]; new Random(17).NextBytes(payload);
                using (var response = Request(first, gateway, "/upload", payload))
                using (var memory = new MemoryStream()) { response.GetResponseStream().CopyTo(memory); Check(Hash(memory.ToArray()) == Hash(payload), "16 MiB upload/download bytes preserved"); }
                using (var c = new TcpClient("127.0.0.1", gateway))
                {
                    c.ReceiveTimeout = 5000; var stream = c.GetStream();
                    Write(stream, "GET /ws HTTP/1.1\r\nHost: " + new Uri(first).Host + "\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: MDEyMzQ1Njc4OWFiY2RlZg==\r\nSec-WebSocket-Version: 13\r\n\r\n");
                    Check(Header(stream).StartsWith("HTTP/1.1 101"), "WebSocket upgrade crosses SSHTunnel web gateway");
                    var ws = new WebSocketConn(stream); byte[] frame = { 0x82, 0x84, 1, 2, 3, 4, (byte)('t'^1), (byte)('e'^2), (byte)('s'^3), (byte)('t'^4) };
                    stream.Write(frame,0,frame.Length); var message=ws.Receive(); Check(message != null && message.Text == "test", "WebSocket bidirectional payload preserved");
                }
                RemoteTunnel.Stop(); Config.Set("tunnelrevision", Guid.NewGuid().ToString("N")); RemoteTunnel.SyncSettings();
                string second = RemoteTunnel.Start(backend.Port);
                Check(second != null && second != first, "reconnect publishes a newly issued address");
                bool closed = false;
                try { using (var response = Request(first, gateway, "/ping", null)) { } }
                catch (WebException ex) { var response=ex.Response as HttpWebResponse; closed=response!=null && response.StatusCode==HttpStatusCode.NotFound; if(response!=null)response.Close(); }
                Check(closed, "old URL no longer routes after tunnel closure");
                RemoteTunnel.Stop(); Config.Set("sshfingerprint", "SHA256:" + new string('A',43)); RemoteTunnel.SyncSettings();
                Check(RemoteTunnel.Start(backend.Port)==null && RemoteTunnel.RetryBlocked, "wrong server fingerprint blocks connection and retries");
                Config.Set("sshfingerprint",Json.Str(info,"fingerprint")); Config.Set("sshuser","wrong"); RemoteTunnel.SyncSettings();
                Check(RemoteTunnel.Start(backend.Port)==null && RemoteTunnel.RetryBlocked, "account authentication failure blocks retries");
                Config.Set("sshuser","test"); RemoteTunnel.SyncSettings();
                Check(RemoteTunnel.Start(backend.Port)!=null, "saving corrected settings clears permanent failure");
                Config.Set("tunnel","tailscale"); RemoteTunnel.SyncSettings();
                Check(!SshTunnel.IsAlive() && RemoteTunnel.Start(backend.Port)!=null && Tunnel.Starts==1, "switch to Tailscale stops only SSH helper");
                Config.Set("tunnel","off"); RemoteTunnel.SyncSettings();
                Check(!RemoteTunnel.IsAlive() && RemoteTunnel.Start(backend.Port)==null, "LAN-only selection does not open tunnel");
                Config.Set("tunnel","private"); Config.Set("extauto","1"); RemoteTunnel.SyncSettings();
                TunnelGuard.Enable(delegate {return backend.Port;},delegate {return Config.Get("extauto")=="1";},Console.WriteLine);
                Wait(RemoteTunnel.IsAlive,"guard restores saved automatic connection");
                string beforeReconnect=RemoteTunnel.CurrentUrl;
                using(var client=new WebClient()) { client.Proxy=null; client.UploadData("http://127.0.0.1:"+Json.Str(info,"controlPort")+"/disconnect",new byte[0]); }
                Wait(delegate {return RemoteTunnel.IsAlive() && RemoteTunnel.CurrentUrl!=beforeReconnect;},"guard reconnects after actual relay disconnect with new URL");
                Config.Set("extauto","0"); RemoteTunnel.SyncSettings(); Thread.Sleep(5500);
                Check(!RemoteTunnel.IsAlive(),"manual stop prevents automatic reconnect"); TunnelGuard.Disable();
                var silent=new TcpListener(IPAddress.Loopback,0); silent.Start();
                try
                {
                    Config.Set("sshport",((IPEndPoint)silent.LocalEndpoint).Port.ToString()); RemoteTunnel.SyncSettings();
                    string result="pending"; var thread=new Thread(delegate(){result=RemoteTunnel.Start(backend.Port);}); thread.Start();
                    using(var accepted=silent.AcceptTcpClient())
                    {
                        var timer=Stopwatch.StartNew(); RemoteTunnel.Stop(); Check(timer.ElapsedMilliseconds<500,"cancel does not block UI during SSH handshake");
                        Check(thread.Join(3000) && result==null && !RemoteTunnel.IsAlive(),"canceled startup cannot publish stale endpoint");
                    }
                } finally {silent.Stop();}
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

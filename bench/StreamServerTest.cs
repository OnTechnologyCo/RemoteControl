using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
namespace RemoteControl
{
    internal static class StreamServerTest
    {
        private static int checks;
        private static void Check(bool ok, string label) { if (!ok) throw new Exception(label); checks++; Console.WriteLine("PASS " + label); }
        private static string Header(Stream stream) { var s = new StringBuilder(); while (!s.ToString().EndsWith("\r\n\r\n")) { int b = stream.ReadByte(); if (b < 0) break; s.Append((char)b); } return s.ToString(); }
        private static void Send(Stream stream, string text)
        {
            byte[] p = Encoding.UTF8.GetBytes(text); var bytes = new List<byte> { 0x81 };
            if (p.Length < 126) bytes.Add((byte)(128 | p.Length));
            else { bytes.Add(254); bytes.Add((byte)(p.Length >> 8)); bytes.Add((byte)p.Length); }
            bytes.AddRange(new byte[] { 1, 2, 3, 4 });
            for (int i = 0; i < p.Length; i++) bytes.Add((byte)(p[i] ^ (1 + (i % 4))));
            byte[] frame = bytes.ToArray(); stream.Write(frame, 0, frame.Length);
        }
        private static Dictionary<string, object> Until(WebSocketConn ws, string type)
        {
            while (true)
            {
                var m = ws.Receive(); if (m == null) throw new Exception("Connection ended waiting for " + type);
                if (m.Op == WsOp.Binary)
                {
                    using (var r = new BinaryReader(new MemoryStream(m.Data)))
                        while (r.BaseStream.Position < r.BaseStream.Length)
                        {
                            int index = r.ReadUInt16(), size = r.ReadInt32();
                            if (index == 65535) { r.ReadInt32(); r.ReadBytes(size); continue; }
                            byte[] jpeg = r.ReadBytes(size);
                            if (jpeg.Length != size || jpeg[0] != 255 || jpeg[1] != 216) throw new Exception("Damaged tile packet");
                        }
                }
                else if (m.Op == WsOp.Text)
                {
                    // 제품의 입력용 미니 파서는 배열을 다루지 않는다. init에는 sizes 배열이 있다.
                    var o = new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<Dictionary<string, object>>(m.Text);
                    if (Json.Str(o, "t") == type) return o;
                }
            }
        }
        public static int Main()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var client = new TcpClient(); client.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            var accepted = listener.AcceptTcpClient(); listener.Stop();
            client.NoDelay = accepted.NoDelay = true; client.ReceiveTimeout = 3000;
            Thread serverThread = new Thread(delegate() {
                try { typeof(Server).GetMethod("DoWebSocket", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(new Server(), new object[] {
                    accepted.GetStream(), new[] { "GET /ws HTTP/1.1", "Sec-WebSocket-Key: MDEyMzQ1Njc4OWFiY2RlZg==" }, new Session { User = "test" }, "127.0.0.1"
                }); } catch (Exception ex) { Console.WriteLine(ex); }
            }) { IsBackground = true };
            serverThread.Start();
            try
            {
                var stream = client.GetStream(); Check(Header(stream).Contains("101"), "actual server WebSocket handshake");
                var ws = new WebSocketConn(stream);
                var init = Until(ws, "init"); int view = Json.Int(init, "view", 0);
                Check(Json.Int(init, "flow", 0) == 1 && view == 1, "server advertises flow control and viewport generation");
                Send(stream, "{\"t\":\"stream\",\"auto\":false,\"probe\":true}");
                int probe = Json.Int(Until(ws, "probe"), "n", 0);
                Check(probe > 0, "server probes base RTT before sending screen data");
                Send(stream, "{\"t\":\"probeack\",\"n\":" + probe + "}");
                int n1 = Json.Int(Until(ws, "frame"), "n", 0), n2 = Json.Int(Until(ws, "frame"), "n", 0);
                Check(n1 > 0 && n2 > n1, "two valid JPEG batches arrive before acknowledgments");
                var timer = Stopwatch.StartNew(); Send(stream, "{\"t\":\"ping\",\"c\":123}");
                Check(Json.Int(Until(ws, "pong"), "c", 0) == 123 && timer.ElapsedMilliseconds < 1000, "control replies proceed while frame window is full");
                Send(stream, "{\"t\":\"btn\",\"b\":2,\"d\":false,\"x\":10,\"y\":10}");
                Send(stream, "{\"t\":\"btn\",\"b\":0,\"d\":true,\"x\":10,\"y\":10,\"view\":1}");
                Send(stream, "{\"t\":\"key\",\"vk\":17,\"d\":true}");
                Send(stream, "{\"t\":\"release\"}"); Send(stream, "{\"t\":\"ping\",\"c\":124}"); Until(ws, "pong");
                Check(ControlTest.Events.Exists(delegate(Native.INPUT e) { return e.type == 0 && e.U.mi.dwFlags == Native.MOUSEEVENTF_LEFTUP; }), "input and cleanup proceed while frames await ACK");
                Check(!ControlTest.Events.Exists(delegate(Native.INPUT e) { return e.type == 0 && e.U.mi.dwFlags == Native.MOUSEEVENTF_RIGHTUP; }), "server sends no unmatched right-up");
                Send(stream, "{\"t\":\"scale\",\"s\":50}");
                int nextView = Json.Int(Until(ws, "init"), "view", 0); Check(nextView > view, "resize serialized with a new viewport generation");
                int before = ControlTest.Events.Count;
                Send(stream, "{\"t\":\"move\",\"x\":50,\"y\":50,\"view\":1}");
                Send(stream, "{\"t\":\"btn\",\"b\":2,\"d\":true,\"x\":50,\"y\":50,\"view\":1}");
                Send(stream, "{\"t\":\"ping\",\"c\":125}"); Until(ws, "pong");
                Check(ControlTest.Events.Count == before, "old-size mouse coordinates are not injected into resized screen");
                Send(stream, "{\"t\":\"ack\",\"n\":" + n1 + "}"); Send(stream, "{\"t\":\"ack\",\"n\":" + n2 + "}");
                Check(Json.Int(Until(ws, "frame"), "n", 0) > n2, "ACK resumes transmission of resized tiles");
                client.Close(); Check(serverThread.Join(2000), "disconnect releases session and terminates both stream threads");
                Check(!ControlTest.Events.Exists(delegate(Native.INPUT e) { return e.type == 0 && e.U.mi.dwFlags == Native.MOUSEEVENTF_RIGHTUP; }), "disconnect never appends a phantom right-up");
                FourK();
                Console.WriteLine("ALL " + checks + " SERVER STREAM CHECKS PASSED"); return 0;
            }
            catch (Exception ex) { Console.WriteLine(ex); return 1; }
            finally { client.Close(); accepted.Close(); }
        }

        private static void FourK()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var client = new TcpClient(); client.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            var accepted = listener.AcceptTcpClient(); listener.Stop();
            client.NoDelay = accepted.NoDelay = true; client.ReceiveTimeout = 15000;
            var tx = new WebSocketConn(accepted.GetStream()); var rx = new WebSocketConn(client.GetStream());
            var output = new StreamCommands { AutoQuality = false, MaxTileSize = 1024 }; output.Flow.Enable();
            using (var source = new ScreenSource(new System.Drawing.Rectangle(0, 0, 3840, 2160)))
            {
                var sender = new Thread(delegate() {
                    try { typeof(Server).GetMethod("StreamLoop", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null,
                        new object[] { tx, true, new Func<ScreenSource>(delegate { source.InvalidateAll(); return source; }), new int[1], output }); }
                    catch { }
                }) { IsBackground = true };
                var acknowledgments = new Thread(delegate() {
                    try { while (!tx.Closed) { var m = tx.Receive(); if (m == null) break; output.Flow.Ack(Json.Int(Json.Parse(m.Text), "n", 0)); } } catch { }
                }) { IsBackground = true };
                acknowledgments.Start(); sender.Start();
                int tile = 128, cols = 30, rows = 17, view = 1; bool large = false, restored = false;
                var seen = new HashSet<int>(); var timer = Stopwatch.StartNew();
                try
                {
                    while (timer.ElapsedMilliseconds < 12000 && !restored)
                    {
                        var m = rx.Receive(); if (m == null) throw new Exception("4K stream ended");
                        if (m.Op == WsOp.Binary)
                        {
                            using (var data = new BinaryReader(new MemoryStream(m.Data)))
                            while (data.BaseStream.Position < data.BaseStream.Length)
                            {
                                int index = data.ReadUInt16(), length = data.ReadInt32();
                                if (index == 65535) { data.ReadInt32(); data.ReadBytes(length); continue; }
                                using (var bytes = new MemoryStream(data.ReadBytes(length)))
                                using (var image = System.Drawing.Image.FromStream(bytes))
                                {
                                    if (index >= cols * rows || image.Width != Math.Min(tile, 3840 - (index % cols) * tile) ||
                                        image.Height != Math.Min(tile, 2160 - (index / cols) * tile)) throw new Exception("4K geometry mismatch");
                                }
                                if (large) seen.Add(index);
                            }
                        }
                        else if (m.Op == WsOp.Text)
                        {
                            var o = Json.Parse(m.Text); string t = Json.Str(o, "t");
                            if (t == "tiles")
                            {
                                tile = Json.Int(o, "tile", 0); cols = Json.Int(o, "cols", 0); rows = Json.Int(o, "rows", 0);
                                int next = Json.Int(o, "view", 0); if (next <= view) throw new Exception("Tile grid reused viewport"); view = next;
                                if (tile == 1024) { large = true; Check(cols == 4 && rows == 3, "actual 4K sender negotiates 12 large tiles on fast ACKs"); }
                                else if (large) { Check(tile == 128 && cols == 30 && rows == 17, "slow ACKs restore 510 small tiles without changing resolution"); restored = true; }
                            }
                            else if (t == "frame")
                            {
                                if (seen.Count == 12) Thread.Sleep(210); // 큰 타일 전체를 확인한 뒤 느린 수신기를 모의한다.
                                Send(client.GetStream(), "{\"t\":\"ack\",\"n\":" + Json.Int(o, "n", 0) + "}");
                            }
                        }
                    }
                    Check(large && seen.Count == 12 && restored, "4K JPEG stream covers every tile then adapts to slowdown");
                }
                finally
                {
                    tx.Abort(); rx.Abort(); client.Close(); accepted.Close();
                    Check(sender.Join(2000) && acknowledgments.Join(2000), "4K capture and ACK threads terminate on disconnect");
                }
            }
        }
    }
}

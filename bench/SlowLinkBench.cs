// 실제 TCP/WebSocket에서 수신 속도를 제한한다. 화면 대신 40개 1KB 타일에 캡처 시각을 넣는다.
// 결과는 전송/대기 지연이며 실제 화면 캡처나 회선 전체의 성능 수치가 아니다.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
namespace RemoteControl
{
    internal sealed class TilePatch { internal int Index; internal byte[] Jpeg; }
    internal static class SlowLinkBench
    {
        public static int Main()
        {
            try
            {
                Run(0); Run(8 * 1024); Run(FrameFlow.PacketBytes); Run(32 * 1024);
                return 0;
            }
            catch (Exception ex) { Console.WriteLine(ex); return 1; }
        }
        private static void Run(int budget)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var client = new TcpClient(); client.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            var server = listener.AcceptTcpClient(); listener.Stop();
            client.NoDelay = server.NoDelay = true; server.SendBufferSize = 256 * 1024;
            var send = new WebSocketConn(server.GetStream()); var receive = new WebSocketConn(client.GetStream());
            var flow = new FrameFlow(); if (budget != 0) flow.Enable();
            var watch = Stopwatch.StartNew(); var ages = new List<double>();
            long bytes = 0;
            Thread reader = new Thread(delegate() {
                try { while (true) { var m = send.Receive(); if (m == null) break; if (m.Op == WsOp.Text) flow.Ack(int.Parse(m.Text)); } } catch { }
            }) { IsBackground = true }; reader.Start();
            Thread writer = new Thread(delegate() {
                try
                {
                    var tiles = new LatestTiles();
                    while (watch.ElapsedMilliseconds < 6500 && !send.Closed)
                    {
                        if (!flow.CanSend) { Thread.Sleep(1); continue; }
                        var patches = new List<TilePatch>();
                        for (int i = 0; i < 40; i++)
                        { var p = new byte[1024]; Buffer.BlockCopy(BitConverter.GetBytes(watch.ElapsedMilliseconds), 0, p, 0, 8); patches.Add(new TilePatch { Index = i, Jpeg = p }); }
                        tiles.Merge(patches);
                        if (budget == 0) send.SendBinary(tiles.Take(96 * 1024));
                        else while (tiles.Count > 0 && flow.CanSend)
                        { int n = flow.Begin(); send.SendBinary(tiles.Take(budget)); send.SendText(n.ToString()); }
                        Thread.Sleep(16);
                    }
                } catch { }
            }) { IsBackground = true }; writer.Start();
            while (watch.ElapsedMilliseconds < 6000)
            {
                var message = receive.Receive(); if (message == null) break;
                if (message.Op == WsOp.Binary)
                {
                    // 1 Mbps 데이터 전송 + 렌더링 4ms. ACK는 추가 80ms 뒤 도착한다.
                    Thread.Sleep((int)Math.Ceiling(message.Data.Length / 125.0) + 4);
                    if (watch.ElapsedMilliseconds > 1000)
                    {
                        bytes += message.Data.Length;
                        for (int i = 0; i < message.Data.Length;)
                        { int len = BitConverter.ToInt32(message.Data, i + 2); ages.Add(watch.ElapsedMilliseconds - BitConverter.ToInt64(message.Data, i + 6)); i += len + 6; }
                    }
                }
                else if (message.Op == WsOp.Text)
                {
                    string ack = message.Text;
                    ThreadPool.QueueUserWorkItem(delegate { Thread.Sleep(80); receive.SendText(ack); });
                }
            }
            send.Abort(); receive.Abort(); client.Close(); server.Close(); writer.Join(2000); reader.Join(2000);
            ages.Sort(); double total = 0; foreach (double age in ages) total += age;
            Console.WriteLine("{0}: mean={1:F0} ms p95={2:F0} ms delivered={3:F0} KB/s samples={4}",
                budget == 0 ? "previous (96KB, no ACK)" : "bounded " + (budget / 1024) + "KB x2",
                total / ages.Count, ages[(int)((ages.Count - 1) * .95)], bytes / 1024.0 / 5, ages.Count);
        }
    }
}

// 실제 TCP/WebSocket + 제품의 대기열/ACK 제어. 캡처 대신 4K 타일 수와 시각을 넣는다.
// JPEG/GPU/브라우저 비용은 포함하지 않는다. 프레임 수가 아닌 타일 전달 지연을 측정한다.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
namespace RemoteControl
{
    internal sealed class TilePatch { internal int Index; internal byte[] Jpeg; }
    internal static class FrameTransportBench
    {
        public static int Main(string[] args)
        {
            try { Run("4K loopback", 510, 2048, 0, 2); Run("slow link", 40, 1024, 125, 80); return 0; }
            catch (Exception ex) { Console.WriteLine(ex); return 1; }
        }
        private static void Run(string label, int count, int size, int bytesPerMs, int ackDelay)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var client = new TcpClient(); client.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            var server = listener.AcceptTcpClient(); listener.Stop();
            client.NoDelay = server.NoDelay = true; client.ReceiveTimeout = 15000;
            var send = new WebSocketConn(server.GetStream()); var receive = new WebSocketConn(client.GetStream());
            var flow = new FrameFlow(); flow.Enable();
            var watch = Stopwatch.StartNew(); var ages = new List<double>();
            var last = new long[count]; var intervals = new List<double>();
            long bytes = 0; Exception failure = null;
            Thread reader = new Thread(delegate() {
                try { while (true) { var m = send.Receive(); if (m == null) break; if (m.Op == WsOp.Text) flow.Ack(int.Parse(m.Text)); } }
                catch { }
            }) { IsBackground = true }; reader.Start();
            Thread writer = new Thread(delegate() {
                try
                {
                    var tiles = new LatestTiles();
                    var schedule = new FrameSchedule();
                    while (watch.ElapsedMilliseconds < 7000 && !send.Closed)
                    {
                        int change = flow.Change;
                        long started = watch.ElapsedMilliseconds;
                        if (!flow.CanSend) { flow.Wait(change, 16); continue; }
                        if (schedule.ShouldCapture(tiles.Count, flow.Budget))
                        {
                        schedule.StartCapture();
                        var patches = new List<TilePatch>();
                        for (int i = 0; i < count; i++)
                        { var p = new byte[size]; Buffer.BlockCopy(BitConverter.GetBytes(started), 0, p, 0, 8); patches.Add(new TilePatch { Index = i, Jpeg = p }); }
                        tiles.Merge(patches);
                        }
                        while (tiles.Count > 0 && flow.CanSend)
                        {
                            byte[] packet = tiles.Take(flow.Budget);
                            int n = flow.Begin(packet.Length); send.SendBinary(packet); send.SendText(n.ToString());
                            if (schedule.CaptureAge >= 100) break;
                        }
                        if (tiles.Count > 0 && flow.CanSend) continue;
                        flow.Wait(change, schedule.WaitMilliseconds);
                    }
                } catch (Exception ex) { if (!send.Closed) failure = ex; }
            }) { IsBackground = true }; writer.Start();
            while (watch.ElapsedMilliseconds < 6000)
            {
                var m = receive.Receive(); if (m == null) break;
                if (m.Op == WsOp.Binary)
                {
                    if (bytesPerMs > 0) Thread.Sleep((int)Math.Ceiling(m.Data.Length / (double)bytesPerMs) + 4);
                    long now = watch.ElapsedMilliseconds;
                    if (now > 1000)
                    {
                        bytes += m.Data.Length;
                        for (int p = 0; p < m.Data.Length;)
                        {
                            int index = BitConverter.ToUInt16(m.Data, p), length = BitConverter.ToInt32(m.Data, p + 2);
                            ages.Add(now - BitConverter.ToInt64(m.Data, p + 6));
                            if (last[index] > 0) intervals.Add(now - last[index]); last[index] = now;
                            p += length + 6;
                        }
                    }
                }
                else if (m.Op == WsOp.Text)
                {
                    string ack = m.Text;
                    ThreadPool.QueueUserWorkItem(delegate { Thread.Sleep(ackDelay); try { receive.SendText(ack); } catch { } });
                }
            }
            send.Abort(); receive.Abort(); client.Close(); server.Close(); writer.Join(2000); reader.Join(2000);
            if (failure != null) throw failure;
            ages.Sort(); intervals.Sort();
            Console.WriteLine("{0}: {1:F0} KiB/s, tile age p95={2:F0}ms, same-tile update p95={3:F0}ms, samples={4}",
                label, bytes / 1024.0 / 5, Percentile(ages), Percentile(intervals), ages.Count);
        }
        private static double Percentile(List<double> values) { return values.Count == 0 ? 0 : values[(int)((values.Count - 1) * .95)]; }
    }
}

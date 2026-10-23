// 소켓 Write 성공은 브라우저 표시 완료가 아니다. 중계/커널에 과거 화면을 쌓지 않는다.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Drawing;
using System.Threading;

namespace RemoteControl
{
    internal sealed class FrameFlow
    {
        internal const int PacketBytes = 16 * 1024;
        internal const int MinPacketBytes = 8 * 1024, MaxPacketBytes = 512 * 1024;
        private sealed class Pending
        {
            internal long At;
            internal int Bytes, Budget;
        }
        private readonly object gate = new object();
        private readonly Dictionary<int, Pending> sent = new Dictionary<int, Pending>();
        private readonly Stopwatch time = Stopwatch.StartNew();
        private bool enabled, negotiated;
        private int sequence;
        private double latency;
        private double networkLatency;
        private int probeNumber;
        private long probeAt = -1, lastProbe = -5000;
        private readonly Queue<double> probes = new Queue<double>();
        private int budget = PacketBytes, change;
        internal int Budget { get { lock (gate) return budget; } }
        internal int Change { get { lock (gate) return change; } }
        internal bool Enabled { get { lock (gate) return enabled; } }
        internal bool Ready { get { lock (gate) return negotiated || time.ElapsedMilliseconds >= 1500; } }
        internal bool CanSend { get { lock (gate) return !enabled || sent.Count < 2; } }
        internal bool TimedOut
        {
            get { lock (gate) { foreach (Pending p in sent.Values) if (time.ElapsedMilliseconds - p.At > 15000) return true; return false; } }
        }
        internal double Latency { get { lock (gate) return latency; } }
        internal double QueueLatency { get { lock (gate) return Math.Max(0, latency - networkLatency); } }
        internal bool ProbeDue { get { lock (gate) return enabled && time.ElapsedMilliseconds - lastProbe >= 5000; } }
        internal int Probe() { lock (gate) { lastProbe = probeAt = time.ElapsedMilliseconds; return ++probeNumber; } }
        internal void ProbeAck(int n)
        {
            lock (gate)
            {
                if (probeAt < 0 || n != probeNumber) return;
                probes.Enqueue(Math.Max(0, time.ElapsedMilliseconds - probeAt)); probeAt = -1;
                while (probes.Count > 12) probes.Dequeue();
                // A probe can itself queue behind an image. Use a rolling floor, not
                // the most congested sample, and let route changes age out in a minute.
                networkLatency = double.MaxValue;
                foreach (double sample in probes) networkLatency = Math.Min(networkLatency, sample);
            }
        }
        internal void Enable() { lock (gate) { enabled = negotiated = true; WakeLocked(); } }
        internal void Wake() { lock (gate) WakeLocked(); }
        private void WakeLocked() { change++; Monitor.PulseAll(gate); }
        internal void Wait(int observedChange, int milliseconds)
        {
            // ACK가 검사와 대기 사이에 도착해도 놓치지 않는다. 별도 커널 핸들도 필요 없다.
            lock (gate) { if (change == observedChange) Monitor.Wait(gate, milliseconds); }
        }
        internal int Begin() { return Begin(0); }
        internal int Begin(int bytes)
        {
            lock (gate)
            {
                if (!enabled) return 0;
                if (sent.Count >= 2) throw new InvalidOperationException("Frame window full");
                int n = ++sequence;
                sent.Add(n, new Pending { At = time.ElapsedMilliseconds, Bytes = bytes, Budget = budget });
                return n;
            }
        }
        internal void Ack(int n)
        {
            lock (gate)
            {
                Pending p;
                if (!sent.TryGetValue(n, out p)) return; // 중복/미래 ACK는 창을 열어 주지 않는다.
                sent.Remove(n);
                double ms = Math.Max(1, time.ElapsedMilliseconds - p.At);
                latency = latency == 0 ? ms : latency * .7 + ms * .3;
                // 16KB 고정은 4K에서 같은 타일을 다시 보내는 데 1초 이상 걸렸다.
                // 실제 디코딩 ACK가 빠르고 묶음을 채웠을 때만 늘린다. 소량의 커서/글자
                // 변화로 회선 용량을 과대평가하거나 같은 크기의 두 ACK로 두 번 늘리지 않는다.
                if (p.Budget == budget && p.Bytes > 0)
                {
                    // 작은 왕복 메시지로 잰 기본 RTT를 뺀다. 빠른 WAN을 단지 거리가
                    // 멀다는 이유로 16KB에 가두지 않고, 추가 전송/디코딩 대기만 제한한다.
                    double queued = Math.Max(1, ms - networkLatency);
                    if (queued > 160)
                        budget = Math.Max(MinPacketBytes, (int)(budget * Math.Max(.25, 100 / queued)));
                    else if (queued < 60 && p.Bytes >= budget * .75)
                        budget = Math.Min(MaxPacketBytes, budget * 2);
                }
                WakeLocked();
            }
        }
    }

    internal static class TilePolicy
    {
        internal static int Choose(int maximum, int width, int height, int budget)
        {
            if (maximum < 1024) return 128; // old browsers negotiate only 128
            if (budget < 24 * 1024) return 128;
            if (budget < 64 * 1024) return 256;
            if (budget < 192 * 1024 || (long)width * height < 3840L * 2160) return 512;
            return 1024;
        }
    }

    internal sealed class FrameSchedule
    {
        private readonly Stopwatch time = Stopwatch.StartNew();
        private long next;
        private long started;
        internal bool CaptureDue { get { return time.ElapsedMilliseconds >= next; } }
        internal long CaptureAge { get { return time.ElapsedMilliseconds - started; } }
        internal bool ShouldCapture(int pending, int budget)
        { return CaptureDue && (pending == 0 || budget <= FrameFlow.PacketBytes || CaptureAge >= 100); }
        internal void StartCapture() { started = time.ElapsedMilliseconds; next = started + 16; }
        internal int WaitMilliseconds { get { return (int)Math.Max(1, Math.Min(16, next - time.ElapsedMilliseconds)); } }
    }

    internal sealed class LatestTiles
    {
        private readonly Dictionary<int, TilePatch> pending = new Dictionary<int, TilePatch>();
        private readonly Queue<int> order = new Queue<int>();
        internal int Count { get { return pending.Count; } }
        internal void Clear() { pending.Clear(); order.Clear(); }
        internal void Merge(List<TilePatch> patches)
        {
            foreach (TilePatch p in patches)
            {
                if (!pending.ContainsKey(p.Index)) order.Enqueue(p.Index);
                pending[p.Index] = p; // 안 보낸 옛 타일을 버리고 최신 내용으로 덮는다. 순서는 유지해 굶지 않게 한다.
            }
        }
        internal byte[] Take(int budget)
        {
            using (MemoryStream ms = new MemoryStream(budget))
            using (BinaryWriter writer = new BinaryWriter(ms))
            {
                while (order.Count > 0)
                {
                    TilePatch p = pending[order.Peek()];
                    if (ms.Length > 0 && ms.Length + p.Jpeg.Length + 6 > budget) break;
                    order.Dequeue(); pending.Remove(p.Index);
                    writer.Write((ushort)p.Index); writer.Write(p.Jpeg.Length); writer.Write(p.Jpeg);
                }
                return ms.ToArray();
            }
        }
    }

    internal interface IRealtimeVideo : IDisposable
    {
        int Id { get; }
        bool Active { get; set; }
        bool Expired { get; }
        void Heartbeat();
        void Send(string value);
        bool TryRead(out string value);
    }

    // 모든 출력/화면 변경을 송신 스레드에 맡긴다. 느린 Write가 입력 수신을 막지 않게 한다.
    internal sealed class StreamCommands
    {
        private readonly object gate = new object();
        private readonly Queue<Action> pending = new Queue<Action>();
        internal int View = 1;
        internal volatile InputViewport Mapping;
        internal bool AutoQuality = true;
        internal int MaxTileSize = 128;
        internal bool Paused;
        internal IRealtimeVideo Video;
        internal int VideoAttemptView = -1;
        internal int VideoAttemptAt = Environment.TickCount - 10000;
        internal readonly LatestTiles Tiles = new LatestTiles();
        internal readonly FrameFlow Flow = new FrameFlow();
        internal void Post(Action action)
        {
            lock (gate)
            {
                if (pending.Count >= 128) throw new IOException("Too many pending stream commands");
                pending.Enqueue(action);
            }
            Flow.Wake();
        }
        internal void Drain()
        {
            // 연속 요청에도 화면과 ACK 처리가 굶지 않게 한 번에 제한한다.
            for (int i = 0; i < 16; i++)
            {
                Action a;
                lock (gate) { if (pending.Count == 0) return; a = pending.Dequeue(); }
                a();
            }
        }
    }

    internal sealed class InputViewport
    {
        internal readonly int View;
        private readonly Rectangle area;
        private readonly int width, height;
        internal InputViewport(Rectangle bounds, int w, int h, int view)
        { area = bounds; width = Math.Max(1, w); height = Math.Max(1, h); View = view; }
        internal int X(int x) { return area.X + (int)((long)Math.Max(0, Math.Min(width - 1, x)) * area.Width / width); }
        internal int Y(int y) { return area.Y + (int)((long)Math.Max(0, Math.Min(height - 1, y)) * area.Height / height); }
    }
}

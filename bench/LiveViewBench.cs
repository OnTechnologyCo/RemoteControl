// 실제 화면 전송 경로(Server.DoWebSocket → StreamLoop → ScreenSource → viewer.js)를 그대로 돌리고
// 화면만 합성 장면으로 바꿔 "화면이 바뀐 순간 → 브라우저 canvas에 그려진 순간"을 잰다.
//
// 왜 따로 만들었나: 기존 벤치는 단계별(압축만, 전송만, 디코딩만)이라 합계가 안 보였다.
// 사용자는 LAN 1440p에서 "끊김·늦음·흐림"을 동시에 겪는데, 어느 단계가 막는지는
// 끝에서 끝까지 재야만 알 수 있다. 합계만 재면 엉뚱한 데를 고친다(AGENTS.md) — 그래서
// 합성 화면의 모서리마다 시간값을 찍어 둔다. 브라우저가 그 픽셀을 읽어 지연을 계산한다.
//
// 실제 화면은 읽지 않는다(DesktopDup 대역). 입력도 넣지 않는다(SendInput 대역).
// 운영 서비스·설정은 건드리지 않는다. 127.0.0.1 임시 포트에서만 듣는다.
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace RemoteControl
{
    /// <summary>60Hz 모니터처럼 16.7ms마다 한 장씩만 새 그림이 생기는 합성 화면.</summary>
    internal static class LiveScene
    {
        // 8x4 칸 x 14px = 112x56. 128 타일 하나 안에 들어가야 한 번에 그려진다
        // (여러 타일에 걸치면 옛 비트와 새 비트가 섞여 엉뚱한 시각이 읽힌다).
        internal const int Block = 14, BarCols = 8, BarRows = 4, Inset = 8;
        internal static string Kind = "scroll";
        internal static double Hz = 60;
        private static readonly Stopwatch clock = Stopwatch.StartNew();
        private static long lastVsync = -1;
        private static int width, height;
        private static Bitmap page, texture;
        private static int typed;
        internal static long Frames, Waits;

        [DllImport("kernel32.dll")] private static extern void GetSystemTimePreciseAsFileTime(out long fileTime);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateWaitableTimerExW(IntPtr attributes, string name, uint flags, uint access);
        [DllImport("kernel32.dll")] private static extern bool SetWaitableTimer(IntPtr timer, ref long due, int period, IntPtr routine, IntPtr arg, bool resume);
        [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr handle, uint ms);
        [DllImport("msvcrt.dll", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr memcpy(IntPtr dst, IntPtr src, UIntPtr count);
        [ThreadStatic] private static IntPtr timer;

        /// <summary>Unix ms 하위 32비트. 브라우저 Date.now()와 같은 시계다.</summary>
        internal static uint Stamp() { long ft; GetSystemTimePreciseAsFileTime(out ft); return (uint)((ft - 116444736000000000L) / 10000); }

        // 실제 AcquireNextFrame은 새 화면이 나오는 순간 깨어난다. Thread.Sleep은 15.6ms 단위로
        // 늦어 캡처 쪽을 실제보다 나쁘게 만든다. 고해상도 대기 타이머로 vsync를 흉내 낸다.
        internal static double SleptMs;
        internal static void SleepUntil(Stopwatch watch, double atMs) { PreciseSleep(atMs - watch.Elapsed.TotalMilliseconds); }
        private static void PreciseSleep(double ms)
        {
            if (ms <= 0) return;
            SleptMs += ms;
            if (timer == IntPtr.Zero) timer = CreateWaitableTimerExW(IntPtr.Zero, null, 2 /*HIGH_RESOLUTION*/, 0x1F0003);
            long due = -(long)(ms * 10000);
            if (timer != IntPtr.Zero && SetWaitableTimer(timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false)) WaitForSingleObject(timer, 1000);
            else Thread.Sleep((int)Math.Ceiling(ms));
        }

        private static long VsyncIndex() { return (long)(clock.Elapsed.TotalMilliseconds * Hz / 1000); }

        /// <summary>DesktopDup.Grab 대역. 새 vsync가 없으면 최대 12ms 기다린 뒤 "변화 없음".</summary>
        internal static int Grab(Bitmap frame, int fw, int fh, out Rectangle[] dirty)
        {
            dirty = null;
            long now = VsyncIndex();
            if (now == lastVsync)
            {
                double next = (now + 1) * 1000.0 / Hz - clock.Elapsed.TotalMilliseconds;
                Interlocked.Increment(ref Waits);
                if (next > 12) { PreciseSleep(12); return DesktopDup.GrabNoChange; }
                PreciseSleep(next);
                now = VsyncIndex();
                if (now == lastVsync) return DesktopDup.GrabNoChange;
            }
            lastVsync = now;
            if (width != fw || height != fh || page == null) Build(fw, fh);
            dirty = Render(frame, now);
            // 실제 DD도 바뀐 것이 없으면 새 그림을 주지 않는다. 빈 dirty로 "새 그림"을 주면
            // ScreenSource가 전체 지문을 다시 계산해 실제보다 느려진다.
            if (dirty.Length == 0) return DesktopDup.GrabNoChange;
            Interlocked.Increment(ref Frames);
            return DesktopDup.GrabNewFrame;
        }

        private static bool primed;

        // 실제 DD는 새로 열면 첫 장에 화면 전체를 준다. ScreenSource는 타일 크기·배율이 바뀌면
        // 새(검은) 버퍼를 만들고 DD를 다시 연다. 다시 칠하지 않으면 바뀐 자리 외엔 검게 남아
        // JPEG가 비정상적으로 작아진다(처음 측정에서 영상 장면이 그렇게 틀렸다).
        internal static void Reprime() { primed = false; }

        private static void Build(int w, int h)
        {
            width = w; height = h; typed = 0; primed = false; typedKey = -1;
            if (page != null) page.Dispose();
            if (texture != null) texture.Dispose();
            page = new Bitmap(w, h * 3, PixelFormat.Format32bppArgb);
            var rnd = new Random(7);
            using (var g = Graphics.FromImage(page))
            using (var body = new Font("Malgun Gothic", 11f, FontStyle.Regular, GraphicsUnit.Point))
            using (var head = new Font("Malgun Gothic", 18f, FontStyle.Bold, GraphicsUnit.Point))
            using (var mono = new Font("Consolas", 10.5f, FontStyle.Regular, GraphicsUnit.Point))
            {
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                g.Clear(Color.White);
                // 웹 문서 같은 화면: 본문 글자, 제목, 코드 상자, 사진. 글자 위주라 JPEG 크기가 실제 문서와 비슷하다.
                string[] words = { "원격제어", "화면", "전송", "지연", "타일", "JPEG", "브라우저", "디코딩", "network", "frame", "latency",
                    "capture", "encoder", "설정", "파일", "업로드", "모니터", "해상도", "Desktop", "Duplication", "WebSocket", "ACK" };
                int y = 20, col = 0;
                int colW = Math.Max(600, (w - 80) / 2);
                while (y < h * 3 - 40)
                {
                    int x0 = 40 + col * (colW + 20);
                    int r = rnd.Next(10);
                    if (r == 0) { g.DrawString("섹션 " + y + " — 화면 전송 측정", head, Brushes.Black, x0, y); y += 44; }
                    else if (r == 1)
                    {
                        g.FillRectangle(new SolidBrush(Color.FromArgb(246, 248, 250)), x0, y, colW - 20, 120);
                        for (int i = 0; i < 6; i++) g.DrawString("for (int i = 0; i < tiles.Length; i++) send(tiles[i], quality=" + rnd.Next(100) + ");", mono, Brushes.DarkSlateBlue, x0 + 8, y + 6 + i * 18);
                        y += 134;
                    }
                    else if (r == 2)
                    {
                        var box = new Rectangle(x0, y, Math.Min(colW - 20, 520), 200);
                        using (var lg = new LinearGradientBrush(box, Color.FromArgb(rnd.Next(256), rnd.Next(256), rnd.Next(256)), Color.FromArgb(rnd.Next(256), rnd.Next(256), rnd.Next(256)), (float)rnd.Next(360)))
                            g.FillRectangle(lg, box);
                        for (int i = 0; i < 30; i++)
                            using (var b = new SolidBrush(Color.FromArgb(160, rnd.Next(256), rnd.Next(256), rnd.Next(256))))
                                g.FillEllipse(b, box.X + rnd.Next(box.Width - 40), box.Y + rnd.Next(box.Height - 40), 20 + rnd.Next(60), 20 + rnd.Next(60));
                        y += 214;
                    }
                    else
                    {
                        var line = new StringBuilder();
                        while (line.Length < colW / 9) line.Append(words[rnd.Next(words.Length)]).Append(' ');
                        g.DrawString(line.ToString(), body, Brushes.Black, x0, y); y += 24;
                    }
                    if (y >= h * 3 - 40 && col == 0) { col = 1; y = 20; }
                }
            }
            // 영상 장면용: 사진처럼 부드러운 색 변화 + 잔무늬. 잡음만 있으면 실제 영상보다 훨씬 무겁다.
            texture = new Bitmap(w * 2, h * 2, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(texture))
            {
                using (var lg = new LinearGradientBrush(new Rectangle(0, 0, w * 2, h * 2), Color.DarkBlue, Color.Orange, 35f)) g.FillRectangle(lg, 0, 0, w * 2, h * 2);
                for (int i = 0; i < 900; i++)
                    using (var b = new SolidBrush(Color.FromArgb(90 + rnd.Next(120), rnd.Next(256), rnd.Next(256), rnd.Next(256))))
                        g.FillEllipse(b, rnd.Next(w * 2), rnd.Next(h * 2), 10 + rnd.Next(160), 10 + rnd.Next(160));
            }
        }

        private static Rectangle VideoBox(int w, int h) { int vw = Math.Min(1280, w - 200), vh = Math.Min(720, h - 200); return new Rectangle((w - vw) / 2 / 16 * 16, (h - vh) / 2 / 16 * 16, vw, vh); }

        // 시간값을 찍는 위치. 128 격자에 맞춘 칸 안이면 256/512/1024 격자에서도 한 타일 안이다.
        internal static Point[] Markers(int w, int h, string kind)
        {
            if (kind == "video")
            {
                // 영상 창의 왼쪽 위와 오른쪽 아래. 창 전체가 한 장으로 갱신되는지 본다.
                var v = VideoBox(w, h);
                return new[] { new Point((v.X + 127) / 128 * 128 + Inset, (v.Y + 127) / 128 * 128 + Inset),
                               new Point(v.Right / 128 * 128 - 128 + Inset, v.Bottom / 128 * 128 - 128 + Inset) };
            }
            if (kind == "typing") return new[] { new Point(Inset, 256 + Inset) };
            return new[] { new Point(Inset, Inset), new Point(w / 128 * 128 - 128 + Inset, h / 128 * 128 - 128 + Inset) };
        }

        private static Rectangle[] Render(Bitmap frame, long vsync)
        {
            int w = width, h = height;
            var dirty = new System.Collections.Generic.List<Rectangle>();
            if (Kind == "scroll")
            {
                // 초당 360px로 문서를 내린다. 실제 스크롤처럼 화면 전체가 매 장 바뀐다.
                int offset = (int)(vsync * 6 % (h * 2));
                Copy(page, 0, offset, frame, new Rectangle(0, 0, w, h));
                dirty.Add(new Rectangle(0, 0, w, h)); primed = true;
            }
            else if (Kind == "video")
            {
                // 문서 화면 가운데 1280x720 창에서 영상이 재생되는 경우.
                var box = VideoBox(w, h);
                if (!primed) { Copy(page, 0, 0, frame, new Rectangle(0, 0, w, h)); dirty.Add(new Rectangle(0, 0, w, h)); primed = true; }
                int ox = (int)(vsync * 5 % w), oy = (int)(vsync * 3 % h);
                Copy(texture, ox, oy, frame, box);
                dirty.Add(box);
            }
            else // typing: 0.12초마다 한 글자. 지연이 중요하고 양은 적다.
            {
                if (!primed) { Copy(page, 0, 0, frame, new Rectangle(0, 0, w, h)); dirty.Add(new Rectangle(0, 0, w, h)); primed = true; }
                long key = (long)(vsync / (Hz * 0.12));
                if (dirty.Count == 0 && key == typedKey) return new Rectangle[0]; // 화면 변화 없음 (커서 깜빡임도 없다)
                typedKey = key; typed++;
                using (var g = Graphics.FromImage(frame))
                using (var f = new Font("Malgun Gothic", 12f))
                {
                    int cx = 160 + (typed % 160) * 12, cy = 256 + 16 + (typed / 160 % 20) * 26;
                    if (cx + 12 > w) cx = 160;
                    g.FillRectangle(Brushes.White, cx, cy, 14, 24);
                    g.DrawString(((char)('가' + typed % 400)).ToString(), f, Brushes.Black, cx, cy);
                    dirty.Add(new Rectangle(cx, cy, 14, 24));
                }
            }
            uint stamp = Stamp();
            foreach (Point m in Markers(w, h, Kind)) { Bar(frame, m.X, m.Y, stamp); dirty.Add(new Rectangle(m.X, m.Y, BarCols * Block, BarRows * Block)); }
            return dirty.ToArray();
        }
        private static long typedKey = -1;

        private static void Bar(Bitmap frame, int x, int y, uint value)
        {
            var bd = frame.LockBits(new Rectangle(x, y, BarCols * Block, BarRows * Block), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                unsafe
                {
                    for (int yy = 0; yy < BarRows * Block; yy++)
                    {
                        int* row = (int*)((byte*)bd.Scan0 + yy * bd.Stride);
                        for (int xx = 0; xx < BarCols * Block; xx++)
                        {
                            int bit = (yy / Block) * BarCols + xx / Block;
                            row[xx] = ((value >> bit) & 1) != 0 ? unchecked((int)0xFF000000) : unchecked((int)0xFFFFFFFF);
                        }
                    }
                }
            }
            finally { frame.UnlockBits(bd); }
        }

        private static void Copy(Bitmap from, int sx, int sy, Bitmap to, Rectangle dst)
        {
            var s = from.LockBits(new Rectangle(sx, sy, dst.Width, dst.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var d = to.LockBits(dst, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                for (int y = 0; y < dst.Height; y++)
                    memcpy(new IntPtr(d.Scan0.ToInt64() + (long)y * d.Stride), new IntPtr(s.Scan0.ToInt64() + (long)y * s.Stride), (UIntPtr)(uint)(dst.Width * 4));
            }
            finally { to.UnlockBits(d); from.UnlockBits(s); }
        }
    }

    internal static class Desktops
    { internal static bool Sync() { return false; } internal static string ThreadDesktopName() { return "bench"; } internal static string InputDesktopName() { return "bench"; } }

    internal sealed class DesktopDup : IDisposable
    {
        internal const int GrabFailed = 0, GrabNoChange = 1, GrabNewFrame = 2;
        internal bool DirtyKnown { get; private set; }
        internal Rectangle[] Dirty { get; private set; }
        internal int SrcW { get; private set; }
        internal int SrcH { get; private set; }
        internal static void FindOutput(Rectangle area, out int adapter, out int output) { adapter = output = -1; }
        internal static DesktopDup Open(Rectangle area) { LiveScene.Reprime(); return new DesktopDup { SrcW = area.Width, SrcH = area.Height, Dirty = new Rectangle[0] }; }
        internal int Grab(Bitmap frame, Rectangle area, int fw, int fh)
        {
            // 실패를 돌려주면 ScreenSource가 GDI로 실제 화면을 읽는다. 절대 실패하지 않고,
            // 축소 전송이면 합성 화면을 그 크기로 직접 그린다(원본 좌표 = 그림 좌표).
            SrcW = fw; SrcH = fh;
            Rectangle[] dirty;
            int r = LiveScene.Grab(frame, fw, fh, out dirty);
            if (r == GrabNewFrame) { Dirty = dirty; DirtyKnown = dirty != null && dirty.Length > 0; }
            return r;
        }
        public void Dispose() { }
    }

    /// <summary>
    /// 무선 LAN 흉내: 서버→브라우저 대역폭과 왕복 지연을 제한하는 중계.
    /// Node setTimeout 은 Windows 기본 타이머(15.6ms)에 묶여 RTT 4ms 가 30ms 이상이 됐다.
    /// 고해상도 대기 타이머로 묶음마다 출발·도착 시각을 계산해 보낸다.
    /// </summary>
    internal sealed class Shaper
    {
        private sealed class Chunk { internal double Due; internal byte[] Data; internal int Length; }
        private readonly double bytesPerMs, halfRtt;
        private readonly int target;
        private static readonly Stopwatch clock = Stopwatch.StartNew();
        internal Shaper(int targetPort, double mbps, double rttMs) { target = targetPort; bytesPerMs = mbps > 0 ? mbps * 1e6 / 8 / 1000 : 0; halfRtt = rttMs / 2; }
        internal int Start()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            new Thread(delegate() {
                while (true)
                {
                    TcpClient client = listener.AcceptTcpClient(), upstream = new TcpClient();
                    upstream.Connect(IPAddress.Loopback, target);
                    client.NoDelay = upstream.NoDelay = true;
                    Pump(upstream, client, bytesPerMs); Pump(client, upstream, 0);
                }
            }) { IsBackground = true }.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        private void Pump(TcpClient from, TcpClient to, double rate)
        {
            var queue = new System.Collections.Generic.Queue<Chunk>(); var gate = new object();
            long queued = 0; bool ended = false; double free = 0;
            new Thread(delegate() {
                try
                {
                    var s = from.GetStream();
                    while (true)
                    {
                        var buf = new byte[64 * 1024]; int n = s.Read(buf, 0, buf.Length);
                        if (n <= 0) break;
                        double now = clock.Elapsed.TotalMilliseconds;
                        free = Math.Max(now, free) + (rate > 0 ? n / rate : 0);
                        lock (gate)
                        {
                            // 공유기 버퍼 흉내: 512KB가 차면 읽지 않아 TCP가 서버를 막게 한다.
                            while (queued > 512 * 1024) Monitor.Wait(gate, 50);
                            queue.Enqueue(new Chunk { Due = free + halfRtt, Data = buf, Length = n }); queued += n; Monitor.PulseAll(gate);
                        }
                    }
                }
                catch { }
                lock (gate) { ended = true; Monitor.PulseAll(gate); }
            }) { IsBackground = true }.Start();
            new Thread(delegate() {
                try
                {
                    var s = to.GetStream();
                    while (true)
                    {
                        Chunk c;
                        lock (gate) { while (queue.Count == 0 && !ended) Monitor.Wait(gate, 50); if (queue.Count == 0) break; c = queue.Peek(); }
                        LiveScene.SleepUntil(clock, c.Due);
                        s.Write(c.Data, 0, c.Length);
                        lock (gate) { queue.Dequeue(); queued -= c.Length; Monitor.PulseAll(gate); }
                    }
                }
                catch { }
                try { to.Client.Shutdown(SocketShutdown.Send); } catch { }
            }) { IsBackground = true }.Start();
        }
    }

    internal static class LiveViewBench
    {
        private sealed class NoCover : IPrivacyCover
        {
            public bool IsRunning { get { return false; } }
            public bool Start(out string error) { error = "bench"; return false; }
            public void Stop() { }
        }
        internal static long BlockedInputs;
        internal static uint BlockInput(uint n) { Interlocked.Increment(ref BlockedInputs); return n; }

        // 네트워크·브라우저 없이 ScreenSource.Grab 한 번의 비용을 단계별로 나눠 잰다.
        // 끝에서 끝까지 잰 FPS가 낮을 때 그 원인이 캡처·지문·압축 중 어디인지 가르기 위한 것.
        private static int GrabCost(string[] args)
        {
            int w = int.Parse(args[1]), h = int.Parse(args[2]), tile = int.Parse(args[3]), quality = int.Parse(args[4]);
            LiveScene.Kind = args.Length > 5 ? args[5] : "scroll";
            using (var src = new ScreenSource(new Rectangle(0, 0, w, h)))
            {
                src.TileSize = tile; src.Quality = quality;
                for (int i = 0; i < 20; i++) src.Grab(); // JIT, 첫 전체 화면
                var timer = Stopwatch.StartNew(); var each = new System.Collections.Generic.List<double>();
                long bytes = 0; int frames = 0, patches = 0;
                while (timer.ElapsedMilliseconds < 4000)
                {
                    double slept = LiveScene.SleptMs;
                    var one = Stopwatch.StartNew();
                    var list = src.Grab();
                    if (list.Count == 0) continue; // vsync 대기만 하고 새 그림이 없던 호출
                    // vsync 대기(실제 AcquireNextFrame의 노는 시간)는 비용이 아니다. 빼고 센다.
                    each.Add(one.Elapsed.TotalMilliseconds - (LiveScene.SleptMs - slept)); frames++; patches += list.Count;
                    foreach (var p in list) bytes += p.Jpeg.Length;
                    if (frames == 100 && Environment.GetEnvironmentVariable("RC_BENCH_TILES") == "1")
                        foreach (var p in list) Console.WriteLine("  tile " + p.Index + " " + p.Jpeg.Length + " B");
                }
                each.Sort();
                Console.WriteLine(string.Format("GRAB {0}x{1} tile {2} q{3} {4}: {5} frames, mean {6:F1}ms p50 {7:F1} p95 {8:F1}, {9:F0} KB/frame, {10:F1} tiles/frame",
                    w, h, tile, quality, LiveScene.Kind, frames, Average(each), each[each.Count / 2], each[(int)(each.Count * .95)], bytes / 1024.0 / frames, patches / (double)frames));
            }
            // 대리 프로세스는 timeBeginPeriod를 부르지 않는다. 짧은 Monitor.Wait가 실제로 얼마나 자는지 본다.
            var gate = new object(); var waits = new System.Collections.Generic.List<double>();
            for (int i = 0; i < 40; i++) { var t = Stopwatch.StartNew(); lock (gate) Monitor.Wait(gate, 5); waits.Add(t.Elapsed.TotalMilliseconds); }
            waits.Sort();
            Console.WriteLine(string.Format("WAIT Monitor.Wait(5ms): p50 {0:F1}ms p95 {1:F1}ms", waits[20], waits[38]));
            return 0;
        }
        private static double Average(System.Collections.Generic.List<double> v) { double s = 0; foreach (double x in v) s += x; return s / Math.Max(1, v.Count); }

        // args: <web dir> <probe.js> <scene> [mbps] [rtt ms]  |  grab <w> <h> <tile> <quality> [scene]
        public static int Main(string[] args)
        {
            if (args[0] == "grab") return GrabCost(args);
            string web = args[0], probe = args[1];
            LiveScene.Kind = args.Length > 2 ? args[2] : "scroll";
            PrivacyScreen.CoverFactory = delegate { return new NoCover(); };
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            double mbps = args.Length > 3 ? double.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture) : 0;
            double rtt = args.Length > 4 ? double.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture) : 0;
            Console.WriteLine("PORT " + (mbps > 0 || rtt > 0 ? new Shaper(port, mbps, rtt).Start() : port));
            var self = Process.GetCurrentProcess();
            var cpuTimer = new Thread(delegate() {
                TimeSpan last = self.TotalProcessorTime; long frames = 0;
                while (true)
                {
                    Thread.Sleep(1000); self.Refresh();
                    TimeSpan now = self.TotalProcessorTime; long f = Interlocked.Read(ref LiveScene.Frames);
                    Console.WriteLine("CPU " + (now - last).TotalMilliseconds.ToString("F0") + " SCENE " + (f - frames));
                    last = now; frames = f;
                }
            }) { IsBackground = true };
            cpuTimer.Start();
            MethodInfo doWs = typeof(Server).GetMethod("DoWebSocket", BindingFlags.Instance | BindingFlags.NonPublic);
            var server = new Server();
            while (true)
            {
                TcpClient client = listener.AcceptTcpClient();
                client.NoDelay = true; // 제품 AcceptLoop와 같게
                new Thread(delegate() {
                    try
                    {
                        using (client)
                        {
                            NetworkStream stream = client.GetStream(); byte[] left;
                            string head = Server.ReadHead(stream, out left);
                            if (head == null) return;
                            string[] lines = head.Split(new[] { "\r\n" }, StringSplitOptions.None);
                            string path = lines[0].Split(' ')[1]; int q = path.IndexOf('?'); if (q >= 0) path = path.Substring(0, q);
                            if (path == "/ws") { doWs.Invoke(server, new object[] { stream, lines, new Session { User = "bench" }, "127.0.0.1" }); return; }
                            if (path == "/peers") { Server.Send(stream, "200 OK", "application/json", Encoding.UTF8.GetBytes("{\"peers\":[]}"), "Cache-Control: no-store\r\n"); return; }
                            if (path == "/") path = "/viewer.html";
                            string file = path == "/bench.js" ? probe : Path.Combine(web, path.TrimStart('/'));
                            if (path.Contains("..") || !File.Exists(file)) { Server.Send(stream, "404 Not Found", "text/plain", new byte[0], ""); return; }
                            byte[] body = File.ReadAllBytes(file);
                            if (path == "/viewer.html") body = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(body).Replace("</body>", "<script src=\"/bench.js\"></script></body>"));
                            Server.Send(stream, "200 OK", file.EndsWith(".html") ? "text/html; charset=utf-8" : "text/javascript; charset=utf-8", body, "Cache-Control: no-store\r\n");
                        }
                    }
                    catch (Exception ex) { if (!(ex is IOException) && !(ex.InnerException is IOException)) Console.Error.WriteLine(ex); }
                }) { IsBackground = true }.Start();
            }
        }
    }
}

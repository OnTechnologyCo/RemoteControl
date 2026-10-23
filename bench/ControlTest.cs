// 실제 SendInput 대신 기록기를 연결한다. 실행 중인 사용자의 키/마우스는 건드리지 않는다.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading;
namespace RemoteControl
{
    internal static class ControlTest
    {
        internal static readonly List<Native.INPUT> Events = new List<Native.INPUT>();
        internal static bool Reject;
        private static int checks;
        internal static uint Record(uint count, Native.INPUT[] input)
        { if (Reject) return 0; for (int i = 0; i < count; i++) Events.Add(input[i]); return count; }
        private static void Check(bool value, string message)
        { if (!value) throw new Exception(message); checks++; Console.WriteLine("PASS " + message); }
        private static int Flags(uint flag) { return Events.FindAll(delegate(Native.INPUT e) { return e.type == Native.INPUT_MOUSE && e.U.mi.dwFlags == flag; }).Count; }
        public static int Main()
        {
            try
            {
                using (var input = new InputInjector()) { input.Release(); }
                Check(Events.Count == 0, "disconnect without input sends no mouse/key release");
                using (var input = new InputInjector()) { input.Button(10, 10, 2, false); input.Key(17, false); }
                Check(Events.Count == 0, "unmatched right-button/key up ignored");
                using (var input = new InputInjector()) { input.Button(10, 10, 0, true); }
                Check(Flags(Native.MOUSEEVENTF_LEFTUP) == 1 && Flags(Native.MOUSEEVENTF_RIGHTUP) == 0, "left drag disconnect releases only left button");
                Events.Clear();
                using (var input = new InputInjector()) { input.Button(10, 10, 2, true); input.Button(10, 10, 2, false); }
                Check(Flags(Native.MOUSEEVENTF_RIGHTDOWN) == 1 && Flags(Native.MOUSEEVENTF_RIGHTUP) == 1, "intentional right click retained without extra release on close");
                Events.Clear();
                using (var a = new InputInjector()) using (var b = new InputInjector())
                {
                    a.Key(17, true); b.Key(17, true); a.Release();
                    Check(Events.Count == 1, "disconnect cannot release another viewer's held Ctrl");
                    b.Release(); Check(Events.Count == 2 && Events[1].U.ki.dwFlags == Native.KEYEVENTF_KEYUP, "last owner releases Ctrl once");
                }
                Events.Clear();
                using (var a = new InputInjector()) { a.Key(65, true); a.Key(65, true); a.Release(); a.Release(); }
                Check(Events.Count == 3 && Events[2].U.ki.wVk == 65, "key repeat and non-modifier cleanup preserved");
                Events.Clear(); Reject = true;
                using (var a = new InputInjector()) { a.Button(1, 1, 2, true); a.Key(17, true); Reject = false; }
                Check(Events.Count == 0, "failed injection does not manufacture later releases");
                var viewport = new InputViewport(new Rectangle(-1920, 0, 1920, 1200), 960, 600, 7);
                Check(viewport.X(480) == -960 && viewport.Y(300) == 600 && viewport.View == 7, "immutable scaled multi-monitor input mapping");
                var flow = new FrameFlow(); flow.Enable(); int n1 = flow.Begin(), n2 = flow.Begin();
                Check(!flow.CanSend, "two packets exhaust the send window");
                flow.Ack(9999); Check(!flow.CanSend, "unknown ACK cannot bypass the window");
                flow.Ack(n2); Check(flow.CanSend, "out-of-order valid ACK reopens the window");
                flow.Begin(); flow.Ack(n2); Check(!flow.CanSend, "duplicate ACK cannot bypass the window"); flow.Ack(n1);
                var adaptive = new FrameFlow(); adaptive.Enable();
                for (int i = 0; i < 8; i++) { int n = adaptive.Begin(adaptive.Budget); adaptive.Ack(n); }
                Check(adaptive.Budget == FrameFlow.MaxPacketBytes, "fast full batches grow to a bounded LAN budget");
                int slow = adaptive.Begin(adaptive.Budget); Thread.Sleep(190); adaptive.Ack(slow);
                Check(adaptive.Budget < FrameFlow.MaxPacketBytes, "slow decoding or network response shrinks the budget");
                int budget = adaptive.Budget;
                for (int i = 0; i < 8; i++) { int n = adaptive.Begin(10); adaptive.Ack(n); }
                adaptive.Ack(slow); adaptive.Ack(99999);
                Check(adaptive.Budget == budget, "tiny updates and duplicate/future ACKs cannot inflate the budget");
                int observed = adaptive.Change; adaptive.Wake(); var wait = System.Diagnostics.Stopwatch.StartNew(); adaptive.Wait(observed, 100);
                Check(wait.ElapsedMilliseconds < 50, "wake between condition check and wait is not lost");
                var distant = new FrameFlow(); distant.Enable(); int probe = distant.Probe(); Thread.Sleep(80); distant.ProbeAck(probe);
                int far = distant.Begin(distant.Budget); Thread.Sleep(90); distant.Ack(far);
                Check(distant.Budget > FrameFlow.PacketBytes, "high base RTT alone does not throttle a fast WAN");
                Check(TilePolicy.Choose(1024, 3840, 2160, 32768) == 256, "medium budget retains useful 256-pixel tiles");
                Check(TilePolicy.Choose(1024, 3840, 2160, 131072) == 512, "moderate bandwidth uses 512-pixel tiles");
                Check(TilePolicy.Choose(128, 3840, 2160, 524288) == 128, "old viewer keeps its negotiated grid");
                IntPtr metadata = System.Runtime.InteropServices.Marshal.AllocHGlobal(40);
                try
                {
                    int[] values = { 5, 7, 20, 30, 100, 120, 140, 150, 180, 190 };
                    System.Runtime.InteropServices.Marshal.Copy(values, 0, metadata, values.Length);
                    var regions = new List<Rectangle>();
                    Check(DesktopRegions.Append(metadata, 24, 40, true, 256, 256, regions) && regions[0] == new Rectangle(20, 30, 80, 90), "move destination is preserved beside ordinary dirty regions");
                    Check(DesktopRegions.Append(new IntPtr(metadata.ToInt64() + 24), 16, 16, false, 256, 256, regions) && regions.Count == 2, "mixed move and dirty metadata is combined");
                    Check(!DesktopRegions.Append(metadata, 23, 40, true, 256, 256, regions), "truncated move metadata requests a complete refresh");
                    Check(!DesktopRegions.Append(metadata, 48, 40, true, 256, 256, regions), "oversized metadata cannot read beyond its buffer");
                    Check(!DesktopRegions.Append(metadata, 24, 40, true, 16, 16, regions), "out-of-screen move cannot cause a partial stale frame");
                }
                finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(metadata); }
                var tiles = new LatestTiles();
                tiles.Merge(new List<TilePatch> { Patch(0, 1), Patch(1, 2) });
                tiles.Merge(new List<TilePatch> { Patch(0, 3), Patch(2, 4) });
                using (var r = new BinaryReader(new MemoryStream(tiles.Take(7))))
                { Check(r.ReadUInt16() == 0 && r.ReadInt32() == 1 && r.ReadByte() == 3, "unsent tile replaced by newest content"); }
                using (var r = new BinaryReader(new MemoryStream(tiles.Take(7))))
                { Check(r.ReadUInt16() == 1, "repeated updates do not starve other tiles"); }
                tiles.Clear(); Check(tiles.Count == 0, "view change clears old geometry's queued tiles");
                using (var src = new ScreenSource(new Rectangle(0, 0, 256, 256)))
                {
                    var first = src.Grab(); Check(first.Count == 4 && src.LastGrabWasFull, "first capture supplies every tile");
                    foreach (var p in first) Check(p.Jpeg[0] == 255 && p.Jpeg[1] == 216, "each JPEG starts at byte zero");
                    Check(src.Grab().Count == 0, "unchanged capture sends no tiles");
                    src.Quality = 40; var low = src.Grab(); Check(low.Count == 4 && src.LastGrabWasFull, "quality redraw is distinguished from movement to prevent oscillation");
                    src.InvalidateAll(); Check(src.Grab().Count == 4, "refresh repaints a static desktop");
                    src.ScalePercent = 50; Check(src.Grab().Count == 1 && src.FrameWidth == 128, "new-size buffer gets a complete fresh capture");
                }
                using (var src = new ScreenSource(new Rectangle(0, 0, 259, 131)))
                {
                    var patches = src.Grab();
                    using (var ms = new MemoryStream(patches[5].Jpeg)) using (var image = Image.FromStream(ms))
                        Check(image.Width == 3 && image.Height == 3, "edge tile encodes only its real pixels without stale padding");
                    src.TileSize = 1024;
                    Check(src.Cols == 1 && src.Rows == 1 && src.Grab().Count == 1, "tile-grid change rebuilds all geometry and sends a complete image");
                    src.TileSize = 128; Check(src.Grab().Count == 6, "returning to small tiles restores the complete previous grid");
                }
                Console.WriteLine("ALL " + checks + " CONTROL CHECKS PASSED"); return 0;
            }
            catch (Exception ex) { Console.WriteLine(ex); return 1; }
        }
        private static TilePatch Patch(int index, byte value) { return new TilePatch { Index = index, Jpeg = new[] { value } }; }
    }
    internal static class Desktops
    { internal static bool Sync() { return false; } internal static string ThreadDesktopName() { return "test"; } internal static string InputDesktopName() { return "test"; } }
    internal sealed class DesktopDup : IDisposable
    {
        internal const int GrabNewFrame = 0, GrabNoChange = 1, GrabFailed = -1;
        internal bool DirtyKnown { get { return false; } }
        internal Rectangle[] Dirty { get { return new Rectangle[0]; } }
        internal int SrcW { get { return 256; } } internal int SrcH { get { return 256; } }
        private bool first = true;
        internal static void FindOutput(Rectangle area, out int adapter, out int output) { adapter = output = -1; }
        internal static DesktopDup Open(Rectangle area) { return new DesktopDup(); }
        internal int Grab(Bitmap frame, Rectangle area, int w, int h)
        {
            if (!first) return GrabNoChange; first = false;
            using (var g = Graphics.FromImage(frame)) { g.Clear(Color.White); for (int i = 0; i < w; i += 7) g.DrawLine(Pens.Blue, i, 0, w - i, h); }
            return GrabNewFrame;
        }
        public void Dispose() { }
    }
}

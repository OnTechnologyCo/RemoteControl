// 화면을 잡아서 "바뀐 조각" 만 골라 낸다.
//
// 원격제어에서 지연을 좌우하는 것은 화질이 아니라 보내는 양이다.
// 1920x1200 한 장을 통째로 JPEG 로 만들면 수백 KB 가 되고, 그걸 초당 여러 번 보내면
// 회선이 감당하지 못해 화면이 밀린다. 그래서 화면을 타일로 쪼개고
// 직전 프레임과 달라진 타일만 보낸다. 대개 몇 개뿐이다.
//
// 실측(이 PC, 모니터 3대, 1920x1200):
//   화면 한 장 가져오기(GDI)  36 ms   ← 여기가 90%
//   바뀐 타일 찾기             2.4 ms
//   JPEG 인코딩(타일당)        0.06 ms
// 위 값은 당시 1920x1200 측정이다. 4K의 타일 준비/전체 압축/디코딩 비용은 별도로
// 재야 한다 (v1.6.3, docs/4k-performance.md). 가져오기는 두 가지로 줄인다.
//   1) 줄여서 가져온다 (2/3 로 줄이면 21ms). 휴대폰 화면은 어차피 작다.
//   2) 가능하면 Desktop Duplication 으로 가져온다 (DesktopDup.cs). GDI 는 되돌아갈 길.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading.Tasks;

namespace RemoteControl
{
    /// <summary>보낼 타일 한 조각.</summary>
    internal sealed class TilePatch
    {
        public int Index;      // 타일 번호 (왼쪽 위부터 가로 우선)
        public byte[] Jpeg;
    }

    /// <summary>커서는 화면과 따로 보낸다. 그래야 마우스만 움직일 때 타일이 안 더러워진다.</summary>
    internal sealed class CursorState
    {
        public int X, Y;           // 보내는 그림 기준 좌표 (축소가 반영된 값)
        public int ShapeId;        // 모양이 바뀌면 값이 바뀐다
        public bool Visible;
        public byte[] Png;         // 모양이 새것일 때만 채운다
        public int HotX, HotY;
    }

    internal sealed class ScreenSource : IDisposable
    {
        private int tileSize = 128;
        internal int TileSize
        {
            get { return tileSize; }
            set
            {
                if (value != 128 && value != 256 && value != 512 && value != 1024) throw new ArgumentOutOfRangeException("value");
                lock (gate) { if (tileSize == value) return; tileSize = value; Rebuild(); }
            }
        }

        private readonly object gate = new object();

        private Rectangle area;          // 잡을 화면 영역 (가상 데스크톱 좌표)
        private int fw, fh;              // 실제로 만들어 보내는 그림 크기
        private int scalePct = 100;
        private int cols, rows;
        private ulong[] hashes;          // 타일별 직전 내용의 지문
        private Bitmap frame;
        private Graphics frameG;
        private bool hasFrame;

        private readonly ImageCodecInfo jpegCodec;
        private long quality = 70;

        // 이번 프레임에 지문을 다시 계산할 타일. null 이면 전부 본다.
        //
        // 왜 필요한가 - 화면 전체 지문이 2.74ms 다 (실측 1920x1200, bench/DirtyBench.cs).
        // 글자 한 자를 쳐도 그만큼 들었다. Desktop Duplication 이 "어디가 바뀌었는지" 를
        // 이미 알려 주므로 그 자리만 보면 0.07ms 로 떨어진다 (98% 절감).
        // 놓치는 타일이 있는지도 재 봤다 - 전체를 훑을 때와 바뀐 타일 수가 정확히 같았다.
        private bool[] tileMask;
        private bool[] maskBuf;

        // 지문 표가 비어 있으면(첫 프레임, 새 접속자) 바뀐 자리만 봐서는 안 된다.
        // 나머지 타일이 "아직 안 보냄" 인 채로 영영 남는다.
        private bool needFull = true;

        private DesktopDup dup;          // 빠른 경로. 못 쓰면 null 로 두고 GDI 로 간다.
        private bool dupTried;
        private int dupAttemptAt;
        private int dupFailStreak;

        internal Rectangle Area { get { return area; } }
        internal int Cols { get { return cols; } }
        internal int Rows { get { return rows; } }
        internal int FrameWidth { get { return fw; } }
        internal int FrameHeight { get { return fh; } }
        internal bool LastGrabWasFull { get; private set; }

        /// <summary>지금 어떤 방법으로 화면을 가져오고 있는지 (창에 보여 준다).</summary>
        internal string Method { get { return dup != null ? "Desktop Duplication" : "GDI"; } }

        /// <summary>
        /// 화면을 줄여서 잡는다 (100 = 원본).
        ///
        /// 이게 가장 크게 듣는 손잡이다. 가져오는 비용이 픽셀 수에 거의 비례하므로
        /// 2/3 로 줄이면 36ms 가 21ms 로 떨어지고, 보내는 양도 함께 줄어든다.
        /// </summary>
        internal int ScalePercent
        {
            get { lock (gate) { return scalePct; } }
            set
            {
                int v = value < 30 ? 30 : (value > 100 ? 100 : value);
                lock (gate)
                {
                    if (v == scalePct) return;
                    scalePct = v;
                    Rebuild();
                }
            }
        }

        internal long Quality
        {
            get { return quality; }
            set
            {
                long v = value < 20 ? 20 : (value > 95 ? 95 : value);
                lock (gate)
                {
                    if (v == quality) return;
                    quality = v;
                    // 화질이 바뀌면 이전 지문은 의미가 없다. 전부 다시 보낸다.
                    InvalidateAllLocked();
                }
            }
        }

        /// <summary>보낸 그림 위의 좌표를 실제 화면 좌표로 되돌린다.</summary>
        internal int ToScreenX(int x) { lock (gate) { return area.X + (fw <= 0 ? x : (int)((long)x * area.Width / fw)); } }
        internal int ToScreenY(int y) { lock (gate) { return area.Y + (fh <= 0 ? y : (int)((long)y * area.Height / fh)); } }

        internal ScreenSource(Rectangle captureArea)
        {
            foreach (ImageCodecInfo c in ImageCodecInfo.GetImageEncoders())
                if (c.FormatID == ImageFormat.Jpeg.Guid) jpegCodec = c;

            SetArea(captureArea);
        }

        internal void SetArea(Rectangle r)
        {
            lock (gate)
            {
                if (r.Width <= 0 || r.Height <= 0) return;

                area = r;
                DropDup();
                Rebuild();
            }
        }

        private void Rebuild()
        {
            // 새 버퍼에는 예전 DD 그림이 없다. 정지 화면에서도 첫 그림을 다시 받는다.
            DropDup();
            hasFrame = false;
            fw = Math.Max(16, area.Width * scalePct / 100);
            fh = Math.Max(16, area.Height * scalePct / 100);

            cols = (fw + TileSize - 1) / TileSize;
            rows = (fh + TileSize - 1) / TileSize;
            hashes = new ulong[cols * rows];

            if (frameG != null) { frameG.Dispose(); frameG = null; }
            if (frame != null) { frame.Dispose(); frame = null; }

            frame = new Bitmap(fw, fh, PixelFormat.Format32bppArgb);
            frameG = Graphics.FromImage(frame);
            frameG.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            frameG.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;

            InvalidateAllLocked();
        }

        /// <summary>다음 번에 모든 타일을 다시 보내게 한다 (새 접속자가 붙었을 때).</summary>
        internal void InvalidateAll()
        {
            lock (gate) { InvalidateAllLocked(); }
        }

        private void InvalidateAllLocked()
        {
            needFull = true;                 // 표를 비웠으니 다음 한 장은 전부 훑어야 한다
            if (hashes == null) return;
            for (int i = 0; i < hashes.Length; i++) hashes[i] = 0;
        }

        /// <summary>
        /// 화면을 한 번 잡고, 직전과 달라진 타일만 JPEG 로 만들어 돌려준다.
        /// 바뀐 것이 없으면 빈 목록을 준다 - 그때는 아무것도 보내지 않는다.
        /// </summary>
        internal List<TilePatch> Grab()
        {
            lock (gate)
            {
                // 잠금 화면으로 넘어갔으면 그쪽으로 옮겨 붙는다.
                // 이게 없으면 잠긴 뒤 계속 예전 바탕화면만 잡혀 화면이 멈춘 것처럼 보인다.
                if (Desktops.Sync()) DropDupIfDesktopChanged();

                List<TilePatch> patches = new List<TilePatch>();
                LastGrabWasFull = needFull;
                if (frame == null) return patches;

                // "안 바뀌었다" 를 확실히 아는 경우에는 지문 계산까지 통째로 건너뛴다.
                // GDI 로는 알 수 없어서 매번 전부 다시 계산해야 했다 (2.4ms).
                if (!CaptureInto()) return patches;

                // 이번 한 장을 다 훑었으면 다음부터는 바뀐 자리만 봐도 된다.
                bool[] mask = tileMask;
                needFull = false;

                BitmapData bd = frame.LockBits(new Rectangle(0, 0, fw, fh),
                                               ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                List<int> changed = new List<int>();
                try
                {
                    unsafe
                    {
                        byte* baseP = (byte*)bd.Scan0;

                        for (int ty = 0; ty < rows; ty++)
                        {
                            int y0 = ty * TileSize;
                            int th = Math.Min(TileSize, fh - y0);

                            for (int tx = 0; tx < cols; tx++)
                            {
                                int idx0 = ty * cols + tx;
                                if (mask != null && !mask[idx0]) continue;

                                int x0 = tx * TileSize;
                                int tw = Math.Min(TileSize, fw - x0);

                                // FNV-1a. 픽셀을 전부 비교하는 것보다 훨씬 싸고,
                                // 화면 변화 감지 용도로는 충돌 확률이 무시할 만하다.
                                ulong h = 1469598103934665603UL;
                                for (int yy = 0; yy < th; yy++)
                                {
                                    int* row = (int*)(baseP + (y0 + yy) * bd.Stride + x0 * 4);
                                    for (int xx = 0; xx < tw; xx++)
                                        h = (h ^ (uint)row[xx]) * 1099511628211UL;
                                }

                                if (h == 0) h = 1;   // 0 은 "아직 안 보냄" 표시로 쓴다

                                int idx = ty * cols + tx;
                                if (hashes[idx] != h)
                                {
                                    hashes[idx] = h;
                                    changed.Add(idx);
                                }
                            }
                        }
                    }
                }
                finally { frame.UnlockBits(bd); }

                if (changed.Count == 0) return patches;

                // DrawImage로 큰 원본에서 타일을 510번 자르면 4K에서 전체 준비에
                // 570ms가 들었다. 잠근 원본의 해당 영역을 JPEG 인코더가 바로 읽게 한다.
                // 화면은 불투명하므로 DD 버퍼의 미정의 alpha를 합성하지 않는다.
                BitmapData pixels = frame.LockBits(new Rectangle(0, 0, fw, fh),
                    ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    var encoded = new TilePatch[changed.Count];
                    Action<int, TileEncoder> encode = delegate(int i, TileEncoder encoder)
                    {
                        int idx = changed[i];
                        int tx = idx % cols, ty = idx / cols;
                        int x0 = tx * TileSize, y0 = ty * TileSize;
                        int tw = Math.Min(TileSize, fw - x0);
                        int th = Math.Min(TileSize, fh - y0);
                        encoded[i] = encoder.Encode(pixels, idx, x0, y0, tw, th, jpegCodec);
                    };
                    if (changed.Count >= 4 && (long)changed.Count * TileSize * TileSize >= 1024 * 1024)
                    {
                        Parallel.For<TileEncoder>(0, changed.Count,
                            new ParallelOptions { MaxDegreeOfParallelism = Math.Min(4, Math.Max(1, Environment.ProcessorCount / 2)) },
                            delegate { return new TileEncoder(quality); },
                            delegate(int i, ParallelLoopState state, TileEncoder encoder) { encode(i, encoder); return encoder; },
                            delegate(TileEncoder encoder) { encoder.Dispose(); });
                    }
                    else
                    {
                        using (var encoder = new TileEncoder(quality))
                            for (int i = 0; i < changed.Count; i++) encode(i, encoder);
                    }
                    patches.AddRange(encoded); // 압축 완료 순서가 타일 순서를 뒤집지 않게 한다.
                }
                finally { frame.UnlockBits(pixels); }

                return patches;
            }
        }

        internal byte[] Snapshot()
        {
            lock (gate)
            {
                if (Desktops.Sync()) DropDupIfDesktopChanged();
                if (!CaptureInto() && !hasFrame) throw new IOException("현재 데스크톱 화면을 가져오지 못했습니다.");
                using (var bytes = new MemoryStream())
                using (var parameters = new EncoderParameters(1))
                {
                    parameters.Param[0] = new EncoderParameter(Encoder.Quality, 75L);
                    frame.Save(bytes, jpegCodec, parameters); return bytes.ToArray();
                }
            }
        }

        private sealed class TileEncoder : IDisposable
        {
            private readonly MemoryStream bytes = new MemoryStream(16 * 1024);
            private readonly EncoderParameters parameters = new EncoderParameters(1);
            internal TileEncoder(long quality) { parameters.Param[0] = new EncoderParameter(Encoder.Quality, quality); }
            internal TilePatch Encode(BitmapData pixels, int index, int x, int y, int width, int height, ImageCodecInfo codec)
            {
                bytes.Position = 0; bytes.SetLength(0);
                using (var tile = new Bitmap(width, height, pixels.Stride, PixelFormat.Format32bppRgb,
                    new IntPtr(pixels.Scan0.ToInt64() + (long)y * pixels.Stride + x * 4)))
                    tile.Save(bytes, codec, parameters);
                return new TilePatch { Index = index, Jpeg = bytes.ToArray() };
            }
            public void Dispose() { parameters.Dispose(); bytes.Dispose(); }
        }

        // ---------------------------------------------------------------- 가져오기

        /// <summary>화면을 그림에 채운다. 바뀐 게 없는 것이 확실하면 false.</summary>
        /// <summary>
        /// Desktop Duplication 이 알려 준 "바뀐 자리" 를 타일 표로 옮긴다.
        ///
        /// 좌표는 원본 화면 기준이라, 줄여서 보내는 중이면 그 배율로 옮겨야 한다.
        /// 가장자리는 바깥쪽으로 한 칸 넉넉히 잡는다 - 반올림으로 한 줄이 빠지면
        /// 그 타일이 안 바뀐 것으로 보여 화면에 찌꺼기가 남는다.
        /// </summary>
        private void BuildMask()
        {
            if (needFull || dup == null || !dup.DirtyKnown || dup.Dirty.Length == 0)
            {
                tileMask = null;
                return;
            }

            int sw = dup.SrcW, sh = dup.SrcH;
            if (sw <= 0 || sh <= 0) { tileMask = null; return; }

            if (maskBuf == null || maskBuf.Length != cols * rows) maskBuf = new bool[cols * rows];
            Array.Clear(maskBuf, 0, maskBuf.Length);

            foreach (Rectangle q in dup.Dirty)
            {
                int x0 = (int)((long)q.Left * fw / sw) - 1;
                int y0 = (int)((long)q.Top  * fh / sh) - 1;
                int x1 = (int)(((long)q.Right  * fw + sw - 1) / sw) + 1;
                int y1 = (int)(((long)q.Bottom * fh + sh - 1) / sh) + 1;

                if (x0 < 0) x0 = 0;
                if (y0 < 0) y0 = 0;
                if (x1 > fw) x1 = fw;
                if (y1 > fh) y1 = fh;
                if (x1 <= x0 || y1 <= y0) continue;

                int tx0 = x0 / TileSize, tx1 = (x1 - 1) / TileSize;
                int ty0 = y0 / TileSize, ty1 = (y1 - 1) / TileSize;

                if (tx1 >= cols) tx1 = cols - 1;
                if (ty1 >= rows) ty1 = rows - 1;

                for (int ty = ty0; ty <= ty1; ty++)
                    for (int tx = tx0; tx <= tx1; tx++)
                        maskBuf[ty * cols + tx] = true;
            }

            tileMask = maskBuf;
        }

        private bool CaptureInto()
        {
            // 빠른 경로: Desktop Duplication. 윈도우가 GPU 쪽에서 바로 넘겨준다.
            int r = TryDup();

            if (r == DesktopDup.GrabNewFrame)
            {
                hasFrame = true;
                BuildMask();
                return true;
            }

            if (r == DesktopDup.GrabNoChange)
            {
                // 화질 변경/refresh는 화면이 정지했어도 이미 가진 픽셀을 다시 보내야 한다.
                tileMask = null;
                return needFull && hasFrame;
            }

            // GDI 로는 어디가 바뀌었는지 알 수 없다. 전부 훑는 수밖에 없다.
            tileMask = null;

            IntPtr dst = frameG.GetHdc();
            IntPtr desk = Native.GetDesktopWindow();
            IntPtr src = Native.GetWindowDC(desk);
            try
            {
                if (fw == area.Width && fh == area.Height)
                {
                    Native.BitBlt(dst, 0, 0, fw, fh, src, area.X, area.Y, Native.SRCCOPY);
                }
                else
                {
                    // COLORONCOLOR: 줄일 때 줄을 그냥 버린다. HALFTONE 은 예쁘지만
                    // 측정해 보니 오히려 원본 크기보다 느렸다 (39ms). 여기서는 속도가 먼저다.
                    Native.SetStretchBltMode(dst, Native.COLORONCOLOR);
                    Native.StretchBlt(dst, 0, 0, fw, fh, src, area.X, area.Y,
                                      area.Width, area.Height, Native.SRCCOPY);
                }
            }
            finally
            {
                Native.ReleaseDC(desk, src);
                frameG.ReleaseHdc(dst);
            }

            hasFrame = true;
            return true;      // GDI 로는 바뀌었는지 알 수 없다. 지문으로 가려낸다.
        }

        private int TryDup()
        {
            if (!dupTried || (dup == null && Environment.TickCount - dupAttemptAt >= 5000))
            {
                dupTried = true;
                dupAttemptAt = Environment.TickCount;
                dup = DesktopDup.Open(area);
            }

            if (dup == null) return DesktopDup.GrabFailed;

            try
            {
                int r = dup.Grab(frame, area, fw, fh);
                if (r != DesktopDup.GrabFailed) { dupFailStreak = 0; return r; }
            }
            catch { }

            // 화면 모드가 바뀌거나 보안 화면으로 넘어가면 실패할 수 있다.
            // 몇 번 연속으로 실패하면 미련 없이 GDI 로 돌아간다.
            if (++dupFailStreak >= 3) DropDup();
            return DesktopDup.GrabFailed;
        }

        private void DropDup()
        {
            if (dup != null) { try { dup.Dispose(); } catch { } dup = null; }
            dupTried = false;
            dupFailStreak = 0;
        }

        private string dupDesktop;

        private void DropDupIfDesktopChanged()
        {
            string now = Desktops.ThreadDesktopName();
            if (dupDesktop == now) return;

            // 잠금 화면으로 넘어갔다. 복제본은 그 데스크톱에서 다시 열어야 한다.
            dupDesktop = now;
            DropDup();
            hasFrame = false;
            InvalidateAllLocked();
        }

        // ---------------------------------------------------------------- 커서

        private int lastShape;

        /// <summary>
        /// 커서를 화면과 따로 알려 준다.
        ///
        /// 예전에는 커서를 화면에 그려 넣어 보냈다. 그러면 마우스를 흔들기만 해도
        /// 그 자리 타일이 계속 바뀐 것으로 잡혀 프레임마다 다시 인코딩·전송했다.
        /// 위치만 보내고 브라우저가 그리게 하면 그 비용이 통째로 사라지고,
        /// 커서가 회선 지연과 무관하게 즉시 따라 움직여 조작감도 좋아진다.
        /// </summary>
        internal CursorState GetCursor()
        {
            CursorState cs = new CursorState();

            Native.CURSORINFO ci = new Native.CURSORINFO();
            ci.cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.CURSORINFO));

            if (!Native.GetCursorInfo(ref ci) || (ci.flags & Native.CURSOR_SHOWING) == 0)
                return cs;                       // Visible = false

            cs.Visible = true;

            lock (gate)
            {
                cs.X = fw == area.Width ? ci.ptScreenPos.X - area.X
                                        : (int)((long)(ci.ptScreenPos.X - area.X) * fw / Math.Max(1, area.Width));
                cs.Y = fh == area.Height ? ci.ptScreenPos.Y - area.Y
                                         : (int)((long)(ci.ptScreenPos.Y - area.Y) * fh / Math.Max(1, area.Height));
            }

            cs.ShapeId = ci.hCursor.ToInt32();

            if (cs.ShapeId != lastShape)
            {
                if (RenderCursor(ci.hCursor, cs)) lastShape = cs.ShapeId;
            }

            return cs;
        }

        /// <summary>커서 모양을 PNG 로 만든다. 모양이 바뀔 때만 한 번 보낸다.</summary>
        private static bool RenderCursor(IntPtr hCursor, CursorState cs)
        {
            IntPtr icon = Native.CopyIcon(hCursor);
            if (icon == IntPtr.Zero) return false;

            try
            {
                Native.ICONINFO ii;
                if (!Native.GetIconInfo(icon, out ii)) return false;

                try
                {
                    cs.HotX = ii.xHotspot;
                    cs.HotY = ii.yHotspot;

                    using (Icon ico = Icon.FromHandle(icon))
                    using (Bitmap b = new Bitmap(ico.Width, ico.Height, PixelFormat.Format32bppArgb))
                    {
                        using (Graphics g = Graphics.FromImage(b)) g.DrawIcon(ico, 0, 0);

                        using (MemoryStream ms = new MemoryStream(4096))
                        {
                            b.Save(ms, ImageFormat.Png);
                            cs.Png = ms.ToArray();
                        }
                    }

                    return true;
                }
                finally
                {
                    if (ii.hbmMask != IntPtr.Zero) Native.DeleteObject(ii.hbmMask);
                    if (ii.hbmColor != IntPtr.Zero) Native.DeleteObject(ii.hbmColor);
                }
            }
            catch { return false; }
            finally { Native.DestroyIcon(icon); }
        }

        public void Dispose()
        {
            lock (gate)
            {
                DropDup();
                if (frameG != null) { frameG.Dispose(); frameG = null; }
                if (frame != null) { frame.Dispose(); frame = null; }
            }
        }
    }
}

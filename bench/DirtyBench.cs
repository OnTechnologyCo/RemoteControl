// 화면 전송을 더 빠르게 할 수 있는지 잰다.
//
// 지금 파이프라인은 프레임마다 이렇게 돈다.
//   1) CopyResource  GPU 전체 화면 -> staging          (실측 13ms 중 대부분)
//   2) FNV 지문      화면 전체 타일                     (실측 2.4ms)
//   3) JPEG          바뀐 타일만                        (실측 0.06ms/타일)
//
// 그런데 윈도우는 "이번에 어디가 바뀌었는지" 를 이미 알고 있다(GetFrameDirtyRects).
// 그걸 안 받으니 글자 한 자를 쳐도 1920x1200 전체를 복사하고 전체를 지문 계산한다.
//
// 여기서 재는 것:
//   - 실제로 화면의 몇 %가 바뀌는가 (이게 작아야 고칠 값어치가 있다)
//   - 지문 계산을 바뀐 자리로 좁히면 얼마나 줄어드는가
//   - 바뀐 타일 수가 얼마나 주는가 (= JPEG 인코딩·전송량)
//
// 추측으로 고르지 말 것. 숫자가 작으면 손대지 않는다.
//
//   $csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
//   & $csc /nologo /target:exe /platform:x64 /unsafe /langversion:5 /out:$env:TEMP\db.exe `
//         /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll `
//         bench\DirtyBench.cs src\DesktopDup.cs src\Native.cs
//   & $env:TEMP\db.exe

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Threading;
using System.Windows.Forms;
using RemoteControl;

class DirtyBench
{
    const int TileSize = 128;          // ScreenSource 와 같은 값이어야 뜻이 있다

    static Form blinker;
    static System.Windows.Forms.Timer ticker;
    static int blinkW, blinkH;

    static void Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        // 예외가 나면 .NET 이 대화상자를 띄우고 프로세스가 거기서 멈춘다.
        // 벤치는 사람이 안 볼 수도 있으니 콘솔에 찍고 끝내야 한다.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += delegate(object s2, ThreadExceptionEventArgs e2)
        {
            Console.WriteLine("예외: " + e2.Exception);
            Environment.Exit(1);
        };

        Rectangle area = Screen.PrimaryScreen.Bounds;
        Console.WriteLine("화면 {0}x{1}", area.Width, area.Height);
        Console.WriteLine();

        // 세 가지 화면 변화를 각각 잰다. 하나로 뭉뚱그리면 평균이 거짓말을 한다 -
        // 글자 한 자와 전체 스크롤은 얻는 것이 정반대다.
        Run(area, "글자 한 자쯤 (작은 사각형 하나)", 24, 24);
        Run(area, "창 하나 다시 그리기", 520, 380);
        Run(area, "화면 거의 전체", area.Width - 80, area.Height - 80);
    }

    static void Run(Rectangle area, string what, int w, int h)
    {
        Console.WriteLine("== " + what + " ==");

        blinkW = w; blinkH = h;
        StartBlinker(area);
        Thread.Sleep(400);

        DesktopDup dup = DesktopDup.Open(area);
        if (dup == null)
        {
            Console.WriteLine("   복제본을 못 열었습니다 (RDP 세션이거나 드라이버 문제).");
            StopBlinker();
            return;
        }

        int fw = area.Width, fh = area.Height;
        int cols = (fw + TileSize - 1) / TileSize;
        int rows = (fh + TileSize - 1) / TileSize;

        using (Bitmap frame = new Bitmap(fw, fh, PixelFormat.Format32bppArgb))
        using (Bitmap shadow = new Bitmap(fw, fh, PixelFormat.Format32bppArgb))
        {
            // 부분 옮기기가 전체 옮기기와 픽셀까지 같은지 매 프레임 대조한다.
            // 여기서 틀리면 화면에 찌꺼기가 남는다 - 눈으로는 한참 뒤에야 안다.
            dup.Shadow = shadow;
            int mismatch = 0;

            // 표를 둘로 나눈다. 하나로 쓰면 (가)가 갱신한 것을 (나)가 또 보게 되어
            // 바뀐 타일 수가 통째로 거짓이 된다 (실제로 150개 중 146개로 나왔다).
            ulong[] hFull = new ulong[cols * rows];
            ulong[] hDirty = new ulong[cols * rows];

            int frames = 0, noChange = 0, unknown = 0;
            long dirtyPx = 0;
            int rectCount = 0;
            double msFull = 0, msDirty = 0, msGrab = 0;
            double msAcq = 0, msCopy = 0, msMap = 0, msBlit = 0;
            int tilesFull = 0, tilesDirty = 0;

            Stopwatch sw = new Stopwatch();
            DateTime until = DateTime.Now.AddSeconds(6);

            while (DateTime.Now < until)
            {
                int r;
                sw.Restart();
                try { r = dup.Grab(frame, area, fw, fh); }
                catch (Exception ex) { Console.WriteLine("   예외: " + ex.Message); break; }
                sw.Stop();
                double grabMs = sw.Elapsed.TotalMilliseconds;

                if (r == DesktopDup.GrabNoChange) { noChange++; Application.DoEvents(); continue; }
                if (r != DesktopDup.GrabNewFrame) break;

                frames++;
                msGrab += grabMs;
                if (!SamePixels(frame, shadow, fw, fh)) mismatch++;
                msAcq += dup.DbgAcquireMs; msCopy += dup.DbgCopyMs;
                msMap += dup.DbgMapMs; msBlit += dup.DbgBlitMs;
                if (!dup.DirtyKnown)
                {
                    unknown++;
                    if (unknown <= 3)
                        Console.WriteLine("   [왜 못 받나] meta={0} need={1} accum={2} coalesced={3} hr=0x{4:X8} ex={5}",
                                          dup.DbgMeta, dup.DbgNeed, dup.DbgAccum, dup.DbgCoalesced, dup.DbgHr, dup.DbgEx);
                }

                Rectangle[] dirty = dup.Dirty;
                rectCount += dirty.Length;
                foreach (Rectangle d in dirty) dirtyPx += (long)d.Width * d.Height;

                // (가) 지금 방식: 전체 타일 지문
                sw.Restart();
                int nFull = HashTiles(frame, fw, fh, cols, rows, hFull, null);
                sw.Stop();
                msFull += sw.Elapsed.TotalMilliseconds;
                tilesFull += nFull;

                // (나) 바뀐 자리에 걸치는 타일만 지문
                sw.Restart();
                int nDirty = HashTiles(frame, fw, fh, cols, rows, hDirty,
                                       dup.DirtyKnown ? TileMask(dirty, cols, rows) : null);
                sw.Stop();
                msDirty += sw.Elapsed.TotalMilliseconds;
                tilesDirty += nDirty;

                Application.DoEvents();
            }

            StopBlinker();
            dup.Dispose();

            if (frames == 0) { Console.WriteLine("   새 그림이 한 장도 안 왔습니다.\n"); return; }

            double screenPx = (double)fw * fh;

            Console.WriteLine("   새 그림 {0}장 / 변화없음 {1}회", frames, noChange);
            Console.WriteLine("   바뀐 자리를 못 받은 프레임  {0}장", unknown);
            Console.WriteLine("   부분 옮기기 대조  {0}",
                              mismatch == 0 ? "전부 일치" : ("어긋난 프레임 " + mismatch + "장  <-- 고쳐야 함"));
            Console.WriteLine("   프레임당 사각형 {0:F1}개,  바뀐 넓이 {1:F1}%",
                              (double)rectCount / frames, dirtyPx / frames / screenPx * 100);
            Console.WriteLine("   지문  전체 {0:F2} ms  ->  바뀐 자리만 {1:F2} ms   ({2:F0}% 절약)",
                              msFull / frames, msDirty / frames,
                              (1 - msDirty / Math.Max(msFull, 0.0001)) * 100);
            Console.WriteLine("   바뀐 타일  전체훑기 {0:F1}개  /  바뀐자리만 {1:F1}개   (타일 {2}개 중)",
                              (double)tilesFull / frames, (double)tilesDirty / frames, cols * rows);

            double g = msGrab / frames, hf = msFull / frames, hd = msDirty / frames;
            Console.WriteLine("   가져오기 {0:F2} ms  =  기다림 {1:F2} + 복사 {2:F2} + Map {3:F2} + 옮기기 {4:F2}",
                              g, msAcq / frames, msCopy / frames, msMap / frames, msBlit / frames);
            Console.WriteLine("   한 장  {0:F2} ms ({1:F0} fps)   ->  바뀐자리만 {2:F2} ms ({3:F0} fps)",
                              g + hf, 1000 / (g + hf), g + hd, 1000 / (g + hd));
            Console.WriteLine();
        }
    }

    /// <summary>두 그림이 픽셀까지 같은가.</summary>
    static bool SamePixels(Bitmap a, Bitmap b, int fw, int fh)
    {
        BitmapData ba = a.LockBits(new Rectangle(0, 0, fw, fh), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        BitmapData bb = b.LockBits(new Rectangle(0, 0, fw, fh), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            unsafe
            {
                for (int y = 0; y < fh; y++)
                {
                    int* pa = (int*)((byte*)ba.Scan0 + (long)y * ba.Stride);
                    int* pb = (int*)((byte*)bb.Scan0 + (long)y * bb.Stride);

                    for (int x = 0; x < fw; x++)
                        if ((pa[x] & 0xFFFFFF) != (pb[x] & 0xFFFFFF)) return false;
                }
            }
        }
        finally { a.UnlockBits(ba); b.UnlockBits(bb); }

        return true;
    }

    /// <summary>바뀐 사각형에 걸치는 타일만 true. null 이면 전부 본다.</summary>
    static bool[] TileMask(Rectangle[] dirty, int cols, int rows)
    {
        bool[] m = new bool[cols * rows];

        foreach (Rectangle d in dirty)
        {
            int x0 = Math.Max(0, d.Left / TileSize);
            int y0 = Math.Max(0, d.Top / TileSize);
            int x1 = Math.Min(cols - 1, (d.Right - 1) / TileSize);
            int y1 = Math.Min(rows - 1, (d.Bottom - 1) / TileSize);

            for (int ty = y0; ty <= y1; ty++)
                for (int tx = x0; tx <= x1; tx++)
                    m[ty * cols + tx] = true;
        }

        return m;
    }

    /// <summary>ScreenSource 와 같은 FNV-1a 지문. mask 가 있으면 그 타일만 본다.</summary>
    static int HashTiles(Bitmap frame, int fw, int fh, int cols, int rows,
                         ulong[] hashes, bool[] mask)
    {
        int changed = 0;

        BitmapData bd = frame.LockBits(new Rectangle(0, 0, fw, fh),
                                       ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
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
                        int idx = ty * cols + tx;
                        if (mask != null && !mask[idx]) continue;

                        int x0 = tx * TileSize;
                        int tw = Math.Min(TileSize, fw - x0);

                        ulong h = 1469598103934665603UL;
                        for (int yy = 0; yy < th; yy++)
                        {
                            int* row = (int*)(baseP + (y0 + yy) * bd.Stride + x0 * 4);
                            for (int xx = 0; xx < tw; xx++)
                                h = (h ^ (uint)row[xx]) * 1099511628211UL;
                        }

                        if (h == 0) h = 1;

                        if (hashes[idx] != h) { hashes[idx] = h; changed++; }
                    }
                }
            }
        }
        finally { frame.UnlockBits(bd); }

        return changed;
    }

    // ---------------------------------------------------------------- 흔드는 창
    //
    // 화면이 가만히 있으면 윈도우는 새 그림을 주지 않는다. 그러면 전부 "변화 없음" 만
    // 나와서 정작 재려는 경로를 한 번도 안 지나간다 (DupBench 에서 그렇게 헛돌았다).

    static void StartBlinker(Rectangle area)
    {
        blinker = new Form
        {
            FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.Manual,
            Bounds = new Rectangle(area.X + 40, area.Y + 40, blinkW, blinkH),
            TopMost = true,
            ShowInTaskbar = false
        };

        Random rnd = new Random(1);

        // 타이머를 지역변수로 두면 창을 닫은 뒤에도 계속 돌아 blinker(null) 를 건드린다.
        // 실제로 그렇게 NullReferenceException 대화상자가 떠서 벤치가 멈춰 섰다.
        ticker = new System.Windows.Forms.Timer { Interval = 15 };
        ticker.Tick += delegate
        {
            Form f = blinker;
            if (f == null || f.IsDisposed) return;

            try { f.BackColor = Color.FromArgb(rnd.Next(40, 250), rnd.Next(40, 250), rnd.Next(40, 250)); }
            catch { }
        };

        blinker.Load += delegate { ticker.Start(); };
        blinker.Show();
        Application.DoEvents();
    }

    static void StopBlinker()
    {
        try { if (ticker != null) { ticker.Stop(); ticker.Dispose(); ticker = null; } }
        catch { }

        try { if (blinker != null) { blinker.Close(); blinker.Dispose(); blinker = null; } }
        catch { }

        Application.DoEvents();
    }
}

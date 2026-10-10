// 화면 캡처 - 변경 감지 - 인코딩 경로의 실제 비용을 잰다.
//
// 원격제어는 처리량이 아니라 지연이 사용성을 좌우한다. 그래서 설계를 고르기 전에
// "한 프레임을 만드는 데 몇 ms 가 드는가" 를 먼저 재고, 그 숫자로 결정한다.
//
// 재는 것:
//   1) BitBlt 로 화면 한 장 가져오는 비용
//   2) 타일 단위로 바뀐 곳을 찾아내는 비용 (해시 비교)
//   3) 바뀐 타일만 JPEG 로 인코딩하는 비용 (품질별)
//   4) 위를 합쳤을 때 초당 몇 프레임이 가능한가
//
// 빌드:
//   csc.exe /target:exe /r:System.Drawing.dll /r:System.Windows.Forms.dll /unsafe /out:CaptureBench.exe CaptureBench.cs

using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

class CaptureBench
{
    [DllImport("user32.dll")] static extern IntPtr GetDesktopWindow();
    [DllImport("user32.dll")] static extern IntPtr GetWindowDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h,
                                                       IntPtr src, int sx, int sy, int rop);
    const int SRCCOPY = 0x00CC0020;
    const int CAPTUREBLT = 0x40000000;

    const int TILE = 128;

    static void Main(string[] a)
    {
        Screen scr = Screen.PrimaryScreen;
        int W = scr.Bounds.Width, H = scr.Bounds.Height;
        int ox = scr.Bounds.X, oy = scr.Bounds.Y;

        Console.WriteLine("화면 {0}x{1}  타일 {2}px  →  가로 {3} x 세로 {4} = {5}개",
            W, H, TILE, (W + TILE - 1) / TILE, (H + TILE - 1) / TILE,
            ((W + TILE - 1) / TILE) * ((H + TILE - 1) / TILE));
        Console.WriteLine();

        // ---------------------------------------------------------------- 1. 캡처
        Bitmap prev = null, cur = null;
        double capMs = Measure(20, delegate
        {
            if (cur != null) cur.Dispose();
            cur = Capture(ox, oy, W, H);
        });
        Console.WriteLine("1) 화면 한 장 가져오기 (BitBlt)      {0,7:F1} ms   → 이것만으로 최대 {1:F0} fps",
            capMs, 1000.0 / capMs);

        // 두 장을 만들어 비교 대상 준비
        prev = Capture(ox, oy, W, H);
        System.Threading.Thread.Sleep(120);
        if (cur != null) cur.Dispose();
        cur = Capture(ox, oy, W, H);

        // ---------------------------------------------------------------- 2. 변경 감지
        int changed = 0;
        double diffMs = Measure(20, delegate { changed = CountChangedTiles(prev, cur, W, H); });
        int totalTiles = ((W + TILE - 1) / TILE) * ((H + TILE - 1) / TILE);
        Console.WriteLine("2) 바뀐 타일 찾기 (해시 비교)        {0,7:F1} ms   → 바뀐 타일 {1}/{2}",
            diffMs, changed, totalTiles);

        // ---------------------------------------------------------------- 3. 인코딩
        Console.WriteLine();
        Console.WriteLine("3) JPEG 인코딩 (타일 1개당 / 화면 전체 환산)");
        foreach (long q in new long[] { 40, 60, 75, 90 })
        {
            double per = MeasureTileEncode(cur, q);
            Console.WriteLine("     품질 {0,2}   타일 1개 {1,6:F2} ms   전체({2}개) {3,7:F0} ms   {4}개 갱신 시 {5,6:F1} ms",
                q, per, totalTiles, per * totalTiles,
                changed, per * changed);
        }

        // ---------------------------------------------------------------- 4. 종합
        Console.WriteLine();
        double enc75 = MeasureTileEncode(cur, 75);
        double frame = capMs + diffMs + enc75 * changed;
        Console.WriteLine("4) 한 프레임 총 비용 (품질 75, 바뀐 타일 {0}개 기준)", changed);
        Console.WriteLine("     캡처 {0:F1} + 변경감지 {1:F1} + 인코딩 {2:F1} = {3:F1} ms  →  {4:F0} fps",
            capMs, diffMs, enc75 * changed, frame, 1000.0 / frame);

        Console.WriteLine();
        Console.WriteLine("   ※ 여기에 네트워크 왕복 지연이 더해진다.");
        Console.WriteLine("      Tailscale Funnel(릴레이) 경유면 그 값이 지배적일 수 있으므로 따로 잰다.");

        prev.Dispose();
        cur.Dispose();

        if (a.Length == 0)
        {
            Console.WriteLine();
            Console.Write("Enter 를 누르면 닫힙니다");
            Console.ReadLine();
        }
    }

    static Bitmap Capture(int x, int y, int w, int h)
    {
        Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            IntPtr dst = g.GetHdc();
            IntPtr desk = GetDesktopWindow();
            IntPtr src = GetWindowDC(desk);
            BitBlt(dst, 0, 0, w, h, src, x, y, SRCCOPY | CAPTUREBLT);
            ReleaseDC(desk, src);
            g.ReleaseHdc(dst);
        }
        return bmp;
    }

    /// <summary>
    /// 타일별로 바뀌었는지 센다.
    /// 픽셀을 전부 비교하지 않고 타일마다 해시를 내서 비교한다.
    /// 전체 화면을 매번 통째로 보내면 대역폭도 지연도 감당이 안 되므로,
    /// 바뀐 곳만 보내는 것이 원격제어의 기본이다.
    /// </summary>
    static unsafe int CountChangedTiles(Bitmap prev, Bitmap cur, int W, int H)
    {
        Rectangle full = new Rectangle(0, 0, W, H);
        BitmapData a = prev.LockBits(full, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        BitmapData b = cur.LockBits(full, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

        int changed = 0;
        try
        {
            byte* pa = (byte*)a.Scan0, pb = (byte*)b.Scan0;

            for (int ty = 0; ty < H; ty += TILE)
            {
                int th = Math.Min(TILE, H - ty);
                for (int tx = 0; tx < W; tx += TILE)
                {
                    int tw = Math.Min(TILE, W - tx);

                    ulong ha = 1469598103934665603UL, hb = 1469598103934665603UL;
                    for (int yy = 0; yy < th; yy++)
                    {
                        int* ra = (int*)(pa + (ty + yy) * a.Stride + tx * 4);
                        int* rb = (int*)(pb + (ty + yy) * b.Stride + tx * 4);
                        for (int xx = 0; xx < tw; xx++)
                        {
                            ha = (ha ^ (uint)ra[xx]) * 1099511628211UL;
                            hb = (hb ^ (uint)rb[xx]) * 1099511628211UL;
                        }
                    }
                    if (ha != hb) changed++;
                }
            }
        }
        finally
        {
            prev.UnlockBits(a);
            cur.UnlockBits(b);
        }
        return changed;
    }

    static double MeasureTileEncode(Bitmap src, long quality)
    {
        ImageCodecInfo jpg = null;
        foreach (ImageCodecInfo c in ImageCodecInfo.GetImageEncoders())
            if (c.FormatID == ImageFormat.Jpeg.Guid) jpg = c;

        EncoderParameters ep = new EncoderParameters(1);
        ep.Param[0] = new EncoderParameter(Encoder.Quality, quality);

        using (Bitmap tile = new Bitmap(TILE, TILE, PixelFormat.Format32bppArgb))
        using (Graphics g = Graphics.FromImage(tile))
        {
            g.DrawImage(src, new Rectangle(0, 0, TILE, TILE), new Rectangle(0, 0, TILE, TILE), GraphicsUnit.Pixel);

            return Measure(60, delegate
            {
                using (System.IO.MemoryStream ms = new System.IO.MemoryStream(8192))
                    tile.Save(ms, jpg, ep);
            });
        }
    }

    /// <summary>여러 번 돌려 평균을 낸다. 처음 몇 번은 준비 비용이라 버린다.</summary>
    static double Measure(int times, Action body)
    {
        for (int i = 0; i < 3; i++) body();

        Stopwatch sw = Stopwatch.StartNew();
        for (int i = 0; i < times; i++) body();
        sw.Stop();

        return sw.Elapsed.TotalMilliseconds / times;
    }
}

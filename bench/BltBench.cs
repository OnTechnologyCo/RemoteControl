// 화면 한 장 가져오는 방법별 비용 비교.
//
// 앞선 측정에서 BitBlt 한 번이 38ms 나왔다. 그것만으로 26fps 가 상한이라
// 화질이나 인코딩을 아무리 손봐도 소용이 없다. 어디서 그 시간이 나가는지 가른다.
//
//   CAPTUREBLT 는 계층 창(투명 창)까지 담아 주는 대신 아주 비싸다.
//   지금은 DWM 이 화면을 합쳐 주므로 대개 없어도 결과가 같다.
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

class BltBench
{
    [DllImport("user32.dll")] static extern IntPtr GetDesktopWindow();
    [DllImport("user32.dll")] static extern IntPtr GetWindowDC(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);

    [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h,
                                                       IntPtr src, int sx, int sy, int rop);
    [DllImport("gdi32.dll")] static extern bool StretchBlt(IntPtr dst, int x, int y, int w, int h,
                                                           IntPtr src, int sx, int sy, int sw, int sh, int rop);
    [DllImport("gdi32.dll")] static extern int SetStretchBltMode(IntPtr dc, int mode);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);

    const int SRCCOPY = 0x00CC0020;
    const int CAPTUREBLT = 0x40000000;
    const int HALFTONE = 4;
    const int COLORONCOLOR = 3;

    static void Main()
    {
        Screen scr = Screen.PrimaryScreen;
        int W = scr.Bounds.Width, H = scr.Bounds.Height;
        int ox = scr.Bounds.X, oy = scr.Bounds.Y;

        Console.WriteLine("화면 {0}x{1}\n", W, H);

        Console.WriteLine("방법                                   1장당      최대 fps");
        Console.WriteLine("--------------------------------------------------------");

        Time("BitBlt  SRCCOPY|CAPTUREBLT  (지금)", W, H, delegate (Graphics g, Bitmap b)
        {
            IntPtr dst = g.GetHdc();
            IntPtr src = GetWindowDC(GetDesktopWindow());
            try { BitBlt(dst, 0, 0, W, H, src, ox, oy, SRCCOPY | CAPTUREBLT); }
            finally { ReleaseDC(GetDesktopWindow(), src); g.ReleaseHdc(dst); }
        });

        Time("BitBlt  SRCCOPY 만", W, H, delegate (Graphics g, Bitmap b)
        {
            IntPtr dst = g.GetHdc();
            IntPtr src = GetWindowDC(GetDesktopWindow());
            try { BitBlt(dst, 0, 0, W, H, src, ox, oy, SRCCOPY); }
            finally { ReleaseDC(GetDesktopWindow(), src); g.ReleaseHdc(dst); }
        });

        Time("BitBlt  GetDC(NULL) + SRCCOPY", W, H, delegate (Graphics g, Bitmap b)
        {
            IntPtr dst = g.GetHdc();
            IntPtr src = GetDC(IntPtr.Zero);
            try { BitBlt(dst, 0, 0, W, H, src, ox, oy, SRCCOPY); }
            finally { ReleaseDC(IntPtr.Zero, src); g.ReleaseHdc(dst); }
        });

        Time("Graphics.CopyFromScreen (참고)", W, H, delegate (Graphics g, Bitmap b)
        {
            g.CopyFromScreen(ox, oy, 0, 0, new Size(W, H), CopyPixelOperation.SourceCopy);
        });

        // 절반 크기로 줄여서 받기. 휴대폰 화면은 작아서 원본 해상도가 필요 없다.
        HalfTime("StretchBlt  1/2 축소 (COLORONCOLOR)", W, H, COLORONCOLOR);
        HalfTime("StretchBlt  1/2 축소 (HALFTONE)", W, H, HALFTONE);
        HalfTime("StretchBlt  2/3 축소 (COLORONCOLOR)", W, H, COLORONCOLOR, 2, 3);

        Console.WriteLine("\nEnter 를 누르면 닫힙니다");
        Console.ReadLine();
    }

    static void Time(string name, int W, int H, Action<Graphics, Bitmap> shot)
    {
        using (Bitmap b = new Bitmap(W, H, PixelFormat.Format32bppArgb))
        using (Graphics g = Graphics.FromImage(b))
        {
            shot(g, b);                       // 예열

            Stopwatch sw = Stopwatch.StartNew();
            const int N = 30;
            for (int i = 0; i < N; i++) shot(g, b);
            sw.Stop();

            double ms = sw.Elapsed.TotalMilliseconds / N;
            Console.WriteLine("{0,-38} {1,6:F1} ms   {2,5:F0} fps", name, ms, 1000.0 / ms);
        }
    }

    static void HalfTime(string name, int W, int H, int mode, int num = 1, int den = 2)
    {
        int dw = W * num / den, dh = H * num / den;

        using (Bitmap b = new Bitmap(dw, dh, PixelFormat.Format32bppArgb))
        using (Graphics g = Graphics.FromImage(b))
        {
            Action shot = delegate
            {
                IntPtr dst = g.GetHdc();
                IntPtr src = GetWindowDC(GetDesktopWindow());
                try
                {
                    SetStretchBltMode(dst, mode);
                    StretchBlt(dst, 0, 0, dw, dh, src, Screen.PrimaryScreen.Bounds.X,
                               Screen.PrimaryScreen.Bounds.Y, W, H, SRCCOPY);
                }
                finally { ReleaseDC(GetDesktopWindow(), src); g.ReleaseHdc(dst); }
            };

            shot();

            Stopwatch sw = Stopwatch.StartNew();
            const int N = 30;
            for (int i = 0; i < N; i++) shot();
            sw.Stop();

            double ms = sw.Elapsed.TotalMilliseconds / N;
            Console.WriteLine("{0,-38} {1,6:F1} ms   {2,5:F0} fps   ({3}x{4})",
                              name, ms, 1000.0 / ms, dw, dh);
        }
    }
}

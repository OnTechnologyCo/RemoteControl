// 실제로 보내게 될 것과 같은 경로를 그대로 돌려 본다.
//   화면 가져오기 → 바뀐 타일 찾기 → JPEG 인코딩
// 커서를 따로 보내도록 바꾼 뒤라, 마우스만 움직일 때 정말로 아무것도 안 만드는지도 본다.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using RemoteControl;

class StreamBench
{
    static Form blinker;

    static void Main()
    {
        Native.MakeDpiAware();

        Rectangle area = Screen.PrimaryScreen.Bounds;
        Console.WriteLine("화면 {0}x{1}\n", area.Width, area.Height);

        foreach (int pct in new int[] { 100, 75, 50 })
        {
            using (ScreenSource s = new ScreenSource(area))
            {
                s.ScalePercent = pct;
                s.Grab();                       // 첫 장은 전부 새것이라 빼고 잰다

                Console.WriteLine("=== 크기 {0}%  ({1}x{2})  방법: {3} ===",
                                  pct, s.FrameWidth, s.FrameHeight, s.Method);

                Still(s);
                Moving(s, area);
                Console.WriteLine();
            }
        }

        Console.WriteLine("Enter 를 누르면 닫힙니다");
        Console.ReadLine();
    }

    /// <summary>가만히 있는 화면. 여기서 비용이 0 에 가까워야 한다.</summary>
    static void Still(ScreenSource s)
    {
        Thread.Sleep(300);
        s.Grab();

        Stopwatch sw = Stopwatch.StartNew();
        int n = 0, tiles = 0;
        long bytes = 0;

        while (sw.ElapsedMilliseconds < 1500)
        {
            List<TilePatch> p = s.Grab();
            n++;
            tiles += p.Count;
            foreach (TilePatch t in p) bytes += t.Jpeg.Length;
        }
        sw.Stop();

        Console.WriteLine("  가만히      {0,6:F1} ms/회   보낸 타일 {1,4}개  {2,6:F0} KB/s",
                          sw.Elapsed.TotalMilliseconds / n, tiles,
                          bytes / 1024.0 / sw.Elapsed.TotalSeconds);
    }

    /// <summary>화면이 계속 바뀌는 상황. 실제로 쓸 때의 상한이다.</summary>
    static void Moving(ScreenSource s, Rectangle area)
    {
        StartBlinker(area);
        Thread.Sleep(400);

        Stopwatch sw = Stopwatch.StartNew();
        int n = 0, tiles = 0;
        long bytes = 0;

        while (sw.ElapsedMilliseconds < 2000)
        {
            List<TilePatch> p = s.Grab();
            n++;
            tiles += p.Count;
            foreach (TilePatch t in p) bytes += t.Jpeg.Length;
        }
        sw.Stop();

        StopBlinker();

        double ms = sw.Elapsed.TotalMilliseconds / n;
        Console.WriteLine("  움직일 때   {0,6:F1} ms/회   {1,4:F0} fps   타일 {2,5}개  {3,6:F0} KB/s",
                          ms, 1000.0 / ms, tiles, bytes / 1024.0 / sw.Elapsed.TotalSeconds);
    }

    static void StartBlinker(Rectangle area)
    {
        Thread t = new Thread(delegate ()
        {
            blinker = new Form
            {
                FormBorderStyle = FormBorderStyle.None,
                StartPosition = FormStartPosition.Manual,
                Location = new Point(area.X + 200, area.Y + 200),
                Size = new Size(600, 400),
                TopMost = true,
                ShowInTaskbar = false
            };

            Random rnd = new Random(3);
            System.Windows.Forms.Timer tm = new System.Windows.Forms.Timer { Interval = 16 };
            tm.Tick += delegate
            {
                blinker.BackColor = Color.FromArgb(rnd.Next(30, 250), rnd.Next(30, 250), rnd.Next(30, 250));
            };
            tm.Start();

            Application.Run(blinker);
        });

        t.IsBackground = true;
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
    }

    static void StopBlinker()
    {
        try { if (blinker != null) blinker.Invoke((MethodInvoker)delegate { blinker.Close(); }); }
        catch { }
        blinker = null;
        Thread.Sleep(200);
    }
}

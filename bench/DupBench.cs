// Desktop Duplication 을 제품에 넣기 전에 따로 돌려 본다.
//
// COM 함수 표를 손으로 선언했기 때문에, 자리를 하나라도 잘못 세면 엉뚱한 함수가 불려
// 프로세스가 즉사한다. 그게 서비스 안에서 일어나면 원격제어가 통째로 끊긴다.
// 그래서 먼저 여기서 돌려 보고, 멀쩡할 때만 제품에 넣는다.
//
// 화면이 가만히 있으면 윈도우는 새 그림을 주지 않는다. 그래서 이 벤치는
// 스스로 창을 하나 띄워 계속 다시 그린다. 안 그러면 전부 "변화 없음" 만 나와
// 정작 검증하려는 복사 경로를 한 번도 안 지나간다 (실제로 그렇게 헛돌았다).
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Threading;
using System.Windows.Forms;
using RemoteControl;

class DupBench
{
    static Form blinker;

    static void Main()
    {
        Screen scr = Screen.PrimaryScreen;
        Rectangle area = scr.Bounds;

        Console.WriteLine("화면 {0}x{1}\n", area.Width, area.Height);

        StartBlinker(area);
        Thread.Sleep(500);

        Console.WriteLine("1) 복제본 열기…");
        DesktopDup dup = DesktopDup.Open(area);

        if (dup == null)
        {
            Console.WriteLine("   열지 못했습니다. GDI 로 계속 씁니다 (문제 없음).");
            Done();
            return;
        }

        Console.WriteLine("   열렸습니다.\n");
        Console.WriteLine("   축소     크기         새 그림 1장당   변화없음 1번당   새 그림/전체");
        Console.WriteLine("   ------------------------------------------------------------------");

        Bitmap keep = null;

        foreach (int pct in new int[] { 100, 75, 50 })
        {
            int fw = area.Width * pct / 100, fh = area.Height * pct / 100;

            Bitmap b = new Bitmap(fw, fh, PixelFormat.Format32bppArgb);

            long newTicks = 0, idleTicks = 0;
            int newN = 0, idleN = 0, fail = 0;

            for (int i = 0; i < 120; i++)
            {
                Stopwatch sw = Stopwatch.StartNew();
                int r;
                try { r = dup.Grab(b, area, fw, fh); }
                catch (Exception ex) { Console.WriteLine("   예외: " + ex.Message); fail++; break; }
                sw.Stop();

                if (r == DesktopDup.GrabNewFrame) { newTicks += sw.ElapsedTicks; newN++; }
                else if (r == DesktopDup.GrabNoChange) { idleTicks += sw.ElapsedTicks; idleN++; }
                else fail++;
            }

            double perNew = newN == 0 ? 0 : newTicks * 1000.0 / Stopwatch.Frequency / newN;
            double perIdle = idleN == 0 ? 0 : idleTicks * 1000.0 / Stopwatch.Frequency / idleN;

            Console.WriteLine("   {0,3}%  {1,4}x{2,-5}  {3,10:F2} ms  {4,12:F2} ms  {5,8}/{6}",
                              pct, fw, fh, perNew, perIdle, newN, newN + idleN + fail);

            if (pct == 75) keep = b; else b.Dispose();
        }

        Console.WriteLine("\n2) 픽셀이 진짜로 들어왔는지 확인");

        if (keep != null)
        {
            // 온통 같은 색이면 복사가 안 된 것이다. 색이 몇 가지나 되는지 센다.
            System.Collections.Generic.Dictionary<int, int> seen =
                new System.Collections.Generic.Dictionary<int, int>();

            for (int y = 0; y < keep.Height; y += 17)
                for (int x = 0; x < keep.Width; x += 23)
                {
                    int c = keep.GetPixel(x, y).ToArgb();
                    if (!seen.ContainsKey(c)) seen[c] = 0;
                    seen[c]++;
                }

            Console.WriteLine("   서로 다른 색 {0} 가지  (1 이면 복사 실패)", seen.Count);

            string png = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dupshot.png");
            keep.Save(png, ImageFormat.Png);
            Console.WriteLine("   확인용 그림: " + png);
            keep.Dispose();
        }

        dup.Dispose();
        Console.WriteLine("\n3) 정리까지 통과.");
        Done();
    }

    /// <summary>화면이 계속 바뀌도록 작은 창 하나를 깜빡인다.</summary>
    static void StartBlinker(Rectangle area)
    {
        Thread t = new Thread(delegate ()
        {
            blinker = new Form
            {
                FormBorderStyle = FormBorderStyle.None,
                StartPosition = FormStartPosition.Manual,
                Location = new Point(area.X + 60, area.Y + 60),
                Size = new Size(420, 260),
                TopMost = true,
                ShowInTaskbar = false
            };

            Random rnd = new Random(7);
            System.Windows.Forms.Timer tm = new System.Windows.Forms.Timer { Interval = 16 };
            tm.Tick += delegate
            {
                blinker.BackColor = Color.FromArgb(rnd.Next(40, 250), rnd.Next(40, 250), rnd.Next(40, 250));
            };
            tm.Start();

            Application.Run(blinker);
        });

        t.IsBackground = true;
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
    }

    static void Done()
    {
        try { if (blinker != null) blinker.Invoke((MethodInvoker)delegate { blinker.Close(); }); }
        catch { }

        Console.WriteLine("\nEnter 를 누르면 닫힙니다");
        Console.ReadLine();
    }
}

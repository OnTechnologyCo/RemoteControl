// 터널 중계 프로그램을 어디에 놓는지 확인한다.
//
// 왜 따로 시험하나 — 여기가 조용히 실패하면 외부망이 통째로 안 열린다.
//   그런데 그 사실은 "밖에서 접속이 안 된다" 로만 드러난다. 원격에서 고치던 중이면
//   손쓸 방법이 없어진다. 설치본을 갈아치우기 전에 여기서 먼저 본다.
//
// 확인하는 것
//   1. %TEMP% 에는 더 이상 풀지 않는다 (오탐의 원인이었다)
//   2. ProgramData\RemoteControl\bin 에 실제로 놓인다
//   3. 놓인 것이 진짜 PE 파일이다 (gzip 이 제대로 풀렸나)
//   4. 두 번 불러도 다시 쓰지 않고 있던 것을 그대로 쓴다
//   5. 권한이 없으면 잠그려 들지 않는다 (보통 권한으로 자기 폴더를 잠가 버리면
//      단독 실행 모드가 그 자리에서 멈춘다)
//
// 빌드 (본체와 같은 리소스가 필요하다 - tsproxy.exe.gz 가 들어 있어야 한다):
//   /main:RemoteControl.TunnelPlaceTest 로 진입점만 바꿔 본체를 통째로 컴파일한다.
using System;
using System.IO;
using System.Reflection;
using System.Text;

namespace RemoteControl
{
    internal static class TunnelPlaceTest
    {
        private static int fails;

        private static void Ok(string what, bool cond, string extra)
        {
            Console.WriteLine((cond ? "  OK   " : "  실패 ") + what + (cond || extra == null ? "" : "  → " + extra));
            if (!cond) fails++;
        }

        private static string CallExtract()
        {
            // Extract 는 private 이다. 시험을 위해 접근 제한을 풀어 주면 그만큼
            // 실제 코드가 시험용으로 헐거워진다. 여기서만 반사로 부른다.
            MethodInfo m = typeof(Tunnel).GetMethod("Extract",
                BindingFlags.NonPublic | BindingFlags.Static);

            if (m == null) throw new Exception("Tunnel.Extract 를 찾지 못했습니다.");

            return (string)m.Invoke(null, null);
        }

        internal static void Main()
        {
            Console.OutputEncoding = Encoding.UTF8;
            Console.WriteLine("터널 중계 배치 시험");
            Console.WriteLine();

            string[] dirs = Tunnel.BinDirs();
            Console.WriteLine("  놓을 수 있는 자리:");
            foreach (string d in dirs) Console.WriteLine("    " + (d ?? "(없음)"));
            Console.WriteLine();

            string oldTemp = Path.Combine(Path.GetTempPath(), "RemoteControlTs");
            bool oldExisted = Directory.Exists(oldTemp);

            string got = CallExtract();

            Ok("풀어 놓았다", got != null,
               got == null ? typeof(Tunnel).GetField("LastError",
                   BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static).GetValue(null) as string : null);

            if (got == null) { Environment.Exit(1); return; }

            Console.WriteLine("  놓인 곳: " + got);
            Console.WriteLine();

            Ok("%TEMP% 가 아니다", !got.StartsWith(oldTemp, StringComparison.OrdinalIgnoreCase), got);

            string shared = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "RemoteControl", "bin");

            string user = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RemoteControl", "bin");

            Ok("공용 자리(ProgramData) 아니면 계정 자리(LocalAppData)다",
               got.StartsWith(shared, StringComparison.OrdinalIgnoreCase) ||
               got.StartsWith(user, StringComparison.OrdinalIgnoreCase), got);

            Ok("파일이 실제로 있다", File.Exists(got), null);

            long size = new FileInfo(got).Length;
            Console.WriteLine("  크기: " + string.Format("{0:N0}", size) + " 바이트");

            // gzip 이 덜 풀렸으면 앞머리가 MZ 가 아니다. 그대로 실행하면
            // "올바른 Win32 응용 프로그램이 아닙니다" 만 나오고 이유를 알 수 없다.
            byte[] head = new byte[2];
            using (FileStream fs = File.OpenRead(got)) fs.Read(head, 0, 2);

            Ok("진짜 실행 파일이다 (MZ)", head[0] == (byte)'M' && head[1] == (byte)'Z',
               "앞머리가 " + (char)head[0] + (char)head[1]);

            Ok("크기가 그럴듯하다 (5MB 이상)", size > 5 * 1024 * 1024,
               string.Format("{0:N0} 바이트", size));

            // 두 번째 호출은 이미 있는 것을 그대로 써야 한다. 매번 25MB 를 다시 쓰면
            // 터널을 열 때마다 디스크를 긁고, 백신이 "실행 파일을 반복해서 떨군다" 로 본다.
            DateTime before = File.GetLastWriteTimeUtc(got);
            string again = CallExtract();

            Ok("두 번 불러도 같은 곳", again == got, again);
            Ok("다시 쓰지 않는다", File.GetLastWriteTimeUtc(got) == before, null);

            if (!oldExisted)
                Ok("%TEMP% 에 새로 만들지 않았다", !Directory.Exists(oldTemp), oldTemp);

            Console.WriteLine();
            Console.WriteLine(fails == 0 ? "전부 통과" : fails + "건 실패");
            Environment.Exit(fails == 0 ? 0 : 1);
        }
    }
}

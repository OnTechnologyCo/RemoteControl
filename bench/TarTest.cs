// tar 만들기 시험.
//
// 확인하는 것:
//   1. Plan() 이 셈한 크기와 실제로 쓴 바이트 수가 정확히 같은가
//      (어긋나면 Content-Length 가 틀려 브라우저가 끝나지 않는 다운로드를 붙잡는다)
//   2. 윈도우에 들어 있는 진짜 tar 가 풀 수 있는가
//   3. 한글 이름·긴 경로·빈 파일·큰 파일이 그대로 살아 돌아오는가
//   4. 만드는 도중 파일이 짧아져도 크기가 어긋나지 않는가
using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace RemoteControl
{
    internal static class TarTest
    {
        private static int fails;

        private static void Ok(string what, bool cond)
        {
            Console.WriteLine((cond ? "  OK   " : "  실패 ") + what);
            if (!cond) fails++;
        }

        internal static void Main()
        {
            Console.OutputEncoding = Encoding.UTF8;

            string work = Path.Combine(Path.GetTempPath(), "tartest");
            if (Directory.Exists(work)) { try { Directory.Delete(work, true); } catch { } }

            string src = Path.Combine(work, "보낼폴더");
            Directory.CreateDirectory(src);
            Directory.CreateDirectory(Path.Combine(src, "하위 폴더"));
            Directory.CreateDirectory(Path.Combine(src, "깊이1", "깊이2", "깊이3"));

            File.WriteAllText(Path.Combine(src, "한글 이름.txt"), "안녕하세요 원격제어입니다.", Encoding.UTF8);
            File.WriteAllText(Path.Combine(src, "ascii.txt"), "hello");
            File.WriteAllBytes(Path.Combine(src, "empty.bin"), new byte[0]);
            File.WriteAllText(Path.Combine(src, "하위 폴더", "안쪽.txt"), "inner");

            // 512 로 딱 나누어떨어지는 크기 - 채움 계산이 틀리기 쉬운 자리다
            File.WriteAllBytes(Path.Combine(src, "exact512.bin"), new byte[512]);

            // 100글자를 넘는 이름 (USTAR 이름 칸에 안 들어간다 → PAX 로 가야 한다)
            File.WriteAllText(Path.Combine(src, "깊이1", "깊이2", "깊이3",
                new string('가', 60) + ".txt"), "long");

            byte[] big = new byte[3 * 1024 * 1024 + 777];
            new Random(7).NextBytes(big);
            File.WriteAllBytes(Path.Combine(src, "big.bin"), big);

            Console.WriteLine("[1] 셈한 크기 = 실제로 쓴 크기");

            long planned;
            var plan = Tar.Plan(src, out planned);

            string tarPath = Path.Combine(work, "out.tar");
            using (FileStream fs = File.Create(tarPath)) Tar.Write(fs, plan);

            long actual = new FileInfo(tarPath).Length;
            Ok("담긴 파일 " + plan.Count + "개", plan.Count == 7);
            Ok("셈 " + planned + " = 실제 " + actual, planned == actual);
            Ok("512 의 배수", actual % 512 == 0);

            Console.WriteLine();
            Console.WriteLine("[2] 윈도우 tar 로 풀기");

            string outDir = Path.Combine(work, "푼것");
            Directory.CreateDirectory(outDir);

            string err;
            int rc = Run("tar", "-xf \"" + tarPath + "\" -C \"" + outDir + "\"", out err);

            Ok("tar 가 오류 없이 풀었다" + (rc != 0 ? "  → " + err.Trim() : ""), rc == 0);

            string root = Path.Combine(outDir, "보낼폴더");
            Ok("뿌리 폴더가 그대로", Directory.Exists(root));

            Console.WriteLine();
            Console.WriteLine("[3] 내용 확인");

            Ok("한글 이름이 살아 있다",
                File.Exists(Path.Combine(root, "한글 이름.txt")));

            Ok("한글 내용이 그대로",
                File.Exists(Path.Combine(root, "한글 이름.txt")) &&
                File.ReadAllText(Path.Combine(root, "한글 이름.txt"), Encoding.UTF8) == "안녕하세요 원격제어입니다.");

            Ok("빈 파일", File.Exists(Path.Combine(root, "empty.bin")) &&
                          new FileInfo(Path.Combine(root, "empty.bin")).Length == 0);

            Ok("512 배수 파일", File.Exists(Path.Combine(root, "exact512.bin")) &&
                                new FileInfo(Path.Combine(root, "exact512.bin")).Length == 512);

            string bigOut = Path.Combine(root, "big.bin");
            bool bigOk = File.Exists(bigOut) && Same(File.ReadAllBytes(bigOut), big);
            Ok("3MB 파일이 한 바이트도 안 틀린다", bigOk);

            Ok("하위 폴더", File.Exists(Path.Combine(root, "하위 폴더", "안쪽.txt")));

            string longName = new string('가', 60) + ".txt";
            Ok("100글자 넘는 경로 (PAX)",
                File.Exists(Path.Combine(root, "깊이1", "깊이2", "깊이3", longName)));

            Console.WriteLine();
            Console.WriteLine("[4] 만드는 도중 파일이 짧아져도 크기가 안 어긋난다");

            string shrink = Path.Combine(src, "줄어들파일.bin");
            File.WriteAllBytes(shrink, new byte[100000]);

            long planned2;
            var plan2 = Tar.Plan(src, out planned2);

            // 셈한 뒤에 짧아졌다 - 실제로 옮기는 중에 흔히 벌어지는 일이다
            File.WriteAllBytes(shrink, new byte[10]);

            string tar2 = Path.Combine(work, "out2.tar");
            using (FileStream fs = File.Create(tar2)) Tar.Write(fs, plan2);

            long actual2 = new FileInfo(tar2).Length;
            Ok("셈 " + planned2 + " = 실제 " + actual2, planned2 == actual2);

            string out2 = Path.Combine(work, "푼것2");
            Directory.CreateDirectory(out2);
            Ok("그래도 tar 가 풀 수 있다", Run("tar", "-xf \"" + tar2 + "\" -C \"" + out2 + "\"", out err) == 0);

            Console.WriteLine();
            Console.WriteLine("[5] 고른 것 여럿 묶기 (파일 + 폴더 섞어서)");

            var picks = new System.Collections.Generic.List<string>();
            picks.Add(Path.Combine(src, "한글 이름.txt"));
            picks.Add(Path.Combine(src, "ascii.txt"));
            picks.Add(Path.Combine(src, "하위 폴더"));            // 폴더도 섞는다

            long planned3;
            var plan3 = Tar.PlanMany(picks, out planned3);

            string tar3 = Path.Combine(work, "out3.tar");
            using (FileStream fs = File.Create(tar3)) Tar.Write(fs, plan3);

            Ok("담긴 파일 " + plan3.Count + "개 (파일2 + 폴더안1)", plan3.Count == 3);
            Ok("셈 " + planned3 + " = 실제 " + new FileInfo(tar3).Length,
                planned3 == new FileInfo(tar3).Length);

            string out3 = Path.Combine(work, "푼것3");
            Directory.CreateDirectory(out3);
            Ok("tar 가 풀었다", Run("tar", "-xf \"" + tar3 + "\" -C \"" + out3 + "\"", out err) == 0);

            // 고른 것들의 공통 부모(보낼폴더)는 안 담긴다. 고른 그대로 나와야 한다.
            Ok("고른 파일이 뿌리에 바로 나온다 (공통 부모 없음)",
                File.Exists(Path.Combine(out3, "한글 이름.txt")) &&
                File.Exists(Path.Combine(out3, "ascii.txt")));

            Ok("고른 폴더는 폴더째로 나온다",
                File.Exists(Path.Combine(out3, "하위 폴더", "안쪽.txt")));

            Ok("고르지 않은 것은 안 딸려 온다",
                !File.Exists(Path.Combine(out3, "big.bin")) &&
                !Directory.Exists(Path.Combine(out3, "깊이1")));

            Console.WriteLine();
            Console.WriteLine(fails == 0 ? "전부 통과" : fails + "건 실패");
            Environment.Exit(fails == 0 ? 0 : 1);
        }

        private static bool Same(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        private static int Run(string exe, string args, out string err)
        {
            ProcessStartInfo psi = new ProcessStartInfo(exe, args);
            psi.UseShellExecute = false;
            psi.RedirectStandardError = true;
            psi.RedirectStandardOutput = true;
            psi.CreateNoWindow = true;

            using (Process p = Process.Start(psi))
            {
                err = p.StandardError.ReadToEnd();
                p.StandardOutput.ReadToEnd();
                p.WaitForExit(60000);
                return p.ExitCode;
            }
        }
    }
}

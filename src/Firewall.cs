// 방화벽 규칙을 지금 쓰는 포트에 맞춰 둔다.
//
// 규칙을 설치할 때 한 번만 넣어 두면, 나중에 포트를 바꿨을 때 옛 포트가 열린 채
// 새 포트는 막힌다. 같은 공유기 안에서 접속이 안 되는데 이유를 알기 어렵다.
// 그래서 프로그램이 뜰 때와 포트가 바뀔 때마다 다시 맞춘다.
//
// 규칙은 두 개를 넣는다.
//   1) 프로그램 규칙 - 이 exe 로 들어오는 연결을 포트와 상관없이 허용
//   2) 포트 규칙     - 1) 이 회사 정책 등으로 막혔을 때를 위한 대비
//
// 넣으려면 관리자 권한이 필요하다. 서비스(SYSTEM)로 돌 때는 그냥 되고,
// 창만 띄운 상태에서는 조용히 실패한다. 실패해도 바깥에서 오는 접속(Tailscale)은
// 나가는 연결이라 영향이 없다. 막히는 것은 같은 공유기 안에서 오는 접속뿐이다.
using System;
using System.Diagnostics;
using System.Text;

namespace RemoteControl
{
    internal static class Firewall
    {
        internal const string RuleName = "원격제어";

        private static int lastPort = -1;
        private static readonly object gate = new object();

        /// <summary>규칙을 지금 포트에 맞춘다. 이미 맞으면 아무것도 안 한다.</summary>
        internal static bool Ensure(string exePath, int port)
        {
            lock (gate)
            {
                if (port == lastPort) return true;

                Remove();

                bool ok = true;

                if (!string.IsNullOrEmpty(exePath))
                    ok &= Run("advfirewall firewall add rule name=\"" + RuleName + "\"" +
                              " dir=in action=allow program=\"" + exePath + "\"" +
                              " enable=yes profile=private,domain");

                ok &= Run("advfirewall firewall add rule name=\"" + RuleName + "\"" +
                          " dir=in action=allow protocol=TCP localport=" + port +
                          " enable=yes profile=private,domain");

                if (ok) lastPort = port;
                return ok;
            }
        }

        internal static void Remove()
        {
            Run("advfirewall firewall delete rule name=\"" + RuleName + "\"");
        }

        private static bool Run(string args)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = "netsh.exe",
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8
                };

                using (Process p = Process.Start(psi))
                {
                    p.StandardOutput.ReadToEnd();
                    p.StandardError.ReadToEnd();
                    p.WaitForExit(10000);
                    return p.ExitCode == 0;
                }
            }
            catch { return false; }
        }
    }
}

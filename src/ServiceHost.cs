// 잠금 화면까지 제어하려면 서비스가 필요하다.
//
// 왜 서비스인가.
//   잠금 화면(Winlogon 데스크톱)은 SYSTEM 만 열 수 있다. 보통 사용자로 실행한
//   프로그램은 그 화면을 캡처할 수도, 거기에 키를 넣을 수도 없다. 권한 문제라
//   코드로는 우회가 없다. 원격제어 프로그램들이 설치할 때 서비스를 까는 이유다.
//
// 그런데 서비스는 세션 0 에 갇힌다. 세션 0 에는 화면이 없다.
// 그래서 서비스 자신은 아무것도 하지 않고, 실제 일을 할 "대리 프로세스" 를
// 사람이 쓰는 세션(콘솔 세션)에 SYSTEM 권한으로 밀어 넣는다.
//   서비스(세션 0, SYSTEM)  ->  대리 프로세스(세션 1, SYSTEM)
// 대리 프로세스는 세션 1 안에 있으므로 Default 와 Winlogon 사이를 오갈 수 있다.
// 잠그거나 풀어도 대리 프로세스를 다시 띄우지 않는다. 스레드만 옮겨 붙는다.
// 그래서 잠금/해제 순간에도 연결이 끊기지 않는다.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Threading;

namespace RemoteControl
{
    internal static class ServiceHost
    {
        internal const string ServiceName = "RemoteControlSvc";
        internal const string DisplayName = "원격제어";

        // ---------------------------------------------------------------- 서비스 본체

        internal static void RunAsService()
        {
            ServiceBase.Run(new RcService());
        }

        private sealed class RcService : ServiceBase
        {
            private Thread keeper;
            private volatile bool stop;
            private Process agent;
            private uint agentSession = 0xFFFFFFFF;
            private readonly ManualResetEvent poke = new ManualResetEvent(false);

            internal RcService()
            {
                base.ServiceName = ServiceHost.ServiceName;
                CanHandleSessionChangeEvent = true;
                CanShutdown = true;
                CanStop = true;
            }

            protected override void OnStart(string[] args)
            {
                stop = false;
                SvcLog.Write("서비스 시작");

                keeper = new Thread(Keep);
                keeper.IsBackground = true;
                keeper.Start();
            }

            protected override void OnStop() { SvcLog.Write("서비스 중지"); Shut(); }
            protected override void OnShutdown() { Shut(); }

            private void Shut()
            {
                stop = true;
                poke.Set();
                KillAgent();
            }

            // 로그인, 잠금, 사용자 전환 때 바로 다시 살펴본다.
            //
            // 시각을 적어 두는 이유: 이 직후가 tscon 을 부르기에 가장 위험한 순간이다.
            // RDP 가 붙는 중에 이 이벤트가 오는데, 그때 세션은 아직 Active 가 아니다.
            protected override void OnSessionChange(SessionChangeDescription d)
            {
                lastChange = DateTime.Now;
                poke.Set();
            }

            private void Keep()
            {
                while (!stop)
                {
                    try { EnsureAgent(); }
                    catch (Exception ex) { SvcLog.Write("대리 프로세스 실행 실패: " + ex.Message); }

                    poke.WaitOne(3000);
                    poke.Reset();
                }
            }

            /// <summary>
            /// 끊긴 원격 데스크톱 세션을 콘솔로 되돌린다.
            ///
            /// 모니터 없는 PC 를 원격 데스크톱으로 쓰다가 창을 닫으면, 그 세션은
            /// "끊김" 이 되고 윈도우는 더 이상 그 화면을 그리지 않는다. 모니터도 없으니
            /// 어디에도 그려지는 화면이 없어 우리는 새까만 그림만 잡게 된다.
            /// 콘솔로 옮겨 놓으면 다시 그려진다.
            /// </summary>
            private DateTime lastRescue;
            private DateTime lastChange;
            private bool saidHasMonitor;

            /// <summary>세션이 끊긴 채로 있기 시작한 시각. 방금 끊긴 것은 건드리지 않는다.</summary>
            private readonly Dictionary<uint, DateTime> goneSince = new Dictionary<uint, DateTime>();

            private void RescueDisconnected()
            {
                Config.Reload();
                if (Config.Get("keepdesktop") == "0") return;

                // 모니터가 있으면 할 일이 없다.
                //
                // 이 기능은 처음부터 "모니터 없는 PC" 용이었는데 그 조건이 빠져 있었다.
                // 모니터가 달린 PC 에서는 콘솔 화면이 계속 그려지므로 우리는 그걸 잡으면 된다.
                // 그런 PC 에서 tscon 을 부르면 얻는 것 없이 남의 RDP 세션만 뺏어 끊어 놓는다.
                // 실제로 "원격 데스크톱이 자꾸 끊긴다" 로 겪었다.
                if (!Display.LooksHeadless())
                {
                    if (!saidHasMonitor)
                    {
                        saidHasMonitor = true;
                        SvcLog.Write("모니터가 있는 PC 입니다. 끊긴 세션을 콘솔로 되돌리지 않습니다 " +
                                     "(그럴 필요가 없고, 원격 데스크톱만 끊깁니다).");
                    }
                    return;
                }

                // 세션이 방금 바뀌었으면 손대지 않는다.
                //
                // RDP 접속은 이 이벤트가 온 뒤 몇 초에 걸쳐 자리를 잡는다. 그 사이에는
                // 어느 세션도 Active 가 아니어서 "아무도 안 쓴다" 로 보인다.
                // 거기서 tscon 을 부르면 지금 붙고 있는 그 세션을 뺏는다.
                if ((DateTime.Now - lastChange).TotalSeconds < 10) return;

                Win.SessionInfo[] all = Win.Sessions();

                // 누군가 쓰고 있거나 붙는 중이면 손대지 않는다.
                // 예전에는 Active 만 봤다 - Connected/ConnectQuery 를 놓쳐서 접속 중인
                // 세션을 뺏었다 (Win.InUse 주석 참고).
                foreach (Win.SessionInfo s in all)
                    if (s.Id != 0 && Win.InUse(s.State)) { goneSince.Clear(); return; }

                // 끊긴 채로 얼마나 있었는지 센다. 방금 끊긴 것은 사람이 다시 붙는 중일 수 있다.
                DateTime now = DateTime.Now;
                foreach (Win.SessionInfo s in all)
                {
                    if (s.Id == 0) continue;

                    if (s.State != Win.WTSDisconnected) { goneSince.Remove(s.Id); continue; }
                    if (!goneSince.ContainsKey(s.Id)) goneSince[s.Id] = now;
                }

                // 안 되는 상황이면 3초마다 계속 시도하며 기록만 쌓인다. 간격을 둔다.
                if ((now - lastRescue).TotalSeconds < 30) return;
                lastRescue = now;

                foreach (Win.SessionInfo s in all)
                {
                    if (s.Id == 0 || s.State != Win.WTSDisconnected) continue;

                    DateTime gone;
                    if (!goneSince.TryGetValue(s.Id, out gone)) continue;
                    if ((now - gone).TotalSeconds < 60) continue;      // 아직 다시 붙을 수 있다

                    SvcLog.Write("끊긴 원격 데스크톱 세션 " + s.Id + " 을 콘솔로 되돌립니다 " +
                                 "(그대로 두면 화면이 검게 나옵니다).");

                    // 콘솔에 제대로 된 화면 장치가 없으면, 검은 화면은 면하지만 대신
                    // 아주 작은 해상도로 떨어진다. 왜 작아졌는지 알 수 있게 남긴다.
                    try
                    {
                        // 여기까지 왔다는 것은 이미 모니터가 없다는 뜻이다(위에서 걸렀다).
                        // 콘솔에 제대로 된 화면 장치가 없으면 검은 화면은 면하지만 대신
                        // 아주 작은 해상도로 떨어진다. 왜 작아졌는지 알 수 있게 남긴다.
                        SvcLog.Write("  주의: 이 PC 에는 모니터가 없어 콘솔이 " +
                                     Display.Current().Width + "x" + Display.Current().Height +
                                     " 밖에 못 냅니다. 더미 플러그나 가상 디스플레이가 필요합니다.");
                    }
                    catch { }

                    if (Win.ReconnectToConsole(s.Id))
                    {
                        SvcLog.Write("  되돌렸습니다.");
                        goneSince.Remove(s.Id);
                        Thread.Sleep(1500);
                        return;
                    }

                    SvcLog.Write("  되돌리지 못했습니다.");
                }
            }

            private void EnsureAgent()
            {
                RescueDisconnected();

                // 콘솔 세션만 보면 안 된다. 원격 데스크톱으로 들어와 있으면 사람은
                // 다른 세션에 앉아 있고, 우리는 잠긴 콘솔 화면만 잡게 된다.
                uint sid = Win.PickSession();
                if (sid == 0xFFFFFFFF) return;          // 붙을 세션이 없다 (전환 중)

                bool alive;
                try { alive = agent != null && !agent.HasExited; }
                catch { alive = false; }

                if (alive && agentSession == sid) return;

                KillAgent();

                string exe = Self();
                Process p = Win.LaunchInSession(sid, "\"" + exe + "\" --agent",
                                                Path.GetDirectoryName(exe));
                if (p == null)
                {
                    SvcLog.Write("세션 " + sid + " 에 대리 프로세스를 띄우지 못했습니다 (오류 " +
                                 Marshal.GetLastWin32Error() + ")");
                    return;
                }

                agent = p;
                agentSession = sid;
                Interop.Adopt(p);                        // 서비스가 죽으면 커널이 같이 정리
                SvcLog.Write("세션 " + sid + " 에서 대리 프로세스 시작 (pid " + p.Id + ")");
            }

            private void KillAgent()
            {
                if (agent == null) return;

                try { if (!agent.HasExited) agent.Kill(); }
                catch { }
                try { agent.Dispose(); } catch { }

                agent = null;
                agentSession = 0xFFFFFFFF;
            }
        }

        internal static string Self()
        {
            return System.Reflection.Assembly.GetExecutingAssembly().Location;
        }

        // ---------------------------------------------------------------- 상태 확인

        internal static bool IsInstalled()
        {
            try
            {
                foreach (ServiceController s in ServiceController.GetServices())
                {
                    bool hit = string.Equals(s.ServiceName, ServiceName, StringComparison.OrdinalIgnoreCase);
                    s.Dispose();
                    if (hit) return true;
                }
            }
            catch { }
            return false;
        }

        internal static bool IsRunning()
        {
            try
            {
                using (ServiceController s = new ServiceController(ServiceName))
                    return s.Status == ServiceControllerStatus.Running ||
                           s.Status == ServiceControllerStatus.StartPending;
            }
            catch { return false; }
        }

        internal static bool IsElevated()
        {
            try
            {
                using (System.Security.Principal.WindowsIdentity id =
                       System.Security.Principal.WindowsIdentity.GetCurrent())
                    return new System.Security.Principal.WindowsPrincipal(id)
                        .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        /// <summary>관리자 권한으로 자기 자신을 다시 띄운다 (UAC 창이 뜬다).</summary>
        internal static bool Elevate(string args)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = Self(),
                    Arguments = args,
                    UseShellExecute = true,
                    Verb = "runas",
                    WorkingDirectory = Path.GetDirectoryName(Self())
                };

                Process p = Process.Start(psi);
                if (p == null) return false;

                p.WaitForExit(120000);
                return p.ExitCode == 0;
            }
            catch { return false; }         // 사용자가 UAC 를 거절하면 여기로 온다
        }

        // ---------------------------------------------------------------- 설치와 제거

        internal static string Install()
        {
            IntPtr scm = Win.OpenSCManager(null, null, Win.SC_MANAGER_CREATE_SERVICE);
            if (scm == IntPtr.Zero)
                return "서비스 관리자를 열지 못했습니다 (오류 " + Marshal.GetLastWin32Error() + ")";

            try
            {
                string bin = "\"" + Self() + "\" --service";

                IntPtr svc = Win.CreateService(scm, ServiceName, DisplayName, Win.SERVICE_ALL_ACCESS,
                    Win.SERVICE_WIN32_OWN_PROCESS, Win.SERVICE_AUTO_START, Win.SERVICE_ERROR_NORMAL,
                    bin, null, IntPtr.Zero, null, null, null);

                if (svc == IntPtr.Zero)
                {
                    int e = Marshal.GetLastWin32Error();

                    // 1073 = 이미 있다. 그러면 열어서 실행 파일 경로만 새로 맞춘다.
                    if (e == 1073)
                    {
                        svc = Win.OpenService(scm, ServiceName, Win.SERVICE_ALL_ACCESS);
                        if (svc != IntPtr.Zero)
                            Win.ChangeServiceConfig(svc, 0xFFFFFFFF, Win.SERVICE_AUTO_START,
                                0xFFFFFFFF, bin, null, IntPtr.Zero, null, null, null, null);
                    }

                    if (svc == IntPtr.Zero) return "서비스를 만들지 못했습니다 (오류 " + e + ")";
                }

                try
                {
                    Win.SERVICE_DESCRIPTION d = new Win.SERVICE_DESCRIPTION();
                    d.lpDescription = "잠금 화면에서도 이 PC 를 원격으로 쓸 수 있게 합니다.";
                    Win.ChangeServiceConfig2(svc, Win.SERVICE_CONFIG_DESCRIPTION, ref d);

                    // 죽으면 스스로 다시 뜨게 한다. 원격제어가 꺼지면 손쓸 방법이 없다.
                    Win.SetRestartOnFailure(svc);

                    if (!Win.StartService(svc, 0, IntPtr.Zero))
                    {
                        int e = Marshal.GetLastWin32Error();
                        if (e != 1056)      // 1056 = 이미 실행 중
                            return "서비스를 시작하지 못했습니다 (오류 " + e + ")";
                    }
                }
                finally { Win.CloseServiceHandle(svc); }

                return "";
            }
            finally { Win.CloseServiceHandle(scm); }
        }

        /// <summary>등록은 돼 있는데 멈춰 있는 서비스를 다시 돌린다.</summary>
        internal static string StartNow()
        {
            IntPtr scm = Win.OpenSCManager(null, null, Win.SC_MANAGER_CREATE_SERVICE);
            if (scm == IntPtr.Zero)
                return "서비스 관리자를 열지 못했습니다 (오류 " + Marshal.GetLastWin32Error() + ")";

            try
            {
                IntPtr svc = Win.OpenService(scm, ServiceName, Win.SERVICE_ALL_ACCESS);
                if (svc == IntPtr.Zero) return "서비스가 등록되어 있지 않습니다.";

                try
                {
                    if (!Win.StartService(svc, 0, IntPtr.Zero))
                    {
                        int e = Marshal.GetLastWin32Error();
                        if (e != 1056) return "시작하지 못했습니다 (오류 " + e + ")";   // 1056 = 이미 실행 중
                    }

                    SvcLog.Write("서비스를 다시 시작했습니다.");
                    return "";
                }
                finally { Win.CloseServiceHandle(svc); }
            }
            finally { Win.CloseServiceHandle(scm); }
        }

        internal static string Uninstall()
        {
            IntPtr scm = Win.OpenSCManager(null, null, Win.SC_MANAGER_CREATE_SERVICE);
            if (scm == IntPtr.Zero)
                return "서비스 관리자를 열지 못했습니다 (오류 " + Marshal.GetLastWin32Error() + ")";

            try
            {
                IntPtr svc = Win.OpenService(scm, ServiceName, Win.SERVICE_ALL_ACCESS);
                if (svc == IntPtr.Zero) return "";       // 없으면 이미 목적 달성

                try
                {
                    Win.SERVICE_STATUS st = new Win.SERVICE_STATUS();
                    Win.ControlService(svc, Win.SERVICE_CONTROL_STOP, ref st);

                    for (int i = 0; i < 40; i++)
                    {
                        if (!IsRunning()) break;
                        Thread.Sleep(250);
                    }

                    if (!Win.DeleteService(svc))
                    {
                        int e = Marshal.GetLastWin32Error();
                        if (e != 1072)      // 1072 = 이미 삭제 표시됨
                            return "서비스를 지우지 못했습니다 (오류 " + e + ")";
                    }
                }
                finally { Win.CloseServiceHandle(svc); }

                return "";
            }
            finally { Win.CloseServiceHandle(scm); }
        }
    }

    /// <summary>서비스에는 화면이 없다. 무슨 일이 있었는지 남길 곳이 필요하다.</summary>
    internal static class SvcLog
    {
        private static readonly object gate = new object();

        internal static string Dir
        {
            get
            {
                string root;
                try { root = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData); }
                catch { root = Path.GetTempPath(); }

                if (string.IsNullOrEmpty(root)) root = Path.GetTempPath();

                string dir = Path.Combine(root, "RemoteControl");
                try { Directory.CreateDirectory(dir); } catch { }
                return dir;
            }
        }

        internal static string Path0 { get { return Path.Combine(Dir, "service.log"); } }

        internal static void Write(string s)
        {
            lock (gate)
            {
                try
                {
                    string p = Path0;

                    // 무한정 자라지 않게 한다.
                    try { if (File.Exists(p) && new FileInfo(p).Length > 256 * 1024) File.Delete(p); }
                    catch { }

                    File.AppendAllText(p, DateTime.Now.ToString("MM-dd HH:mm:ss") + "  " + s + "\r\n",
                                       System.Text.Encoding.UTF8);
                }
                catch { }
            }
        }

        /// <summary>마지막 몇 줄만 읽어 온다 (창에 보여 주려고).</summary>
        internal static string[] Tail(int n)
        {
            try
            {
                if (!File.Exists(Path0)) return new string[0];

                string[] all = File.ReadAllLines(Path0, System.Text.Encoding.UTF8);
                if (all.Length <= n) return all;

                string[] r = new string[n];
                Array.Copy(all, all.Length - n, r, 0, n);
                return r;
            }
            catch { return new string[0]; }
        }
    }
}

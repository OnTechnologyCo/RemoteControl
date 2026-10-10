// 서비스 등록과 "다른 세션에 프로세스 밀어 넣기" 에 쓰는 운영체제 호출.
//
// 여기서 핵심은 LaunchInSession 이다.
// 서비스는 세션 0 에 있고 화면이 없다. 화면이 있는 세션(사람이 앉아 있는 콘솔 세션)에
// SYSTEM 권한 그대로 프로세스를 만들어 넣어야 잠금 화면을 다룰 수 있다.
//
// 방법: 서비스 자신의 SYSTEM 토큰을 복제하고, 복제본의 "세션 번호" 만 콘솔 세션으로
// 바꿔서 CreateProcessAsUser 에 넘긴다. 토큰의 세션 번호를 바꾸는 건 SE_TCB_NAME
// 권한이 필요한데 SYSTEM 은 그걸 가지고 있다. 그래서 서비스에서만 성공한다.
using System;
using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RemoteControl
{
    internal static class Win
    {
        // ---------------------------------------------------------------- 세션

        [DllImport("kernel32.dll")]
        internal static extern uint WTSGetActiveConsoleSessionId();

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool ProcessIdToSessionId(uint pid, out uint sid);

        // ---------------------------------------------------------------- 어느 세션을 잡을 것인가
        //
        // 원격 데스크톱(RDP)으로 들어오면 사용자는 콘솔 세션이 아니라 새 세션에 앉는다.
        // 콘솔 세션만 쳐다보고 있으면 우리는 잠긴 화면만 잡게 된다.
        // 그래서 "지금 사람이 실제로 보고 있는 세션" 을 골라야 한다.

        [DllImport("wtsapi32.dll", SetLastError = true)]
        private static extern bool WTSEnumerateSessions(IntPtr server, int reserved, int version,
            out IntPtr sessionInfo, out int count);

        [DllImport("wtsapi32.dll")]
        private static extern void WTSFreeMemory(IntPtr mem);

        [StructLayout(LayoutKind.Sequential)]
        private struct WTS_SESSION_INFO
        {
            public uint SessionId;
            public IntPtr pWinStationName;
            public int State;
        }

        // 세션 상태. Active 하나만 보면 안 된다 - RDP 가 붙는 도중에는 새 세션이
        // Connected → ConnectQuery 를 거쳐 몇 초 뒤에야 Active 가 된다. 그 사이를
        // "아무도 안 쓴다" 로 읽고 tscon 을 부르면 붙는 중인 세션을 뺏어 끊어 놓는다.
        internal const int WTSActive = 0;
        internal const int WTSConnected = 1;
        internal const int WTSConnectQuery = 2;
        internal const int WTSShadow = 3;
        internal const int WTSDisconnected = 4;

        /// <summary>사람이 쓰고 있거나 붙는 중이면 true. 이런 세션은 절대 건드리지 않는다.</summary>
        internal static bool InUse(int state)
        {
            return state == WTSActive || state == WTSConnected ||
                   state == WTSConnectQuery || state == WTSShadow;
        }

        private const int SM_REMOTESESSION = 0x1000;

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int index);

        /// <summary>
        /// 이 프로세스가 원격 데스크톱(RDP) 세션 안에서 돌고 있는가.
        ///
        /// 대리 프로세스는 사람이 앉아 있는 세션에 들어가므로, RDP 로 들어와 있으면
        /// 여기가 true 가 된다. 그 세션에서는 해상도를 건드리면 안 된다 - 아래 참조.
        /// </summary>
        internal static bool InRemoteSession()
        {
            try { return GetSystemMetrics(SM_REMOTESESSION) != 0; }
            catch { return false; }
        }

        /// <summary>세션 하나의 상태.</summary>
        internal struct SessionInfo
        {
            public uint Id;
            public int State;
        }

        internal static SessionInfo[] Sessions()
        {
            IntPtr mem = IntPtr.Zero;
            try
            {
                int n;
                if (!WTSEnumerateSessions(IntPtr.Zero, 0, 1, out mem, out n)) return new SessionInfo[0];

                SessionInfo[] r = new SessionInfo[n];
                int size = Marshal.SizeOf(typeof(WTS_SESSION_INFO));

                for (int i = 0; i < n; i++)
                {
                    WTS_SESSION_INFO si = (WTS_SESSION_INFO)Marshal.PtrToStructure(
                        new IntPtr(mem.ToInt64() + i * size), typeof(WTS_SESSION_INFO));

                    r[i].Id = si.SessionId;
                    r[i].State = si.State;
                }

                return r;
            }
            catch { return new SessionInfo[0]; }
            finally { if (mem != IntPtr.Zero) WTSFreeMemory(mem); }
        }

        /// <summary>
        /// 화면을 잡아야 할 세션을 고른다.
        ///
        /// 사람이 실제로 보고 있는 세션(Active)이 있으면 그것. RDP 로 들어와 있으면
        /// 그게 RDP 세션이다. 아무도 붙어 있지 않으면 콘솔 세션(로그인 화면)으로 간다.
        /// </summary>
        internal static uint PickSession()
        {
            uint console = WTSGetActiveConsoleSessionId();

            foreach (SessionInfo s in Sessions())
            {
                if (s.Id == 0) continue;                  // 세션 0 은 서비스용이라 화면이 없다
                if (s.State == WTSActive) return s.Id;
            }

            return console;
        }

        /// <summary>
        /// 끊긴 원격 데스크톱 세션을 물리 콘솔로 되돌린다.
        ///
        /// 왜 필요한가.
        ///   RDP 로 들어왔다가 창을 닫으면 그 세션은 "끊김" 상태가 된다.
        ///   윈도우는 끊긴 세션의 화면을 더 이상 그리지 않는다. 모니터까지 없으면
        ///   어디에도 그려지는 화면이 없어서, 잡아 봐야 새까만 그림만 나온다.
        ///   그 세션을 콘솔로 옮겨 놓으면 다시 그려지기 시작한다.
        ///
        /// tscon 은 SYSTEM 권한이 필요하다. 서비스에서만 성공한다.
        /// </summary>
        internal static bool ReconnectToConsole(uint session)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.System), "tscon.exe"),
                    Arguments = session + " /dest:console",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using (Process p = Process.Start(psi))
                {
                    p.StandardOutput.ReadToEnd();
                    p.StandardError.ReadToEnd();
                    p.WaitForExit(15000);
                    return p.ExitCode == 0;
                }
            }
            catch { return false; }
        }

        internal static uint MySession()
        {
            try
            {
                uint sid;
                if (ProcessIdToSessionId((uint)Process.GetCurrentProcess().Id, out sid)) return sid;
            }
            catch { }
            return 0xFFFFFFFF;
        }

        // ---------------------------------------------------------------- 토큰

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool DuplicateTokenEx(IntPtr token, uint access, IntPtr attrs,
            int impersonationLevel, int tokenType, out IntPtr dup);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool SetTokenInformation(IntPtr token, int cls, ref uint info, int len);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr h);

        private const uint TOKEN_DUPLICATE = 0x0002;
        private const uint TOKEN_QUERY = 0x0008;
        private const uint TOKEN_ASSIGN_PRIMARY = 0x0001;
        private const uint MAXIMUM_ALLOWED = 0x02000000;

        private const int SecurityImpersonation = 2;
        private const int TokenPrimary = 1;
        private const int TokenSessionId = 12;

        // ---------------------------------------------------------------- 프로세스 만들기

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct STARTUPINFO
        {
            public int cb;
            public string lpReserved, lpDesktop, lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute;
            public int dwFlags;
            public short wShowWindow, cbReserved2;
            public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION
        {
            public IntPtr hProcess, hThread;
            public uint dwProcessId, dwThreadId;
        }

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CreateProcessAsUser(IntPtr token, string app, string cmd,
            IntPtr procAttrs, IntPtr threadAttrs, bool inherit, uint flags, IntPtr env,
            string dir, ref STARTUPINFO si, out PROCESS_INFORMATION pi);

        private const uint CREATE_NO_WINDOW = 0x08000000;
        private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
        private const uint CREATE_BREAKAWAY_FROM_JOB = 0x01000000;

        /// <summary>
        /// 지정한 세션에 SYSTEM 권한으로 프로세스를 만든다.
        /// 서비스(SYSTEM)에서만 성공한다. 실패하면 null.
        /// </summary>
        internal static Process LaunchInSession(uint session, string cmdLine, string workDir)
        {
            IntPtr mine = IntPtr.Zero, dup = IntPtr.Zero;

            try
            {
                if (!OpenProcessToken(Process.GetCurrentProcess().Handle,
                        TOKEN_DUPLICATE | TOKEN_QUERY | TOKEN_ASSIGN_PRIMARY, out mine))
                    return null;

                if (!DuplicateTokenEx(mine, MAXIMUM_ALLOWED, IntPtr.Zero,
                        SecurityImpersonation, TokenPrimary, out dup))
                    return null;

                uint sid = session;
                if (!SetTokenInformation(dup, TokenSessionId, ref sid, sizeof(uint)))
                    return null;

                STARTUPINFO si = new STARTUPINFO();
                si.cb = Marshal.SizeOf(typeof(STARTUPINFO));

                // 이 이름으로 만들어 두면 프로세스가 winsta0 안에 들어간다.
                // 그 안에 들어가야만 나중에 Winlogon 데스크톱으로 옮겨 붙을 수 있다.
                si.lpDesktop = "winsta0\\default";

                PROCESS_INFORMATION pi;
                StringBuilder0 cmd = new StringBuilder0(cmdLine);

                if (!CreateProcessAsUser(dup, null, cmd.Value, IntPtr.Zero, IntPtr.Zero, false,
                        CREATE_NO_WINDOW | CREATE_UNICODE_ENVIRONMENT | CREATE_BREAKAWAY_FROM_JOB,
                        IntPtr.Zero, workDir, ref si, out pi))
                {
                    // 작업 개체에서 떼어 내는 게 막혀 있으면 그것만 빼고 다시 해 본다.
                    if (!CreateProcessAsUser(dup, null, cmd.Value, IntPtr.Zero, IntPtr.Zero, false,
                            CREATE_NO_WINDOW | CREATE_UNICODE_ENVIRONMENT,
                            IntPtr.Zero, workDir, ref si, out pi))
                        return null;
                }

                try { CloseHandle(pi.hThread); } catch { }

                try { return Process.GetProcessById((int)pi.dwProcessId); }
                catch { return null; }
                finally { try { CloseHandle(pi.hProcess); } catch { } }
            }
            catch { return null; }
            finally
            {
                if (dup != IntPtr.Zero) { try { CloseHandle(dup); } catch { } }
                if (mine != IntPtr.Zero) { try { CloseHandle(mine); } catch { } }
            }
        }

        /// <summary>CreateProcessAsUser 는 명령줄 버퍼를 고쳐 쓸 수 있어 상수 문자열을 주면 안 된다.</summary>
        private sealed class StringBuilder0
        {
            internal readonly string Value;
            internal StringBuilder0(string s) { Value = new string(s.ToCharArray()); }
        }

        // ---------------------------------------------------------------- 서비스 제어 관리자

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern IntPtr OpenSCManager(string machine, string database, uint access);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern IntPtr CreateService(IntPtr scm, string name, string display,
            uint access, uint type, uint start, uint errorControl, string binPath, string group,
            IntPtr tagId, string deps, string user, string password);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern bool ChangeServiceConfig(IntPtr svc, uint type, uint start,
            uint errorControl, string binPath, string group, IntPtr tagId, string deps,
            string user, string password, string display);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern IntPtr OpenService(IntPtr scm, string name, uint access);

        [DllImport("advapi32.dll", SetLastError = true)]
        internal static extern bool DeleteService(IntPtr svc);

        [DllImport("advapi32.dll", SetLastError = true, EntryPoint = "StartServiceW", CharSet = CharSet.Unicode)]
        internal static extern bool StartService(IntPtr svc, int argc, IntPtr argv);

        [DllImport("advapi32.dll", SetLastError = true)]
        internal static extern bool ControlService(IntPtr svc, uint control, ref SERVICE_STATUS status);

        [DllImport("advapi32.dll", SetLastError = true)]
        internal static extern bool CloseServiceHandle(IntPtr h);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern bool ChangeServiceConfig2(IntPtr svc, int level, ref SERVICE_DESCRIPTION desc);

        [DllImport("advapi32.dll", SetLastError = true, EntryPoint = "ChangeServiceConfig2W", CharSet = CharSet.Unicode)]
        private static extern bool ChangeServiceConfig2Actions(IntPtr svc, int level, ref SERVICE_FAILURE_ACTIONS a);

        [StructLayout(LayoutKind.Sequential)]
        internal struct SERVICE_STATUS
        {
            public uint dwServiceType, dwCurrentState, dwControlsAccepted;
            public uint dwWin32ExitCode, dwServiceSpecificExitCode, dwCheckPoint, dwWaitHint;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct SERVICE_DESCRIPTION { public string lpDescription; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SERVICE_FAILURE_ACTIONS
        {
            public uint dwResetPeriod;
            public string lpRebootMsg;
            public string lpCommand;
            public uint cActions;
            public IntPtr lpsaActions;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SC_ACTION { public uint Type; public uint Delay; }

        internal const uint SC_MANAGER_CREATE_SERVICE = 0x0002 | 0x0001 | 0x0004;
        internal const uint SERVICE_ALL_ACCESS = 0xF01FF;
        internal const uint SERVICE_WIN32_OWN_PROCESS = 0x00000010;
        internal const uint SERVICE_AUTO_START = 0x00000002;
        internal const uint SERVICE_ERROR_NORMAL = 0x00000001;
        internal const uint SERVICE_CONTROL_STOP = 0x00000001;
        internal const int SERVICE_CONFIG_DESCRIPTION = 1;
        private const int SERVICE_CONFIG_FAILURE_ACTIONS = 2;
        private const uint SC_ACTION_RESTART = 1;

        /// <summary>서비스가 죽으면 스스로 다시 뜨게 한다.</summary>
        internal static void SetRestartOnFailure(IntPtr svc)
        {
            IntPtr mem = IntPtr.Zero;
            try
            {
                SC_ACTION[] acts = new SC_ACTION[3];
                acts[0].Type = SC_ACTION_RESTART; acts[0].Delay = 5000;
                acts[1].Type = SC_ACTION_RESTART; acts[1].Delay = 10000;
                acts[2].Type = SC_ACTION_RESTART; acts[2].Delay = 30000;

                int one = Marshal.SizeOf(typeof(SC_ACTION));
                mem = Marshal.AllocHGlobal(one * acts.Length);

                for (int i = 0; i < acts.Length; i++)
                    Marshal.StructureToPtr(acts[i], new IntPtr(mem.ToInt64() + i * one), false);

                SERVICE_FAILURE_ACTIONS fa = new SERVICE_FAILURE_ACTIONS();
                fa.dwResetPeriod = 86400;
                fa.cActions = (uint)acts.Length;
                fa.lpsaActions = mem;

                ChangeServiceConfig2Actions(svc, SERVICE_CONFIG_FAILURE_ACTIONS, ref fa);
            }
            catch { }
            finally { if (mem != IntPtr.Zero) Marshal.FreeHGlobal(mem); }
        }
    }
}

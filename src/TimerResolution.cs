// 화면을 보는 동안만 이 프로세스의 타이머 해상도를 1ms 로 올린다.
//
// 왜 필요한가 — 대리 프로세스는 timeBeginPeriod 를 부른 적이 없었다. Windows 10 2004 부터
// 타이머 해상도는 프로세스별이라 브라우저 같은 다른 프로그램이 올려 둬도 우리에겐 소용없다.
// 그래서 StreamLoop 의 Monitor.Wait(…, 1~16ms) 가 실제로는 15.6ms 단위로 잤다
// (bench/LiveViewBench.cs grab: Monitor.Wait(5ms) → p50 15.5ms).
// "캡처 → 남은 시간 대기(15.6ms 로 늘어남) → 다음 vsync 대기" 가 겹쳐 한 바퀴가 ~33ms 가 됐다.
// 1440p 화면 전체가 바뀌어도 캡처·지문·압축은 장당 10ms 인데 스크롤이 25fps 에 묶였던 원인이다.
//
// Windows 11 은 창을 가진 프로세스가 보이지 않으면 이 요청을 무시할 수 있다(절전).
// 대리 프로세스는 숨은 창을 가질 수 있으므로 그 절전 동작만 이 프로세스에서 끈다.
// 전력이 더 들므로 화면 연결이 하나라도 있을 때만 켜고, 마지막 연결이 끝나면 되돌린다.
using System;
using System.Runtime.InteropServices;

namespace RemoteControl
{
    internal static class TimerResolution
    {
        [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint ms);
        [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint ms);

        [StructLayout(LayoutKind.Sequential)]
        private struct PowerThrottlingState { public uint Version, ControlMask, StateMask; }
        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll")] private static extern bool SetProcessInformation(IntPtr process, int infoClass, ref PowerThrottlingState info, int size);
        private const int ProcessPowerThrottling = 4;
        private const uint IgnoreTimerResolution = 0x4;

        private static readonly object gate = new object();
        private static int users;
        private static bool optedOut;

        internal static void Acquire()
        {
            lock (gate)
            {
                if (users++ > 0) return;
                if (!optedOut)
                {
                    optedOut = true;
                    // StateMask 0 = 이 프로세스의 해상도 요청을 항상 지킨다. Windows 10 에는 없는 값이라 실패해도 그만이다.
                    try
                    {
                        var state = new PowerThrottlingState { Version = 1, ControlMask = IgnoreTimerResolution, StateMask = 0 };
                        SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling, ref state, Marshal.SizeOf(typeof(PowerThrottlingState)));
                    }
                    catch { }
                }
                try { timeBeginPeriod(1); } catch { }
            }
        }

        internal static void Release()
        {
            lock (gate)
            {
                if (users == 0 || --users > 0) return;
                try { timeEndPeriod(1); } catch { }
            }
        }
    }
}

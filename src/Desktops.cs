// 잠금 화면과 UAC 창까지 다루기 위한 데스크톱 전환.
//
// 윈도우는 로그온 세션 하나 안에 데스크톱을 여러 개 둔다.
//   Default   - 평소 쓰는 바탕화면
//   Winlogon  - 잠금 화면, 로그인 화면, Ctrl+Alt+Del
//   그 외      - UAC 동의 창(보안 데스크톱)
//
// 스레드는 자기에게 붙은 데스크톱 하나만 본다. Default 에 묶인 스레드는
// 잠금 화면을 캡처하지도, 거기에 입력을 넣지도 못한다.
// 잠긴 PC 를 보면 화면이 그 자리에 멈춘 것처럼 보이고 클릭이 안 먹는 이유가 이것이다.
// (BitBlt 가 실패하는 게 아니라, 여전히 Default 의 옛 내용을 잡고 있다.)
//
// 그래서 캡처 직전과 입력 직전에 "지금 화면에 떠 있는 데스크톱" 으로 옮겨 붙는다.
//
// 다만 Winlogon 데스크톱은 아무나 못 연다. 그 데스크톱의 권한 목록에는 SYSTEM 만 있다.
// 서비스로 설치해 SYSTEM 으로 띄웠을 때만 성공하고, 보통 사용자로 실행하면
// Sync() 가 조용히 실패한 뒤 예전과 똑같이 동작한다. 실패해도 망가지지 않는다.
using System;
using System.Runtime.InteropServices;
using System.Text;

namespace RemoteControl
{
    internal static class Desktops
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetThreadDesktop(IntPtr hDesktop);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr GetThreadDesktop(uint threadId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool CloseDesktop(IntPtr hDesktop);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool GetUserObjectInformation(IntPtr obj, int index,
            [Out] byte[] info, uint length, out uint needed);

        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

        private const int UOI_NAME = 2;
        private const uint GENERIC_ALL = 0x10000000;

        // 지금 이 스레드가 붙잡고 있는 데스크톱 핸들. 스레드마다 따로 간다.
        // 붙여 놓은 동안에는 닫으면 안 되므로 다음 것으로 갈아탄 뒤에 닫는다.
        [ThreadStatic] private static IntPtr held;
        [ThreadStatic] private static string heldName;

        /// <summary>서비스(SYSTEM)로 도는 중인지. 로그에만 쓴다.</summary>
        internal static bool CanSwitch;

        /// <summary>
        /// 이 스레드를 지금 화면에 떠 있는 데스크톱에 붙인다.
        /// 이미 붙어 있으면 아무것도 안 한다 (매 프레임 부르므로 싸야 한다).
        /// </summary>
        internal static bool Sync()
        {
            IntPtr h = IntPtr.Zero;
            try
            {
                h = OpenInputDesktop(0, false, GENERIC_ALL);
                if (h == IntPtr.Zero) return false;      // 권한 없음 - 보통 사용자

                string name = NameOf(h);

                // 같은 데스크톱이면 방금 연 핸들만 버리고 끝낸다.
                if (heldName != null && name == heldName) { CloseDesktop(h); return true; }

                if (!SetThreadDesktop(h)) { CloseDesktop(h); return false; }

                IntPtr old = held;
                held = h;
                heldName = name;
                h = IntPtr.Zero;                          // 아래 finally 에서 닫지 않게

                if (old != IntPtr.Zero) { try { CloseDesktop(old); } catch { } }

                CanSwitch = true;
                return true;
            }
            catch { return false; }
            finally { if (h != IntPtr.Zero) { try { CloseDesktop(h); } catch { } } }
        }

        /// <summary>지금 화면에 떠 있는 데스크톱 이름 (Default / Winlogon / …). 실패하면 빈 문자열.</summary>
        internal static string InputDesktopName()
        {
            IntPtr h = IntPtr.Zero;
            try
            {
                h = OpenInputDesktop(0, false, GENERIC_ALL);
                return h == IntPtr.Zero ? "" : NameOf(h);
            }
            catch { return ""; }
            finally { if (h != IntPtr.Zero) { try { CloseDesktop(h); } catch { } } }
        }

        /// <summary>이 스레드가 지금 붙어 있는 데스크톱 이름.</summary>
        internal static string ThreadDesktopName()
        {
            try { return NameOf(GetThreadDesktop(GetCurrentThreadId())); }
            catch { return ""; }
        }

        private static string NameOf(IntPtr h)
        {
            if (h == IntPtr.Zero) return "";

            byte[] buf = new byte[256];
            uint need;
            if (!GetUserObjectInformation(h, UOI_NAME, buf, (uint)buf.Length, out need)) return "";

            int n = (int)need;
            if (n <= 2 || n > buf.Length) n = buf.Length;

            string s = Encoding.Unicode.GetString(buf, 0, n);
            int z = s.IndexOf('\0');
            return z >= 0 ? s.Substring(0, z) : s;
        }
    }
}

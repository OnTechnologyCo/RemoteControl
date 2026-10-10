// 화면 캡처와 입력 주입에 쓰는 운영체제 호출.
//
// 원격제어에서 "정확하게" 의 절반이 여기서 갈린다.
//  - 마우스: 다중 모니터 + DPI 배율에서도 좌표가 어긋나면 안 된다
//  - 키보드: 조합키와 한/영, 그리고 화면에 없는 문자까지 넣을 수 있어야 한다
using System;
using System.Runtime.InteropServices;

namespace RemoteControl
{
    internal static class Native
    {
        // ---------------------------------------------------------------- 화면

        [DllImport("user32.dll")] internal static extern IntPtr GetDesktopWindow();
        [DllImport("user32.dll")] internal static extern IntPtr GetWindowDC(IntPtr hWnd);
        [DllImport("user32.dll")] internal static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("gdi32.dll")]
        internal static extern bool BitBlt(IntPtr hdcDest, int x, int y, int w, int h,
                                           IntPtr hdcSrc, int sx, int sy, int rop);

        [DllImport("gdi32.dll")]
        internal static extern bool StretchBlt(IntPtr hdcDest, int x, int y, int w, int h,
                                               IntPtr hdcSrc, int sx, int sy, int sw, int sh, int rop);

        [DllImport("gdi32.dll")]
        internal static extern int SetStretchBltMode(IntPtr hdc, int mode);

        internal const int SRCCOPY = 0x00CC0020;
        internal const int CAPTUREBLT = 0x40000000;   // 계층 창(투명 창)까지 포함해서 캡처

        // 줄여서 가져올 때 쓰는 방식.
        // COLORONCOLOR 는 줄을 그냥 버린다. 거칠지만 빠르다.
        // HALFTONE 은 섞어서 곱게 만드는데, 실측에서 원본 크기보다도 느렸다(39ms).
        internal const int COLORONCOLOR = 3;
        internal const int HALFTONE = 4;

        // 마우스 커서는 BitBlt 로 안 잡힌다. 따로 그려 넣어야 한다.
        [StructLayout(LayoutKind.Sequential)]
        internal struct CURSORINFO
        {
            public int cbSize;
            public int flags;
            public IntPtr hCursor;
            public POINT ptScreenPos;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        internal struct ICONINFO
        {
            public bool fIcon;
            public int xHotspot, yHotspot;
            public IntPtr hbmMask, hbmColor;
        }

        internal const int CURSOR_SHOWING = 0x00000001;

        [DllImport("user32.dll")] internal static extern bool GetCursorInfo(ref CURSORINFO pci);
        [DllImport("user32.dll")] internal static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);
        [DllImport("user32.dll")] internal static extern IntPtr CopyIcon(IntPtr hIcon);
        [DllImport("user32.dll")] internal static extern bool DestroyIcon(IntPtr hIcon);
        [DllImport("gdi32.dll")] internal static extern bool DeleteObject(IntPtr hObject);

        // ---------------------------------------------------------------- DPI
        //
        // 배율이 걸린 화면에서 이 선언을 빠뜨리면 윈도우가 좌표를 몰래 늘려 준다.
        // 그러면 캡처한 그림과 실제 좌표가 어긋나 클릭이 엉뚱한 곳에 들어간다.
        [DllImport("shcore.dll")] internal static extern int SetProcessDpiAwareness(int value);
        [DllImport("user32.dll")] internal static extern bool SetProcessDPIAware();

        internal static void MakeDpiAware()
        {
            try { SetProcessDpiAwareness(2); return; }   // PROCESS_PER_MONITOR_DPI_AWARE
            catch { }

            try { SetProcessDPIAware(); }                // 옛 윈도우용
            catch { }
        }

        // ---------------------------------------------------------------- 입력

        [StructLayout(LayoutKind.Sequential)]
        internal struct INPUT
        {
            public uint type;
            public InputUnion U;
        }

        [StructLayout(LayoutKind.Explicit)]
        internal struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct MOUSEINPUT
        {
            public int dx, dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        internal const uint INPUT_MOUSE = 0;
        internal const uint INPUT_KEYBOARD = 1;

        internal const uint MOUSEEVENTF_MOVE = 0x0001;
        internal const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        internal const uint MOUSEEVENTF_LEFTUP = 0x0004;
        internal const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
        internal const uint MOUSEEVENTF_RIGHTUP = 0x0010;
        internal const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
        internal const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
        internal const uint MOUSEEVENTF_WHEEL = 0x0800;
        internal const uint MOUSEEVENTF_HWHEEL = 0x1000;
        internal const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
        internal const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;   // 다중 모니터 전체를 기준으로

        internal const uint KEYEVENTF_KEYUP = 0x0002;
        internal const uint KEYEVENTF_UNICODE = 0x0004;
        internal const uint KEYEVENTF_SCANCODE = 0x0008;
        internal const uint KEYEVENTF_EXTENDEDKEY = 0x0001;

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll")] internal static extern int GetSystemMetrics(int nIndex);

        internal const int SM_XVIRTUALSCREEN = 76;
        internal const int SM_YVIRTUALSCREEN = 77;
        internal const int SM_CXVIRTUALSCREEN = 78;
        internal const int SM_CYVIRTUALSCREEN = 79;
    }
}

// 브라우저에서 온 마우스·키보드 조작을 이 PC 에 실제로 넣는다.
//
// 여기서 틀리면 "정확하게" 가 무너진다. 특히 조심한 것:
//
//  1) 좌표
//     SendInput 의 절대 좌표는 화면 픽셀이 아니라 0~65535 로 정규화된 값이다.
//     그리고 MOUSEEVENTF_VIRTUALDESK 를 주지 않으면 주 모니터 기준으로만 계산돼서,
//     두 번째·세 번째 모니터에서는 클릭이 엉뚱한 곳에 들어간다.
//     이 PC 는 모니터가 3대라 반드시 가상 데스크톱 전체를 기준으로 잡아야 한다.
//
//  2) 확장키
//     방향키·Home/End·Insert/Delete·오른쪽 Ctrl/Alt 는 EXTENDEDKEY 를 붙이지 않으면
//     넘패드 키로 인식된다. NumLock 상태에 따라 결과가 달라진다.
//
//  3) 한글
//     한글은 가상 키로 넣을 수 없다. KEYEVENTF_UNICODE 로 문자 자체를 넣는다.
//     이러면 IME 를 거치지 않아 조합 과정 없이 완성된 글자가 그대로 들어간다.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace RemoteControl
{
    internal sealed class InputInjector : IDisposable
    {
        private static readonly object inputGate = new object();
        private static readonly HashSet<InputInjector> owners = new HashSet<InputInjector>();
        private static readonly int[] keyOwners = new int[256], buttonOwners = new int[3];
        private readonly bool[] keys = new bool[256], buttons = new bool[3];
        private bool disposed;

        internal InputInjector() { lock (inputGate) owners.Add(this); }

        internal void Button(int x, int y, int button, bool down)
        {
            if (button < 0 || button >= buttons.Length) return;
            lock (inputGate)
            {
                if (disposed || buttons[button] == down) return;
                if (down)
                {
                    if (buttonOwners[button] == 0 && !SendButton(x, y, button, true)) return;
                    buttons[button] = true; buttonOwners[button]++;
                }
                else { buttons[button] = false; if (--buttonOwners[button] == 0) SendButton(x, y, button, false); }
            }
        }

        internal void Key(ushort vk, bool down)
        {
            if (vk == 0 || vk >= keys.Length) return;
            lock (inputGate)
            {
                if (disposed || (!down && !keys[vk])) return;
                if (down)
                {
                    bool repeat = keys[vk];
                    if ((repeat || keyOwners[vk] == 0) && !SendKey(vk, true)) return;
                    if (!repeat) keyOwners[vk]++;
                    keys[vk] = true;
                }
                else { keys[vk] = false; if (--keyOwners[vk] == 0) SendKey(vk, false); }
            }
        }
        /// <summary>
        /// 가상 데스크톱(모든 모니터를 합친 영역) 안의 픽셀 좌표로 마우스를 옮긴다.
        /// </summary>
        /// <summary>
        /// 입력을 넣기 직전에 지금 화면에 떠 있는 데스크톱으로 옮겨 붙는다.
        /// 잠금 화면과 UAC 창은 다른 데스크톱이다. 이걸 안 하면 키가 허공으로 들어가고,
        /// 화면은 멈춘 것처럼 보인다. 서비스(SYSTEM)로 실행 중일 때만 실제로 옮겨진다.
        /// </summary>
        private static uint Send(uint n, Native.INPUT[] inp, int cb)
        {
            Desktops.Sync();
            return Native.SendInput(n, inp, cb);
        }

        internal static void MoveTo(int x, int y)
        {
            Native.INPUT[] inp = new Native.INPUT[1];
            inp[0].type = Native.INPUT_MOUSE;
            inp[0].U.mi = MakeMove(x, y);
            Send(1, inp, Marshal.SizeOf(typeof(Native.INPUT)));
        }

        private static Native.MOUSEINPUT MakeMove(int x, int y)
        {
            int vx = Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN);
            int vy = Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN);
            int vw = Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN);
            int vh = Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN);

            if (vw <= 0) vw = 1;
            if (vh <= 0) vh = 1;

            // 0~65535 정규화. 반올림을 위해 (vw-1) 로 나눈다 - 그냥 vw 로 나누면
            // 화면 오른쪽 끝 픽셀에 도달하지 못해 1픽셀씩 밀린다.
            long nx = (long)(x - vx) * 65535 / Math.Max(1, vw - 1);
            long ny = (long)(y - vy) * 65535 / Math.Max(1, vh - 1);

            Native.MOUSEINPUT mi = new Native.MOUSEINPUT();
            mi.dx = (int)Clamp(nx, 0, 65535);
            mi.dy = (int)Clamp(ny, 0, 65535);
            mi.dwFlags = Native.MOUSEEVENTF_MOVE | Native.MOUSEEVENTF_ABSOLUTE | Native.MOUSEEVENTF_VIRTUALDESK;
            return mi;
        }

        private static long Clamp(long v, long lo, long hi)
        {
            return v < lo ? lo : (v > hi ? hi : v);
        }

        /// <summary>버튼: 0=왼쪽 1=가운데 2=오른쪽</summary>
        private static bool SendButton(int x, int y, int button, bool down)
        {
            uint flag;
            switch (button)
            {
                case 1: flag = down ? Native.MOUSEEVENTF_MIDDLEDOWN : Native.MOUSEEVENTF_MIDDLEUP; break;
                case 2: flag = down ? Native.MOUSEEVENTF_RIGHTDOWN : Native.MOUSEEVENTF_RIGHTUP; break;
                default: flag = down ? Native.MOUSEEVENTF_LEFTDOWN : Native.MOUSEEVENTF_LEFTUP; break;
            }

            // 이동과 누름을 한 번에 보낸다. 따로 보내면 그 사이에 사용자가 마우스를
            // 건드렸을 때 엉뚱한 곳이 눌린다.
            Native.INPUT[] inp = new Native.INPUT[down ? 2 : 1];
            if (down) { inp[0].type = Native.INPUT_MOUSE; inp[0].U.mi = MakeMove(x, y); }
            inp[inp.Length - 1].type = Native.INPUT_MOUSE;
            inp[inp.Length - 1].U.mi.dwFlags = flag;
            return Send((uint)inp.Length, inp, Marshal.SizeOf(typeof(Native.INPUT))) == inp.Length;
        }

        /// <summary>휠. delta 는 120 단위(한 칸)를 기준으로 한다.</summary>
        internal static void Wheel(int x, int y, int delta, bool horizontal)
        {
            Native.INPUT[] inp = new Native.INPUT[2];
            inp[0].type = Native.INPUT_MOUSE;
            inp[0].U.mi = MakeMove(x, y);

            inp[1].type = Native.INPUT_MOUSE;
            inp[1].U.mi.mouseData = unchecked((uint)delta);
            inp[1].U.mi.dwFlags = horizontal ? Native.MOUSEEVENTF_HWHEEL : Native.MOUSEEVENTF_WHEEL;

            Send(2, inp, Marshal.SizeOf(typeof(Native.INPUT)));
        }

        /// <summary>가상 키 코드로 누르거나 뗀다. 조합키·기능키에 쓴다.</summary>
        private static bool SendKey(ushort vk, bool down)
        {
            Native.INPUT[] inp = new Native.INPUT[1];
            inp[0].type = Native.INPUT_KEYBOARD;
            inp[0].U.ki.wVk = vk;
            inp[0].U.ki.dwFlags = down ? 0 : Native.KEYEVENTF_KEYUP;

            if (IsExtended(vk)) inp[0].U.ki.dwFlags |= Native.KEYEVENTF_EXTENDEDKEY;

            return Send(1, inp, Marshal.SizeOf(typeof(Native.INPUT))) == 1;
        }

        /// <summary>
        /// 문자를 그대로 넣는다. 한글처럼 가상 키로 표현할 수 없는 글자에 쓴다.
        /// IME 를 거치지 않으므로 조합 중간 상태 없이 완성된 글자가 들어간다.
        /// </summary>
        internal static void Text(string s)
        {
            if (string.IsNullOrEmpty(s)) return;

            // 서로게이트 쌍(이모지 등)도 그대로 통과시킨다.
            Native.INPUT[] inp = new Native.INPUT[s.Length * 2];
            int n = 0;

            foreach (char c in s)
            {
                // 줄바꿈은 글자로 넣지 않는다.
                // 붙여넣기 등으로 딸려 오면 검색창에서 그대로 "확인" 이 눌린 것처럼
                // 동작해 버린다. 엔터는 사용자가 분명히 원할 때만 따로 보낸다.
                if (c == '\r' || c == '\n' || c == '\t') continue;

                inp[n].type = Native.INPUT_KEYBOARD;
                inp[n].U.ki.wScan = c;
                inp[n].U.ki.dwFlags = Native.KEYEVENTF_UNICODE;
                n++;

                inp[n].type = Native.INPUT_KEYBOARD;
                inp[n].U.ki.wScan = c;
                inp[n].U.ki.dwFlags = Native.KEYEVENTF_UNICODE | Native.KEYEVENTF_KEYUP;
                n++;
            }

            Send((uint)n, inp, Marshal.SizeOf(typeof(Native.INPUT)));
        }

        /// <summary>
        /// 넘패드와 구분해야 하는 키들.
        /// 이걸 빠뜨리면 방향키가 NumLock 상태에 따라 숫자로 들어간다.
        /// </summary>
        private static bool IsExtended(ushort vk)
        {
            switch (vk)
            {
                case 0x21: // PageUp
                case 0x22: // PageDown
                case 0x23: // End
                case 0x24: // Home
                case 0x25: // Left
                case 0x26: // Up
                case 0x27: // Right
                case 0x28: // Down
                case 0x2D: // Insert
                case 0x2E: // Delete
                case 0x2C: // PrintScreen
                case 0x90: // NumLock
                case 0x5B: // 왼쪽 Win
                case 0x5C: // 오른쪽 Win
                case 0x5D: // Menu
                case 0xA3: // 오른쪽 Ctrl
                case 0xA5: // 오른쪽 Alt (한글 자판에서는 한자)
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// 눌린 채로 남은 키를 모두 뗀다.
        /// 연결이 끊기면 Ctrl 이나 Alt 가 눌린 상태로 남아 PC 가 이상하게 동작한다.
        /// 접속이 끝날 때 반드시 부른다.
        /// </summary>
        internal static void ReleaseAll()
        {
            lock (inputGate) foreach (InputInjector owner in owners) owner.Release();
        }

        internal void Release()
        {
            lock (inputGate)
            {
                // 누르지도 않은 RIGHTUP은 Windows에서 문맥 메뉴를 연다.
                // 실제로 이 접속이 눌렀고 다른 접속도 잡고 있지 않은 입력만 해제한다.
                for (ushort k = 1; k < keys.Length; k++)
                    if (keys[k]) { keys[k] = false; if (--keyOwners[k] == 0) { try { SendKey(k, false); } catch { } } }
                uint[] ups = { Native.MOUSEEVENTF_LEFTUP, Native.MOUSEEVENTF_MIDDLEUP, Native.MOUSEEVENTF_RIGHTUP };
                for (int i = 0; i < buttons.Length; i++)
                {
                    if (!buttons[i]) continue;
                    buttons[i] = false;
                    if (--buttonOwners[i] != 0) continue;
                    try
                    {
                        Native.INPUT[] inp = new Native.INPUT[1];
                        inp[0].type = Native.INPUT_MOUSE;
                        inp[0].U.mi.dwFlags = ups[i];
                        Send(1, inp, Marshal.SizeOf(typeof(Native.INPUT)));
                    }
                    catch { }
                }
            }
        }

        public void Dispose()
        {
            lock (inputGate)
            {
                Release(); disposed = true; owners.Remove(this);
            }
        }
    }
}

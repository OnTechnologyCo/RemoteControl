// 화면 해상도를 읽고 바꾼다.
//
// 왜 필요한가.
//   모니터가 없는 PC 는 윈도우가 기본 어댑터로 잡아서 대개 1024x768 이 된다.
//   원격으로만 쓰는 PC 라면 그 크기로는 답답하다. 그렇다고 우리가 마음대로
//   늘릴 수도 없다 - 실제로 지원되는 모드만 골라야 한다.
//
//   그래서 "이 PC 가 받아 주는 크기 목록" 을 뽑아 고르게 하고, 고른 것을 기억했다가
//   대리 프로세스가 뜰 때마다 다시 맞춘다. 원격 데스크톱을 콘솔로 되돌리고 나면
//   크기가 원래대로 돌아가는 일이 있어서, 한 번 맞추는 것으로는 부족하다.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;

namespace RemoteControl
{
    internal static class Display
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
            public int dmFields;
            public int dmPositionX, dmPositionY;
            public int dmDisplayOrientation, dmDisplayFixedOutput;
            public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public short dmLogPixels;
            public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
            public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType;
            public int dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool EnumDisplaySettings(string device, int mode, ref DEVMODE dm);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int ChangeDisplaySettingsEx(string device, ref DEVMODE dm,
            IntPtr wnd, uint flags, IntPtr param);

        private const int ENUM_CURRENT_SETTINGS = -1;

        private const int DM_BITSPERPEL = 0x00040000;
        private const int DM_PELSWIDTH = 0x00080000;
        private const int DM_PELSHEIGHT = 0x00100000;

        private const uint CDS_UPDATEREGISTRY = 0x01;
        private const int DISP_CHANGE_SUCCESSFUL = 0;

        /// <summary>지금 크기.</summary>
        internal static Size Current()
        {
            DEVMODE dm = New();
            if (EnumDisplaySettings(null, ENUM_CURRENT_SETTINGS, ref dm))
                return new Size(dm.dmPelsWidth, dm.dmPelsHeight);

            return Size.Empty;
        }

        /// <summary>이 PC 가 받아 주는 크기 목록 (같은 크기는 한 번만, 큰 것부터).</summary>
        internal static List<Size> Modes()
        {
            List<Size> list = new List<Size>();
            Dictionary<int, bool> seen = new Dictionary<int, bool>();

            DEVMODE dm = New();

            for (int i = 0; EnumDisplaySettings(null, i, ref dm); i++)
            {
                if (dm.dmBitsPerPel < 24) continue;                 // 32비트 색만

                // 640x480 도 남긴다.
                // 모니터가 없으면 윈도우가 그 크기 하나만 내놓는 일이 있는데,
                // 걸러 버리면 목록이 통째로 비어 "장치를 못 찾음" 처럼 보인다.
                // 실제로 그 PC 에서 그랬다. 있는 그대로 보여 줘야 원인을 알 수 있다.
                if (dm.dmPelsWidth < 640 || dm.dmPelsHeight < 480) continue;

                int key = dm.dmPelsWidth * 100000 + dm.dmPelsHeight;
                if (seen.ContainsKey(key)) continue;

                seen[key] = true;
                list.Add(new Size(dm.dmPelsWidth, dm.dmPelsHeight));
            }

            list.Sort(delegate (Size a, Size b)
            {
                long x = (long)b.Width * b.Height, y = (long)a.Width * a.Height;
                return x == y ? 0 : (x > y ? 1 : -1);
            });

            return list;
        }

        private const uint CDS_TEST = 0x02;

        /// <summary>크기를 바꾼다. 이미 그 크기면 아무것도 안 한다.</summary>
        internal static bool Apply(int w, int h)
        {
            string why;
            return Apply(w, h, out why);
        }

        /// <summary>
        /// 크기를 바꾸고, 안 되면 왜 안 되는지 알려 준다.
        ///
        /// 모니터 없는 PC 에서는 이게 자주 실패한다. 그런데 그 PC 앞에 갈 수가 없으니
        /// "안 된다" 만으로는 손쓸 방법이 없다. 윈도우가 준 이유를 그대로 남긴다.
        ///
        /// 목록에 없는 크기라도 일단 넣어 본다. 모니터가 없을 때 윈도우가 내놓는 목록은
        /// 실제로 되는 것보다 좁게 나오는 일이 있어서, 미리 포기하면 손해다.
        /// </summary>
        internal static bool Apply(int w, int h, out string why)
        {
            why = "";
            if (w <= 0 || h <= 0) { why = "크기가 잘못됐습니다"; return false; }

            Size now = Current();
            if (now.Width == w && now.Height == h) return true;

            DEVMODE dm = New();
            if (!EnumDisplaySettings(null, ENUM_CURRENT_SETTINGS, ref dm))
            {
                why = "지금 화면 설정을 읽지 못했습니다";
                return false;
            }

            dm.dmPelsWidth = w;
            dm.dmPelsHeight = h;
            dm.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_BITSPERPEL;

            int test = ChangeDisplaySettingsEx(null, ref dm, IntPtr.Zero, CDS_TEST, IntPtr.Zero);
            if (test != DISP_CHANGE_SUCCESSFUL)
            {
                why = Explain(test);
                return false;
            }

            int r = ChangeDisplaySettingsEx(null, ref dm, IntPtr.Zero, CDS_UPDATEREGISTRY, IntPtr.Zero);
            if (r == DISP_CHANGE_SUCCESSFUL) { Forget(); return true; }

            why = Explain(r);
            return false;
        }

        internal static void Forget() { lock (gate) { cached = null; } }

        private static string Explain(int code)
        {
            switch (code)
            {
                case 0: return "";
                case -1: return "다시 켜야 적용됩니다";
                case -2: return "화면 드라이버가 거부했습니다";
                case -3: return "이 PC 가 받아 주지 않는 크기입니다 (모니터가 없으면 목록이 좁습니다)";
                case -4: return "설정은 됐는데 반영되지 않았습니다";
                case -5: return "요청이 잘못됐습니다 (플래그)";
                case -6: return "요청이 잘못됐습니다 (인자)";
                case 1: return "두 번째 화면에는 적용할 수 없습니다";
                default: return "알 수 없는 오류 " + code;
            }
        }

        /// <summary>
        /// 모니터가 안 붙어 있는 것으로 보이는가.
        ///
        /// 확실히 알아내는 방법은 없다. 다만 윈도우가 내놓는 크기 목록이 아주 좁고
        /// 큰 것도 없으면 기본 화면 장치(Microsoft Basic Display Adapter)로 잡힌 것이다.
        /// 그 상태에서는 어떤 API 로도 크기를 늘릴 수 없다. 장치가 그것밖에 못 한다.
        /// </summary>
        internal static bool LooksHeadless()
        {
            List<Size> m = Modes();

            if (m.Count == 0) return true;
            if (m.Count > 6) return false;

            foreach (Size s in m)
                if (s.Width >= 1280) return false;

            return true;
        }

        /// <summary>지금 이 PC 가 내놓는 크기 목록을 한 줄로. 기록에 남겨 원격에서 확인한다.</summary>
        internal static string Report()
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            Size c = Current();

            sb.Append("현재 ").Append(c.Width).Append('x').Append(c.Height).Append("  받는 크기: ");

            List<Size> m = Modes();
            if (m.Count == 0) sb.Append("(없음 - 화면 장치를 못 찾습니다)");
            else
            {
                for (int i = 0; i < m.Count && i < 24; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append(m[i].Width).Append('x').Append(m[i].Height);
                }
                if (m.Count > 24) sb.Append(" …(" + m.Count + "개)");
            }

            return sb.ToString();
        }

        internal const int AutoW = 1920, AutoH = 1080;

        // 모드 목록은 잘 안 바뀌는데 뽑는 데 시간이 걸린다. 한 번 뽑아 두고 쓴다.
        private static List<Size> cached;
        private static readonly object gate = new object();

        internal static List<Size> ModesCached()
        {
            lock (gate)
            {
                if (cached == null) cached = Modes();
                return cached;
            }
        }

        /// <summary>
        /// 브라우저에 보여 줄 크기 목록. FHD 를 넘는 것은 뺀다.
        /// 원격으로 보는 화면이라 그보다 커 봐야 회선만 먹고 잘 보이지도 않는다.
        /// </summary>
        internal static List<Size> Choices()
        {
            List<Size> r = new List<Size>();
            Dictionary<int, bool> seen = new Dictionary<int, bool>();

            foreach (Size s in ModesCached())
            {
                if (s.Width > AutoW || s.Height > AutoH) continue;

                seen[s.Width * 100000 + s.Height] = true;
                r.Add(s);
            }

            // 흔한 크기는 목록에 없어도 넣어 둔다.
            //
            // 모니터가 없으면 윈도우가 내놓는 목록이 실제로 되는 것보다 좁게 나온다.
            // 목록에 없다고 미리 빼 버리면 사용자는 시도조차 못 한다.
            // 눌러 보고 안 되면 그때 이유를 알려 주는 편이 낫다.
            Size[] common = {
                new Size(1920, 1080), new Size(1600, 900), new Size(1440, 900),
                new Size(1366, 768),  new Size(1280, 800), new Size(1280, 720),
                new Size(1024, 768)
            };

            foreach (Size s in common)
            {
                int key = s.Width * 100000 + s.Height;
                if (seen.ContainsKey(key)) continue;

                seen[key] = true;
                r.Add(s);
            }

            r.Sort(delegate (Size a, Size b)
            {
                long x = (long)b.Width * b.Height, y = (long)a.Width * a.Height;
                return x == y ? 0 : (x > y ? 1 : -1);
            });

            return r;
        }

        /// <summary>이 크기를 이 PC 가 받아 주는가.</summary>
        internal static bool Supports(int w, int h)
        {
            foreach (Size s in ModesCached())
                if (s.Width == w && s.Height == h) return true;

            return false;
        }

        /// <summary>
        /// 화면 크기를 맞춘다.
        ///
        ///   설정에 적어 둔 것이 있으면 그것.
        ///   없으면 "자동" 이다 - 화면이 작을 때만 1920x1080 으로 올린다.
        ///
        /// 자동일 때 무조건 바꾸지 않는 이유가 있다.
        /// 모니터가 달린 PC 는 사람이 자기 화면 크기를 정해 놓고 쓴다.
        /// 원격제어를 깔았다고 그 화면이 멋대로 바뀌면 안 된다.
        /// 모니터가 없는 PC 만 윈도우가 기본값(대개 1024x768)으로 잡아 두므로,
        /// 지금 크기가 작을 때만 올린다.
        /// </summary>
        internal static string ApplySaved()
        {
            // 원격 데스크톱(RDP) 세션에서는 해상도를 건드리지 않는다.
            //
            // RDP 는 클라이언트가 자기 창 크기에 맞춰 해상도를 정하는 것이 정상이고,
            // 그 세션의 화면은 언제나 그려진다 - 검은 화면 문제가 없으니 강제할 이유가 없다.
            // 그런데 5초마다 저장된 크기로 되돌리면 클라이언트가 다시 맞추고, 그 다툼이
            // 끝나지 않는다. 화면이 계속 깜빡이거나 멈춘 것처럼 보인다.
            //
            // 이 기능이 원래 필요한 곳은 tscon 으로 콘솔에 돌아온 뒤(=RDP 가 아니다)
            // 모니터가 없어 작은 크기로 잡힌 경우다. 거기는 그대로 동작한다.
            if (Win.InRemoteSession()) return "";

            int w = 0, h = 0;
            bool told = int.TryParse(Config.Get("screenw"), out w) &
                        int.TryParse(Config.Get("screenh"), out h);

            told = told && w > 0 && h > 0;

            Size now = Current();

            if (!told)
            {
                if (now.Width >= 1600 || now.Height >= 900) return "";   // 이미 넉넉하다. 손대지 않는다
                if (!Supports(AutoW, AutoH)) return "";

                w = AutoW; h = AutoH;
            }

            if (now.Width == w && now.Height == h) return "";

            string why;

            if (Apply(w, h, out why))
                return "화면 크기를 " + now.Width + "x" + now.Height + " 에서 " + w + "x" + h + " 로 맞췄습니다.";

            return "화면 크기를 " + w + "x" + h + " 로 바꾸지 못했습니다: " + why;
        }

        private static DEVMODE New()
        {
            DEVMODE dm = new DEVMODE();
            dm.dmDeviceName = "";
            dm.dmFormName = "";
            dm.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
            return dm;
        }
    }
}

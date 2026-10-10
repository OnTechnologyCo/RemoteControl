// 가상 모니터 장치를 만들고 지운다. (설치 관리자의 일부)
//
// 왜 이런 게 필요한가.
//   모니터를 안 꽂은 PC 는 윈도우가 GPU 드라이버를 못 쓰고 기본 화면 장치로 잡는다.
//   그 장치는 640x480 하나만 내놓는다. 프로그램으로는 못 늘린다 - 장치가 못 하는 일이다.
//   가상 모니터를 만들어 줘야 비로소 1920x1080 이 열린다.
//
// 이 드라이버(MikeTheTech Virtual Display Driver)는 물리 장치가 없는
// "루트 열거 장치"(Root\MttVDD)라 설치가 두 단계다.
//
//   1) 드라이버를 윈도우 드라이버 저장소에 등록   → pnputil (Setup.cs 쪽)
//   2) 장치 노드를 직접 만들어 붙임               → 여기 (SetupAPI)
//
// 1) 만 하면 드라이버는 등록되지만 화면은 안 생긴다. 꽂을 장치가 없기 때문이다.
// 보통 devcon.exe 로 2) 를 하는데 그건 WDK 에 딸린 별도 프로그램이다. 윈도우에 이미
// 들어 있는 SetupAPI 를 직접 부르면 같이 배포할 파일이 없다.
// (이 프로젝트가 지금까지 외부 DLL 없이 온 방식과 같다)
using System;
using System.Runtime.InteropServices;
using System.Text;

namespace RemoteControlSetup
{
    internal static class Vdd
    {
        internal const string HardwareId = "Root\\MttVDD";
        internal const string SettingsDir = @"C:\VirtualDisplayDriver";

        // 화면 장치 종류 (Display class)
        private static Guid DisplayClass = new Guid("4D36E968-E325-11CE-BFC1-08002BE10318");

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVINFO_DATA
        {
            public int cbSize;
            public Guid ClassGuid;
            public uint DevInst;
            public IntPtr Reserved;
        }

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr SetupDiCreateDeviceInfoList(ref Guid classGuid, IntPtr parent);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator,
            IntPtr parent, uint flags);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool SetupDiCreateDeviceInfo(IntPtr set, string name,
            ref Guid classGuid, string description, IntPtr parent, uint flags,
            ref SP_DEVINFO_DATA data);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool SetupDiSetDeviceRegistryProperty(IntPtr set,
            ref SP_DEVINFO_DATA data, uint property, byte[] buffer, uint size);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool SetupDiGetDeviceRegistryProperty(IntPtr set,
            ref SP_DEVINFO_DATA data, uint property, out uint regType,
            byte[] buffer, uint size, out uint required);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index,
            ref SP_DEVINFO_DATA data);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiCallClassInstaller(uint func, IntPtr set,
            ref SP_DEVINFO_DATA data);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

        [DllImport("newdev.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool UpdateDriverForPlugAndPlayDevices(IntPtr parent,
            string hardwareId, string infPath, uint flags, out bool rebootNeeded);

        private const uint DICD_GENERATE_ID = 0x00000001;
        private const uint SPDRP_HARDWAREID = 0x00000001;
        private const uint DIF_REGISTERDEVICE = 0x00000019;
        private const uint DIF_REMOVE = 0x00000005;
        private const uint DIGCF_PRESENT = 0x00000002;
        private const uint INSTALLFLAG_FORCE = 0x00000001;

        /// <summary>이미 가상 모니터 장치가 붙어 있는가.</summary>
        internal static bool Exists()
        {
            return Walk(false) > 0;
        }

        /// <summary>가상 모니터 장치를 모두 뗀다. 뗀 개수를 돌려준다.</summary>
        internal static int RemoveAll()
        {
            return Walk(true);
        }

        /// <summary>장치를 만들고 드라이버를 물린다. 빈 문자열이면 성공.</summary>
        internal static string Create(string infPath)
        {
            if (Exists()) return "";      // 이미 있다

            IntPtr set = SetupDiCreateDeviceInfoList(ref DisplayClass, IntPtr.Zero);
            if (set == IntPtr.Zero || set.ToInt64() == -1)
                return "장치 목록을 만들지 못했습니다 (오류 " + Marshal.GetLastWin32Error() + ")";

            try
            {
                SP_DEVINFO_DATA d = new SP_DEVINFO_DATA();
                d.cbSize = Marshal.SizeOf(typeof(SP_DEVINFO_DATA));

                if (!SetupDiCreateDeviceInfo(set, "Display", ref DisplayClass, null,
                                             IntPtr.Zero, DICD_GENERATE_ID, ref d))
                    return "장치를 만들지 못했습니다 (오류 " + Marshal.GetLastWin32Error() + ")";

                // 하드웨어 ID 는 MULTI_SZ 다. 끝에 널 문자가 둘 있어야 한다.
                byte[] id = Encoding.Unicode.GetBytes(HardwareId + "\0\0");

                if (!SetupDiSetDeviceRegistryProperty(set, ref d, SPDRP_HARDWAREID, id, (uint)id.Length))
                    return "하드웨어 ID 를 넣지 못했습니다 (오류 " + Marshal.GetLastWin32Error() + ")";

                if (!SetupDiCallClassInstaller(DIF_REGISTERDEVICE, set, ref d))
                    return "장치를 등록하지 못했습니다 (오류 " + Marshal.GetLastWin32Error() + ")";
            }
            finally { SetupDiDestroyDeviceInfoList(set); }

            // 만들어 둔 장치에 드라이버를 물린다.
            bool reboot;
            if (!UpdateDriverForPlugAndPlayDevices(IntPtr.Zero, HardwareId, infPath,
                                                   INSTALLFLAG_FORCE, out reboot))
                return "드라이버를 물리지 못했습니다 (오류 " + Marshal.GetLastWin32Error() + ")";

            return "";
        }

        /// <summary>
        /// 우리 하드웨어 ID 를 가진 장치를 훑는다.
        /// remove 가 참이면 지우면서 센다.
        /// </summary>
        private static int Walk(bool remove)
        {
            IntPtr set = SetupDiGetClassDevs(ref DisplayClass, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT);
            if (set == IntPtr.Zero || set.ToInt64() == -1) return 0;

            int n = 0;

            try
            {
                // 지우면 목록이 줄어든다. 하나 지울 때마다 처음부터 다시 훑는다.
                bool again = true;

                while (again)
                {
                    again = false;

                    SP_DEVINFO_DATA d = new SP_DEVINFO_DATA();
                    d.cbSize = Marshal.SizeOf(typeof(SP_DEVINFO_DATA));

                    for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref d); i++)
                    {
                        if (!IsOurs(set, ref d)) continue;

                        n++;
                        if (!remove) return n;

                        try { SetupDiCallClassInstaller(DIF_REMOVE, set, ref d); }
                        catch { }

                        again = true;
                        break;
                    }

                    if (again)
                    {
                        SetupDiDestroyDeviceInfoList(set);
                        set = SetupDiGetClassDevs(ref DisplayClass, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT);
                        if (set == IntPtr.Zero || set.ToInt64() == -1) break;
                    }
                }
            }
            finally
            {
                if (set != IntPtr.Zero && set.ToInt64() != -1) SetupDiDestroyDeviceInfoList(set);
            }

            return n;
        }

        private static bool IsOurs(IntPtr set, ref SP_DEVINFO_DATA d)
        {
            try
            {
                uint type, need;
                byte[] buf = new byte[2048];

                if (!SetupDiGetDeviceRegistryProperty(set, ref d, SPDRP_HARDWAREID,
                                                      out type, buf, (uint)buf.Length, out need))
                    return false;

                int len = (int)Math.Min(need, (uint)buf.Length);
                string all = Encoding.Unicode.GetString(buf, 0, len);

                return all.IndexOf("MttVDD", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch { return false; }
        }
    }
}

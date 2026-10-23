// 자식 프로세스와 소켓 핸들 관리.
//
// 옆 프로젝트에서 실제로 겪은 문제를 그대로 막는다.
// 본체를 작업관리자로 죽였더니 자식(터널)이 살아남아 리스닝 소켓을 계속 물고 있었고,
// 다시 켤 때 "포트 사용 불가" 가 떴다. 두 가지로 막는다.
//   1) 소켓 핸들을 자식이 물려받지 못하게 한다
//   2) 자식을 작업 개체에 묶어, 부모가 어떻게 죽든 커널이 같이 정리하게 한다
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RemoteControl
{
    internal static class Interop
    {
        // ---------------------------------------------------------------- 핸들 상속 차단

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetHandleInformation(IntPtr hObject, uint dwMask, uint dwFlags);

        private const uint HANDLE_FLAG_INHERIT = 0x00000001;

        internal static void MakeHandleNonInheritable(IntPtr h)
        {
            try { SetHandleInformation(h, HANDLE_FLAG_INHERIT, 0); }
            catch { }
        }

        // ---------------------------------------------------------------- 작업 개체

        private const int JobObjectExtendedLimitInformation = 9;
        private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

        private static IntPtr job = IntPtr.Zero;
        private static readonly object gate = new object();

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateJobObject(IntPtr attrs, string name);

        [DllImport("kernel32.dll")]
        private static extern bool SetInformationJobObject(IntPtr job, int cls, IntPtr info, uint len);

        [DllImport("kernel32.dll")]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
            public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
        internal sealed class ProcessJob : IDisposable
        {
            private IntPtr handle;
            internal ProcessJob(Process process)
            {
                handle = CreateJobObject(IntPtr.Zero, null);
                var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
                int size = Marshal.SizeOf(info); IntPtr mem = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.StructureToPtr(info, mem, false);
                    if (handle == IntPtr.Zero || !SetInformationJobObject(handle, JobObjectExtendedLimitInformation, mem, (uint)size) ||
                        !AssignProcessToJobObject(handle, process.Handle))
                    { Dispose(); throw new InvalidOperationException("Cannot contain video process"); }
                }
                finally { Marshal.FreeHGlobal(mem); }
            }
            public void Dispose()
            {
                IntPtr h = System.Threading.Interlocked.Exchange(ref handle, IntPtr.Zero);
                if (h != IntPtr.Zero) CloseHandle(h);
            }
        }

        internal static void Adopt(Process p)
        {
            if (p == null) return;

            try
            {
                lock (gate)
                {
                    if (job == IntPtr.Zero)
                    {
                        job = CreateJobObject(IntPtr.Zero, null);
                        if (job == IntPtr.Zero) return;

                        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                        info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;

                        int size = Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
                        IntPtr mem = Marshal.AllocHGlobal(size);
                        try
                        {
                            Marshal.StructureToPtr(info, mem, false);
                            SetInformationJobObject(job, JobObjectExtendedLimitInformation, mem, (uint)size);
                        }
                        finally { Marshal.FreeHGlobal(mem); }
                    }

                    AssignProcessToJobObject(job, p.Handle);
                }
            }
            catch { }
        }
    }
}

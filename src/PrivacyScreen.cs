// Privacy Screen: obscure physical displays while keeping capture input visible to remote viewers.
// An opaque black top-level window is EXCLUDED from capture (Windows 10 2004+).
// Avoid powering down monitors: Desktop Duplication and cursor/input may stop working.
// The per-viewer owner is released on socket loss; emergency unlock is Ctrl+Alt+Shift+F12.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace RemoteControl
{
    // Isolated seam for deterministic tests (tests never display real overlays).
    internal interface IPrivacyCover
    {
        bool IsRunning { get; }
        bool Start(out string error);
        void Stop();
    }

    internal static class PrivacyScreen
    {
        private static readonly object Gate = new object();
        private static object owner;
        private static IPrivacyCover cover;
        internal static Func<IPrivacyCover> CoverFactory = delegate { return new WindowsPrivacyCover(); };

        internal static bool IsActive
        {
            get { lock (Gate) { Refresh(); return cover != null; } }
        }

        private static void Refresh()
        {
            if (cover != null && !cover.IsRunning) { owner = null; cover = null; }
        }

        // Only authenticated WebSocket code calls Set. Each authenticated viewer
        // has its own unguessable in-memory owner token, never accepted from JSON.
        internal static bool Set(object viewer, bool enable, out bool active, out string error)
        {
            lock (Gate)
            {
                Refresh();
                error = "";
                if (!enable)
                {
                    // Any authenticated viewer can RELEASE a cover for safety.
                    IPrivacyCover old = cover;
                    cover = null; owner = null;
                    if (old != null) old.Stop();
                    active = false;
                    return true;
                }

                if (cover != null)
                {
                    active = true;
                    if (Object.ReferenceEquals(owner, viewer)) return true;
                    error = "이미 다른 원격 연결에서 모니터를 가리고 있습니다. 가리기 해제는 가능합니다.";
                    return false;
                }

                IPrivacyCover candidate = CoverFactory();
                if (!candidate.Start(out error))
                {
                    try { candidate.Stop(); } catch { }
                    active = false;
                    return false;
                }
                cover = candidate;
                owner = viewer;
                active = true;
                SvcLog.Write("프라이버시 화면 켜짐 (웹 원격제어)");
                return true;
            }
        }

        internal static void Release(object viewer)
        {
            lock (Gate)
            {
                Refresh();
                if (cover == null || !Object.ReferenceEquals(owner, viewer)) return;
                IPrivacyCover previous = cover;
                cover = null; owner = null;
                previous.Stop();
                SvcLog.Write("프라이버시 화면 해제 (원격 연결 종료)");
            }
        }

        internal static void Reset()
        {
            lock (Gate)
            {
                IPrivacyCover previous = cover;
                cover = null; owner = null;
                if (previous != null) previous.Stop();
            }
        }
    }

    internal sealed class WindowsPrivacyCover : IPrivacyCover
    {
        private const uint ExcludeFromCapture = 0x00000011;
        private const int HotkeyId = 0x7A21;
        private const uint HotkeyModifiers = 0x0001 | 0x0002 | 0x0004; // Alt + Ctrl + Shift
        private const uint VirtualKeyF12 = 0x7B;

        [DllImport("ntdll.dll")]
        private static extern int RtlGetVersion(ref NativeVersion version);
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NativeVersion
        {
            public uint Size, Major, Minor, Build, Platform;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string ServicePack;
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmIsCompositionEnabled([MarshalAs(UnmanagedType.Bool)] out bool enabled);
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowDisplayAffinity(IntPtr hwnd, out uint affinity);
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr OpenDesktop(string desktop, uint flags, bool inherit, uint access);
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetThreadDesktop(IntPtr desktop);
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseDesktop(IntPtr desktop);
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint vk);
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnregisterHotKey(IntPtr window, int id);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint key, byte alpha, uint flags);

        private static readonly IntPtr HwndTopMost = new IntPtr(-1);
        private const uint NoSize = 0x0001, NoMove = 0x0002, NoActivate = 0x0010;

        private readonly ManualResetEvent ready = new ManualResetEvent(false);
        private readonly List<CoverForm> windows = new List<CoverForm>();
        private Thread thread;
        private CoverForm primary;
        private System.Windows.Forms.Timer maintain;
        private volatile bool running, abort;
        private string failure = "";
        private bool hotkeyRegistered;

        public bool IsRunning { get { return running && thread != null && thread.IsAlive; } }

        public bool Start(out string error)
        {
            error = "";
            // Affinity bit 0x11 becomes 0x01 on older builds (black in BOTH captures).
            NativeVersion version = new NativeVersion();
            version.Size = (uint)Marshal.SizeOf(typeof(NativeVersion));
            try
            {
                if (RtlGetVersion(ref version) != 0 || version.Major < 10 || version.Build < 19041)
                { error = "Windows 10 2004 이상에서만 모니터 가리기를 지원합니다."; return false; }
                bool dwm;
                if (DwmIsCompositionEnabled(out dwm) != 0 || !dwm)
                { error = "화면 합성(DWM)이 비활성화되어 모니터를 안전하게 가릴 수 없습니다."; return false; }
            }
            catch (Exception)
            {
                error = "화면 캡처 제외 기능의 지원 여부를 확인하지 못했습니다.";
                return false;
            }
            if (Desktops.InputDesktopName() != "Default")
            {
                error = "잠금 화면 또는 UAC 보안 화면에서는 시작할 수 없습니다. 바탕화면으로 돌아온 후 실행하세요.";
                return false;
            }

            thread = new Thread(Run);
            thread.IsBackground = true;
            thread.Name = "RemoteControl.PrivacyOverlay";
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            if (!ready.WaitOne(6000))
            {
                abort = true;
                error = "화면 가리기 준비 시간이 초과됐습니다.";
                Stop();
                return false;
            }
            if (!IsRunning)
            {
                error = failure.Length == 0 ? "모니터 가리기를 시작하지 못했습니다." : failure;
                Stop();
                return false;
            }
            return true;
        }

        private void Run()
        {
            IntPtr desktop = IntPtr.Zero;
            try
            {
                // The capture thread switches to Winlogon as needed. The cover itself
                // must remain on the normal "Default" desktop across those changes.
                desktop = OpenDesktop("Default", 0, false, 0x10000000);
                if (desktop == IntPtr.Zero || !SetThreadDesktop(desktop))
                    throw new InvalidOperationException("일반 바탕화면(Default)에 검정색 창을 만들 수 없습니다.");

                Screen[] monitors = Screen.AllScreens;
                if (monitors.Length == 0) throw new InvalidOperationException("활성 모니터를 찾지 못했습니다.");
                for (int i = 0; i < monitors.Length; i++) CreateCover(monitors[i].Bounds);
                primary = windows[0];

                // Users at the physical keyboard must be able to unlock WITHOUT a browser.
                if (!RegisterHotKey(primary.Handle, HotkeyId, HotkeyModifiers, VirtualKeyF12))
                    throw new InvalidOperationException("긴급 해제 키(Ctrl+Alt+Shift+F12)를 등록할 수 없습니다.");
                hotkeyRegistered = true;
                primary.HotkeyPressed += delegate { primary.Close(); };
                primary.DisplayChanged += delegate { UpdateDisplays(); };

                maintain = new System.Windows.Forms.Timer();
                maintain.Interval = 1400;
                maintain.Tick += delegate
                {
                    if (abort) { primary.Close(); return; }
                    foreach (CoverForm window in windows)
                    {
                        if (!window.IsDisposed)
                            SetWindowPos(window.Handle, HwndTopMost, 0, 0, 0, 0, NoSize | NoMove | NoActivate);
                    }
                };
                maintain.Start();

                if (abort) return;
                running = true;
                ready.Set();
                Application.Run(primary);  // Closing primary automatically ends the overlay thread.
            }
            catch (Exception ex)
            {
                failure = ex.Message;
            }
            finally
            {
                running = false;
                ready.Set();
                try { if (maintain != null) { maintain.Stop(); maintain.Dispose(); } } catch { }
                try { if (hotkeyRegistered && primary != null) UnregisterHotKey(primary.Handle, HotkeyId); } catch { }
                foreach (CoverForm window in windows) { try { window.Dispose(); } catch { } }
                windows.Clear();
                if (desktop != IntPtr.Zero) { try { CloseDesktop(desktop); } catch { } }
            }
        }

        private void CreateCover(Rectangle area)
        {
            var form = new CoverForm(area);
            form.Show();
            uint affinity;
            if (!SetWindowDisplayAffinity(form.Handle, ExcludeFromCapture) ||
                !GetWindowDisplayAffinity(form.Handle, out affinity) || affinity != ExcludeFromCapture)
            {
                form.Dispose();
                throw new InvalidOperationException("그래픽 환경이 캡처 제외를 지원하지 않아 가리기를 취소했습니다.");
            }
            windows.Add(form);
        }

        private void UpdateDisplays()
        {
            if (!running || primary == null) return;
            try
            {
                Screen[] monitors = Screen.AllScreens;
                if (monitors.Length == 0) throw new InvalidOperationException("모니터 연결이 해제됐습니다.");
                for (int i = 0; i < monitors.Length; i++)
                {
                    if (i < windows.Count)
                    {
                        windows[i].Bounds = monitors[i].Bounds;
                        uint affinity;
                        if (!GetWindowDisplayAffinity(windows[i].Handle, out affinity) || affinity != ExcludeFromCapture)
                        {
                            if (!SetWindowDisplayAffinity(windows[i].Handle, ExcludeFromCapture))
                                throw new InvalidOperationException("화면 캡처 제외 설정이 손실됐습니다.");
                        }
                    }
                    else CreateCover(monitors[i].Bounds);
                }
                for (int i = windows.Count - 1; i >= monitors.Length; i--)
                {
                    if (i == 0) throw new InvalidOperationException("기본 디스플레이를 변경할 수 없습니다.");
                    windows[i].Dispose();
                    windows.RemoveAt(i);
                }
            }
            catch (Exception ex)
            {
                failure = "모니터 구성 변경으로 가리기가 해제됐습니다: " + ex.Message;
                primary.Close();
            }
        }

        public void Stop()
        {
            abort = true;
            CoverForm form = primary;
            if (form != null && !form.IsDisposed)
            {
                try { form.BeginInvoke((MethodInvoker)delegate { if (!form.IsDisposed) form.Close(); }); }
                catch { }
            }
            Thread t = thread;
            if (t != null && t != Thread.CurrentThread) t.Join(2200);
        }

        private sealed class CoverForm : Form
        {
            private const int WmHotkey = 0x0312, WmDisplayChange = 0x007e, WmHitTest = 0x0084;
            internal event Action HotkeyPressed, DisplayChanged;
            internal CoverForm(Rectangle area)
            {
                BackColor = Color.Black;
                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = false;
                StartPosition = FormStartPosition.Manual;
                Bounds = area;
                TopMost = true;
                MinimizeBox = false;
                MaximizeBox = false;
                ControlBox = false;
                Text = "RemoteControl Privacy Cover";
            }
            protected override void OnHandleCreated(EventArgs e)
            {
                base.OnHandleCreated(e);
                // Opaque layered + transparent hit test: remote SendInput reaches desktop apps.
                if (!SetLayeredWindowAttributes(Handle, 0, 255, 0x2))
                    throw new InvalidOperationException("입력 통과형 화면 가림 창을 만들 수 없습니다.");
            }
            protected override bool ShowWithoutActivation { get { return true; } }
            protected override CreateParams CreateParams
            {
                get
                {
                    CreateParams p = base.CreateParams;
                    p.ExStyle |= 0x00000080 | 0x08000000 | 0x00000020 | 0x00080000; // toolwindow, noactivate, click-through, layered
                    return p;
                }
            }
            protected override void WndProc(ref Message m)
            {
                if (m.Msg == WmHitTest) { m.Result = new IntPtr(-1); return; }
                if (m.Msg == WmHotkey && (int)m.WParam == HotkeyId)
                { Action f = HotkeyPressed; if (f != null) f(); return; }
                if (m.Msg == WmDisplayChange)
                {
                    Action f = DisplayChanged;
                    if (f != null) BeginInvoke((MethodInvoker)delegate { f(); });
                }
                base.WndProc(ref m);
            }
        }
    }
}

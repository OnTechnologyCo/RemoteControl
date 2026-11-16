using System;
using System.Threading;
using System.Windows.Forms;

namespace RemoteControl
{
    // Only opens/focuses settings windows; this IPC never carries settings or credentials.
    internal static class SettingsWindow
    {
        private static string Name(string kind) { return "Local\\RemoteControl.Settings." + kind; }
        internal static void Initialize()
        {
            Native.MakeDpiAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
        }
        internal static bool TryActivate(string kind)
        {
            try { using (var wake = EventWaitHandle.OpenExisting(Name(kind) + ".Show", System.Security.AccessControl.EventWaitHandleRights.Modify)) return wake.Set(); }
            catch (WaitHandleCannotBeOpenedException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }
        internal static int Run(string kind, Func<Form> create, IWin32Window owner = null)
        {
            bool first;
            using (var only = new Mutex(true, Name(kind), out first))
            using (var wake = new EventWaitHandle(false, EventResetMode.AutoReset, Name(kind) + ".Show"))
            {
                if (!first) { wake.Set(); return 0; }
                try
                {
                    using (var form = create())
                    using (var timer = new System.Windows.Forms.Timer { Interval = 150 })
                    {
                        timer.Tick += delegate {
                            if (!wake.WaitOne(0)) return;
                            if (form.WindowState == FormWindowState.Minimized) form.WindowState = FormWindowState.Normal;
                            form.Show(); form.Activate();
                        };
                        timer.Start();
                        if (owner == null) Application.Run(form);
                        else form.ShowDialog(owner);
                    }
                    return 0;
                }
                finally { only.ReleaseMutex(); }
            }
        }
    }
}

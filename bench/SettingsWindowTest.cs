using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Windows.Forms;

internal static class SettingsWindowTest
{
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
    private static int checks;
    private static MethodInfo run, activate;
    private static string app, folder;
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); Console.WriteLine("PASS " + message); checks++; }
    private static IntPtr UserToken()
    {
        foreach (var process in Process.GetProcessesByName("explorer")) {
            IntPtr token;
            try {
                if (OpenProcessToken(process.Handle, 0x000A, out token)) {
                    using (var id = new WindowsIdentity(token))
                        if (!id.IsSystem && !new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator)) return token;
                    CloseHandle(token);
                }
            } catch { } finally { process.Dispose(); }
        }
        throw new Exception("No ordinary interactive token; privilege check not executed.");
    }
    private static void Until(Func<bool> ready, string message)
    {
        var clock = Stopwatch.StartNew();
        while (!ready()) { if (clock.ElapsedMilliseconds > 10000) throw new Exception(message); Thread.Sleep(30); }
    }
    private static Process Start(string kind, string marker)
    {
        return Process.Start(new ProcessStartInfo(Assembly.GetExecutingAssembly().Location,
            "--host \"" + app + "\" \"" + folder + "\" \"" + kind + "\" \"" + marker + "\"") {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden
            });
    }
    private static void Stop(Process process, string marker)
    {
        File.WriteAllText(Path.Combine(folder, marker + ".stop"), "stop");
        if (!process.WaitForExit(7000)) { process.Kill(); throw new Exception("Owned fixture did not exit."); }
        Check(process.ExitCode == 0, "owned fixture closes cleanly: " + marker);
        process.Dispose();
    }
    private sealed class HiddenForm : Form
    {
        private readonly string marker;
        private readonly System.Windows.Forms.Timer close = new System.Windows.Forms.Timer { Interval = 30 };
        private int shows;
        internal HiddenForm(string name)
        {
            marker = name; ShowInTaskbar = false; ClientSize = new Size(260, 100);
            var label = new Label { Text = "🖥️ 원격제어  ● 실행 중", AutoSize = false, Size = new Size(250, 50), Font = new Font("맑은 고딕", 12), BackColor = Color.White };
            var check = new CheckBox();
            if (label.UseCompatibleTextRendering || check.UseCompatibleTextRendering) throw new Exception("UI text initialization missing.");
            check.Dispose(); Controls.Add(label);
            var handle = Handle; var labelHandle = label.Handle;
            using (var bitmap = new Bitmap(label.Width,label.Height)) { label.DrawToBitmap(bitmap, new Rectangle(Point.Empty,label.Size)); bitmap.Save(Path.Combine(folder, marker + ".png")); }
            File.WriteAllText(Path.Combine(folder, marker + ".created"), "one settings form; GDI text");
            close.Tick += delegate { if (File.Exists(Path.Combine(folder, marker + ".stop"))) Close(); };
            close.Start();
        }
        protected override void SetVisibleCore(bool value)
        {
            if (value && ++shows > 1) File.WriteAllText(Path.Combine(folder, marker + ".wake"), "focused existing form");
            base.SetVisibleCore(false); // Synthetic forms never appear on the user's desktop.
        }
        protected override void Dispose(bool disposing) { if (disposing) close.Dispose(); base.Dispose(disposing); }
    }
    [STAThread] public static int Main(string[] args)
    {
        try {
            bool host = args.Length > 0 && args[0] == "--host";
            app = Path.GetFullPath(args[host ? 1 : 0]);
            folder = Path.GetFullPath(args[host ? 2 : 1]);
            var assembly = Assembly.LoadFile(app);
            if (!host) {
                var main = assembly.GetType("RemoteControl.MainForm",true).GetMethod("Main",Static);
                IntPtr token = UserToken();
                try {
                    using (WindowsIdentity.Impersonate(token)) {
                        Check((int)main.Invoke(null,new object[]{new[]{"--mcp-settings"}}) == 5, "ordinary direct MCP settings entry denied before configuration or UI");
                        Check((int)main.Invoke(null,new object[]{new[]{"--admin-ui"}}) == 5, "ordinary administrator entry still denied");
                    }
                } finally { CloseHandle(token); }
            }
            var type = assembly.GetType("RemoteControl.SettingsWindow",true);
            run = type.GetMethod("Run",Static); activate = type.GetMethod("TryActivate",Static);
            if (host) {
                type.GetMethod("Initialize",Static).Invoke(null,null);
                return (int)run.Invoke(null,new object[]{args[3],new Func<Form>(delegate{return new HiddenForm(args[4]);}),null});
            }
            string kind="Fixture."+Guid.NewGuid().ToString("N");
            Check(!(bool)activate.Invoke(null,new object[]{kind}),"closed settings window is not reported as open");
            Process first=null, other=null;
            try {
                first=Start(kind,"first");
                Until(delegate{return File.Exists(Path.Combine(folder,"first.created"));},"First settings fixture did not start.");
                using(var duplicate=Start(kind,"duplicate")) {
                    Check(duplicate.WaitForExit(7000)&&duplicate.ExitCode==0,"second process returns without another window");
                }
                Check(!File.Exists(Path.Combine(folder,"duplicate.created")),"duplicate never invokes the form factory");
                Until(delegate{return File.Exists(Path.Combine(folder,"first.wake"));},"Existing settings window was not activated.");
                Check(!first.HasExited,"existing settings process remains alive");
                Check((bool)activate.Invoke(null,new object[]{kind}),"repeat click activates the existing settings window");
                other=Start(kind+".Other","other");
                Until(delegate{return File.Exists(Path.Combine(folder,"other.created"));},"Independent settings fixture did not start.");
                Check(!other.HasExited&&!first.HasExited,"different settings pages do not share a mutex");
                Stop(other,"other");other=null;
                Stop(first,"first");first=null;
                Check(!(bool)activate.Invoke(null,new object[]{kind}),"closing settings releases its activation event");
                first=Start(kind,"reopened");
                Until(delegate{return File.Exists(Path.Combine(folder,"reopened.created"));},"Settings did not reopen after closing.");
                Check(!first.HasExited,"settings can reopen after close");
                Stop(first,"reopened");first=null;
            } finally {
                foreach(var process in new[]{first,other}) if(process!=null){if(!process.HasExited)process.Kill();process.Dispose();}
            }
            Console.WriteLine("ALL "+checks+" SETTINGS WINDOW CHECKS PASSED; synthetic hidden forms, no service/config changes.");
            return 0;
        } catch(Exception error) { Console.Error.WriteLine(error);return 1; }
    }
    [DllImport("advapi32.dll",SetLastError=true)] private static extern bool OpenProcessToken(IntPtr process,uint access,out IntPtr token);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
namespace RemoteControl
{
    // Test-Config.ps1은 Config.cs의 저장 위치 한 줄만 이 경로로 바꿔 컴파일한다.
    internal static class ConfigWriteTest
    {
        internal static readonly string Path0 = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.txt");
        private static int count;
        private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); count++; Console.WriteLine("PASS " + message); }
        public static int Main(string[] args)
        {
            try
            {
                if (args.Length > 0)
                {
                    Config.Get("port"); // 다른 프로세스가 쓰기 전 사본을 일부러 갖는다.
                    for (int i = 0; i < 25; i++) Config.SetMany(new Dictionary<string, string> { { args[0], i.ToString() } });
                    return 0;
                }
                Config.SetMany(new Dictionary<string, string> { { "port", "5900" }, { "tunnel", "private" }, { "extauto", "0" } });
                Config.Reload(); Check(Config.Port == 5900 && Config.Get("tunnel") == "private", "grouped settings survive reload");
                using (var a = Worker("a")) using (var b = Worker("b"))
                {
                    Check(a.WaitForExit(15000) && b.WaitForExit(15000) && a.ExitCode == 0 && b.ExitCode == 0, "concurrent writers complete");
                }
                Config.Reload(); Check(Config.Get("a") == "24" && Config.Get("b") == "24" && Config.Port == 5900, "UI and service updates do not overwrite each other");
                bool failed = false;
                using (var held = new FileStream(Path0 + ".tmp", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                {
                    try { Config.SetMany(new Dictionary<string, string> { { "extauto", "1" }, { "tunnel", "tailscale" } }); }
                    catch (IOException) { failed = true; }
                }
                Check(failed && Config.Get("extauto") == "0" && Config.Get("tunnel") == "private", "failed save reports error and rolls back memory");
                failed = false;
                using (var held = new FileStream(Path0, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    try { Config.SetMany(new Dictionary<string, string> { { "extauto", "1" } }); }
                    catch (IOException) { failed = true; }
                }
                Check(failed && Config.Get("extauto") == "0" && Config.Get("a") == "24", "unreadable existing file cannot be replaced with empty settings");
                failed = false;
                try { Config.SetMany(new Dictionary<string, string> { { "sshhost", "host\nextauto=1" } }); }
                catch (ArgumentException) { failed = true; }
                Check(failed && Config.Get("extauto") == "0", "settings line injection is rejected");
                Check(File.Exists(Path0 + ".bak"), "previous settings retained as backup");
                Console.WriteLine("ALL " + count + " CONFIG CHECKS PASSED"); return 0;
            }
            catch (Exception ex) { Console.WriteLine(ex); return 1; }
        }
        private static Process Worker(string name) { return Process.Start(new ProcessStartInfo {
            FileName = Assembly.GetExecutingAssembly().Location, Arguments = name, UseShellExecute = false, CreateNoWindow = true
        }); }
    }
    internal static class Auth
    {
        internal static void DropAll() { }
        internal static string MakeHash(string s) { return s; }
        internal static bool VerifyHash(string a, string b) { return a == b; }
    }
}

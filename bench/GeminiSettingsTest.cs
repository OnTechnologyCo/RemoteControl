using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RemoteControl
{
    // Only the generated test assembly substitutes the ACL boundary. Production
    // Config's locks, atomic file commit and reload logic are compiled unchanged.
    internal static class GeminiConfigProtection
    {
        internal static bool AllowWrites = true;
        internal static void RequireAdministrator() { if (!AllowWrites) throw new UnauthorizedAccessException("fixture write denied"); }
        internal static void PrepareDataDirectory(string path) { Directory.CreateDirectory(path); }
        internal static void Verify(string path, bool directory, bool secret) { }
        internal static void CheckPath(string path) { }
    }

    internal static class GeminiSettingsTest
    {
        internal static string Path0 = System.IO.Path.Combine(System.IO.Path.GetFullPath(Environment.GetCommandLineArgs()[2]), "settings.txt");
        private static int checks;
        private static void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; Console.WriteLine("PASS " + message); }
        private static void Reject(string json)
        {
            string before = Config.Get(GeminiSettings.Key);
            bool failed = false; try { GeminiSettings.Save(json); } catch (ArgumentException) { failed = true; }
            Check(failed && Config.Get(GeminiSettings.Key) == before, "invalid input rejected without mutation: " + (json == null ? "null input" : json.Substring(0, Math.Min(60, json.Length))));
        }
        private static IEnumerable<Control> All(Control parent)
        { foreach (Control control in parent.Controls) { yield return control; foreach (var child in All(control)) yield return child; } }
        private static void Click(Form form, string text)
        {
            var button = All(form).OfType<Button>().Single(b => b.Text == text);
            typeof(Button).GetMethod("OnClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(button, new object[] { EventArgs.Empty });
        }
        [STAThread]
        public static int Main(string[] args)
        {
            try {
                if (args.Length != 2) return 64;
                string root = System.IO.Path.GetFullPath(args[1]); Directory.CreateDirectory(root); Path0 = System.IO.Path.Combine(root, "settings.txt");
                if (args[0] == "serve") return Serve(root);
                if (args[0] == "restart") { Check(GeminiSettings.Load()["pc_get_status"] == false, "false persisted across new process"); return 0; }
                if (args[0] != "test") return 64;
                Config.EnsureWritable();
                Check(GeminiSettings.Load().Count == 0, "missing field keeps empty overrides");
                var catalog = GeminiSettings.Catalog();
                File.WriteAllText(System.IO.Path.Combine(root, "catalog-before.json"), McpJson.Write(catalog));
                Check(catalog.Length == McpTools.List().Length + 1, "UI uses actual native and shared web catalog");
                GeminiSettings.Save("{\"pc_get_status\":false,\"pc_write_file\":true,\"pc_open_file_transfer\":false}");
                var current = GeminiSettings.Load(); Check(current.Count == 3 && !current["pc_get_status"] && current["pc_write_file"] && !current["pc_open_file_transfer"], "true, false and web tool persisted");
                foreach (string bad in new[] { "null", "[]", "true", "{", "{\"unknown\":true}", "{\"pc_get_status\":\"true\"}", "{\"pc_get_status\":1}", "{\"pc_get_status\":null}", "{\"pc_get_status\":false,\"unknown\":true}", new string(' ', GeminiSettings.MaxBytes) + "{}" }) Reject(bad);
                Check(GeminiSettings.Parse("{\"retired_tool\":false,\"pc_get_status\":false}", true).Count == 1, "retired names ignored on restore");
                using (var held = new FileStream(Path0 + ".tmp", FileMode.Create, FileAccess.ReadWrite, FileShare.None)) {
                    bool failed = false; try { GeminiSettings.Save("{\"pc_get_status\":true}"); } catch (IOException) { failed = true; }
                    Check(failed && !GeminiSettings.Load()["pc_get_status"], "failed atomic commit retains saved and runtime false");
                }
                GeminiConfigProtection.AllowWrites = false;
                bool denied = false; try { GeminiSettings.Save("{}"); } catch (UnauthorizedAccessException) { denied = true; }
                Check(denied && !GeminiSettings.Load()["pc_get_status"], "administrator rejection leaves saved state intact");
                GeminiConfigProtection.AllowWrites = true;
                Parallel.For(0, 40, i => {
                    GeminiSettings.Save(i % 2 == 0 ? "{\"pc_get_status\":true,\"pc_read_file\":true}" : "{\"pc_get_status\":false,\"pc_read_file\":false}");
                    var snapshot = GeminiSettings.Load();
                    if (snapshot.Count != 2 || snapshot["pc_get_status"] != snapshot["pc_read_file"]) throw new Exception("torn snapshot");
                });
                Check(true, "concurrent writes and reads use complete snapshots");
                GeminiSettings.Save("{\"pc_get_status\":false}");
                using (var form = new GeminiSettingsForm()) {
                    form.CreateControl(); form.PerformLayout();
                    var grid = All(form).OfType<DataGridView>().Single();
                    Check(grid.Rows.Count == catalog.Length, "settings UI includes every tool");
                    Click(form, "모두 기본값으로");
                    Check(!GeminiSettings.Load()["pc_get_status"], "reset remains staged until Save");
                    Click(form, "취소");
                    Check(!GeminiSettings.Load()["pc_get_status"], "cancel discards staged reset");
                }
                using (var form = new GeminiSettingsForm()) {
                    form.CreateControl(); form.PerformLayout();
                    Click(form, "모두 기본값으로"); Click(form, "저장");
                    Check(GeminiSettings.Load().Count == 0, "Save commits reset");
                    var grid = All(form).OfType<DataGridView>().Single();
                    grid.Rows[0].Cells["choice"].Value = "false";
                    GeminiConfigProtection.AllowWrites = false; Click(form, "저장"); GeminiConfigProtection.AllowWrites = true;
                    Check(All(form).OfType<Label>().Any(l => l.Text.StartsWith("저장 실패:")) && GeminiSettings.Load().Count == 0, "UI reports failed save without mutation");
                    foreach (var size in new[] { new Size(820,610), new Size(680,480) }) {
                        form.ClientSize = size; form.PerformLayout();
                        foreach (var button in All(form).OfType<Button>()) {
                            var point = button.Location;
                            for (Control parent = button.Parent; parent != form; parent = parent.Parent) point.Offset(parent.Location);
                            Check(point.X >= 0 && point.Y >= 0 && point.X + button.Width <= form.ClientSize.Width && point.Y + button.Height <= form.ClientSize.Height, "action button fits " + size + " " + button.Text + " " + point + " " + button.Size);
                        }
                        foreach (var control in All(form)) { IntPtr handle = control.Handle; }
                        using (var bitmap = new Bitmap(form.ClientSize.Width, form.ClientSize.Height)) {
                            using (var graphics = Graphics.FromImage(bitmap)) graphics.Clear(form.BackColor);
                            foreach (Control control in form.Controls) control.DrawToBitmap(bitmap, control.Bounds);
                            bitmap.Save(System.IO.Path.Combine(root, "gemini-settings-" + size.Width + ".png"));
                        }
                    }
                }
                Check(McpJson.Write(catalog) == McpJson.Write(GeminiSettings.Catalog()), "default catalog unchanged");
                GeminiSettings.Save("{\"pc_get_status\":false}");
                Console.WriteLine("ALL " + checks + " GEMINI SETTINGS CHECKS PASSED (isolated ACL fixture)");
                return 0;
            } catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }
        private static int Serve(string root)
        {
            var reserve = new TcpListener(IPAddress.Loopback, 0); reserve.Start(); int port = ((IPEndPoint)reserve.LocalEndpoint).Port; reserve.Stop();
            Func<McpOptions> settings = delegate { Config.Reload(); return new McpOptions { Enabled=true, Port=port, Workspaces=new[]{root}, AllowCommands=false, AllowScreen=false, GeminiReadOnlyHints=Config.Get(GeminiSettings.Key) }; };
            using (var host = new McpHost(settings, delegate(string user, string pass) { return user == "fixture" && pass == "fixture-password"; }, delegate { }, System.IO.Path.Combine(root,"sessions.bin"))) {
                host.Start(); Console.WriteLine(port); Console.Out.Flush();
                string line; while ((line = Console.ReadLine()) != null) {
                    try { GeminiSettings.Save(line); Console.WriteLine("saved"); }
                    catch { Console.WriteLine("rejected"); }
                    Console.Out.Flush();
                }
            }
            return 0;
        }
    }
}

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
                if (args[0].StartsWith("worker-")) {
                    var o = new McpOptions { Enabled=true, SettingsOwner="fixture", CredentialStamp="fixture|fixture-hash" };
                    string tool=args[0]=="worker-left"?"pc_get_status":"pc_read_file";
                    for (int i=0;i<12;i++) GeminiSettings.Call(GeminiSettings.Setter,Request(args[0]+i,"\"mode\":\"patch\",\"read_only_hints\":{\""+tool+"\":false}"),o,"fixture",new string('w',43));
                    return 0;
                }
                if (args[0] == "restart") { Check(GeminiSettings.Load()["pc_get_status"] == false, "false persisted across new process"); return 0; }
                if (args[0] != "test") return 64;
                Config.EnsureWritable();
                Config.SetMany(new Dictionary<string, string> { {"user","fixture"}, {"pwhash","fixture-hash"}, {"mcp_enabled","1"} });
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
                ContractTests(root);
                GeminiSettings.Save("{\"pc_get_status\":false}");
                Console.WriteLine("ALL " + checks + " GEMINI SETTINGS CHECKS PASSED (isolated ACL fixture)");
                return 0;
            } catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }
        private static int Serve(string root)
        {
            Config.EnsureWritable();
            Config.SetMany(new Dictionary<string, string> { {"user","fixture"}, {"pwhash","fixture-hash"}, {"mcp_enabled","1"} });
            var reserve = new TcpListener(IPAddress.Loopback, 0); reserve.Start(); int port = ((IPEndPoint)reserve.LocalEndpoint).Port; reserve.Stop();
            Func<McpOptions> settings = delegate { Config.Reload(); return new McpOptions { Enabled=true, Port=port, Workspaces=new[]{root}, AllowCommands=false, AllowScreen=false, SettingsOwner="fixture", CredentialStamp="fixture|fixture-hash", GeminiReadOnlyHints=Config.Get(GeminiSettings.Key) }; };
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

        private static Dictionary<string, object> Request(string id, string fields)
        { return McpJson.Read("{\"expected_hostname\":" + McpJson.Write(Environment.MachineName) + ",\"request_id\":" + McpJson.Write(id) + "," + fields + "}"); }

        private static void ContractTests(string root)
        {
            var options = new McpOptions { Enabled=true, AllowCommands=false, AllowScreen=false, Workspaces=new[]{root}, SettingsOwner="fixture", CredentialStamp="fixture|fixture-hash" };
            string token = new string('a', 43);
            Func<Dictionary<string, object>, Dictionary<string, object>> set = a => McpJson.Read(McpJson.Write(GeminiSettings.Call(GeminiSettings.Setter, a, options, "fixture", token)));
            Func<string, Dictionary<string, object>> get = id => McpJson.Read(McpJson.Write(GeminiSettings.Call("pc_get_gemini_settings", Request(id, "\"unused\":true").Where(p => p.Key != "unused").ToDictionary(p => p.Key, p => p.Value), options, "fixture", token)));
            GeminiSettings.Save("{}");
            var request = Request("first", "\"mode\":\"patch\",\"read_only_hints\":{\"pc_get_status\":false}");
            var applied = set(request);
            Check((bool)applied["saved"] && !(bool)applied["gemini_permission_updated"] && !GeminiSettings.Load()["pc_get_status"], "SET saves false with commands and screen disabled");
            string savedBytes = File.ReadAllText(Path0);
            Check((bool)set(request)["replayed"] && File.ReadAllText(Path0) == savedBytes, "same request does not write or apply twice");
            Check((string)get("first")["request_status"] == "saved", "GET recovers owned receipt");
            var other = McpJson.Read(McpJson.Write(GeminiSettings.Call("pc_get_gemini_settings", Request("first", "\"unused\":true").Where(p => p.Key != "unused").ToDictionary(p => p.Key, p => p.Value), options, "fixture", new string('b',43))));
            Check((string)other["request_status"] == "not_found", "another authenticated connection cannot read request receipt");
            set(Request("second", "\"mode\":\"patch\",\"read_only_hints\":{\"pc_read_file\":false}"));
            Check(!(bool)set(request)["result_is_current"] && GeminiSettings.Load().Count == 2, "replay after later mutation returns historical result without reverting it");
            string revision; GeminiSettings.Read(out revision);
            set(Request("third", "\"mode\":\"patch\",\"reset_tools\":[\"pc_get_status\"]"));
            Check(!GeminiSettings.Load().ContainsKey("pc_get_status") && !GeminiSettings.Load()["pc_read_file"], "selected reset preserves other override");
            bool conflict=false; try { GeminiSettings.Save("{}", revision); } catch (GeminiSettingsException ex) { conflict=ex.Code=="SETTINGS_CONFLICT"; }
            Check(conflict && GeminiSettings.Load().Count == 1, "stale UI snapshot conflicts without overwriting MCP update");
            var badRequests = new[] {
                Request("first", "\"mode\":\"reset_all\""), Request("bad-target", "\"mode\":\"reset_all\""),
                Request("bad1", "\"mode\":\"patch\""), Request("bad2", "\"mode\":\"reset_all\",\"read_only_hints\":{}"),
                Request("bad3", "\"mode\":\"patch\",\"reset_tools\":[\"pc_read_file\",\"pc_read_file\"]"),
                Request("bad4", "\"mode\":\"patch\",\"reset_tools\":[\"pc_read_file\"],\"read_only_hints\":{\"pc_read_file\":true}"),
                Request("bad5", "\"mode\":\"patch\",\"read_only_hints\":{\"pc_read_file\":true,\"pc_set_gemini_settings\":true}"),
                Request("bad6", "\"mode\":\"patch\",\"read_only_hints\":{\"pc_read_file\":true,\"unknown\":false}"),
                Request("bad7", "\"mode\":\"patch\",\"read_only_hints\":{\"pc_read_file\":\"false\"}"),
                Request("bad8", "\"mode\":\"patch\",\"read_only_hints\":[]"),
                Request("bad9", "\"mode\":\"reset_all\",\"extra\":true"),
                Request("bad10", "\"mode\":\"patch\",\"reset_tools\":[\"pc_set_gemini_settings\"]"),
                Request("bad11", "\"mode\":\"patch\",\"reset_tools\":true")
            };
            badRequests[1]["expected_hostname"] = "wrong-target";
            foreach (var bad in badRequests) {
                string before=File.ReadAllText(Path0); bool refused=false;
                try { set(bad); } catch (Exception ex) {
                    refused=ex is ArgumentException || ex is GeminiSettingsException;
                    if ((string)bad["request_id"]=="first") refused=ex is GeminiSettingsException && ((GeminiSettingsException)ex).Code=="REQUEST_ID_CONFLICT";
                    if ((string)bad["request_id"]=="bad-target") refused=ex is GeminiSettingsException && ((GeminiSettingsException)ex).Code=="TARGET_MISMATCH";
                }
                Check(refused && before==File.ReadAllText(Path0), "invalid SET is atomic: " + bad["request_id"]);
            }
            using (var held=new FileStream(Path0+".tmp",FileMode.Create,FileAccess.ReadWrite,FileShare.None)) {
                bool failed=false; try { set(Request("failed-save", "\"mode\":\"reset_all\"")); } catch (GeminiSettingsException ex) { failed=ex.Code=="SETTINGS_SAVE_FAILED"; }
                Check(failed && (string)get("failed-save")["request_status"]=="not_found" && GeminiSettings.Load().Count==1, "failed commit saves neither policy nor receipt");
            }
            GeminiConfigProtection.AllowWrites=false;
            bool denied=false; try { set(Request("denied", "\"mode\":\"reset_all\"")); } catch (UnauthorizedAccessException) { denied=true; }
            GeminiConfigProtection.AllowWrites=true;
            Check(denied && GeminiSettings.Load().Count==1, "remote SET requires Windows administrator");
            options.Enabled=false;
            bool disabled=false; try { set(Request("disabled", "\"mode\":\"reset_all\"")); } catch (McpCapabilityDisabledException) { disabled=true; }
            options.Enabled=true;
            Check(disabled, "remote MCP disable is enforced");
            Config.Set("mcp_enabled","0"); disabled=false;
            try { set(Request("disabled-under-lock", "\"mode\":\"reset_all\"")); } catch (McpCapabilityDisabledException) { disabled=true; }
            Config.Set("mcp_enabled","1");
            Check(disabled, "commit rechecks newly disabled policy inside transaction");
            denied=false; try { GeminiSettings.Call(GeminiSettings.Setter,request,options,"other-user",token); } catch(UnauthorizedAccessException) { denied=true; }
            Check(denied, "another login identity cannot administer this owner");
            GeminiSettings.Save("{}");
            Parallel.For(0, 20, i => set(Request("parallel-"+i, "\"mode\":\"patch\",\"read_only_hints\":{\""+(i%2==0?"pc_get_status":"pc_read_file")+"\":false}")));
            Check(GeminiSettings.Load().Count==2, "concurrent independent patches do not lose updates");
            GeminiSettings.Save("{}");
            string exe=System.Reflection.Assembly.GetExecutingAssembly().Location;
            using (var left=System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe,"worker-left \""+root+"\""){UseShellExecute=false,CreateNoWindow=true}))
            using (var right=System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe,"worker-right \""+root+"\""){UseShellExecute=false,CreateNoWindow=true})) {
                if(!left.WaitForExit(20000)||!right.WaitForExit(20000)) throw new Exception("cross-process test timeout");
                Check(left.ExitCode==0&&right.ExitCode==0&&GeminiSettings.Load().Count==2,"separate processes merge under the existing file lock");
            }
            using (var form=new GeminiSettingsForm()) {
                form.CreateControl(); var grid=All(form).OfType<DataGridView>().Single();
                var locked=grid.Rows.Cast<DataGridViewRow>().Single(r=>(string)r.Cells["tool"].Value==GeminiSettings.Setter);
                Check(locked.Cells["choice"].ReadOnly && (string)locked.Cells["effective"].Value=="false", "UI setter is locked false");
                set(Request("ui-conflict", "\"mode\":\"patch\",\"read_only_hints\":{\"pc_write_file\":true}"));
                Click(form,"모두 기본값으로"); Click(form,"저장");
                Check(GeminiSettings.Load().Count==3 && All(form).OfType<Label>().Any(l=>l.Text.Contains("다시 불러온")), "open UI detects a later MCP save");
                Click(form,"다시 불러오기"); Click(form,"모두 기본값으로"); Click(form,"저장");
                Check(GeminiSettings.Load().Count==0, "UI reload resolves conflict before explicit reset");
            }
            Config.Set(GeminiSettings.Key,"{\"pc_set_gemini_settings\":true,\"retired\":\"old\",\"pc_read_file\":false}");
            Check(GeminiSettings.Load().Count==1, "protected and retired legacy overrides migrate without blocking management");
            set(Request("reset-all", "\"mode\":\"reset_all\""));
            Check(GeminiSettings.Load().Count==0, "remote reset all clears overrides");
            string journal=Config.Get("mcp_gemini_requests");
            var history=McpJson.Read(journal); string prefix=history.Keys.First(k=>k.EndsWith(McpTools.Hash(System.Text.Encoding.UTF8.GetBytes("first")),StringComparison.Ordinal)).Split(':')[0];
            var filled=new Dictionary<string,object>();
            for(int i=0;i<256;i++) filled[prefix+":"+McpTools.Hash(System.Text.Encoding.UTF8.GetBytes("full-"+i))]=new Dictionary<string,object>();
            Config.Set("mcp_gemini_requests",McpJson.Write(filled)); string beforeFull=File.ReadAllText(Path0); bool full=false;
            try { set(Request("quota", "\"mode\":\"patch\",\"read_only_hints\":{\"pc_get_status\":true}")); } catch(GeminiSettingsException ex) { full=ex.Code=="REQUEST_HISTORY_FULL"; }
            Check(full&&File.ReadAllText(Path0)==beforeFull,"receipt limit fails closed without evicting or applying");
            Config.Set("mcp_gemini_requests",journal);
        }
    }
}

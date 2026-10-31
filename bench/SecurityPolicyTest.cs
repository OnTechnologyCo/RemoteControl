using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using RemoteControl;
using RemoteControlSecrets;

internal static class SecurityPolicyTest
{
    private const string Token = "123456789:abcdefghijklmnopqrstuvwxyz_12345";
    private static int checks;
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); Console.WriteLine("PASS " + message); checks++; }
    private static void Denied(Action operation, string message)
    {
        bool blocked = false; try { operation(); } catch (UnauthorizedAccessException) { blocked = true; } catch (System.Security.SecurityException) { blocked = true; } catch (IOException ex) { if (ex.HResult != unchecked((int)0x80070005)) throw; blocked = true; }
        Check(blocked, message);
    }
    private static IntPtr UserToken()
    {
        foreach (var process in Process.GetProcessesByName("explorer"))
        {
            IntPtr token;
            try {
                if (OpenProcessToken(process.Handle, 0x000A, out token)) {
                    using (var id = new WindowsIdentity(token)) {
                        if (!id.IsSystem && !new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator)) return token;
                    }
                    CloseHandle(token);
                }
            } catch { }
            finally { process.Dispose(); }
        }
        throw new Exception("No standard interactive token is available; Windows ACL tests NOT executed.");
    }
    public static int Main(string[] args)
    {
        IntPtr token = IntPtr.Zero;
        try {
            token = UserToken();
            ConfigProtection.HardenDirectory(args[0], false);
            string root = Path.Combine(args[0], "machine"), ui = Path.Combine(args[0], "ordinary-ui");
            Directory.CreateDirectory(root); Directory.CreateDirectory(ui);
            // Reproduce the old inherited AND explicit grants, with synthetic credentials only.
            var old = ConfigProtection.DirectoryAcl(false);
            old.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.Modify,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            Directory.SetAccessControl(root, old);
            var uiAcl = new DirectorySecurity(); uiAcl.SetSecurityDescriptorSddlForm(old.GetSecurityDescriptorSddlForm(AccessControlSections.All)); Directory.SetAccessControl(ui, uiAcl);
            string settings = Path.Combine(root, "settings.txt");
            File.WriteAllText(settings, "user=fixture\npwhash=" + Auth.MakeHash("fixture-only") + "\nmcp_commands=0\nmcp_enabled=1\n");
            File.Copy(settings, settings + ".bak");
            string credentials = Path.Combine(root, "credentials"); Directory.CreateDirectory(credentials);
            Directory.SetAccessControl(credentials, ConfigProtection.DirectoryAcl(false));
            string encrypted = Path.Combine(credentials, "telegram-token.dpapi");
            File.WriteAllBytes(encrypted, ProtectedData.Protect(Encoding.UTF8.GetBytes(Token), null, DataProtectionScope.LocalMachine));
            using (WindowsIdentity.Impersonate(token)) {
                Check(!ConfigProtection.IsAdministrator, "effective identity is an ordinary local user");
                File.AppendAllText(settings, "legacy_write=confirmed\n");
                Check(Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(encrypted), null, DataProtectionScope.LocalMachine)) == Token,
                    "before: ordinary user can read and decrypt synthetic legacy token");
            }
            typeof(Config).GetField("Path0", BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, settings);
            Config.EnsureWritable(); Config.Reload();
            Check(Config.CheckLogin("fixture", "fixture-only"), "migration preserves existing password and login");
            Check(TelegramCredentials.ReadFrom(credentials) == Token, "service decrypts migrated machine DPAPI token");
            var before = File.ReadAllBytes(encrypted);
            using (WindowsIdentity.Impersonate(token)) {
                Denied(delegate { File.AppendAllText(settings, "mcp_commands=1\n"); }, "after: settings write denied");
                string replacement = Path.Combine(ui, "replacement.txt"); File.WriteAllText(replacement, "mcp_commands=1");
                Denied(delegate { File.Replace(replacement, settings, null); }, "after: settings replacement denied");
                Denied(delegate { File.Delete(settings); }, "after: settings deletion denied");
                Denied(delegate { File.Move(settings, settings + ".moved"); }, "after: settings rename denied");
                Denied(delegate { File.Delete(settings + ".bak"); }, "after: backup deletion denied");
                Denied(delegate { Directory.Move(root, root + "-moved"); }, "after: root directory replacement denied");
                Denied(delegate { File.WriteAllText(Path.Combine(root, "replacement.txt"), "forged"); }, "after: parent directory file creation denied");
                Denied(delegate { File.ReadAllBytes(encrypted); }, "after: token ciphertext read denied");
                Denied(delegate { Config.Set("mcp_commands", "1"); }, "after: Config API rejects ordinary-user policy changes");
                UiPreferences.SavePositionTo(Path.Combine(ui, "ui-settings.txt"), 120, 240);
                Check(File.ReadAllLines(Path.Combine(ui, "ui-settings.txt"))[0] == "120", "ordinary UI preferences save successfully");
                File.AppendAllText(Path.Combine(ui, "ui-settings.txt"), "\nuser=forged\npwhash=forged\nmcp_commands=1");
            }
            Config.Reload(); Check(Config.Get("mcp_commands") == "0" && Config.CheckLogin("fixture", "fixture-only"), "UI file cannot inject machine authentication or permissions");
            Config.Set("mcp_commands", "1"); Config.Reload(); Check(Config.Get("mcp_commands") == "1", "authorized administrator change is applied");
            Config.EnsureWritable(); Config.Reload();
            Check(Convert.ToBase64String(before) == Convert.ToBase64String(File.ReadAllBytes(encrypted)), "repeated upgrade preserves exact encrypted token");
            using (WindowsIdentity.Impersonate(token)) Denied(delegate { File.ReadAllBytes(encrypted); }, "restart/upgrade does not restore token read grant");
            Check(NotifyBroker.RequestAt(Path.Combine(root, "notify-pipe.txt"), "arbitrary-message") == "rejected", "IPC rejects arbitrary operations before connection");
            string handoff = Path.Combine(root, "notify-pipe.txt"), name = "RemoteControl.Notify." + Guid.NewGuid().ToString("N");
            NotifyBroker.Diagnostic = delegate(string message) { Console.WriteLine("IPC fixture: " + message); };
            int sent = 0;
            var server = new Thread(delegate() { NotifyBroker.Serve(name, handoff, delegate(string op) { if (op == "status") return "configured"; Interlocked.Increment(ref sent); return "sent"; }); });
            server.IsBackground = true; server.Start();
            for (int i = 0; i < 50 && !File.Exists(handoff); i++) Thread.Sleep(50);
            using (WindowsIdentity.Impersonate(token)) {
                Check(NotifyBroker.RequestAt(handoff, "status") == "configured", "ordinary tray obtains status without token");
                Check(NotifyBroker.RequestAt(handoff, "send-addresses") == "sent", "ordinary tray requests fixed service notification without token");
            }
            Check(sent == 1, "notification IPC dispatches exactly once; fake sender only");
            Console.WriteLine("ALL " + checks + " SECURITY CHECKS PASSED; isolated files, no account/service changes or real messages."); return 0;
        } catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { if (token != IntPtr.Zero) CloseHandle(token); }
    }
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
namespace RemoteControl
{
    internal static class Notify
    {
        internal static bool IsConfigured { get { return false; } }
        internal static string CurrentAddresses() { throw new Exception("Live notification must not run in fixture"); }
        internal static bool Send(string text) { throw new Exception("Live notification must not run in fixture"); }
    }
}

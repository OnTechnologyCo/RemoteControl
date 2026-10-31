using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using RemoteControlSecrets;

internal static class InstallerUpgradeTest {
    const string Token = "123456789:abcdefghijklmnopqrstuvwxyz_12345";
    const string Other = "987654321:ABCDEFGHIJKLMNOPQRSTUVWXYZ_54321";
    static int checks;
    static Type credentialsType = typeof(TelegramCredentials);
    static Assembly builtSetup;
    const BindingFlags Flags = BindingFlags.Static | BindingFlags.NonPublic;
    static object Invoke(string name, Type[] types, params object[] args) {
        try { return credentialsType.GetMethod(name, Flags, null, types, null).Invoke(null, args); }
        catch (TargetInvocationException e) { throw e.InnerException; }
    }
    static void Preserve(string image, string directory) { Invoke("PreserveFrom", new[] { typeof(string), typeof(string) }, image, directory); }
    static string Read(string directory) { return (string)Invoke("ReadFrom", new[] { typeof(string) }, directory); }
    static void Save(string directory, string token) { Invoke("SaveTo", new[] { typeof(string), typeof(string), typeof(bool) }, directory, token, false); }
    static void Check(bool ok, string message) { if (!ok) throw new Exception(message); checks++; }
    static void Writable(string path) {
        if (builtSetup != null) {
            // Run the real packaged extraction after migration, without service/registry changes.
            builtSetup.GetType("RemoteControlSetup.Sys", true).GetMethod("Extract", Flags).Invoke(null, new object[] { "RemoteControl.exe", path });
            using (var sha = SHA256.Create())
            using (var resource = builtSetup.GetManifestResourceStream("RemoteControl.exe"))
            using (var installed = File.OpenRead(path))
                Check(Convert.ToBase64String(sha.ComputeHash(resource)) == Convert.ToBase64String(sha.ComputeHash(installed)), "packaged payload differs after replacement");
        }
        byte[] original = File.ReadAllBytes(path);
        // A metadata read must not prevent the installer's immediate overwrite in this same process.
        using (var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            file.Write(original, 0, original.Length);
        Check(File.ReadAllBytes(path).Length == original.Length, "overwrite failed");
        File.Delete(path);
        Check(!File.Exists(path), "old executable remains locked");
    }
    static int Main(string[] args) {
        string root = args[0];
        try {
            if (args.Length > 1) {
                builtSetup = Assembly.LoadFrom(args[1]);
                credentialsType = builtSetup.GetType("RemoteControlSecrets.TelegramCredentials", true);
            }
            foreach (string name in new[] { "NoToken", "EmptyToken", "LegacyToken", "GetterToken" }) {
                string image = Path.Combine(root, name + ".exe");
                string credentials = Path.Combine(root, name + "-credentials");
                Preserve(image, credentials);
                string value = Read(credentials);
                Check(value == (name == "LegacyToken" ? Token : ""), name + ": migration changed token");
                Writable(image);
            }
            string bad = Path.Combine(root, "InvalidToken.exe");
            bool rejected = false;
            try { Preserve(bad, Path.Combine(root, "invalid-credentials")); }
            catch (IOException) { rejected = true; }
            Check(rejected, "invalid legacy token accepted");
            Check(!File.Exists(Path.Combine(root, "invalid-credentials", "telegram-token.dpapi")), "invalid token saved");
            Writable(bad);
            string existing = Path.Combine(root, "existing-credentials");
            Save(existing, Other);
            byte[] before = File.ReadAllBytes(Path.Combine(existing, "telegram-token.dpapi"));
            string old = Path.Combine(root, "ExistingToken.exe");
            Preserve(old, existing);
            Check(Read(existing) == Other, "existing credential replaced");
            Check(Convert.ToBase64String(before) == Convert.ToBase64String(File.ReadAllBytes(Path.Combine(existing, "telegram-token.dpapi"))), "encrypted credential changed");
            Writable(old);
            Preserve(Path.Combine(root, "missing.exe"), Path.Combine(root, "missing-credentials"));
            Check(!Directory.Exists(Path.Combine(root, "missing-credentials")), "new install created empty credentials");
            Console.WriteLine("PASS " + checks + " installer upgrade checks: metadata-only migration, immediate overwrite/delete, optional/invalid/existing token preservation. Isolated fixtures only.");
            return 0;
        } catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
}

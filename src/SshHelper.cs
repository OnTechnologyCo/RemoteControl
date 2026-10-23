using System;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;

namespace RemoteControl
{
    internal static class SshHelper
    {
        internal static string Prepare() { return Prepare("sshtunnel-client.exe.gz", "sshtunnel-client", "sshtunnel"); }
        internal static string Prepare(string resourceName, string prefix, string folder)
        {
            byte[] raw;
            using (Stream resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName))
            {
                if (resource == null) throw new InvalidOperationException("내장 SSHTunnel 연결 모듈이 없습니다. 새 설치 파일로 업데이트하세요.");
                using (var gzip = new GZipStream(resource, CompressionMode.Decompress))
                using (var memory = new MemoryStream()) { gzip.CopyTo(memory); raw = memory.ToArray(); }
            }
            string hash = Hash(raw), file = prefix + "-" + hash.Substring(0, 20) + ".exe";
            bool privileged;
            using (WindowsIdentity me = WindowsIdentity.GetCurrent())
                privileged = me.IsSystem || new WindowsPrincipal(me).IsInRole(WindowsBuiltInRole.Administrator);
            string[] roots = { Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) };
            Exception failure = null;
            foreach (string root in roots)
            {
                if (root.Length == 0) continue;
                try
                {
                    // Video executables live outside the user-writable settings tree.
                    // Protecting only a leaf allows a writable ancestor to be renamed.
                    string dir = folder == "video" ? Path.Combine(root, "RemoteControlVideoRuntime")
                        : Path.Combine(root, "RemoteControl", "bin", folder);
                    for (string part = dir; part != null; part = Path.GetDirectoryName(part))
                        if (Directory.Exists(part) && (File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0)
                            throw new IOException("SSHTunnel 모듈 경로에 재분석 지점을 사용할 수 없습니다.");
                    Directory.CreateDirectory(dir);
                    if (privileged) Harden(dir);
                    string executable = Path.Combine(dir, file);
                    if (File.Exists(executable) && (File.GetAttributes(executable) & FileAttributes.ReparsePoint) == 0 &&
                        (!privileged || Trusted(executable)) && Hash(File.ReadAllBytes(executable)) == hash) return executable;
                    string temporary = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".tmp");
                    try
                    {
                        File.WriteAllBytes(temporary, raw);
                        if (File.Exists(executable)) File.Delete(executable);
                        File.Move(temporary, executable);
                    }
                    finally { if (File.Exists(temporary)) File.Delete(temporary); }
                    if (privileged && !Trusted(executable)) throw new IOException("SSHTunnel 모듈 파일 소유자를 확인할 수 없습니다.");
                    return executable;
                }
                catch (Exception ex) { failure = ex; }
            }
            throw new IOException("SSHTunnel 연결 모듈을 안전한 폴더에 준비하지 못했습니다.", failure);
        }
        private static string Hash(byte[] raw)
        { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(raw)).Replace("-", "").ToLowerInvariant(); }
        private static bool Trusted(string file)
        {
            var owner = File.GetAccessControl(file).GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            return owner != null && (owner.IsWellKnown(WellKnownSidType.LocalSystemSid) || owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid));
        }
        private static void Harden(string dir)
        {
            // 부모 설정 폴더의 사용자 쓰기 권한이 SYSTEM 실행 파일까지 내려오면 안 된다.
            var acl = new DirectorySecurity(); acl.SetAccessRuleProtection(true, false);
            var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            acl.SetOwner(admins);
            InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            acl.AddAccessRule(new FileSystemAccessRule(admins, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
            Directory.SetAccessControl(dir, acl);
        }
    }
}

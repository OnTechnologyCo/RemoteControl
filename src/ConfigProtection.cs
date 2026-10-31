using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace RemoteControlSecrets
{
    // Shared by the service and installer so an update cannot restore the old Users:Modify ACL.
    internal static class ConfigProtection
    {
        internal static bool IsAdministrator
        {
            get { using (var id = WindowsIdentity.GetCurrent()) return id.IsSystem || new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator); }
        }

        internal static void RequireAdministrator()
        {
            if (!IsAdministrator) throw new UnauthorizedAccessException("관리자 권한으로 설정 창을 열어 주세요.");
        }

        internal static void CheckPath(string path)
        {
            for (string p = Path.GetFullPath(path); p != null; p = Path.GetDirectoryName(p))
                if ((File.Exists(p) || Directory.Exists(p)) && (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("보호된 경로에 재분석 지점을 사용할 수 없습니다.");
            if (File.Exists(path))
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    FileInformation info;
                    if (!GetFileInformationByHandle(stream.SafeFileHandle, out info) || info.Links != 1)
                        throw new IOException("보호된 파일의 하드 링크를 사용할 수 없습니다.");
                }
        }

        internal static bool Trusted(SecurityIdentifier sid)
        {
            return sid != null && (sid.IsWellKnown(WellKnownSidType.LocalSystemSid) || sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid));
        }

        internal static void Verify(string path, bool directory, bool confidential)
        {
            CheckPath(path);
            FileSystemSecurity acl = directory ? (FileSystemSecurity)Directory.GetAccessControl(path) : File.GetAccessControl(path);
            if (!Trusted(acl.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier)) throw new IOException("보안 설정 소유자가 올바르지 않습니다. 관리자 업데이트가 필요합니다.");
            var forbidden = confidential ? FileSystemRights.FullControl :
                FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
            foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
                if (rule.AccessControlType == AccessControlType.Allow && (rule.FileSystemRights & forbidden) != 0 && !Trusted(rule.IdentityReference as SecurityIdentifier))
                    throw new IOException("보안 설정 권한이 안전하지 않습니다. 관리자 업데이트가 필요합니다.");
        }

        internal static DirectorySecurity DirectoryAcl(bool confidential)
        {
            var acl = new DirectorySecurity();
            var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            acl.SetOwner(admins); acl.SetAccessRuleProtection(true, false);
            var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            foreach (var sid in new[] { WellKnownSidType.BuiltinAdministratorsSid, WellKnownSidType.LocalSystemSid })
                acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            if (!confidential) acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
            return acl;
        }

        internal static void ProtectFile(string path, bool confidential)
        {
            CheckPath(path);
            var acl = new FileSecurity();
            acl.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)); acl.SetAccessRuleProtection(true, false);
            foreach (var sid in new[] { WellKnownSidType.BuiltinAdministratorsSid, WellKnownSidType.LocalSystemSid })
                acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl, AccessControlType.Allow));
            if (!confidential) acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute, AccessControlType.Allow));
            File.SetAccessControl(path, acl);
            Verify(path, false, confidential);
        }

        internal static void HardenDirectory(string directory, bool confidential)
        {
            RequireAdministrator(); CheckPath(directory);
            if (!Directory.Exists(directory)) Directory.CreateDirectory(directory, DirectoryAcl(confidential));
            Directory.SetAccessControl(directory, DirectoryAcl(confidential));
            Verify(directory, true, confidential);
            // Revoke explicit old grants as well as inherited grants on backups and temporary files.
            foreach (string file in Directory.GetFiles(directory)) ProtectFile(file, confidential);
        }

        internal static void PrepareDataDirectory(string directory)
        {
            HardenDirectory(directory, false);
            string credentials = Path.Combine(directory, "credentials");
            if (Directory.Exists(credentials)) HardenDirectory(credentials, true);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FileInformation
        {
            public uint Attributes; public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
            public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        }
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation info);
    }
}

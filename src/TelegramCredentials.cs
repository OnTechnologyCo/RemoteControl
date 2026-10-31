using System;
using System.IO;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;

namespace RemoteControlSecrets
{
    internal static class TelegramCredentials
    {
        private static string DirectoryPath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RemoteControl", "credentials"); }
        }

        internal static string Read()
        {
            if (!ConfigProtection.IsAdministrator) return "";
            string value = Environment.GetEnvironmentVariable("REMOTE_CONTROL_TELEGRAM_BOT_TOKEN");
            if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
            try { return ReadFrom(DirectoryPath); }
            catch { return ""; } // 토큰 읽기 실패가 원격제어 서비스 자체를 멈추면 안 된다.
        }

        internal static string ReadFrom(string directory)
        {
            string file = Path.Combine(directory, "telegram-token.dpapi");
            if (!File.Exists(file)) return "";
            CheckPath(file);
            CheckOwnerAndWrites(directory, true);
            CheckOwnerAndWrites(file, false);
            byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(file), null, DataProtectionScope.LocalMachine);
            try { return Encoding.UTF8.GetString(plain); }
            finally { Array.Clear(plain, 0, plain.Length); }
        }

        internal static string ValidateToken(string token)
        {
            token = (token ?? "").Trim();
            if (token.Length > 256 || !Regex.IsMatch(token, @"\A[0-9]{6,}:[A-Za-z0-9_-]{20,}\z"))
                throw new ArgumentException("텔레그램 봇 토큰 형식을 확인해 주세요. BotFather의 토큰 전체를 입력하세요.");
            return token;
        }

        internal static void CheckEnvironment(string token)
        {
            foreach (EnvironmentVariableTarget target in new[] { EnvironmentVariableTarget.Process, EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine }) {
                string value = Environment.GetEnvironmentVariable("REMOTE_CONTROL_TELEGRAM_BOT_TOKEN", target);
                if (!string.IsNullOrWhiteSpace(value) && value.Trim() != token)
                    throw new IOException("다른 텔레그램 토큰이 환경 변수에 설정되어 있습니다. 해당 환경 변수를 먼저 확인하세요. 기존 설정은 변경하지 않았습니다.");
            }
        }

        // 사용자 입력은 배포 리소스나 명령줄에 넣지 않고 대상 PC에서만 저장한다.
        internal static void Save(string token) { SaveTo(DirectoryPath, token, true); }

        internal static void SaveTo(string directory, string token, bool replaceExisting)
        {
            token = ValidateToken(token);
            CheckEnvironment(token);
            string destination = Path.Combine(directory, "telegram-token.dpapi");
            CheckPath(directory); CheckPath(destination);
            ConfigProtection.RequireAdministrator();
            if (Directory.Exists(directory)) CheckOwnerAndWrites(directory, true);
            if (File.Exists(destination)) {
                string previous = ReadFrom(directory); // 손상/권한 오류를 미등록으로 취급하지 않는다.
                if (previous == token) return;
                if (!replaceExisting) throw new IOException("다른 봇 토큰이 이미 등록되어 있습니다. 기존 토큰을 보존했습니다.");
            }
            if (Directory.Exists(directory)) CheckOwnerAndWrites(directory, true);
            else Directory.CreateDirectory(directory, CredentialDirectorySecurity());
            CheckOwnerAndWrites(directory, true);
            WriteEncrypted(directory, token, replaceExisting);
            if (ReadFrom(directory) != token) throw new IOException("알림 토큰 저장 확인에 실패했습니다.");
        }

        internal static DirectorySecurity CredentialDirectorySecurity()
        {
            return ConfigProtection.DirectoryAcl(true);
        }

        internal static void HardenExisting(string directory)
        {
            if (Directory.Exists(directory)) ConfigProtection.HardenDirectory(directory, true);
        }

        private static void WriteEncrypted(string directory, string token, bool replaceExisting)
        {
            string destination = Path.Combine(directory, "telegram-token.dpapi");
            string temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
            byte[] plain = Encoding.UTF8.GetBytes(token);
            byte[] encrypted;
            try { encrypted = ProtectedData.Protect(plain, null, DataProtectionScope.LocalMachine); }
            finally { Array.Clear(plain, 0, plain.Length); }
            try {
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                    file.Write(encrypted, 0, encrypted.Length); file.Flush(true);
                }
                byte[] check = ProtectedData.Unprotect(File.ReadAllBytes(temporary), null, DataProtectionScope.LocalMachine);
                try { if (Encoding.UTF8.GetString(check) != token) throw new IOException("알림 토큰 암호화 검증 실패."); }
                finally { Array.Clear(check, 0, check.Length); }
                if (replaceExisting && File.Exists(destination)) File.Replace(temporary, destination, null);
                else File.Move(temporary, destination);
            } finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        internal static void PreserveFrom(string previousExecutable)
        { PreserveFrom(previousExecutable, DirectoryPath); }

        internal static void PreserveFrom(string previousExecutable, string directory)
        {
            string destination = Path.Combine(directory, "telegram-token.dpapi");
            ConfigProtection.RequireAdministrator();
            HardenExisting(directory);
            // 기존 암호화 자격 증명은 검증만 한다. 갱신·재발급·덮어쓰기는 하지 않는다.
            if (File.Exists(destination)) { ReadFrom(directory); return; }
            if (!File.Exists(previousExecutable)) return;
            // 구버전을 실행하거나 속성 getter를 호출하지 않고 상수 메타데이터만 읽는다.
            var assembly = Assembly.ReflectionOnlyLoadFrom(previousExecutable);
            var type = assembly.GetType("RemoteControl.Notify", false);
            var field = type == null ? null : type.GetField("BotToken", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            if (field == null || !field.IsLiteral || field.FieldType != typeof(string)) return;
            string token = field.GetRawConstantValue() as string;
            if (string.IsNullOrEmpty(token)) return;
            if (!Regex.IsMatch(token, @"\A[0-9]{6,}:[A-Za-z0-9_-]{20,}\z")) throw new IOException("기존 알림 토큰 형식을 확인할 수 없습니다. 기존 본체를 보존합니다.");
            CheckPath(directory);
            Directory.CreateDirectory(directory);
            Directory.SetAccessControl(directory, CredentialDirectorySecurity());
            CheckOwnerAndWrites(directory, true);
            byte[] plain = Encoding.UTF8.GetBytes(token);
            byte[] encrypted;
            try { encrypted = ProtectedData.Protect(plain, null, DataProtectionScope.LocalMachine); }
            finally { Array.Clear(plain, 0, plain.Length); }
            // CreateNew는 동시 설치나 기존 파일을 덮어쓰지 않는다.
            using (var file = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                file.Write(encrypted, 0, encrypted.Length);
            if (ReadFrom(directory) != token) throw new IOException("알림 자격 증명 이관 검증 실패. 기존 본체를 보존합니다.");
        }

        private static void CheckPath(string path) { ConfigProtection.CheckPath(path); }
        private static void CheckOwnerAndWrites(string path, bool directory)
        { ConfigProtection.Verify(path, directory, true); }
    }
}

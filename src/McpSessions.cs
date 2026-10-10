// MCP 인증은 창/잠금 화면 대리 프로세스가 바뀌어도 유지해야 한다. 화면 로그인 세션과 분리한다.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace RemoteControl
{
    internal sealed class McpSessions
    {
        private readonly string path;
        private readonly object gate = new object();
        internal McpSessions(string file)
        { path = file ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RemoteControl", "mcp-sessions.bin"); }
        private static byte[] Stamp(string credentials)
        { using (var sha = SHA256.Create()) return sha.ComputeHash(Encoding.UTF8.GetBytes("RemoteControl MCP sessions v1\n" + credentials)); }
        private FileStream LockFile()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            for (int i = 0; ; i++)
                try { return new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                catch (IOException) { if (i >= 40) throw; Thread.Sleep(25); }
        }
        private Dictionary<string, Session> Read(string credentials)
        {
            var rows = new Dictionary<string, Session>();
            if (!File.Exists(path)) return rows;
            byte[] sealedRows = File.ReadAllBytes(path);
            if (sealedRows.Length > 1024 * 1024) throw new IOException("MCP 인증 저장 파일이 너무 큽니다.");
            byte[] clear;
            try { clear = ProtectedData.Unprotect(sealedRows, Stamp(credentials), DataProtectionScope.LocalMachine); }
            // 비밀번호 변경으로 이전 인증 저장 파일이 복원되지 않는 것은 정상이다.
            catch (CryptographicException) { return rows; }
            try
            {
                var data = McpJson.Read(Encoding.UTF8.GetString(clear));
                object items;
                if (data == null || !data.TryGetValue("sessions", out items)) throw new IOException("MCP 인증 저장 형식이 올바르지 않습니다.");
                foreach (object item in (IEnumerable)items)
                {
                    var row = item as Dictionary<string, object>;
                    string token = Json.Str(row, "token"), user = Json.Str(row, "user"), ip = Json.Str(row, "ip");
                    if (!Regex.IsMatch(token ?? "", "^[A-Za-z0-9_-]{43}$") || user == null || ip == null || rows.Count >= 256)
                        throw new IOException("MCP 인증 저장 항목이 올바르지 않습니다.");
                    rows.Add(token, new Session { Token = token, User = user, Ip = ip, Expires = DateTime.MaxValue });
                }
                return rows;
            }
            finally { Array.Clear(clear, 0, clear.Length); }
        }
        private void Save(Dictionary<string, Session> rows, string credentials)
        {
            var values = new List<object>();
            foreach (Session s in rows.Values) values.Add(new { token = s.Token, user = s.User, ip = s.Ip });
            byte[] clear = Encoding.UTF8.GetBytes(McpJson.Write(new { version = 1, sessions = values }));
            byte[] sealedRows;
            try { sealedRows = ProtectedData.Protect(clear, Stamp(credentials), DataProtectionScope.LocalMachine); }
            finally { Array.Clear(clear, 0, clear.Length); }
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { File.WriteAllBytes(temp, sealedRows); if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path); }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        internal Session Create(string user, string ip, string credentials)
        {
            lock (gate) using (LockFile())
            {
                var rows = Read(credentials);
                if (rows.Count >= 256) throw new IOException("MCP 인증 연결 수가 많습니다. 사용하지 않는 연결을 해제하세요.");
                byte[] raw = new byte[32]; using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(raw);
                string token = Convert.ToBase64String(raw).TrimEnd('=').Replace('+', '-').Replace('/', '_');
                var session = new Session { Token = token, User = user, Ip = ip, Expires = DateTime.MaxValue };
                rows.Add(token, session); Save(rows, credentials); return session;
            }
        }
        internal Session Find(string token, string credentials)
        {
            if (!Regex.IsMatch(token ?? "", "^[A-Za-z0-9_-]{43}$")) return null;
            lock (gate) using (LockFile()) { Session result; return Read(credentials).TryGetValue(token, out result) ? result : null; }
        }
        internal void Drop(string token, string credentials)
        { lock (gate) using (LockFile()) { var rows = Read(credentials); if (rows.Remove(token)) Save(rows, credentials); } }
        internal void Clear(string credentials)
        { lock (gate) using (LockFile()) Save(new Dictionary<string, Session>(), credentials); }
    }
}

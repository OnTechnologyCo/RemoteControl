// 설정 보관.
//
// 비밀번호는 해시만 저장한다. 파일을 통째로 가져가도 비밀번호를 알 수 없다.
// 옆 프로젝트에서 겪은 사고를 반영해, 저장은 임시 파일에 쓴 뒤 바꿔치기한다.
// 그냥 덮어쓰면 저장 도중에 프로그램이 죽었을 때 파일이 통째로 비어 버린다.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace RemoteControl
{
    internal static class Config
    {
        // All machine policy stays under a protected common root. UI preferences are per-user.
        private static readonly string Path0 = ResolvePath();
        private static string ResolvePath()
        {
            string root = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            if (string.IsNullOrEmpty(root)) throw new IOException("공용 보안 설정 경로를 찾지 못했습니다.");
            return Path.Combine(root, "RemoteControl", "settings.txt");
        }

        private static readonly object gate = new object();
        private static Dictionary<string, string> map;

        internal static int Port
        {
            get { int p; return int.TryParse(Get("port"), out p) && p > 0 && p < 65536 ? p : 5900; }
            set { Set("port", value.ToString()); }
        }

        internal static string User
        {
            get { string u = Get("user"); return string.IsNullOrEmpty(u) ? "admin" : u; }
            set { Set("user", value); }
        }

        internal static bool HasPassword { get { return !string.IsNullOrEmpty(Get("pwhash")); } }

        internal static void SetPassword(string plain)
        {
            Set("pwhash", Auth.MakeHash(plain));
            // 비밀번호가 바뀌면 기존 세션은 전부 무효로 한다.
            Auth.DropAll();
        }

        internal static bool CheckLogin(string user, string pass)
        {
            if (string.IsNullOrEmpty(pass)) return false;
            if (!string.Equals(user, User, StringComparison.OrdinalIgnoreCase)) return false;

            return Auth.VerifyHash(pass, Get("pwhash"));
        }

        // ---------------------------------------------------------------- 읽고 쓰기

        internal static string Get(string key)
        {
            lock (gate)
            {
                Load();

                string v;
                return map.TryGetValue(key, out v) ? v : "";
            }
        }

        // Kept as a compatibility entry point; this now REVOKES ordinary-user writes.
        internal static void EnsureWritable()
        {
            RemoteControlSecrets.ConfigProtection.PrepareDataDirectory(Path.GetDirectoryName(Path0));
        }

        /// <summary>
        /// 다음에 읽을 때 파일에서 다시 가져오게 한다.
        ///
        /// 창과 서비스는 서로 다른 프로세스다. 창에서 포트나 비밀번호를 바꿔도
        /// 서비스는 자기 기억만 보고 있어서 모른다. 그래서 서비스 쪽이 주기적으로
        /// 이걸 불러 파일을 다시 본다.
        /// </summary>
        internal static void Reload()
        {
            lock (gate) { map = null; }
        }

        internal static void Set(string key, string value)
        {
            SetMany(new Dictionary<string, string> { { key, value ?? "" } });
        }

        internal static string[] GetMany(string[] keys)
        {
            lock (gate)
            {
                Load();
                string[] result = new string[keys.Length];
                for (int i = 0; i < keys.Length; i++)
                { string value; result[i] = map.TryGetValue(keys[i], out value) ? value : ""; }
                return result;
            }
        }

        internal static void SetMany(Dictionary<string, string> changes)
        {
            SetManyIf(null, null, changes, null);
        }

        // 조건 검사와 파일 변경을 같은 파일 잠금 안에서 한다. 직접 입력 모드로 바뀐 뒤
        // 늦게 끝난 Tailscale 작업이 URL/플러그인을 덮어쓰지 못하게 한다.
        internal static bool SetManyIf(string requiredKey, string requiredValue, Dictionary<string, string> changes, Action beforeSave)
        {
            RemoteControlSecrets.ConfigProtection.RequireAdministrator();
            lock (gate)
            {
                RemoteControlSecrets.ConfigProtection.Verify(Path.GetDirectoryName(Path0), true, false);
                // UI와 SYSTEM이 각각 오래된 사본을 저장하면 다른 쪽 설정이 사라진다.
                // 프로세스 사이에도 직렬화하고 최신 파일에 변경분만 합쳐 한 번 교체한다.
                FileStream lease = null;
                DateTime until = DateTime.UtcNow.AddSeconds(3);
                while (lease == null)
                {
                    try { lease = new FileStream(Path0 + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                    catch (IOException) { if (DateTime.UtcNow >= until) throw; System.Threading.Thread.Sleep(30); }
                }
                using (lease)
                {
                    map = null;
                    Load(true);
                    string actual;
                    if (requiredKey != null && (!map.TryGetValue(requiredKey, out actual) || actual != requiredValue)) return false;
                    try
                    {
                        foreach (KeyValuePair<string, string> pair in changes)
                        {
                            if (string.IsNullOrWhiteSpace(pair.Key) || pair.Key.IndexOfAny(new[] { '\r', '\n', '\0', '=' }) >= 0)
                                throw new ArgumentException("설정 키 형식이 올바르지 않습니다.");
                            string value = pair.Value ?? "";
                            if (value.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0) throw new ArgumentException("설정 값에 줄바꿈을 넣을 수 없습니다.");
                            map[pair.Key] = value;
                        }
                        if (beforeSave != null) beforeSave();
                        Save();
                        return true;
                    }
                    catch { map = null; throw; }
                }
            }
        }

        private static void Load(bool strict = false)
        {
            if (map != null) return;
            map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                string directory = Path.GetDirectoryName(Path0);
                if (!Directory.Exists(directory)) return; // No file means no password: listeners remain closed.
                RemoteControlSecrets.ConfigProtection.Verify(directory, true, false);
                string p = Path0;
                if (!File.Exists(p))
                {
                    // 저장 도중에 죽어 본 파일이 비었으면 직전 세대에서 되살린다.
                    if (File.Exists(p + ".bak")) p = p + ".bak";
                    else return;
                }

                RemoteControlSecrets.ConfigProtection.Verify(p, false, false);
                string[] lines = File.ReadAllLines(p, Encoding.UTF8);
                if (lines.Length == 0 && File.Exists(Path0 + ".bak"))
                {
                    RemoteControlSecrets.ConfigProtection.Verify(Path0 + ".bak", false, false);
                    lines = File.ReadAllLines(Path0 + ".bak", Encoding.UTF8);
                }

                foreach (string line in lines)
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    map[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                }
            }
            catch { map = null; throw; } // Never replace an unreadable policy with defaults.
        }

        private static void Save()
        {
            List<string> lines = new List<string>();
            foreach (KeyValuePair<string, string> kv in map)
                lines.Add(kv.Key + "=" + kv.Value);

            string tmp = Path0 + ".tmp";
            RemoteControlSecrets.ConfigProtection.CheckPath(tmp);
            using (FileStream stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                byte[] bytes = Encoding.UTF8.GetBytes(string.Join("\r\n", lines.ToArray()) + "\r\n");
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }

            if (File.Exists(Path0))
            {
                File.Replace(tmp, Path0, Path0 + ".bak");
                return;
            }

            File.Move(tmp, Path0);
        }
    }
}

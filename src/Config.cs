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
        /// <summary>
        /// 설정을 두는 곳.
        ///
        /// exe 옆에 두면 안 된다. 설치하면 exe 는 Program Files 에 있고 그 폴더는
        /// 보통 사용자가 쓸 수 없다. 그러면 창에서 비밀번호를 바꿔도 조용히 저장에
        /// 실패한다 (실패해도 아무 말이 없어서 더 나쁘다).
        /// 게다가 창은 내 계정으로, 서비스는 SYSTEM 으로 돌기 때문에 계정 폴더도 못 쓴다.
        /// 둘 다 닿을 수 있는 공용 폴더에 둔다. 설치할 때 이 폴더에만 쓰기 권한을 준다
        /// (exe 가 있는 폴더에는 주지 않는다 - 거기에 쓸 수 있으면 SYSTEM 권한을 뺏긴다).
        /// </summary>
        private static readonly string Path0 = ResolvePath();

        private static string ResolvePath()
        {
            string root;
            try { root = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData); }
            catch { root = ""; }

            if (string.IsNullOrEmpty(root))
                return System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.txt");

            string dir = System.IO.Path.Combine(root, "RemoteControl");
            try { Directory.CreateDirectory(dir); }
            catch { }

            string p = System.IO.Path.Combine(dir, "settings.txt");

            // 예전 판에서 exe 옆에 두던 것을 한 번만 옮겨 온다.
            try
            {
                string old = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.txt");
                if (!File.Exists(p) && File.Exists(old)) File.Copy(old, p, false);
            }
            catch { }

            return p;
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

        /// <summary>
        /// 설정 폴더와 파일을 보통 사용자도 고칠 수 있게 해 둔다.
        ///
        /// 서비스는 SYSTEM 으로 돈다. SYSTEM 이 만든 파일은 기본적으로 보통 사용자가
        /// 못 고친다. 그러면 창에서 비밀번호를 바꿔도 저장이 조용히 실패한다.
        /// 설치할 때 폴더에 권한을 주긴 하지만, 그 전에 만들어진 파일이나 설치 없이
        /// 쓰는 경우까지 덮으려면 서비스 쪽에서도 한 번 맞춰 줘야 한다.
        ///
        /// 프로그램이 있는 폴더에는 절대 권한을 주지 않는다. 거기에 쓸 수 있으면
        /// SYSTEM 서비스가 실행하는 파일을 바꿔치기당한다. 여기는 설정만 있는 곳이다.
        /// </summary>
        internal static void EnsureWritable()
        {
            try
            {
                string dir = System.IO.Path.GetDirectoryName(Path0);
                if (string.IsNullOrEmpty(dir)) return;

                var users = new System.Security.Principal.SecurityIdentifier(
                    System.Security.Principal.WellKnownSidType.BuiltinUsersSid, null);

                var ds = Directory.GetAccessControl(dir);
                ds.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                    users, System.Security.AccessControl.FileSystemRights.Modify,
                    System.Security.AccessControl.InheritanceFlags.ContainerInherit |
                    System.Security.AccessControl.InheritanceFlags.ObjectInherit,
                    System.Security.AccessControl.PropagationFlags.None,
                    System.Security.AccessControl.AccessControlType.Allow));
                Directory.SetAccessControl(dir, ds);

                // 이미 만들어진 파일은 폴더 권한을 물려받지 않는다. 하나씩 손봐 준다.
                foreach (string f in Directory.GetFiles(dir))
                {
                    try
                    {
                        var fs = File.GetAccessControl(f);
                        fs.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                            users, System.Security.AccessControl.FileSystemRights.Modify,
                            System.Security.AccessControl.AccessControlType.Allow));
                        File.SetAccessControl(f, fs);
                    }
                    catch { }
                }
            }
            catch { }      // 관리자 권한이 없으면 그냥 넘어간다
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
            try { SetMany(new Dictionary<string, string> { { key, value ?? "" } }); }
            catch { } // 기존 호출 계약. 설정 창은 SetMany의 저장 실패를 직접 표시한다.
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
            lock (gate)
            {
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
                string p = Path0;
                if (!File.Exists(p))
                {
                    // 저장 도중에 죽어 본 파일이 비었으면 직전 세대에서 되살린다.
                    if (File.Exists(p + ".bak")) p = p + ".bak";
                    else return;
                }

                string[] lines = File.ReadAllLines(p, Encoding.UTF8);
                if (lines.Length == 0 && File.Exists(Path0 + ".bak"))
                    lines = File.ReadAllLines(Path0 + ".bak", Encoding.UTF8);

                foreach (string line in lines)
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    map[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                }
            }
            catch { if (strict) { map = null; throw; } }
        }

        private static void Save()
        {
            List<string> lines = new List<string>();
            foreach (KeyValuePair<string, string> kv in map)
                lines.Add(kv.Key + "=" + kv.Value);

            string tmp = Path0 + ".tmp";
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

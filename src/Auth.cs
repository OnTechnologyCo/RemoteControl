// 로그인.
//
// 파일 공유는 "링크를 아는 사람만 받는다" 로 충분했다. 원격제어는 다르다.
// 한 번 뚫리면 PC 전체가 넘어간다. 그래서:
//
//  - 비밀번호를 그대로 저장하지 않는다 (PBKDF2 + 임의 소금)
//  - 비교를 상수 시간으로 한다 (한 글자씩 빨리 실패하면 그 시간차로 알아낼 수 있다)
//  - 무차별 대입을 막는다 (실패가 쌓이면 점점 오래 잠근다)
//  - 세션 토큰은 추측할 수 없는 난수여야 한다
//  - 화면 로그인은 대리 프로세스가 바뀌어도 이어져야 한다 (아래 "세션 저장")
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace RemoteControl
{
    internal sealed class Session
    {
        public string Token;
        public string User;
        public DateTime Expires;
        public string Ip;
    }

    internal static class Auth
    {
        private const int Iterations = 120000;   // 요즘 PC 에서 0.05초 안팎. 대입 공격 비용을 올린다
        private const int SaltLen = 16;
        private const int HashLen = 32;

        private static readonly RNGCryptoServiceProvider rng = new RNGCryptoServiceProvider();
        private static readonly object gate = new object();

        // 열쇠는 토큰이 아니라 토큰의 SHA-256 이다. 저장 파일에서 되살린 세션은 토큰을 모르므로
        // 같은 열쇠로 찾아야 하고, 처음 쿠키가 들어올 때 Token 을 채운다.
        private static readonly Dictionary<string, Session> sessions = new Dictionary<string, Session>();

        // 접속 IP 별 실패 기록. 무차별 대입을 늦춘다.
        private sealed class Fail { public int Count; public DateTime Until; }
        private static readonly Dictionary<string, Fail> fails = new Dictionary<string, Fail>();

        internal static TimeSpan SessionLifetime = TimeSpan.FromHours(8);

        // ---------------------------------------------------------------- 비밀번호

        /// <summary>저장용 문자열을 만든다. 형식: pbkdf2$반복수$소금$해시 (모두 base64)</summary>
        internal static string MakeHash(string password)
        {
            byte[] salt = new byte[SaltLen];
            rng.GetBytes(salt);

            byte[] hash;
            using (Rfc2898DeriveBytes k = new Rfc2898DeriveBytes(password, salt, Iterations))
                hash = k.GetBytes(HashLen);

            return string.Format("pbkdf2${0}${1}${2}",
                Iterations, Convert.ToBase64String(salt), Convert.ToBase64String(hash));
        }

        internal static bool VerifyHash(string password, string stored)
        {
            if (string.IsNullOrEmpty(stored)) return false;

            string[] p = stored.Split('$');
            if (p.Length != 4 || p[0] != "pbkdf2") return false;

            int iter;
            if (!int.TryParse(p[1], out iter) || iter < 1000) return false;

            byte[] salt, want;
            try
            {
                salt = Convert.FromBase64String(p[2]);
                want = Convert.FromBase64String(p[3]);
            }
            catch { return false; }

            byte[] got;
            using (Rfc2898DeriveBytes k = new Rfc2898DeriveBytes(password, salt, iter))
                got = k.GetBytes(want.Length);

            return FixedTimeEquals(got, want);
        }

        /// <summary>
        /// 길이와 내용이 같은지를 항상 같은 시간에 판단한다.
        /// 앞에서부터 비교하다 다르면 바로 멈추는 방식은, 걸린 시간으로
        /// 몇 글자까지 맞았는지 알아낼 수 있어 위험하다.
        /// </summary>
        private static bool FixedTimeEquals(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;

            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        // ---------------------------------------------------------------- 대입 공격 지연

        /// <summary>지금 이 IP 가 잠겨 있으면 남은 시간(초). 아니면 0.</summary>
        internal static int LockedFor(string ip)
        {
            lock (gate)
            {
                Fail f;
                if (!fails.TryGetValue(ip ?? "-", out f)) return 0;

                double left = (f.Until - DateTime.UtcNow).TotalSeconds;
                return left > 0 ? (int)Math.Ceiling(left) : 0;
            }
        }

        internal static void NoteFailure(string ip)
        {
            lock (gate)
            {
                string k = ip ?? "-";

                Fail f;
                if (!fails.TryGetValue(k, out f)) { f = new Fail(); fails[k] = f; }

                f.Count++;

                // 5번까지는 봐주고, 그 뒤로는 5초 -> 10 -> 20 ... 최대 5분까지 늘린다.
                if (f.Count > 5)
                {
                    int wait = (int)Math.Min(300, 5 * Math.Pow(2, Math.Min(6, f.Count - 6)));
                    f.Until = DateTime.UtcNow.AddSeconds(wait);
                }
            }
        }

        internal static void NoteSuccess(string ip)
        {
            lock (gate) { fails.Remove(ip ?? "-"); }
        }

        // ---------------------------------------------------------------- 세션

        internal static Session CreateSession(string user, string ip)
        {
            byte[] raw = new byte[32];
            rng.GetBytes(raw);

            Session s = new Session
            {
                Token = Convert.ToBase64String(raw).TrimEnd('=').Replace('+', '-').Replace('/', '_'),
                User = user,
                Ip = ip,
                Expires = DateTime.UtcNow.Add(SessionLifetime)
            };

            string key = Key(s.Token);
            lock (gate)
            {
                Sweep();
                Persist(delegate { sessions[key] = s; });
            }
            return s;
        }

        internal static Session Find(string token)
        {
            if (string.IsNullOrEmpty(token) || token.Length > 128) return null;
            string key = Key(token);

            lock (gate)
            {
                Session s;
                // 다른 대리 프로세스가 방금 만든 로그인일 수 있다. 파일이 바뀌었을 때만 다시 읽는다.
                if (!sessions.TryGetValue(key, out s))
                {
                    Refresh();
                    if (!sessions.TryGetValue(key, out s)) return null;
                }

                if (DateTime.UtcNow > s.Expires) { sessions.Remove(key); return null; }
                if (s.Token == null) s.Token = token;
                return s;
            }
        }

        internal static void Drop(string token)
        {
            if (string.IsNullOrEmpty(token) || token.Length > 128) return;
            string key = Key(token);
            lock (gate) { Persist(delegate { sessions.Remove(key); }); }
        }

        internal static void DropAll()
        {
            lock (gate) { Persist(delegate { sessions.Clear(); }); }
        }

        private static string Key(string token)
        {
            using (SHA256 sha = SHA256.Create())
                return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(token)))
                    .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        // ---------------------------------------------------------------- 세션 저장
        //
        // 왜 필요한가.
        //   원격 데스크톱(RDP)으로 쓰던 세션을 창만 닫아 두면 그 세션은 "끊김" 이고, 콘솔에는
        //   로그인 화면용 임시 세션이 뜬다. 대리 프로세스는 그 콘솔에 붙어 로그인 화면을 보여 준다.
        //   거기서 로그인하면 윈도우가 사용자 세션을 콘솔로 옮기고 임시 세션을 없앤다 — 그 안의
        //   대리 프로세스도 함께 죽는다. 서비스가 새 세션에 대리 프로세스를 다시 띄우지만, 화면
        //   로그인이 메모리에만 있어서 뷰어가 1.5초마다 다시 붙어도 /ws 가 "인증 없음" 으로 계속
        //   끊겼다. 사용자는 새로고침해서 다시 로그인해야 했다
        //   (DESKTOP-DS8E56R 기록: 세션 1 로그인 화면 → 2초 뒤 세션 2 에서 대리 프로세스 재시작).
        //
        // 어떻게 지키나.
        //   - 토큰이 아니라 SHA-256 만 적는다. 파일이 새도 그것으로 로그인할 수 없다.
        //   - DPAPI(LocalMachine)로 봉인하고 현재 아이디·비밀번호 해시를 엔트로피로 섞는다.
        //     비밀번호가 바뀌면 옛 파일은 풀리지 않는다 (DropAll 도 비운다).
        //   - SYSTEM·관리자만 읽는 credentials 폴더에 둔다. 만료(8시간)·로그아웃은 그대로다.
        //   - 파일을 다루다 실패하면 이 프로세스는 예전처럼 메모리에만 둔다. 로그인이 막히면 안 된다.
        private static string storePath;
        private static Func<string> storeStamp;
        private static DateTime storeSeen = DateTime.MinValue;
        private const int StoredLimit = 256;

        /// <summary>저장소를 쓰지 못한 이유. 비어 있으면 정상이거나 쓰지 않는 중이다.</summary>
        internal static string StoreError = "";

        internal static string DefaultStorePath
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                                    "RemoteControl", "credentials", "screen-sessions.bin");
            }
        }

        /// <param name="credentialStamp">현재 아이디·비밀번호 해시. 바뀌면 옛 저장분은 풀리지 않는다.</param>
        /// <param name="prepareDirectory">폴더를 만들고 잠근다. null 이면 만들기만 한다(시험용).</param>
        internal static bool UsePersistentStore(string path, Func<string> credentialStamp, Action<string> prepareDirectory)
        {
            lock (gate)
            {
                try
                {
                    string directory = Path.GetDirectoryName(path);
                    if (prepareDirectory != null) prepareDirectory(directory); else Directory.CreateDirectory(directory);
                    storePath = path; storeStamp = credentialStamp; storeSeen = DateTime.MinValue;
                    using (LockStore()) { Merge(ReadStore()); storeSeen = StoreTime(); }
                    StoreError = "";
                    return true;
                }
                catch (Exception ex) { DisableStore(ex); return false; }
            }
        }

        private static void DisableStore(Exception ex)
        {
            storePath = null; storeStamp = null;
            StoreError = ex.GetType().Name + ": " + ex.Message;
        }

        // 다른 프로세스가 같은 파일을 읽고 쓰는 동안 끼어들지 않는다. 호출하는 쪽이 gate 를 잡고 있다.
        private static void Persist(Action change)
        {
            if (storePath == null) { change(); return; }
            bool applied = false;
            try
            {
                using (LockStore())
                {
                    Merge(ReadStore());      // 다른 대리 프로세스의 로그인·로그아웃을 먼저 반영한다
                    change(); applied = true;
                    WriteStore();
                }
            }
            catch (Exception ex) { DisableStore(ex); if (!applied) change(); }
        }

        private static void Refresh()
        {
            if (storePath == null) return;
            try
            {
                if (StoreTime() == storeSeen) return;   // 바뀌지 않았으면 읽지 않는다 (잘못된 쿠키가 쏟아져도 싸다)
                using (LockStore()) { Merge(ReadStore()); storeSeen = StoreTime(); }
            }
            catch (Exception ex) { DisableStore(ex); }
        }

        private static DateTime StoreTime()
        {
            return File.Exists(storePath) ? File.GetLastWriteTimeUtc(storePath) : DateTime.MinValue;
        }

        private static FileStream LockStore()
        {
            for (int i = 0; ; i++)
                try { return new FileStream(storePath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                catch (IOException) { if (i >= 40) throw; Thread.Sleep(25); }
        }

        private static byte[] Entropy()
        {
            using (SHA256 sha = SHA256.Create())
                return sha.ComputeHash(Encoding.UTF8.GetBytes("RemoteControl screen sessions v1\n" + storeStamp()));
        }

        // 파일 내용을 그대로 믿지 않는다. 형식이 틀리거나 봉인이 안 풀리면 저장된 로그인이 없는 것으로 본다.
        private static Dictionary<string, Session> ReadStore()
        {
            var rows = new Dictionary<string, Session>();
            if (!File.Exists(storePath)) return rows;
            byte[] sealedRows = File.ReadAllBytes(storePath);
            if (sealedRows.Length == 0 || sealedRows.Length > 1024 * 1024) return rows;

            byte[] clear;
            try { clear = ProtectedData.Unprotect(sealedRows, Entropy(), DataProtectionScope.LocalMachine); }
            catch (CryptographicException) { return rows; }   // 비밀번호가 바뀌었거나 손상됨

            try
            {
                string[] lines = Encoding.UTF8.GetString(clear).Split('\n');
                if (lines.Length == 0 || lines[0] != "rcsessions 1") return rows;
                DateTime now = DateTime.UtcNow;
                for (int i = 1; i < lines.Length && rows.Count < StoredLimit; i++)
                {
                    string[] f = lines[i].Split('\t');
                    if (f.Length != 4 || !Regex.IsMatch(f[0], "^[A-Za-z0-9_-]{43}$")) continue;
                    long ticks;
                    if (!long.TryParse(f[3], out ticks) || ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks) continue;
                    var expires = new DateTime(ticks, DateTimeKind.Utc);
                    if (expires <= now || expires > now.Add(SessionLifetime)) continue;   // 만료됐거나 수명보다 긴 값은 버린다
                    string user, ip;
                    try { user = Encoding.UTF8.GetString(Convert.FromBase64String(f[1])); ip = Encoding.UTF8.GetString(Convert.FromBase64String(f[2])); }
                    catch (FormatException) { continue; }
                    rows[f[0]] = new Session { User = user, Ip = ip, Expires = expires };
                }
                return rows;
            }
            finally { Array.Clear(clear, 0, clear.Length); }
        }

        private static void WriteStore()
        {
            DateTime now = DateTime.UtcNow;
            var live = new List<KeyValuePair<string, Session>>();
            foreach (KeyValuePair<string, Session> kv in sessions) if (kv.Value.Expires > now) live.Add(kv);
            live.Sort(delegate (KeyValuePair<string, Session> a, KeyValuePair<string, Session> b) { return b.Value.Expires.CompareTo(a.Value.Expires); });

            var text = new StringBuilder("rcsessions 1");
            for (int i = 0; i < live.Count && i < StoredLimit; i++)
                text.Append('\n').Append(live[i].Key)
                    .Append('\t').Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(live[i].Value.User ?? "")))
                    .Append('\t').Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(live[i].Value.Ip ?? "")))
                    .Append('\t').Append(live[i].Value.Expires.Ticks);

            byte[] clear = Encoding.UTF8.GetBytes(text.ToString()), sealedRows;
            try { sealedRows = ProtectedData.Protect(clear, Entropy(), DataProtectionScope.LocalMachine); }
            finally { Array.Clear(clear, 0, clear.Length); }

            // 임시 파일에 쓴 뒤 바꿔치기한다. 쓰는 도중 죽어도 옛 파일이 남는다 (Config.cs 와 같은 이유).
            string temp = storePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(temp, sealedRows);
                if (File.Exists(storePath)) File.Replace(temp, storePath, null); else File.Move(temp, storePath);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
            storeSeen = StoreTime();
        }

        // 파일 내용으로 맞추되, 이미 가진 Session 객체는 그대로 둔다. MCP 중계는 같은 객체인지로
        // 로그인이 살아 있는지 본다 (McpHost.BridgeCall).
        private static void Merge(Dictionary<string, Session> rows)
        {
            var next = new Dictionary<string, Session>();
            foreach (KeyValuePair<string, Session> kv in rows)
            {
                Session mine;
                next[kv.Key] = sessions.TryGetValue(kv.Key, out mine) ? mine : kv.Value;
            }
            sessions.Clear();
            foreach (KeyValuePair<string, Session> kv in next) sessions[kv.Key] = kv.Value;
        }

        internal static int ActiveCount
        {
            get { lock (gate) { Sweep(); return sessions.Count; } }
        }

        private static void Sweep()
        {
            DateTime now = DateTime.UtcNow;
            List<string> dead = null;

            foreach (KeyValuePair<string, Session> kv in sessions)
                if (now > kv.Value.Expires)
                {
                    if (dead == null) dead = new List<string>();
                    dead.Add(kv.Key);
                }

            if (dead != null)
                foreach (string t in dead) sessions.Remove(t);
        }

        /// <summary>쿠키 머리글에서 세션 토큰을 꺼낸다.</summary>
        internal static string TokenFromCookie(string cookieHeader)
        {
            if (string.IsNullOrEmpty(cookieHeader)) return null;

            foreach (string part in cookieHeader.Split(';'))
            {
                string p = part.Trim();
                if (p.StartsWith("rcsid=", StringComparison.Ordinal))
                    return p.Substring(6).Trim();
            }
            return null;
        }
    }
}

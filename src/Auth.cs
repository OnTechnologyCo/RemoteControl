// 로그인.
//
// 파일 공유는 "링크를 아는 사람만 받는다" 로 충분했다. 원격제어는 다르다.
// 한 번 뚫리면 PC 전체가 넘어간다. 그래서:
//
//  - 비밀번호를 그대로 저장하지 않는다 (PBKDF2 + 임의 소금)
//  - 비교를 상수 시간으로 한다 (한 글자씩 빨리 실패하면 그 시간차로 알아낼 수 있다)
//  - 무차별 대입을 막는다 (실패가 쌓이면 점점 오래 잠근다)
//  - 세션 토큰은 추측할 수 없는 난수여야 한다
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

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

            lock (gate)
            {
                Sweep();
                sessions[s.Token] = s;
            }
            return s;
        }

        internal static Session Find(string token)
        {
            if (string.IsNullOrEmpty(token)) return null;

            lock (gate)
            {
                Session s;
                if (!sessions.TryGetValue(token, out s)) return null;

                if (DateTime.UtcNow > s.Expires) { sessions.Remove(token); return null; }
                return s;
            }
        }

        internal static void Drop(string token)
        {
            if (string.IsNullOrEmpty(token)) return;
            lock (gate) { sessions.Remove(token); }
        }

        internal static void DropAll()
        {
            lock (gate) { sessions.Clear(); }
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

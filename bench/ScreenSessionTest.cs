// 화면 로그인 저장소(Auth.cs "세션 저장") 시험.
//
// 실제 문제는 "대리 프로세스가 바뀌면 로그인이 사라진다" 였으므로 한 프로세스 안에서 흉내 내지 않고
// 이 시험 프로그램을 별도 프로세스로 다시 띄워 확인한다. 운영 설정·서비스·실제 로그인 파일은 쓰지 않는다
// (임시 폴더의 파일만 쓴다).
using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace RemoteControl
{
    internal static class ScreenSessionTest
    {
        private static int checks;
        private static void Check(bool ok, string label) { if (!ok) throw new Exception("FAIL " + label); checks++; Console.WriteLine("PASS " + label); }

        // 자식 프로세스: <mode> <store> <stamp> [token] [lifetimeSeconds]
        private static int Child(string[] a)
        {
            string mode = a[0], path = a[1], stamp = a[2];
            if (a.Length > 4) Auth.SessionLifetime = TimeSpan.FromSeconds(double.Parse(a[4]));
            bool stored = Auth.UsePersistentStore(path, delegate { return stamp; }, null);
            switch (mode)
            {
                case "create":
                    Session s = Auth.CreateSession("tester", "10.0.0.5");
                    Console.Write((stored ? "stored " : "memory ") + s.Token); return 0;
                case "find":
                    Session f = Auth.Find(a[3]);
                    Console.Write(f == null ? "missing" : "found " + f.User + " " + f.Ip); return 0;
                case "drop": Auth.Drop(a[3]); Console.Write("dropped"); return 0;
                case "dropall": Auth.DropAll(); Console.Write("dropped"); return 0;
            }
            return 2;
        }

        private static string Run(params string[] args)
        {
            var psi = new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName)
            { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
            var quoted = new StringBuilder();
            foreach (string a in args) quoted.Append('"').Append(a).Append("\" ");
            psi.Arguments = quoted.ToString();
            using (Process p = Process.Start(psi))
            {
                string output = p.StandardOutput.ReadToEnd(); p.WaitForExit();
                if (p.ExitCode != 0) throw new Exception("child failed: " + string.Join(" ", args));
                return output.Trim();
            }
        }

        private static string Create(string path, string stamp) { string r = Run("create", path, stamp); if (!r.StartsWith("stored ")) throw new Exception("store not used: " + r); return r.Substring(7); }

        public static int Main(string[] args)
        {
            if (args.Length > 0 && args[0] != "run") return Child(args);
            try
            {
                string dir = Path.Combine(args[1], "credentials"), path = Path.Combine(dir, "screen-sessions.bin");
                const string stamp = "tester|pbkdf2$fixture";

                string token = Create(path, stamp);
                Check(Run("find", path, stamp, token) == "found tester 10.0.0.5", "a new agent process keeps the screen login");

                byte[] raw = File.ReadAllBytes(path);
                byte[] entropy; using (var sha = SHA256.Create()) entropy = sha.ComputeHash(Encoding.UTF8.GetBytes("RemoteControl screen sessions v1\n" + stamp));
                string clear = Encoding.UTF8.GetString(ProtectedData.Unprotect(raw, entropy, DataProtectionScope.LocalMachine));
                string key; using (var sha = SHA256.Create()) key = Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(token))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
                Check(Encoding.ASCII.GetString(raw).IndexOf(token, StringComparison.Ordinal) < 0 && clear.IndexOf(token, StringComparison.Ordinal) < 0 && clear.Contains(key),
                      "store holds only the token hash, sealed with DPAPI");

                Check(Run("find", path, "tester|pbkdf2$changed", token) == "missing", "a password change invalidates stored logins");
                Check(Run("find", path, stamp, token) == "found tester 10.0.0.5", "a wrong stamp does not destroy the store");

                Run("drop", path, stamp, token);
                Check(Run("find", path, stamp, token) == "missing", "logout in one process is honored by the next");

                // MCP 중계는 Auth.Find 결과가 같은 객체인지로 로그인 유효성을 본다. 다시 읽어도 바뀌면 안 된다.
                Auth.UsePersistentStore(path, delegate { return stamp; }, null);
                Session mine = Auth.CreateSession("tester", "10.0.0.6");
                Session first = Auth.Find(mine.Token);
                string other = Create(path, stamp);           // 다른 프로세스가 파일을 바꾼다
                Session seenOther = Auth.Find(other);
                Check(seenOther != null && seenOther.Token == other, "a login made by another agent is picked up on demand");
                Check(object.ReferenceEquals(Auth.Find(mine.Token), first), "reloading keeps existing Session objects (MCP bridge identity)");

                Auth.DropAll();
                Check(Run("find", path, stamp, other) == "missing" && Run("find", path, stamp, mine.Token) == "missing", "DropAll clears the shared store");

                File.WriteAllText(path, "corrupt");
                Check(Run("find", path, stamp, other) == "missing", "a corrupt store is treated as empty");
                string fresh = Create(path, stamp);
                Check(Run("find", path, stamp, fresh) == "found tester 10.0.0.5", "a corrupt store is replaced by the next login");

                string brief = Run("create", path, stamp, "-", "1").Substring(7);
                Thread.Sleep(1300);
                Check(Run("find", path, stamp, brief, "1") == "missing", "expired logins are not restored");

                string blocked = Path.Combine(args[1], "not-a-directory");
                File.WriteAllText(blocked, "file");
                string memory = Run("create", Path.Combine(blocked, "screen-sessions.bin"), stamp);
                Check(memory.StartsWith("memory "), "an unusable store falls back to memory without blocking login");

                Console.WriteLine("ALL " + checks + " SCREEN SESSION CHECKS PASSED");
                return 0;
            }
            catch (Exception ex) { Console.WriteLine(ex.Message); return 1; }
        }
    }
}

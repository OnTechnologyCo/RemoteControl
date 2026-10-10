// 외부망 터널.
//
// 옆 프로젝트(FileSharer)에서 실측으로 다듬은 것을 그대로 쓴다.
// tsnet 으로 Tailscale 노드를 자식 프로세스 안에 띄우고 Funnel(공개 HTTPS)을 연다.
//   - 이 PC 에 Tailscale 을 설치하지 않는다 (드라이버·관리자 권한·서비스 없음)
//   - 받는 사람도 아무것도 설치하지 않는다. 휴대폰 브라우저로 바로 접속
//   - TLS 가 이 PC 에서 끝나므로 Tailscale 도 내용을 못 본다
//
// tsproxy 는 주소를 넘기기 전에 DNS 와 인증서가 준비된 것까지 확인한다.
// 그 확인을 건너뛰면 통신사 DNS 에 "없는 이름" 이 캐시되어 한동안 아무도
// 접속하지 못하는 일이 생긴다 (실제로 겪었다).
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace RemoteControl
{
    internal static class Tunnel
    {
        private const string ResourceName = "tsproxy.exe.gz";

        // 이 둘은 락 없이 읽는다.
        //
        // Start() 는 개통이 끝날 때까지(최대 150초) 락을 쥐고 있다. 그런데 창은 1초마다
        // 상태를 새로 그리면서 IsAlive() 와 CurrentUrl 을 부른다. 그것들이 같은 락을
        // 기다리면 창이 통째로 멈춘다. 실제로 "여는 중..." 에서 프로그램이 얼어붙었다.
        // 락은 "동시에 두 번 열지 않기" 에만 쓰고, 읽기는 막지 않는다.
        private static volatile Process proc;
        private static volatile string url;
        private static volatile bool opening;

        // Stop() 이 불릴 때마다 올라간다. 여는 도중에 닫으라는 말이 들어왔는지를
        // Start 가 끝날 때 이것으로 알아본다 (아래 Stop 주석 참조).
        private static volatile int stopGen;

        private static readonly object gate = new object();

        /// <summary>지금 개통하는 중인가. 창이 상태를 보여 줄 때 쓴다.</summary>
        internal static bool IsOpening { get { return opening; } }

        internal static string LastError = "";

        internal static bool IsConfigured { get { return !string.IsNullOrEmpty(LoadKey()); } }

        /// <summary>
        /// 인증 키는 평문으로 두지 않는다. Trust credential 은 만료가 없어서
        /// 새어 나가면 계속 쓸 수 있다. DPAPI 로 현재 사용자에게 묶어 저장하므로
        /// settings.txt 를 통째로 가져가도 다른 계정/PC 에서는 풀리지 않는다.
        /// </summary>
        internal static void SaveKey(string key)
        {
            // 키가 바뀌었다는 것은 대개 다른 Tailscale 계정으로 옮긴다는 뜻이다.
            // 그런데 이 PC 에는 예전 계정에서 받은 장비 신원이 남아 있다.
            // 그대로 두면 새 키로 등록하려 해도 옛 신원으로 로그인하려 들어 실패한다.
            // (설치 파일에 키를 넣어 두고 "쓰는 사람이 나중에 바꾸면 된다" 고 하려면
            //  이게 되어야 한다. 안 그러면 바꿔도 안 열린다)
            string old = LoadKey();
            if (!string.IsNullOrEmpty(old) && !string.IsNullOrEmpty(key) &&
                !string.Equals(old.Trim(), key.Trim(), StringComparison.Ordinal))
            {
                ResetIdentity();
            }

            Config.Set("tskey", Seal(key, System.Security.Cryptography.DataProtectionScope.CurrentUser));

            // 두 벌로 둔다. 서비스는 SYSTEM 으로 돌기 때문에 내 계정으로 잠근 것을 풀지 못한다.
            // LocalMachine 쪽은 이 PC 안에서는 누구나 풀 수 있지만, 파일을 다른 PC 로
            // 가져가면 여전히 못 푼다. 잠금 화면 제어를 얻는 대가로 이 정도는 받아들인다.
            Config.Set("tskeym", Seal(key, System.Security.Cryptography.DataProtectionScope.LocalMachine));
        }

        internal static void AddKeySettings(System.Collections.Generic.Dictionary<string, string> changes, string key)
        {
            string user = Seal(key, System.Security.Cryptography.DataProtectionScope.CurrentUser);
            string machine = Seal(key, System.Security.Cryptography.DataProtectionScope.LocalMachine);
            if (key.Length > 0 && (user.Length == 0 || machine.Length == 0))
                throw new InvalidOperationException("Tailscale 키를 보호하여 저장할 수 없습니다.");
            changes["tskey"] = user;
            changes["tskeym"] = machine;
            // 서비스가 쓰는 신원을 UI에서 지우면 실행 중인 프로세스와 충돌한다.
            // 키와 의도를 함께 저장하고 실제 터널 소유자가 재연결 직전에 정리한다.
            if (LoadKey().Length > 0 && key != LoadKey()) changes["tsreset"] = Guid.NewGuid().ToString("N");
        }

        /// <summary>
        /// 이 PC 의 장비 신원을 버린다. 다음에 열 때 새 이름으로 새로 등록한다.
        /// 공개 주소가 바뀌므로, 바뀌었다는 것을 반드시 사람에게 알려야 한다.
        /// </summary>
        internal static void ResetIdentity()
        {
            Stop();
            KillOrphans();

            try
            {
                string dir = StateDir();
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
            catch { }

            // 예전 판이 쓰던 계정 폴더도 같이 지운다.
            // 안 그러면 다음에 StateDir() 이 그것을 다시 옮겨 와서 옛 신원이 되살아난다.
            try
            {
                string old = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "RemoteControl", "tsnet");

                if (Directory.Exists(old)) Directory.Delete(old, true);
            }
            catch { }

            Config.Set("tsnode", "");
        }

        private static string Seal(string key, System.Security.Cryptography.DataProtectionScope scope)
        {
            if (string.IsNullOrEmpty(key)) return "";

            try
            {
                byte[] p = System.Security.Cryptography.ProtectedData.Protect(
                    Encoding.UTF8.GetBytes(key), null, scope);
                return Convert.ToBase64String(p);
            }
            catch { return ""; }
        }

        private static string Open(string enc, System.Security.Cryptography.DataProtectionScope scope)
        {
            if (string.IsNullOrEmpty(enc)) return "";

            try
            {
                byte[] p = System.Security.Cryptography.ProtectedData.Unprotect(
                    Convert.FromBase64String(enc), null, scope);
                return Encoding.UTF8.GetString(p);
            }
            catch { return ""; }
        }

        internal static string LoadKey()
        {
            string k = Open(Config.Get("tskey"),
                            System.Security.Cryptography.DataProtectionScope.CurrentUser);
            if (!string.IsNullOrEmpty(k)) return k;

            return Open(Config.Get("tskeym"),
                        System.Security.Cryptography.DataProtectionScope.LocalMachine);
        }

        /// <summary>
        /// 예전 설정에는 내 계정으로 잠근 것만 있다. 서비스가 쓸 수 있도록
        /// 기계용 사본을 채워 둔다 (창을 띄운 쪽에서 한 번 불러 주면 된다).
        /// </summary>
        internal static void EnsureMachineKey()
        {
            if (!string.IsNullOrEmpty(Config.Get("tskeym"))) return;

            string k = Open(Config.Get("tskey"),
                            System.Security.Cryptography.DataProtectionScope.CurrentUser);
            if (string.IsNullOrEmpty(k)) return;

            Config.Set("tskeym", Seal(k, System.Security.Cryptography.DataProtectionScope.LocalMachine));
        }
        internal static string CurrentUrl { get { return url ?? ""; } }

        /// <summary>락을 잡지 않는다. 개통하는 동안에도 창이 멈추면 안 된다.</summary>
        internal static bool IsAlive()
        {
            Process p = proc;
            if (p == null || string.IsNullOrEmpty(url)) return false;

            try { return !p.HasExited; }
            catch { return false; }
        }

        /// <summary>공개 주소를 연다. 실패하면 null (사유는 LastError).</summary>
        internal static string Start(int port)
        { return Start(port, delegate { return false; }); }

        internal static string Start(int port, Func<bool> canceled)
        {
            lock (gate)
            {
                LastError = "";
                opening = true;

                try { return StartLocked(port, canceled); }
                finally { opening = false; }
            }
        }

        private static string StartLocked(int port, Func<bool> canceled)
        {
            {
                if (proc != null && !proc.HasExited && !string.IsNullOrEmpty(url)) return url;
                Stop();

                // 여기부터 세대를 센다. 위의 Stop() 은 우리가 부른 것이라 세지 않는다.
                int gen = stopGen;

                string key = LoadKey();
                if (string.IsNullOrEmpty(key)) { LastError = "Tailscale 인증 키가 없습니다."; return null; }

                string exe = Extract();
                if (exe == null) return null;

                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo
                    {
                        FileName = exe,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };

                    // 키는 명령줄이 아니라 환경변수로. 명령줄은 같은 PC 의 다른 프로세스에서 보인다.
                    psi.EnvironmentVariables["TS_AUTHKEY"] = PrepareKey(key);
                    psi.EnvironmentVariables["BACKEND"] = "http://127.0.0.1:" + port;
                    psi.EnvironmentVariables["TS_HOSTNAME"] = NodeName();
                    psi.EnvironmentVariables["TS_STATEDIR"] = StateDir();
                    TailscaleMcp.ConfigureProcess(psi);

                    Process p = new Process { StartInfo = psi };

                    string got = null, err = null;
                    AutoResetEvent done = new AutoResetEvent(false);

                    DataReceivedEventHandler onLine = delegate (object s, DataReceivedEventArgs e)
                    {
                        if (string.IsNullOrEmpty(e.Data)) return;

                        if (e.Data.StartsWith("FUNNEL_URL=")) { got = e.Data.Substring(11).Trim(); done.Set(); }
                        else if (e.Data.StartsWith("MCP_URL=") && gen == stopGen) TailscaleMcp.Address(e.Data.Substring(8).Trim());
                        else if (e.Data.StartsWith("MCP_ERROR=") && gen == stopGen) TailscaleMcp.Error("MCP 터널 연결 대기 · 자동 재시도 중");
                        else if (e.Data.StartsWith("TSPROXY_ERROR="))
                        {
                            err = e.Data.Substring(14).Trim().Replace("\\n", "\r\n");
                            done.Set();
                        }
                    };

                    p.OutputDataReceived += onLine;
                    p.ErrorDataReceived += onLine;

                    p.Start();
                    Interop.Adopt(p);          // 본체가 죽으면 커널이 같이 정리한다
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();

                    // 노드 등록 + Funnel 개통 + DNS/인증서 확인까지 기다린다.
                    Stopwatch timer = Stopwatch.StartNew();
                    bool signaled = false;
                    while (!(signaled = done.WaitOne(100)) && timer.ElapsedMilliseconds < 150000)
                    {
                        if (gen != stopGen || canceled() || p.HasExited) break;
                    }
                    if (gen != stopGen || canceled())
                    {
                        LastError = "여는 도중에 닫혔습니다.";
                        try { if (!p.HasExited) p.Kill(); } catch { }
                        return null;
                    }
                    if (!signaled)
                    {
                        LastError = "터널 연결 시간이 초과되었습니다.";
                        try { if (!p.HasExited) p.Kill(); } catch { }
                        return null;
                    }

                    if (!string.IsNullOrEmpty(err) || string.IsNullOrEmpty(got))
                    {
                        LastError = string.IsNullOrEmpty(err) ? "터널 연결에 실패했습니다." : err;
                        try { if (!p.HasExited) p.Kill(); } catch { }
                        return null;
                    }

                    // 여는 40초 사이에 누가 닫으라고 했다. 그 말이 먼저다.
                    // 여기서 그냥 proc/url 을 채우면, 닫은 줄 아는데 중계는 살아 있는
                    // 상태가 된다 (창은 "닫힘" 인데 밖에서는 들어와진다).
                    if (gen != stopGen || canceled())
                    {
                        try { if (!p.HasExited) p.Kill(); } catch { }
                        LastError = "여는 도중에 닫혔습니다.";
                        return null;
                    }

                    proc = p;
                    url = got;
                    return url;
                }
                catch (Exception ex)
                {
                    LastError = "터널 실행 중 오류: " + ex.Message;
                    return null;
                }
            }
        }

        /// <summary>
        /// 터널을 접는다. 락을 잡지 않는다.
        ///
        /// Start 는 개통이 끝날 때까지(최대 150초) 락을 쥔다. 여기서 그 락을 기다리면
        /// 부른 쪽이 그만큼 멈춘다. 창의 1초 타이머와 [외부망 닫기] 는 UI 스레드에서
        /// 이걸 부르므로 창이 통째로 얼어붙는다. 지킴이가 뒤에서 다시 여는 지금은
        /// 그 겹침이 언제든 일어난다.
        ///
        /// 그래서 세대 번호만 올려 두고 바로 돌아온다. 여는 중이던 Start 는 끝날 때
        /// 번호가 달라진 것을 보고 자기가 띄운 중계를 스스로 접는다.
        /// </summary>
        internal static void Stop()
        {
            stopGen++;

            Process p = proc;
            proc = null;
            url = null;

            if (p != null)
            {
                try { if (!p.HasExited) p.Kill(); }
                catch { }
            }
        }

        /// <summary>
        /// Trust credential(OAuth, tskey-client-...) 은 만료가 없지만 태그가 필수다.
        /// 일반 auth key(tskey-auth-...) 는 그대로 쓴다.
        /// </summary>
        private static string PrepareKey(string key)
        {
            key = (key ?? "").Trim();
            if (!key.StartsWith("tskey-client-", StringComparison.OrdinalIgnoreCase)) return key;
            if (key.IndexOf('?') >= 0) return key;
            return key + "?ephemeral=true&preauthorized=true";
        }

        /// <summary>
        /// 공개 주소에 쓰일 이름. 한 번 정하면 계속 쓴다.
        /// 이름이 바뀌면 공개 DNS 이름도 새로 생기고, 그것이 퍼지기 전에 누가 조회하면
        /// 통신사 DNS 가 "없는 이름" 을 캐시해 한동안 아무도 못 들어온다.
        /// PC 이름은 쓰지 않는다 - 주소에 그대로 드러나 실명이 노출된다.
        /// </summary>
        private static string NodeName()
        {
            string saved = Sanitize(Config.Get("tsnode"));
            if (!string.IsNullOrEmpty(saved)) return saved;

            string made = "rc-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            Config.Set("tsnode", made);
            return made;
        }

        internal static string Sanitize(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";

            StringBuilder sb = new StringBuilder();
            foreach (char c in raw.Trim().ToLowerInvariant())
            {
                if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')) sb.Append(c);
                else if (c == '-' || c == '_' || c == ' ' || c == '.') sb.Append('-');
            }

            string n = sb.ToString().Trim('-');
            while (n.Contains("--")) n = n.Replace("--", "-");
            return n.Length > 40 ? n.Substring(0, 40).Trim('-') : n;
        }

        /// <summary>
        /// 장비 신원을 보관할 곳. FileSharer 와 섞이면 안 되므로 따로 둔다.
        /// (같은 신원을 두 프로그램이 동시에 쓰면 서로 밀어낸다)
        /// </summary>
        private static string StateDir()
        {
            // 창으로 띄울 때는 내 계정, 서비스로 띄울 때는 SYSTEM 이다.
            // 계정마다 다른 폴더를 쓰면 장비 신원이 갈라져 공개 주소가 매번 새로 생긴다.
            // 그래서 계정과 무관한 공용 폴더에 둔다.
            string root;
            try { root = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData); }
            catch { root = Path.GetTempPath(); }

            if (string.IsNullOrEmpty(root)) root = Path.GetTempPath();

            string dir = Path.Combine(root, "RemoteControl", "tsnet");
            try { Directory.CreateDirectory(dir); }
            catch { }

            MigrateOldState(dir);
            return dir;
        }

        /// <summary>
        /// 예전에는 계정 폴더에 두었다. 거기 있는 신원을 그대로 옮겨 온다.
        /// 옮기지 않으면 노드가 새로 생겨 공개 주소가 바뀌고, 새 이름이 퍼지기 전까지
        /// 한동안 아무도 접속하지 못한다.
        /// </summary>
        private static void MigrateOldState(string dir)
        {
            try
            {
                if (Directory.GetFileSystemEntries(dir).Length > 0) return;

                string old = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "RemoteControl", "tsnet");

                if (!Directory.Exists(old)) return;

                // 인증서와 프로필은 하위 폴더에 있다. 파일만 옮기면 신원이 반쪽이 되어
                // 노드가 새로 만들어지고 주소 끝에 -1 이 붙는다.
                CopyTree(old, dir);
            }
            catch { }
        }

        private static void CopyTree(string from, string to)
        {
            try
            {
                Directory.CreateDirectory(to);

                foreach (string f in Directory.GetFiles(from))
                {
                    try { File.Copy(f, Path.Combine(to, Path.GetFileName(f)), true); }
                    catch { }
                }

                foreach (string d in Directory.GetDirectories(from))
                    CopyTree(d, Path.Combine(to, Path.GetFileName(d)));
            }
            catch { }
        }

        // ---------------------------------------------------------------- 중계 프로그램 두는 곳

        // v1.4.0 까지 쓰던 자리. 새로 풀지는 않고, 치울 때만 본다 (아래 KillOrphans 주석).
        private static readonly string OldTempDir =
            Path.Combine(Path.GetTempPath(), "RemoteControlTs");

        /// <summary>
        /// 중계 프로그램을 둘 곳. 창(사용자 계정)과 대리 프로세스(SYSTEM)가 같은 파일을 본다.
        ///
        /// 왜 %TEMP% 를 떠났나 - 오탐 때문이다.
        ///   "실행 파일을 %TEMP% 에 풀어서 실행" 은 드로퍼(dropper)의 교과서적 행동이라
        ///   백신의 기계학습이 가장 크게 보는 신호 가운데 하나다. 프로그램이 실제로 무슨
        ///   일을 하는지와 무관하게 그 모양이면 잡힌다. Trojan:Win32/Bearfoos.B!ml 로
        ///   실제로 잡혔다. 설정 폴더 옆 고정된 자리에 두면 "설치된 프로그램" 의 모양이 된다.
        ///
        /// 왜 설정 폴더 바로 밑이 아니라 bin 인가 - 권한 때문이다.
        ///   설정 폴더(ProgramData\RemoteControl)에는 창이 비밀번호를 저장해야 해서
        ///   **사용자 쓰기 권한이 있다.** 그런데 이 중계 프로그램을 실제로 실행하는 것은
        ///   대리 프로세스(SYSTEM)다. 사용자가 바꿔칠 수 있는 자리에 SYSTEM 이 실행할
        ///   파일을 두면 그것이 곧 권한 상승 구멍이다. 오탐을 피하려다 진짜 구멍을 내는
        ///   셈이 된다. 그래서 bin 만 상속을 끊고 SYSTEM·관리자만 쓰게 잠근다(Harden).
        /// </summary>
        private static string SharedBin()
        {
            try
            {
                string root = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
                if (string.IsNullOrEmpty(root)) return null;

                return Path.Combine(root, "RemoteControl", "bin");
            }
            catch { return null; }
        }

        /// <summary>
        /// 공용 자리에 못 쓸 때 물러날 곳.
        ///
        /// 언제 그런가 - SYSTEM 이 이미 bin 을 잠가 둔 PC 에서 사용자 권한으로 창을 띄워
        /// 직접 터널을 열 때다. %TEMP% 로 돌아가면 오탐 신호가 그대로 살아나므로,
        /// 계정 폴더의 고정된 자리로 간다. 여기는 그 계정만 쓰므로 남을 해칠 것이 없다.
        /// </summary>
        private static string UserBin()
        {
            try
            {
                string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrEmpty(root)) return null;

                return Path.Combine(root, "RemoteControl", "bin");
            }
            catch { return null; }
        }

        /// <summary>우리가 중계 프로그램을 두는(또는 예전에 두던) 자리 전부.</summary>
        internal static string[] BinDirs()
        {
            return new string[] { SharedBin(), UserBin(), OldTempDir };
        }

        private static bool Elevated()
        {
            try
            {
                using (WindowsIdentity me = WindowsIdentity.GetCurrent())
                    return me.IsSystem || new WindowsPrincipal(me).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        /// <summary>
        /// SYSTEM 이 실행할 파일이 놓인 폴더를 잠근다. 상속을 끊고 SYSTEM·관리자만 쓰게 한다.
        ///
        /// 이걸 안 하면 보통 권한 사용자가 tsproxy.exe 를 아무것으로나 바꿔칠 수 있고,
        /// 그것을 대리 프로세스가 SYSTEM 으로 그대로 실행한다. 설정 폴더에는 사용자 쓰기를
        /// 주면서 프로그램 폴더에는 절대 주지 않는 것과 같은 이유다.
        ///
        /// **권한이 없으면 건드리지 않는다.** 보통 권한으로 만든 폴더를 여기서 잠가 버리면
        /// 자기가 자기 폴더에 못 쓰게 된다 (단독 실행 모드가 그 자리에서 멈춘다).
        /// 잠글 수 없는 상황이면 SYSTEM 서비스도 없다는 뜻이라 잠글 이유도 없다.
        /// </summary>
        private static void Harden(string dir)
        {
            if (!Elevated()) return;

            try
            {
                DirectorySecurity sec = new DirectorySecurity();

                // 상속을 끊지 않으면 위(ProgramData)에서 내려온 "사용자 쓰기" 가 그대로 남는다.
                sec.SetAccessRuleProtection(true, false);

                SecurityIdentifier system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
                SecurityIdentifier admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
                SecurityIdentifier users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);

                InheritanceFlags inh = InheritanceFlags.ObjectInherit | InheritanceFlags.ContainerInherit;

                sec.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl,
                                                           inh, PropagationFlags.None, AccessControlType.Allow));
                sec.AddAccessRule(new FileSystemAccessRule(admins, FileSystemRights.FullControl,
                                                           inh, PropagationFlags.None, AccessControlType.Allow));

                // 사용자는 읽고 실행만. 바꿔치기는 못 한다.
                sec.AddAccessRule(new FileSystemAccessRule(users, FileSystemRights.ReadAndExecute,
                                                           inh, PropagationFlags.None, AccessControlType.Allow));

                Directory.SetAccessControl(dir, sec);
            }
            catch { }

            // 소유자도 가져온다. **권한만 잠그면 반만 잠근 것이다.**
            //   소유자는 자기 손으로 권한을 언제든 되돌릴 수 있다(암묵적 WRITE_DAC).
            //   보통 권한으로 만들어 둔 폴더를 나중에 SYSTEM 이 잠가도, 소유자가 그대로면
            //   그 사용자가 잠금을 풀고 tsproxy.exe 를 바꿔칠 수 있다. 잠근 의미가 없다.
            //
            // 따로 부르는 이유 - 권한이 모자라 여기서 실패해도 위에서 건 잠금은 살아야 한다.
            // 한 번에 묶어 걸면 소유자 변경이 실패할 때 잠금까지 같이 날아간다.
            try
            {
                DirectorySecurity own = new DirectorySecurity();
                own.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
                Directory.SetAccessControl(dir, own);
            }
            catch { }
        }

        /// <summary>
        /// 이 파일을 SYSTEM 이 실행해도 되는가 — 만든 이가 SYSTEM 이나 관리자인가.
        ///
        /// 폴더를 잠가도 **그 전에 놓인 파일의 소유자는 그대로 남는다.** 보통 권한으로
        /// 먼저 돌려 둔 PC 에 나중에 서비스를 깔면 딱 그 상태가 된다. 소유자는 자기 파일의
        /// 권한을 되돌릴 수 있으므로, 그대로 두면 SYSTEM 이 남이 바꿔 놓은 것을 실행한다.
        /// 그럴 때는 크기가 같아도 다시 쓴다.
        ///
        /// 잠글 권한이 없는 처지(단독 실행)에서는 따지지 않는다 - 어차피 SYSTEM 이 없다.
        /// </summary>
        private static bool Trusted(string path)
        {
            if (!Elevated()) return true;

            try
            {
                SecurityIdentifier sid =
                    File.GetAccessControl(path).GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;

                if (sid == null) return false;

                return sid.IsWellKnown(WellKnownSidType.LocalSystemSid) ||
                       sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid);
            }
            catch { return false; }
        }

        /// <summary>리소스에 든 gzip 을 푼다.</summary>
        private static byte[] Unpack()
        {
            using (Stream res = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName))
            {
                if (res == null)
                {
                    LastError = "내장 터널 중계 파일을 찾지 못했습니다.\r\n" +
                                "(빌드할 때 /resource:tsproxy.exe.gz 가 빠졌습니다)";
                    return null;
                }

                using (GZipStream gz = new GZipStream(res, CompressionMode.Decompress))
                using (MemoryStream ms = new MemoryStream())
                {
                    byte[] buf = new byte[256 * 1024];
                    int n;
                    while ((n = gz.Read(buf, 0, buf.Length)) > 0) ms.Write(buf, 0, n);
                    return ms.ToArray();
                }
            }
        }

        /// <summary>한 자리에 놓아 본다. 못 쓰면 null - 부르는 쪽이 다음 자리로 간다.</summary>
        private static string Place(string dir, byte[] raw)
        {
            if (string.IsNullOrEmpty(dir)) return null;

            try { Directory.CreateDirectory(dir); }
            catch { return null; }

            // **잠그는 것을 쓰기 전에 한다.** 그래야 이 뒤로 놓는 파일이 잠긴 권한을
            // 물려받고 소유자도 관리자가 된다. 다 쓴 뒤에 잠그면 파일 소유자가 그대로
            // 남는데, 소유자는 언제든 스스로 권한을 되돌릴 수 있어 잠근 값이 없다.
            Harden(dir);

            string exe = Path.Combine(dir, "tsproxy.exe");

            // 이미 같은 것이 놓여 있으면 그대로 쓴다. 돌고 있으면 덮어쓸 수도 없고,
            // 시작할 때마다 22MB 를 다시 쓰면 디스크도 긁고 백신도 "실행 파일을 되풀이해
            // 떨군다" 로 본다. 다만 **믿을 만한 것일 때만** 그렇게 한다(Trusted).
            try
            {
                if (File.Exists(exe) && new FileInfo(exe).Length == raw.Length && Trusted(exe)) return exe;
            }
            catch { }

            // 지난번에 옛것이 돌고 있어 제 이름을 못 얻은 찌꺼기를 치운다.
            // 안 치우면 시작할 때마다 22MB 짜리가 하나씩 쌓인다.
            try
            {
                foreach (string junk in Directory.GetFiles(dir, "tsproxy.exe.*.tmp"))
                    try { File.Delete(junk); } catch { }
            }
            catch { }

            string tmp = exe + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";

            try { File.WriteAllBytes(tmp, raw); }
            catch { return null; }          // 여기는 못 쓴다. 다음 자리로.

            try
            {
                if (File.Exists(exe)) File.Delete(exe);
                File.Move(tmp, exe);
            }
            catch { return tmp; }           // 옛것이 돌고 있으면 임시 이름 그대로 쓴다

            return exe;
        }

        /// <summary>exe 안에 gzip 으로 넣어 둔 중계 프로그램을 꺼내 놓는다.</summary>
        private static string Extract()
        {
            try
            {
                byte[] raw = Unpack();
                if (raw == null) return null;        // LastError 는 Unpack 이 채웠다

                string got = Place(SharedBin(), raw);
                if (got != null) return got;

                got = Place(UserBin(), raw);
                if (got != null) return got;

                LastError = "터널 중계 파일을 둘 곳이 없습니다.\r\n" +
                            "(" + SharedBin() + " 와 " + UserBin() + " 둘 다 쓸 수 없습니다)";
                return null;
            }
            catch (Exception ex)
            {
                LastError = "터널 중계 파일을 준비하지 못했습니다: " + ex.Message;
                return null;
            }
        }

        /// <summary>
        /// 예전에 남은 고아 중계 프로세스를 치운다.
        ///
        /// 이름만 보고 죽이면 안 된다. 옆 프로젝트(FileSharer)도 tsproxy 라는 같은 이름을
        /// 쓴다. 실제로 남의 공유 터널을 두 번 끊었다. 우리가 놓은 자리의 것만 고른다.
        ///
        /// 옛 자리(%TEMP%)도 함께 본다. v1.4.0 까지는 거기에 풀었으므로, 판을 올린 PC 에는
        /// 그때 띄운 것이 아직 돌고 있을 수 있다. 프로세스를 접은 뒤 그 폴더도 지운다 -
        /// %TEMP% 에 남은 exe 가 바로 백신이 잡던 그 모양이라, 안 지우면 판을 올려도
        /// 오탐 신호가 그대로 남는다.
        /// </summary>
        internal static void KillOrphans()
        {
            string[] dirs = BinDirs();

            try
            {
                foreach (Process p in Process.GetProcessesByName("tsproxy"))
                {
                    try
                    {
                        if (p.MainModule == null) continue;

                        string path = p.MainModule.FileName;

                        foreach (string d in dirs)
                        {
                            if (string.IsNullOrEmpty(d)) continue;
                            if (!path.StartsWith(d, StringComparison.OrdinalIgnoreCase)) continue;

                            p.Kill();
                            break;
                        }
                    }
                    catch { }
                    finally { try { p.Dispose(); } catch { } }
                }
            }
            catch { }

            // 옛 자리를 치운다. 돌고 있던 것을 방금 접었으므로 이제 지워진다.
            try { if (Directory.Exists(OldTempDir)) Directory.Delete(OldTempDir, true); }
            catch { }
        }

    }
}

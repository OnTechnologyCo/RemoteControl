// private-tunnel 허브와 호환되는 reverse SSH. SSH.NET의 EOF 제한을 피하려고 OpenSSH를 쓴다.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace RemoteControl
{
    internal sealed class SshSettings
    {
        internal string Host = "", Port = "", User = "", KeyFile = "", KnownHosts = "", RemotePort = "", Url = "";
        internal static SshSettings Load()
        {
            string[] v = Config.GetMany(new[] { "sshhost", "sshport", "sshuser", "sshkeyfile", "sshknownhosts", "sshremoteport", "sshurl" });
            return new SshSettings { Host = v[0], Port = v[1], User = v[2], KeyFile = v[3], KnownHosts = v[4], RemotePort = v[5], Url = v[6] };
        }
        internal string Validate(bool files)
        {
            if (string.IsNullOrEmpty(Host)) return "SSH 허브 주소를 설정하세요.";
            if (!Regex.IsMatch(Host, @"\A[a-zA-Z0-9][a-zA-Z0-9.:-]*\z") || Uri.CheckHostName(Host) == UriHostNameType.Unknown)
                return "SSH 허브에는 도메인 또는 IP만 입력하세요 (URL·포트 제외).";
            int p;
            if (!int.TryParse(Port, out p) || p < 1 || p > 65535) return "SSH 접속 포트는 1~65535로 설정하세요.";
            if (!int.TryParse(RemotePort, out p) || p < 1 || p > 65535) return "허브 수신 포트는 1~65535로 설정하세요.";
            if (!Regex.IsMatch(User, @"\A[a-zA-Z0-9_][a-zA-Z0-9_.\\@-]*\z")) return "SSH 계정을 설정하세요 (공백 제외).";
            if (!LocalFile(KeyFile) || !LocalFile(KnownHosts)) return "개인키와 known_hosts에 로컬 파일의 절대 경로를 지정하세요.";
            Uri u;
            if (!Uri.TryCreate(Url, UriKind.Absolute, out u) ||
                (u.Scheme != "https" && !(u.Scheme == "http" && u.IsLoopback)) ||
                u.UserInfo.Length != 0 || u.Query.Length != 0 || u.Fragment.Length != 0 || u.AbsolutePath != "/")
                return "브라우저 주소는 https://도메인[:포트]로 설정하세요 (경로 제외, 로컬 시험은 http 허용).";
            if (files)
            {
                if (!File.Exists(SshTunnel.Executable)) return "Windows OpenSSH 클라이언트가 없습니다. Windows 선택적 기능에서 설치하세요.";
                if (!File.Exists(KeyFile)) return "SSH 개인키 파일을 읽을 수 없습니다. 실행 계정의 권한을 확인하세요.";
                if (!File.Exists(KnownHosts)) return "검증한 서버 키가 담긴 known_hosts 파일을 읽을 수 없습니다.";
            }
            return "";
        }
        private static bool LocalFile(string path)
        {
            // SSH가 경로 안의 %, ~, ${...}를 확장하지 못하게 한다. UNC는 서비스 자격 증명도 유출한다.
            return !string.IsNullOrEmpty(path) && Regex.IsMatch(path, @"\A[a-zA-Z]:[\\/]") &&
                path.IndexOfAny(new[] { '"', '\r', '\n', '\0', '%', '$', '~', '*', '?' }) < 0 &&
                path.Substring(2).IndexOf(':') < 0;
        }
    }

    internal static class SshTunnel
    {
        private static readonly object stateGate = new object();
        private static readonly object startGate = new object();
        private static Process proc;
        private static volatile bool opening, ready, blocked;
        private static volatile string url = "", error = "";
        private static int generation;
        internal static string Executable { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "OpenSSH", "ssh.exe"); } }
        internal static bool IsOpening { get { return opening; } }
        internal static bool RetryBlocked { get { return blocked; } }
        internal static string LastError { get { return error; } }
        internal static string CurrentUrl { get { return url; } }
        internal static bool IsAlive()
        {
            lock (stateGate) { try { return ready && proc != null && !proc.HasExited; } catch { return false; } }
        }
        internal static void ClearFailure() { lock (stateGate) { blocked = false; error = ""; } }
        private static void Fail(int gen, Func<bool> canceled, string message, bool permanent)
        {
            lock (stateGate)
            {
                if (gen != generation || canceled()) { error = "여는 도중에 닫혔습니다."; return; }
                error = message; blocked = permanent;
            }
        }

        // .NET Framework에는 ArgumentList가 없다. 셸 없이 Windows argv 규칙으로 각각 인용한다.
        internal static string Quote(string value)
        {
            StringBuilder b = new StringBuilder("\"");
            int slashes = 0;
            foreach (char c in value)
            {
                if (c == '\\') { slashes++; continue; }
                b.Append('\\', c == '"' ? slashes * 2 + 1 : slashes);
                b.Append(c); slashes = 0;
            }
            b.Append('\\', slashes * 2).Append('"');
            return b.ToString();
        }
        internal static string Arguments(SshSettings s, int localPort)
        {
            if (localPort < 1 || localPort > 65535) throw new ArgumentOutOfRangeException("localPort");
            List<string> args = new List<string> { "-N", "-T", "-v", "-F", "NUL", "-p", s.Port, "-l", s.User, "-i", s.KeyFile,
                "-R", "127.0.0.1:" + s.RemotePort + ":127.0.0.1:" + localPort };
            foreach (string option in new[] { "BatchMode=yes", "IdentitiesOnly=yes", "IdentityAgent=none",
                "StrictHostKeyChecking=yes", "GlobalKnownHostsFile=NUL", "UpdateHostKeys=no", "CheckHostIP=no",
                "ExitOnForwardFailure=yes", "ServerAliveInterval=15", "ServerAliveCountMax=3",
                "ConnectTimeout=10", "ConnectionAttempts=1", "PasswordAuthentication=no",
                "ForwardAgent=no", "ForwardX11=no", "PermitLocalCommand=no", "ProxyCommand=none", "ControlMaster=no",
                "UserKnownHostsFile=\"" + s.KnownHosts.Replace('\\', '/') + "\"" })
            { args.Add("-o"); args.Add(option); }
            args.Add(s.Host);
            return string.Join(" ", args.ConvertAll(Quote).ToArray());
        }

        internal static bool PermanentFailure(string line)
        {
            string s = line.ToLowerInvariant();
            return s.Contains("host key verification failed") || s.Contains("remote host identification has changed") ||
                s.Contains("permission denied") || s.Contains("remote port forwarding failed") ||
                s.Contains("bad permissions") || s.Contains("unprotected private key") || s.Contains("load key") ||
                s.Contains("no such identity") || s.Contains("bad configuration option") ||
                s.Contains("no matching host key type") || s.Contains("no matching key exchange");
        }

        internal static string Start(int localPort, Func<bool> canceled)
        {
            int gen;
            lock (stateGate) gen = generation;
            lock (startGate)
            {
                Process p = null;
                bool keep = false;
                opening = true;
                try
                {
                    SshSettings s = SshSettings.Load();
                    string validation = s.Validate(true);
                    if (validation.Length != 0) { Fail(gen, canceled, validation, true); return null; }
                    p = new Process { StartInfo = new ProcessStartInfo {
                        FileName = Executable, Arguments = Arguments(s, localPort), UseShellExecute = false,
                        CreateNoWindow = true, RedirectStandardError = true, RedirectStandardInput = true
                    } };
                    // 숨겨진 서비스에서 askpass 실행/암호 대화상자가 생기지 않게 한다.
                    p.StartInfo.EnvironmentVariables.Remove("SSH_ASKPASS");
                    p.StartInfo.EnvironmentVariables["SSH_ASKPASS_REQUIRE"] = "never";
                    bool acknowledged = false, permanent = false;
                    object lines = new object();
                    string diagnostic = "";
                    p.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e) {
                        if (e.Data == null) return;
                        lock (lines)
                        {
                            if (e.Data.Contains("remote forward success for: listen 127.0.0.1:" + s.RemotePort + ",")) acknowledged = true;
                            if (PermanentFailure(e.Data)) { permanent = true; diagnostic = e.Data; }
                            else if (!e.Data.StartsWith("debug") && !permanent) diagnostic = e.Data;
                        }
                    };
                    lock (stateGate)
                    {
                        if (gen != generation || canceled()) { error = "여는 도중에 닫혔습니다."; return null; }
                        p.Start();
                        proc = p;
                        Interop.Adopt(p);
                    }
                    p.StandardInput.Close();
                    p.BeginErrorReadLine();
                    Stopwatch timer = Stopwatch.StartNew();
                    while (timer.ElapsedMilliseconds < 20000)
                    {
                        lock (stateGate)
                        {
                            if (gen != generation || canceled()) { error = "여는 도중에 닫혔습니다."; return null; }
                            lock (lines)
                            {
                                if (acknowledged && !permanent && !p.HasExited)
                                { url = s.Url.TrimEnd('/'); ready = true; keep = true; return url; }
                            }
                        }
                        if (p.HasExited) { p.WaitForExit(); break; }
                        Thread.Sleep(50);
                    }
                    lock (lines)
                    {
                        Fail(gen, canceled, diagnostic.Length > 0 ? "SSH 연결 실패: " + diagnostic : "SSH 터널 준비 시간이 초과되었습니다.", permanent);
                    }
                    return null;
                }
                catch (Exception ex) { Fail(gen, canceled, "SSH 실행 실패: " + ex.Message, true); return null; }
                finally
                {
                    if (!keep && p != null)
                    {
                        lock (stateGate) { if (proc == p) { proc = null; ready = false; url = ""; } }
                        Kill(p);
                        p.Dispose();
                    }
                    opening = false;
                }
            }
        }

        internal static void Stop()
        {
            Process p;
            bool dispose;
            lock (stateGate) { generation++; p = proc; dispose = ready; proc = null; ready = false; url = ""; }
            // 이름으로 찾으면 다른 프로그램의 SSH 연결까지 끊는다. 생성한 핸들만 사용한다.
            if (p != null) { Kill(p); if (dispose) p.Dispose(); }
        }
        private static void Kill(Process p) { try { if (!p.HasExited) p.Kill(); } catch { } }
    }
}

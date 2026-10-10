// SSHTunnel 1.3 웹 API를 쓴다. 고정 reverse 포트로는 랜덤 웹 주소를 발급받을 수 없다.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace RemoteControl
{
    internal sealed class SshSettings
    {
        internal string Host = "", Port = "2222", User = "", AccessKey = "", Fingerprint = "", Name = "";
        internal static SshSettings Load()
        {
            string[] v = Config.GetMany(new[] { "sshhost", "sshport", "sshuser", "sshfingerprint", "sshname", "sshaccesskey", "sshaccesskeym" });
            return new SshSettings { Host = v[0], Port = v[1].Length == 0 ? "2222" : v[1], User = v[2],
                Fingerprint = v[3], Name = v[4].Length == 0 ? Environment.MachineName + " 원격제어" : v[4], AccessKey = Open(v[5], v[6]) };
        }
        private static string Open(string user, string machine)
        {
            string[] enc = { user, machine }; DataProtectionScope[] scopes = { DataProtectionScope.CurrentUser, DataProtectionScope.LocalMachine };
            for (int i = 0; i < enc.Length; i++)
                try { if (enc[i].Length > 0) return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(enc[i]), null, scopes[i])); }
                catch { }
            return "";
        }
        internal static void AddKeySettings(Dictionary<string, string> changes, string key)
        {
            // 창은 사용자, 잠금 화면 대리 프로세스는 SYSTEM이다. 두 실행 계정에서 복원한다.
            changes["sshaccesskey"] = key.Length == 0 ? "" : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(key), null, DataProtectionScope.CurrentUser));
            changes["sshaccesskeym"] = key.Length == 0 ? "" : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(key), null, DataProtectionScope.LocalMachine));
        }
        internal string Validate(bool runtime)
        {
            if (Host.Length == 0) return "SSHTunnel 서버 주소를 설정하세요.";
            if (!Regex.IsMatch(Host, @"\A[a-zA-Z0-9][a-zA-Z0-9.:-]*\z") || Uri.CheckHostName(Host) == UriHostNameType.Unknown)
                return "서버 주소에는 도메인 또는 IP만 입력하세요 (URL·포트 제외).";
            int p;
            if (!int.TryParse(Port, out p) || p < 1 || p > 65535) return "SSH 접속 포트는 1~65535로 설정하세요.";
            if (!Regex.IsMatch(User, @"\A[a-zA-Z0-9_.-]{1,64}\z")) return "SSHTunnel에서 발급한 계정을 입력하세요.";
            if (!Regex.IsMatch(AccessKey, @"\A[0-9a-f]{64}\z")) return "SSHTunnel에서 발급한 64자리 인증키를 입력하세요.";
            if (!Regex.IsMatch(Fingerprint, @"\ASHA256:[A-Za-z0-9+/]{43}\z")) return "서버 지문은 SHA256:으로 시작하는 발급값을 입력하세요.";
            if (Name.Length == 0 || Encoding.UTF8.GetByteCount(Name) > 160 || Regex.IsMatch(Name, @"[\x00-\x1f\x7f]"))
                return "터널 이름은 제어문자 없이 UTF-8 160바이트 이내로 입력하세요.";
            return "";
        }
        internal string Request(int localPort)
        {
            if (localPort < 1 || localPort > 65535) throw new ArgumentOutOfRangeException("localPort");
            string server = (Host.IndexOf(':') >= 0 ? "[" + Host + "]" : Host) + ":" + Port;
            return "{\"server\":\"" + Json.Escape(server) + "\",\"user\":\"" + Json.Escape(User) +
                "\",\"accessKey\":\"" + Json.Escape(AccessKey) + "\",\"fingerprint\":\"" + Json.Escape(Fingerprint) +
                "\",\"target\":\"127.0.0.1:" + localPort + "\",\"name\":\"" + Json.Escape(Name) + "\"" + PrivateMcp.RequestFields() + "}";
        }
    }

    internal static class SshTunnel
    {
        private static readonly object stateGate = new object(), startGate = new object();
        private static Process proc;
        private static volatile bool opening, ready, blocked;
        private static volatile string url = "", error = "";
        private static int generation;
        internal static bool IsOpening { get { return opening; } }
        internal static bool RetryBlocked { get { return blocked; } }
        internal static string LastError { get { return error; } }
        internal static string CurrentUrl { get { return url; } }
        internal static bool IsAlive()
        { lock (stateGate) { try { return ready && proc != null && !proc.HasExited; } catch { return false; } } }
        internal static void ClearFailure() { lock (stateGate) { blocked = false; error = ""; } }
        internal static bool ValidUrl(string address)
        {
            Uri u;
            return Uri.TryCreate(address, UriKind.Absolute, out u) && (u.Scheme == "https" || (u.Scheme == "http" && u.IsLoopback)) &&
                u.UserInfo.Length == 0 && u.Query.Length == 0 && u.Fragment.Length == 0 && u.AbsolutePath == "/" &&
                u.IsDefaultPort && u.HostNameType == UriHostNameType.Dns && Regex.IsMatch(address, @"\Ahttps?://[a-zA-Z0-9.-]+/?\z");
        }
        private static void Fail(int gen, Func<bool> canceled, string message, bool permanent)
        {
            lock (stateGate)
            { if (gen != generation || canceled()) { error = "여는 도중에 닫혔습니다."; return; } error = message; blocked = permanent; }
        }
        internal static string Start(int localPort, Func<bool> canceled)
        {
            int gen; lock (stateGate) gen = generation;
            lock (startGate)
            {
                Process p = null; bool keep = false;
                opening = true;
                try
                {
                    lock (stateGate) { if (gen != generation || canceled()) return null; if (IsAlive()) return url; }
                    SshSettings s = SshSettings.Load();
                    string validation = s.Validate(true);
                    if (validation.Length != 0) { Fail(gen, canceled, validation, true); return null; }
                    string executable = SshHelper.Prepare();
                    p = new Process { StartInfo = new ProcessStartInfo { FileName = executable, UseShellExecute = false,
                        CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                        StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 } };
                    string accepted = "", diagnostic = ""; bool permanent = false;
                    object lines = new object();
                    p.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e) {
                        if (string.IsNullOrEmpty(e.Data)) return;
                        var value = Json.Parse(e.Data);
                        lock (lines)
                        {
                            if (Json.Str(value, "event") == "ready")
                            {
                                string candidate = Json.Str(value, "url") ?? "";
                                if (ValidUrl(candidate)) accepted = candidate.TrimEnd('/');
                                else { permanent = true; diagnostic = "SSHTunnel이 올바른 HTTPS 웹 주소를 반환하지 않았습니다."; }
                            }
                            else if (Json.Str(value, "event") == "error" || Json.Str(value, "event") == "closed")
                            { diagnostic = Json.Str(value, "message") ?? "터널 연결이 종료되었습니다."; permanent = Json.Bool(value, "permanent", false); }
                        }
                    };
                    p.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e) {
                        if (e.Data != null) lock (lines) { if (diagnostic.Length == 0) diagnostic = e.Data; }
                    };
                    lock (stateGate)
                    {
                        if (gen != generation || canceled()) { error = "여는 도중에 닫혔습니다."; return null; }
                        p.Start(); proc = p; Interop.Adopt(p);
                    }
                    p.BeginOutputReadLine(); p.BeginErrorReadLine();
                    // 키는 명령줄·환경변수·임시 설정 파일에 남기지 않고 자식의 stdin으로만 넘긴다.
                    byte[] request = Encoding.UTF8.GetBytes(s.Request(localPort));
                    try { p.StandardInput.BaseStream.Write(request, 0, request.Length); p.StandardInput.BaseStream.Flush(); }
                    finally { Array.Clear(request, 0, request.Length); p.StandardInput.Close(); }
                    Stopwatch timer = Stopwatch.StartNew();
                    while (timer.ElapsedMilliseconds < 30000)
                    {
                        lock (stateGate)
                        {
                            if (gen != generation || canceled()) { error = "여는 도중에 닫혔습니다."; return null; }
                            lock (lines)
                                if (accepted.Length > 0 && !permanent && !p.HasExited)
                                { url = accepted; ready = true; keep = true; }
                        }
                        if (keep) { PrivateMcp.Address(accepted); return accepted; }
                        if (p.HasExited) { p.WaitForExit(); break; }
                        Thread.Sleep(50);
                    }
                    lock (lines)
                    {
                        diagnostic = diagnostic.Replace(s.AccessKey, "[인증키]");
                        if (diagnostic.Length > 1024) diagnostic = diagnostic.Substring(0, 1024);
                        Fail(gen, canceled, diagnostic.Length > 0 ? "SSHTunnel 연결 실패: " + diagnostic : "SSHTunnel 연결 준비 시간이 초과되었습니다.", permanent);
                    }
                    return null;
                }
                catch (Exception ex) { Fail(gen, canceled, "SSHTunnel 실행 실패: " + ex.Message, true); return null; }
                finally
                {
                    if (!keep && p != null)
                    { lock (stateGate) { if (proc == p) { proc = null; ready = false; url = ""; } } Kill(p); p.Dispose(); }
                    opening = false;
                }
            }
        }
        internal static void Stop()
        {
            Process p; bool dispose;
            lock (stateGate) { generation++; p = proc; dispose = ready; proc = null; ready = false; url = ""; }
            // 우리가 생성한 자식만 닫는다. 서버나 다른 프로그램의 SSH 연결은 찾지 않는다.
            if (p != null) { Kill(p); if (dispose) p.Dispose(); }
        }
        private static void Kill(Process p) { try { if (!p.HasExited) p.Kill(); } catch { } }
    }
}

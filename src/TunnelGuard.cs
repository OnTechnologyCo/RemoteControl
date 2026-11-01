// 외부망 지킴이. 터널이 살아 있는지 계속 확인하고, 죽었으면 다시 연다.
//
// 왜 필요했나
// -----------
// 터널을 한 번 열고 나면 아무도 다시 보지 않았다. 그래서 몇 시간 뒤 밖에서
// 안 들어와지는 일이 생겼고, 그때는 창을 열어 사람이 다시 눌러 주는 수밖에 없었다.
// 사람이 없는 PC 라면 그것도 못 한다.
//
// "살아 있다" 를 무엇으로 보나 — 프로세스만 보면 안 된다
// -----------------------------------------------------
// 중계(tsproxy)는 Go 프로그램 안에 Tailscale 노드를 품고 있다. 그 노드가 관제쪽에서
// 떨어져 나가도 프로세스 자체는 멀쩡히 살아 있다. 그러면 RemoteTunnel.IsAlive() 는 계속
// true 인데 밖에서는 아무도 못 들어온다 - 겉보기에 제일 멀쩡한 고장이다.
// 그래서 진짜로 공개 주소에 한 번 다녀온다. 그 왕복이 유일하게 믿을 수 있는 답이다.
//
// 실측 (이 PC, 2026-09-06)
//   공개 주소 왕복  0.9 ~ 2.0 초, 2.3 KB   → 60초마다 해도 부담 없다
//   프로세스 확인   거의 0                 → 5초마다
//
// 알림은 왕복이 확인된 뒤에만 보낸다
// -----------------------------------
// 주소가 바뀌었다고 알리는 시점은 "열렸다" 가 아니라 "밖에서 실제로 들어와졌다" 여야
// 한다. 열리다 만 주소를 알려 주면 사람이 그걸 붙여 넣고 안 된다고 헤맨다.
// 그래서 알림도 이름 굳히기도 전부 왕복 성공 한 곳에서만 한다.
//
// 인터넷이 끊긴 것과 터널이 죽은 것을 구별한다
// -------------------------------------------
// 회선이 잠깐 끊겼을 때 터널을 다시 여는 것은 아무 도움이 안 된다. 오히려 관제쪽에
// 등록을 반복해 두들기게 되고, 장비 신원이 새로 생기면 공개 주소까지 바뀐다.
// 그래서 왕복이 실패하면 먼저 인터넷이 있는지 보고, 있을 때만 고장으로 친다.
using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace RemoteControl
{
    internal static class TunnelGuard
    {
        // 프로세스 확인 주기. 짧아도 공짜다.
        private const int TickMs = 5000;

        // 공개 주소 왕복 주기. 1~2초씩 걸리므로 이 정도가 적당하다.
        private const int ProbeSec = 60;

        // 왕복이 몇 번 내리 실패해야 고장으로 볼 것인가.
        // 한 번은 그냥 회선이 튄 것일 수 있다. 두 번(≈2분)이면 진짜다.
        private const int FailsToAct = 2;

        // 다시 여는 간격. 계속 실패하는데 15초마다 두들기면 관제쪽에서 막힌다.
        private static readonly int[] BackoffSec = { 15, 30, 60, 120, 300 };

        // 몇 분을 못 열어야 "닫혔습니다" 를 폰으로 보낼 것인가.
        // 회선이 잠깐 튀는 것마다 보내면 사람이 알림을 꺼 버린다. 그러면 정작
        // 주소가 바뀌었을 때도 못 받는다 - 알림 기능이 통째로 죽는 셈이다.
        private const int DownAlertMin = 10;

        // 창과 상태 파일이 1~2초마다 읽는다. 락을 잡으면 그쪽이 멈춘다.
        private static volatile string note = "";
        private static volatile bool enabled;
        private static volatile bool started;

        private static Func<int> portOf;
        private static Func<bool> wanted;
        private static Action<string> say;

        private static readonly object gate = new object();

        /// <summary>지금 왜 이 상태인지 한 줄. 비어 있으면 정상이다.</summary>
        internal static string Note { get { return note ?? ""; } }

        internal static bool Enabled { get { return enabled; } }

        /// <summary>
        /// 감시를 켠다. 여러 번 불러도 스레드는 하나다.
        /// </summary>
        /// <param name="port">지금 열려 있어야 할 포트 (바뀔 수 있으므로 그때그때 읽는다)</param>
        /// <param name="want">지금 외부망이 열려 있어야 하는가 (사람이 닫았으면 false)</param>
        /// <param name="log">기록할 곳</param>
        internal static void Enable(Func<int> port, Func<bool> want, Action<string> log)
        {
            lock (gate)
            {
                portOf = port;
                wanted = want;
                say = log;
                enabled = true;

                if (started) return;
                started = true;

                Thread t = new Thread(Loop);
                t.IsBackground = true;
                t.Name = "TunnelGuard";
                t.Start();
            }
        }

        /// <summary>
        /// 감시를 끈다. 스레드는 그대로 돌지만 아무것도 하지 않는다.
        /// (사람이 [외부망 닫기] 를 눌렀거나, 서비스가 올라와 터널 주인이 바뀌었을 때)
        /// </summary>
        internal static void Disable()
        {
            enabled = false;
            note = "";
        }

        // ---------------------------------------------------------------- 본체

        private static void Loop()
        {
            int fails = 0;          // 왕복 실패가 내리 몇 번인가
            int step = 0;           // 다시 열기 실패가 이어질수록 간격을 늘린다
            DateTime nextProbe = DateTime.MinValue;
            DateTime nextTry = DateTime.MinValue;
            DateTime downSince = DateTime.MinValue;   // 언제부터 밖에서 못 들어오는가
            DateTime nextAlert = DateTime.MinValue;   // 알림 보내기 재시도 시각
            bool alertedDown = false;                 // "닫혔습니다" 를 이미 보냈는가
            string lastUrl = "";
            string lastSettings = "";

            while (true)
            {
                Thread.Sleep(TickMs);

                try
                {
                    if (!enabled) continue;

                    RemoteTunnel.SyncSettings();
                    string currentSettings = RemoteTunnel.SettingsFingerprint();
                    if (lastSettings != currentSettings)
                    {
                        lastSettings = currentSettings;
                        fails = step = 0; nextTry = nextProbe = DateTime.MinValue;
                        downSince = DateTime.MinValue; alertedDown = false; lastUrl = "";
                    }
                    if (wanted != null && !wanted())
                    { RemoteTunnel.Stop(); note = ""; fails = step = 0; continue; }
                    if (!RemoteTunnel.IsConfigured) { note = RemoteTunnel.ConfigurationError; continue; }
                    if (RemoteTunnel.RetryBlocked) { note = RemoteTunnel.LastError + " (설정 수정 후 다시 저장)"; continue; }

                    // 누군가(사람이든 우리든) 이미 여는 중이다. 옆에서 또 열면 안 된다.
                    if (RemoteTunnel.IsOpening) { note = ""; continue; }

                    string why = null;

                    if (!RemoteTunnel.IsAlive())
                    {
                        why = "중계가 내려가 있습니다";
                        fails = 0;
                    }
                    else if (DateTime.Now >= nextProbe)
                    {
                        nextProbe = DateTime.Now.AddSeconds(ProbeSec);

                        string u = RemoteTunnel.CurrentUrl;

                        bool routeFailure; string detail;
                        bool healthy = Probe(u, out routeFailure, out detail);
                        // 왕복 중에 방식을 바꿨거나 닫았다면 옛 주소로 알림/장비 이름을 남기지 않는다.
                        if (!enabled || currentSettings != RemoteTunnel.SettingsFingerprint()) continue;
                        if (healthy)
                        {
                            // 밖에서 실제로 들어와지는 것이 확인됐다.
                            fails = 0;
                            step = 0;
                            note = "브라우저 접속 확인됨";

                            Healthy(u, ref downSince, ref alertedDown);
                            lastUrl = u;
                            continue;
                        }

                        // DNS/443/인증서 오류는 SSH 터널을 새로 열어도 고쳐지지 않는다.
                        // 살아 있는 SSH 연결과 발급 주소를 보존하고 서버 개통 문제를 표시한다.
                        if (!ShouldReopen(RemoteTunnel.Provider, routeFailure))
                        {
                            fails = 0;
                            note = "SSH 연결됨 · 웹 접속 확인 실패: " + detail;
                            Say(note);
                            continue;
                        }

                        if (!Online())
                        {
                            // 회선이 없다. 다시 열어 봐야 소용없고, 오히려 신원만 흔든다.
                            note = "인터넷이 끊겨 있습니다. 회선이 돌아오면 다시 확인합니다.";
                            continue;
                        }

                        fails++;
                        note = "공개 주소가 응답하지 않습니다 (" + fails + "/" + FailsToAct + ")";

                        if (fails < FailsToAct) continue;
                        why = "공개 주소가 " + fails + "번 응답하지 않았습니다";
                    }
                    else continue;      // 아직 왕복할 때가 아니다

                    // ---- 여기부터 다시 열기 ----

                    // 언제부터 못 들어오는지 여기서 센다. 다시 열기를 기다리는 동안에도
                    // 시간은 가므로, 아래 backoff 로 빠지기 전에 재야 한다.
                    if (downSince == DateTime.MinValue) downSince = DateTime.Now;

                    int downMin = (int)(DateTime.Now - downSince).TotalMinutes;

                    if (!alertedDown && downMin >= DownAlertMin && DateTime.Now >= nextAlert)
                    {
                        // 보내기에 실패하면(회선이 없을 때가 대부분이다) 표시를 남기지 않는다.
                        // 남겨 버리면 회선이 돌아온 뒤에도 영영 안 보내고, 복구 알림까지
                        // 같이 사라진다.
                        if (Notify.Send(Notify.WentDown(downMin, RemoteTunnel.LastError))) alertedDown = true;
                        else nextAlert = DateTime.Now.AddMinutes(5);
                    }

                    if (DateTime.Now < nextTry)
                    {
                        int left = (int)(nextTry - DateTime.Now).TotalSeconds;
                        note = why + ". " + left + "초 뒤 다시 엽니다.";
                        continue;
                    }

                    int wait = BackoffSec[Math.Min(step, BackoffSec.Length - 1)];
                    nextTry = DateTime.Now.AddSeconds(wait + 150);   // 여는 데 최대 150초

                    // 처음 켤 때도 이 길로 온다 (아직 아무것도 안 떠 있으니 "내려가 있음").
                    // 그때 "외부망 이상" 이라고 적으면 기록을 보는 사람이 놀란다.
                    bool first = string.IsNullOrEmpty(lastUrl);

                    Say(first ? "외부망을 엽니다…" : "외부망 이상: " + why + ". 다시 엽니다.");
                    note = first ? "외부망을 여는 중입니다…" : "외부망을 다시 여는 중입니다…";

                    int port = portOf != null ? portOf() : Config.Port;
                    string got = Reopen(port);

                    // 여는 동안 사람이 닫았을 수 있다. 그러면 결과를 무시한다.
                    if (!enabled || (wanted != null && !wanted())) { note = ""; continue; }

                    if (string.IsNullOrEmpty(got))
                    {
                        if (currentSettings != RemoteTunnel.SettingsFingerprint()) continue;
                        if (RemoteTunnel.RetryBlocked) { note = RemoteTunnel.LastError + " (설정 수정 후 다시 저장)"; Say(note); continue; }
                        // 여는 사이에 포트가 바뀌어 접힌 것은 고장이 아니다.
                        // 이걸 실패로 세면 간격이 5분까지 늘어나, 포트를 두어 번 만진
                        // 것만으로 외부망이 한참 동안 닫혀 있게 된다.
                        if (RemoteTunnel.LastError == "여는 도중에 닫혔습니다.")
                        {
                            nextTry = DateTime.MinValue;
                            note = "";
                            continue;
                        }

                        step++;
                        int next = BackoffSec[Math.Min(step, BackoffSec.Length - 1)];
                        nextTry = DateTime.Now.AddSeconds(next);

                        Say("외부망 재연결 실패: " + RemoteTunnel.LastError + " (" + next + "초 뒤 다시)");
                        note = "다시 열지 못했습니다: " + RemoteTunnel.LastError;
                        continue;
                    }

                    step = 0;
                    fails = 0;
                    nextTry = DateTime.MinValue;
                    nextProbe = DateTime.Now.AddSeconds(10);   // 잘 열렸는지 곧 확인한다
                    note = "";

                    // 여기서는 알리지 않는다. 열렸다는 것과 밖에서 들어와진다는 것은
                    // 다른 말이다. 10초 뒤 왕복이 확인되면 Healthy() 가 알린다.
                    Say("외부망 열림: " + got + "/ (확인 중)");
                }
                catch (Exception ex)
                {
                    // 지킴이가 예외로 죽으면 원래 문제로 되돌아간다. 무슨 일이 있어도 계속 돈다.
                    note = "확인 중 오류: " + ex.Message;
                }
            }
        }

        /// <summary>
        /// 밖에서 실제로 들어와지는 것이 확인됐을 때 부른다.
        ///
        /// 알림과 이름 굳히기를 여기 한 곳에 모았다. 두 군데에서 하면 언젠가 한쪽만
        /// 고쳐져서, 열리다 만 주소를 알리거나 굳히게 된다.
        /// </summary>
        private static void Healthy(string url, ref DateTime downSince, ref bool alertedDown)
        {
            // 닫혔다고 알렸으면 돌아온 것도 알려야 한다. 안 그러면 사람은 계속
            // 닫혀 있는 줄 알고 다른 방법을 찾아 헤맨다.
            // 보내는 데 실패하면 표시를 그대로 둔다. 60초 뒤 왕복에서 다시 시도한다.
            // 여기서 내려 버리면 "닫혔습니다" 만 받고 "돌아왔습니다" 는 영영 못 받는다.
            if (alertedDown && Notify.Send(Notify.CameBack(url))) alertedDown = false;

            downSince = DateTime.MinValue;

            if (RemoteTunnel.Provider == "tailscale") Pin(url);

            // 부팅 후 첫 연결 알림에는 현주소와 터널 정보를 포함한다.
            // 성공한 경우 별도의 주소 변경 알림을 중복해서 보내지 않는다.
            if (StartupNotify.OnVerifiedTunnel(url)) return;

            // 비교 기준은 프로세스 메모리가 아니라 설정 파일이다.
            // 메모리에만 두면 제일 중요한 경우를 놓친다 - PC 를 껐다 켜는 사이에 주소가
            // 바뀌는 것. 다시 뜬 직후에는 "이전 주소" 가 비어 있어 바뀐 줄을 모른다.
            // 재부팅이야말로 주소가 바뀌기 딱 좋은 때다.
            string prev = Notify.LastUrl;
            if (string.Equals(prev, url, StringComparison.Ordinal)) return;

            if (!string.IsNullOrEmpty(prev))
            {
                Say("⚠ 외부망 주소가 바뀌었습니다: " + prev + "/ → " + url + "/");

                // 알림을 안 쓰는 사람에게까지 "못 보냈습니다" 를 남길 일은 아니다.
                if (Notify.IsConfigured && !Notify.Send(Notify.AddressChanged(prev, url)))
                {
                    // 못 보냈으면 적어 두지 않는다. 다음 왕복에서 다시 시도한다.
                    // 여기서 적어 버리면 주소가 바뀐 사실이 영영 묻힌다.
                    Say("알림을 보내지 못했습니다: " + Notify.LastError);
                    return;
                }
            }

            Notify.LastUrl = url;
        }

        private static string Reopen(int port)
        {
            // 앞선 중계가 남아 있으면 같은 상태 폴더를 두고 서로 밀어낸다.
            // 접속이 됐다 안 됐다 하는, 원인을 찾기 제일 어려운 상태가 된다.
            RemoteTunnel.Stop();
            if (RemoteTunnel.Provider == "tailscale") Tunnel.KillOrphans();
            Thread.Sleep(500);

            return RemoteTunnel.Start(port);
        }

        private static void Say(string s)
        {
            try { if (say != null) say(s); }
            catch { }
        }

        // ---------------------------------------------------------------- 확인

        /// <summary>
        /// 공개 주소로 실제로 한 번 다녀온다. 여기서 나가는 요청은 통신사 → Tailscale
        /// 접점 → 이 PC 로 한 바퀴 돌아오므로, 밖에 있는 사람이 겪는 것과 같은 길이다.
        ///
        /// 허브 프록시의 502/로그인 리디렉션은 성공이 아니다. 이 앱의 /ping 응답까지 확인한다.
        /// </summary>
        internal static bool Probe(string baseUrl)
        { bool routeFailure; string detail; return Probe(baseUrl, out routeFailure, out detail); }

        internal static bool ShouldReopen(string provider, bool routeFailure)
        { return provider != "private" || routeFailure; }

        internal static bool Probe(string baseUrl, out bool routeFailure, out string detail)
        {
            routeFailure = false; detail = "발급 주소가 없습니다";
            if (string.IsNullOrEmpty(baseUrl)) return false;

            Https.EnsureTls();

            try
            {
                HttpWebRequest r = (HttpWebRequest)WebRequest.Create(baseUrl.TrimEnd('/') + "/ping");
                r.Method = "GET";
                r.Timeout = 12000;
                r.ReadWriteTimeout = 12000;
                r.AllowAutoRedirect = false;
                r.KeepAlive = false;
                r.UserAgent = "RemoteControl-Guard";

                // 프록시를 찾지 않는다. WPAD 조회 때문에 몇 초씩 더 걸리는 일이 있다.
                r.Proxy = null;

                using (HttpWebResponse resp = (HttpWebResponse)r.GetResponse())
                {
                    detail = "HTTP " + (int)resp.StatusCode;
                    routeFailure = BrokenRoute(resp.StatusCode);
                    if (resp.StatusCode != HttpStatusCode.OK) return false;
                    using (System.IO.Stream body = resp.GetResponseStream())
                    {
                        byte[] value = new byte[3];
                        int count = 0, n;
                        while (count < value.Length && (n = body.Read(value, count, value.Length - count)) > 0) count += n;
                        bool ok = count == 2 && value[0] == (byte)'o' && value[1] == (byte)'k';
                        detail = ok ? "정상" : "원격제어 /ping 응답이 아닙니다";
                        return ok;
                    }
                }
            }
            catch (WebException we)
            {
                var response = we.Response as HttpWebResponse;
                if (response != null) { detail = "HTTP " + (int)response.StatusCode; routeFailure = BrokenRoute(response.StatusCode); }
                else if (we.Status == WebExceptionStatus.NameResolutionFailure) detail = "와일드카드 DNS를 확인하세요";
                else if (we.Status == WebExceptionStatus.TrustFailure || we.Status == WebExceptionStatus.SecureChannelFailure) detail = "와일드카드 HTTPS 인증서를 확인하세요";
                else if (we.Status == WebExceptionStatus.ConnectFailure) detail = "서버 HTTPS 443 포트·방화벽·포트 전달을 확인하세요";
                else detail = "HTTPS 응답을 받지 못했습니다 (" + we.Status + ")";
                if (we.Response != null) { try { we.Response.Close(); } catch { } }
                return false;
            }
            catch { detail = "HTTPS 확인 중 오류가 발생했습니다"; return false; }
        }

        private static bool BrokenRoute(HttpStatusCode code)
        { return code == HttpStatusCode.NotFound || code == HttpStatusCode.BadGateway || code == HttpStatusCode.GatewayTimeout; }

        /// <summary>
        /// 인터넷이 되는가. Tailscale 관제쪽에 붙어 본다.
        /// 여기에 못 닿으면 터널도 어차피 못 서므로, 다시 여는 것 자체가 헛수고다.
        /// </summary>
        private static bool Online()
        {
            try
            {
                using (TcpClient c = new TcpClient())
                {
                    SshSettings ssh = SshSettings.Load();
                    string host = RemoteTunnel.Provider == "private" ? ssh.Host : "controlplane.tailscale.com";
                    int port = RemoteTunnel.Provider == "private" ? int.Parse(ssh.Port) : 443;
                    IAsyncResult ar = c.BeginConnect(host, port, null, null);
                    using (var wait = ar.AsyncWaitHandle) if (!wait.WaitOne(5000)) return false;

                    c.EndConnect(ar);
                    return true;
                }
            }
            catch { return false; }
        }

        // ---------------------------------------------------------------- 이름 굳히기

        /// <summary>
        /// 실제로 받은 장비 이름을 설정에 적어 둔다.
        ///
        /// 우리가 부탁한 이름이 이미 쓰이고 있으면 Tailscale 이 -1 을 붙여 준다.
        /// 그것을 적어 두지 않으면 다음에 또 원래 이름을 부탁하게 되는데, 그 사이
        /// 옛 노드가 없어졌으면 이번에는 그 이름을 그냥 받는다. 즉 주소가 바뀐다.
        /// 밖에 알려 준 주소가 어느 날 갑자기 죽는 이유가 이것이다.
        /// (이 PC 도 tsnode 에는 rc-dafed6f5 인데 실제 주소는 rc-dafed6f5-1 이었다)
        ///
        /// 왕복이 확인된 주소일 때만 부른다. 열리다 만 주소를 굳히면 더 나빠진다.
        /// </summary>
        private static void Pin(string funnelUrl)
        {
            try
            {
                if (string.IsNullOrEmpty(funnelUrl)) return;

                Uri u = new Uri(funnelUrl);
                string label = Tunnel.Sanitize(u.Host.Split('.')[0]);
                if (label.Length == 0) return;

                if (string.Equals(label, Config.Get("tsnode"), StringComparison.Ordinal)) return;

                Config.Set("tsnode", label);
                Say("장비 이름을 " + label + " 로 굳혔습니다 (주소가 다시 바뀌지 않게).");
            }
            catch { }
        }
    }
}

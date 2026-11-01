// PC 부팅 후 첫 연결 상태 알림. 서비스 재시작과 터널 재연결은 같은 부팅으로 취급한다.
using System;
using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;

namespace RemoteControl
{
    internal static class StartupNotify
    {
        private const string StateKey = "tg_boot_notice";
        private static readonly object Gate = new object();
        private static bool started;
        private static string bootId;
        private static string deliveredReady;
        private static string deliveredPending;

        [DllImport("kernel32.dll")]
        private static extern ulong GetTickCount64();

        // WMI의 마지막 Windows 부팅 시각은 서비스/대리 프로세스 재시작에도 동일하다.
        private static string BootId()
        {
            if (bootId != null) return bootId;
            try
            {
                using (var search = new ManagementObjectSearcher("SELECT LastBootUpTime FROM Win32_OperatingSystem"))
                using (ManagementObjectCollection results = search.Get())
                {
                    foreach (ManagementBaseObject item in results)
                    {
                        string value = Convert.ToString(item["LastBootUpTime"], CultureInfo.InvariantCulture);
                        if (string.IsNullOrEmpty(value)) continue;
                        bootId = ManagementDateTimeConverter.ToDateTime(value).ToUniversalTime()
                            .ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                SvcLog.Write("부팅 시각 조회 실패: " + ex.GetType().Name);
            }
            if (bootId == null)
            {
                // WMI 장애 시에도 동작하도록 uptime 기반 대체값을 사용한다.
                bootId = DateTime.UtcNow.AddMilliseconds(-(double)GetTickCount64())
                    .ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture);
            }
            return bootId;
        }

        private static string Prefix()
        {
            // 새 텔레그램 수신자를 등록하면 같은 부팅에서도 한 번 알린다.
            return "boot:" + BootId() + "|" + Notify.ChatId;
        }

        internal static void Start()
        {
            lock (Gate)
            {
                if (started) return;
                started = true;
            }
            var worker = new Thread(delegate()
            {
                // 부팅 직후 네트워크와 SSH/DNS가 준비될 시간을 준다.
                Thread.Sleep(120000);
                while (true)
                {
                    try
                    {
                        lock (Gate)
                        {
                            if (Notify.IsConfigured)
                            {
                                string prefix = Prefix() + "|";
                                if (Config.Get(StateKey).StartsWith(prefix, StringComparison.Ordinal) ||
                                    deliveredPending == Prefix() || deliveredReady == Prefix()) return;
                            }
                        }
                        TrySendPending();
                    }
                    catch (Exception ex)
                    {
                        SvcLog.Write("부팅 알림 대기 확인 실패: " + ex.GetType().Name);
                    }
                    Thread.Sleep(60000);
                }
            });
            worker.IsBackground = true;
            worker.Name = "RemoteControl.BootNotify";
            worker.Start();
        }

        // TunnelGuard가 공개 접속 왕복 검사에 성공한 경우에만 호출한다.
        // true면 부팅 알림이 주소 변경 알림을 대신했으므로 중복 전송하지 않는다.
        internal static bool OnVerifiedTunnel(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            lock (Gate)
            {
                if (!Notify.IsConfigured) return false;
                string prefix = Prefix();
                string current = Config.Get(StateKey);
                if (current == prefix + "|ready" || deliveredReady == prefix) return false;

                string oldUrl = Notify.LastUrl;
                string heading = current == prefix + "|pending" || deliveredPending == prefix
                    ? "🟢 PC 부팅 후 터널 연결 완료"
                    : "🖥 PC 부팅 · 원격제어 접속 준비 완료";
                string message = heading + "\n\n" + Notify.CurrentAddressesWithScreen(url);
                if (!string.IsNullOrEmpty(oldUrl) && !string.Equals(oldUrl, url, StringComparison.Ordinal))
                    message += "\n\n이전 화면 주소: " + Notify.NoticeAddress(oldUrl);

                if (!Notify.Send(message)) return false;
                deliveredReady = prefix;
                try { Config.Set(StateKey, prefix + "|ready"); }
                catch (Exception ex) { SvcLog.Write("부팅 알림 기록 실패: " + ex.GetType().Name); }
                try { Notify.LastUrl = url; }  // 이미 변경 사실을 함께 보냈으므로 별도의 변경 알림은 생략한다.
                catch (Exception ex) { SvcLog.Write("부팅 주소 기록 실패: " + ex.GetType().Name); }
                return true;
            }
        }

        // 일정 시간 터널 연결을 확인하지 못해도 PC가 부팅됐음을 알려 준다.
        // 추후 접속이 검증되면 OnVerifiedTunnel이 연결 완료 메시지를 추가 전송한다.
        internal static bool TrySendPending()
        {
            lock (Gate)
            {
                if (!Notify.IsConfigured) return false;
                string prefix = Prefix();
                string current = Config.Get(StateKey);
                if (current == prefix + "|pending" || current == prefix + "|ready" ||
                    deliveredReady == prefix || deliveredPending == prefix) return false;

                string reason = Config.Get("extauto") == "1"
                    ? "화면 터널의 외부 접속이 아직 확인되지 않았습니다. 연결을 계속 확인합니다."
                    : "외부망 자동 연결이 꺼져 있습니다.";
                string message = "🟠 PC 부팅 · 터널 연결 확인 대기\n\n" +
                    Notify.CurrentAddressesWithScreen("") + "\n\n" + reason;

                if (!Notify.Send(message)) return false;
                deliveredPending = prefix;
                try { Config.Set(StateKey, prefix + "|pending"); }
                catch (Exception ex) { SvcLog.Write("부팅 대기 알림 기록 실패: " + ex.GetType().Name); }
                return true;
            }
        }
    }
}

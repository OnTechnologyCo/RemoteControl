// 알림. 외부망 주소가 바뀌면 텔레그램으로 알린다.
//
// 왜 밖으로 내보내야 하나
// -----------------------
// 주소가 바뀌는 순간 사람은 밖에 있다. 그게 이 기능이 필요한 이유다.
// 그러니 트레이 풍선·창·기록은 아무 소용이 없다 — 돌아와야 볼 수 있다.
// 알림은 PC 밖으로 나가야 한다.
//
// 다행히 알림은 나가는 방향이라 터널이 죽어도 인터넷만 있으면 나간다.
//
// 왜 텔레그램인가
// ---------------
// HTTPS POST 한 번이면 끝나고, 토큰이 만료되지 않는다. 무인 PC 에서는 이게 결정적이다.
// (카카오톡은 OAuth 토큰을 주기적으로 갱신해야 해서, 사람이 없는 PC 에서는 언젠가
//  조용히 죽는다. 정작 필요할 때 안 온다)
// 메일은 앱 비밀번호가 필요하고 스팸함으로 갈 수 있으며 푸시가 느리다.
//
// 새는 것
// -------
// 보내는 내용에 공개 주소가 들어간다. 그 주소는 텔레그램 서버에 남는다.
// 로그인은 여전히 비밀번호로 막혀 있지만 "여기에 원격제어가 있다" 는 새어 나간다.
// 토큰은 실행 환경 또는 이 PC의 DPAPI 저장소에서 읽어 배포 파일에 남기지 않는다.
using System;
using System.IO;
using System.Net;
using System.Text;

namespace RemoteControl
{
    /// <summary>
    /// .NET Framework 는 기본값이 아직도 SSL3 + TLS 1.0 이다 (실측으로 확인했다).
    /// 그대로 두면 ts.net 도 api.telegram.org 도 "SSL/TLS 보안 채널을 만들 수 없습니다"
    /// 로 끝난다. 터널과 알림이 둘 다 멀쩡한데 우리만 못 붙는 상태가 된다.
    /// </summary>
    internal static class Https
    {
        private static bool ready;

        internal static void EnsureTls()
        {
            if (ready) return;
            ready = true;

            try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; }   // TLS 1.2
            catch { }

            try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)12288; }  // TLS 1.3
            catch { }
        }
    }

    internal static partial class Notify
    {
        // 구버전 설치의 토큰은 Setup이 PC별 DPAPI 저장소로 보존한다.
        // 새 설치는 실행 환경 변수로 토큰을 제공할 수 있다.
        internal static string BotToken
        {
            get { return RemoteControlSecrets.TelegramCredentials.Read(); }
        }

        internal static string LastError = "";

        /// <summary>실행 환경에 봇 토큰이 제공되었는가.</summary>
        internal static bool HasBot { get { return BotToken.Trim().Length > 0; } }

        /// <summary>
        /// 사람이 [알림 끄기] 를 눌렀는가.
        ///
        /// 토큰은 실행 환경에서 받으므로 설정을 지우는 것만으로는 못 끈다.
        /// 껐다는 사실을 따로 적어 둬야 한다.
        /// </summary>
        private static bool TurnedOff { get { return Config.Get("tgoff") == "1"; } }

        private static DateTime brokerChecked;
        private static bool brokerConfigured;

        internal static bool IsConfigured
        {
            get {
                if (!RemoteControlSecrets.ConfigProtection.IsAdministrator) {
                    if (DateTime.UtcNow - brokerChecked > TimeSpan.FromSeconds(5)) {
                        brokerConfigured = NotifyBroker.Request("status") == "configured";
                        brokerChecked = DateTime.UtcNow;
                    }
                    return brokerConfigured;
                }
                return HasBot && !TurnedOff && ChatId.Length > 0;
            }
        }

        /// <summary>어디로 보낼까. 이것만 쓰는 사람이 정한다.</summary>
        internal static string ChatId
        {
            get { return (Config.Get("tgchat") ?? "").Trim(); }
        }

        internal static void SaveChat(string chatId)
        {
            Config.Set("tgchat", (chatId ?? "").Trim());
            Config.Set("tgoff", "0");      // 저장했다는 것은 켜겠다는 뜻이다
        }

        /// <summary>알림을 끈다.</summary>
        internal static void TurnOff()
        {
            Config.Set("tgchat", "");
            Config.Set("tgoff", "1");
        }

        // ---------------------------------------------------------------- 마지막으로 알린 주소

        /// <summary>
        /// 마지막으로 알린 주소. 프로세스 메모리가 아니라 설정 파일에 둔다.
        ///
        /// 메모리에만 두면 정작 제일 중요한 경우를 놓친다 - PC 를 껐다 켜는 사이에
        /// 주소가 바뀌는 것. 다시 뜬 직후에는 "이전 주소" 가 비어 있어서 바뀐 줄을
        /// 모르고 넘어간다. 재부팅이야말로 주소가 바뀌기 딱 좋은 때다.
        /// </summary>
        internal static string LastUrl
        {
            get { return (Config.Get("tgurl") ?? "").Trim(); }
            set { Config.Set("tgurl", (value ?? "").Trim()); }
        }

        // ---------------------------------------------------------------- 보내기

        /// <summary>
        /// 보낸다. 실패해도 예외를 올리지 않는다 - 알림이 안 갔다고 원격제어가
        /// 멈추면 본말이 뒤집힌다. 사유는 LastError 에 남긴다.
        /// </summary>
        internal static bool Send(string text)
        {
            if (!HasBot) { LastError = "이 PC의 알림 봇 토큰이 설정되지 않았습니다."; return false; }
            if (!IsConfigured) { LastError = "알림 받을 곳(chat_id)이 정해지지 않았습니다."; return false; }

            string token = BotToken.Trim();

            // 회선이 잠깐 튀는 것 정도는 넘긴다. 세 번이면 충분하다.
            for (int i = 0; i < 3; i++)
            {
                string body =
                    "chat_id=" + Uri.EscapeDataString(ChatId) +
                    "&text=" + Uri.EscapeDataString(text) +

                    // 이걸 끄지 않으면 텔레그램 서버가 미리보기를 만들려고 우리 공개 주소를
                    // 직접 가져간다. 알림 한 번에 모르는 곳에서 접속이 하나씩 붙는다.
                    "&disable_web_page_preview=true";

                string resp;
                if (Post("https://api.telegram.org/bot" + token + "/sendMessage", body, out resp))
                {
                    // 텔레그램은 실패도 200 이 아닌 코드로 주지만, 확실히 하려고 본문도 본다.
                    if (resp.IndexOf("\"ok\":true", StringComparison.Ordinal) >= 0) return true;

                    LastError = Reason(resp);
                    return false;      // 토큰·chat_id 가 틀린 것은 다시 보내도 똑같다
                }

                if (i < 2) System.Threading.Thread.Sleep(2000);
            }

            return false;
        }

        /// <summary>
        /// 이 봇에게 말을 건 사람의 chat_id 를 찾아 준다.
        ///
        /// 이게 없으면 사람이 브라우저로 getUpdates 를 열어 눈으로 숫자를 찾아 옮겨
        /// 적어야 한다. 거기서 대부분 포기한다.
        ///
        /// 누구 것인지(who)도 같이 돌려준다. 봇 하나를 여러 사람이 쓰면 여기서
        /// 남의 chat_id 가 잡힐 수 있기 때문이다. 그대로 저장해 버리면 내 원격제어
        /// 주소가 남에게 간다. 이름을 보여 주고 사람이 확인하게 한다.
        /// </summary>
        internal static string FindChatId(out string who)
        {
            who = "";

            string token = BotToken.Trim();
            if (token.Length == 0) { LastError = "이 PC의 알림 봇 토큰이 설정되지 않았습니다."; return null; }

            string resp;
            if (!Post("https://api.telegram.org/bot" + token + "/getUpdates", "limit=100", out resp))
                return null;

            if (resp.IndexOf("\"ok\":true", StringComparison.Ordinal) < 0)
            {
                LastError = Reason(resp);
                return null;
            }

            // Json.cs 는 한 겹짜리 객체만 읽는다. 여기 답은 배열 안의 객체 안의 객체다.
            // 범용 파서를 새로 만들 일은 아니고, 찾는 모양이 하나뿐이라 그것만 집는다.
            //   ..."chat":{"id":123456789,"first_name":...
            // 마지막 것을 쓴다. 여러 사람이 말을 걸었으면 제일 최근이 본인일 가능성이 높다.
            string id = null;
            int at = 0;

            while (true)
            {
                int c = resp.IndexOf("\"chat\":{", at, StringComparison.Ordinal);
                if (c < 0) break;

                int k = resp.IndexOf("\"id\":", c, StringComparison.Ordinal);
                if (k < 0) break;

                k += 5;
                int e = k;
                if (e < resp.Length && resp[e] == '-') e++;          // 그룹은 음수다
                while (e < resp.Length && resp[e] >= '0' && resp[e] <= '9') e++;

                if (e > k)
                {
                    id = resp.Substring(k, e - k);

                    // 이 chat 덩어리 안에서만 이름을 찾는다. 다음 "chat":{ 전까지.
                    int stop = resp.IndexOf("\"chat\":{", c + 8, StringComparison.Ordinal);
                    if (stop < 0) stop = resp.Length;

                    string name = Field(resp, c, stop, "first_name");
                    string user = Field(resp, c, stop, "username");
                    string title = Field(resp, c, stop, "title");         // 그룹

                    who = title.Length > 0 ? title : name;
                    if (user.Length > 0) who = (who.Length > 0 ? who + " " : "") + "@" + user;
                }

                at = c + 8;
            }

            if (string.IsNullOrEmpty(id))
                LastError = "봇에게 말을 건 기록이 없습니다.\r\n" +
                            "텔레그램에서 봇을 찾아 아무 말이나 한 번 보낸 뒤 다시 눌러 주세요.\r\n" +
                            "(봇이 다른 곳에서 이미 읽어 갔다면 한 번 더 보내야 합니다)";

            return id;
        }

        /// <summary>주어진 구간에서 "이름":"값" 하나를 집는다. 없으면 빈 문자열.</summary>
        private static string Field(string s, int from, int to, string name)
        {
            try
            {
                string tag = "\"" + name + "\":\"";
                int i = s.IndexOf(tag, from, StringComparison.Ordinal);
                if (i < 0 || i >= to) return "";

                i += tag.Length;
                int e = s.IndexOf('"', i);
                return e > i && e <= to ? s.Substring(i, e - i) : "";
            }
            catch { return ""; }
        }

        /// <summary>봇의 @이름. 사람이 텔레그램에서 이 봇을 찾아가야 하므로 필요하다.</summary>
        internal static string BotName()
        {
            string token = BotToken.Trim();
            if (token.Length == 0) return "";

            string resp;
            if (!Post("https://api.telegram.org/bot" + token + "/getMe", "", out resp)) return "";
            if (resp.IndexOf("\"ok\":true", StringComparison.Ordinal) < 0) { LastError = Reason(resp); return ""; }

            return Field(resp, 0, resp.Length, "username");
        }

        /// <summary>텔레그램이 돌려준 사유를 사람이 읽을 말로 바꾼다.</summary>
        private static string Reason(string resp)
        {
            if (resp == null) resp = "";

            if (resp.IndexOf("Unauthorized", StringComparison.OrdinalIgnoreCase) >= 0)
                return "봇 토큰이 맞지 않습니다.";

            if (resp.IndexOf("chat not found", StringComparison.OrdinalIgnoreCase) >= 0)
                return "chat_id 를 찾지 못했습니다.\r\n텔레그램에서 봇에게 먼저 말을 걸어야 보낼 수 있습니다.";

            if (resp.IndexOf("bot was blocked", StringComparison.OrdinalIgnoreCase) >= 0)
                return "봇이 차단되어 있습니다. 텔레그램에서 차단을 풀어 주세요.";

            int d = resp.IndexOf("\"description\":\"", StringComparison.Ordinal);
            if (d >= 0)
            {
                d += 15;
                int e = resp.IndexOf('"', d);
                if (e > d) return resp.Substring(d, e - d);
            }

            return string.IsNullOrEmpty(resp) ? "응답이 비었습니다." : resp;
        }

        private static bool Post(string url, string body, out string resp)
        {
            resp = "";
            Https.EnsureTls();

            try
            {
                HttpWebRequest r = (HttpWebRequest)WebRequest.Create(url);
                r.Method = "POST";
                r.ContentType = "application/x-www-form-urlencoded";
                r.Timeout = 15000;
                r.ReadWriteTimeout = 15000;
                r.KeepAlive = false;
                r.UserAgent = "RemoteControl";

                // 프록시를 찾지 않는다. WPAD 조회 때문에 몇 초씩 더 걸리는 일이 있다.
                r.Proxy = null;

                byte[] b = Encoding.UTF8.GetBytes(body);
                r.ContentLength = b.Length;

                using (Stream s = r.GetRequestStream()) s.Write(b, 0, b.Length);

                using (HttpWebResponse w = (HttpWebResponse)r.GetResponse())
                using (StreamReader sr = new StreamReader(w.GetResponseStream(), Encoding.UTF8))
                {
                    resp = sr.ReadToEnd();
                    return true;
                }
            }
            catch (WebException we)
            {
                // 텔레그램은 잘못된 요청을 4xx 로 준다. 그 본문에 사유가 들어 있으므로
                // 예외로 끝내지 말고 읽어야 한다 ("Unauthorized" 같은 것).
                try
                {
                    if (we.Response != null)
                        using (StreamReader sr = new StreamReader(we.Response.GetResponseStream(), Encoding.UTF8))
                        {
                            resp = sr.ReadToEnd();
                            return true;
                        }
                }
                catch { }

                LastError = "보내지 못했습니다: " + we.Message;
                return false;
            }
            catch (Exception ex)
            {
                LastError = "보내지 못했습니다: " + ex.Message;
                return false;
            }
        }

        // ---------------------------------------------------------------- 글귀

        private static string Now()
        {
            return DateTime.Now.ToString("MM-dd HH:mm");
        }

        internal static string AddressChanged(string oldUrl, string newUrl)
        {
            return "⚠ 원격제어 주소가 바뀌었습니다\n\n" + ConnectionIdentity() + "\n\n" +
                   "이전 주소: " + NoticeAddress(oldUrl) + "\n" +
                   "변경 주소: " + NoticeAddress(newUrl) + "\n\n" +
                   "예전 주소로는 더 이상 들어갈 수 없습니다.\n" + Now();
        }

        internal static string WentDown(int minutes, string why)
        {
            return "🔴 원격제어 외부망이 닫혔습니다\n\n" + ConnectionIdentity() + "\n\n" +
                   minutes + "분째 다시 열지 못하고 있습니다.\n" +
                   "사유: " + (string.IsNullOrEmpty(why) ? "알 수 없음" : why) + "\n\n" +
                   "계속 다시 열어 보는 중입니다.\n" + Now();
        }

        internal static string CameBack(string url)
        {
            return "🟢 원격제어 외부망이 돌아왔습니다\n\n" + ConnectionIdentity() + "\n\n주소: " + NoticeAddress(url) + "\n\n" + Now();
        }
    }
}

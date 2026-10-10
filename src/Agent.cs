// 대리 프로세스. 서비스가 사람이 앉아 있는 세션에 SYSTEM 권한으로 밀어 넣는 쪽.
//
// 창이 없다. 실제로 화면을 잡고 입력을 넣고 웹을 서비스하는 것은 전부 여기다.
// 창(MainForm)은 이 상태를 파일로 읽어 보여 주기만 한다.
//
// 여기 있는 스레드들은 Desktops.Sync() 로 잠금 화면을 따라 옮겨 다닌다.
// 잠기거나 풀려도 이 프로세스는 그대로 살아 있으므로 브라우저 연결이 안 끊긴다.
using System;
using System.IO;
using System.Text;
using System.Threading;

namespace RemoteControl
{
    internal static class Agent
    {
        internal static void Run()
        {
            Native.MakeDpiAware();
            Tunnel.KillOrphans();

            // SYSTEM 이 만든 설정 파일을 사용자가 못 고치면, 창에서 비밀번호를 바꿔도
            // 조용히 실패한다. 권한이 있는 지금(SYSTEM) 맞춰 둔다.
            Config.EnsureWritable();

            SvcLog.Write("대리 프로세스 시작 (세션 " + Win.MySession() +
                         ", 데스크톱 " + Desktops.InputDesktopName() + ")");

            // 모니터가 없으면 윈도우가 1024x768 같은 작은 크기로 잡아 둔다.
            // 원격으로만 쓰는 PC 라면 그 크기로는 답답하다.
            // 세션이 바뀌거나 원격 데스크톱을 콘솔로 되돌리면 크기가 되돌아가는 일이
            // 있어서, 대리 프로세스가 뜰 때마다 다시 맞춘다.
            try
            {
                string note = Display.ApplySaved();
                if (!string.IsNullOrEmpty(note)) SvcLog.Write(note);
            }
            catch { }

            // 이 PC 가 실제로 어떤 크기를 받아 주는지 남긴다.
            // 모니터 없는 PC 는 목록이 아주 좁다. 그 앞에 갈 수 없으니 기록으로 알아야 한다.
            try { SvcLog.Write("화면: " + Display.Report()); } catch { }

            Server server = new Server();
            server.Notice += delegate (string s) { SvcLog.Write(s); };

            if (!Config.HasPassword)
            {
                SvcLog.Write("비밀번호가 정해져 있지 않습니다. 창에서 먼저 정해 주세요.");
            }
            else if (!server.Start(Config.Port, true))
            {
                SvcLog.Write("포트를 열지 못했습니다: " + server.LastError);
            }

            SyncFirewall();

            // 사람이 로그인하지 않아도 밖에서 들어올 수 있어야 한다.
            // 그러려면 창을 띄우지 않아도 터널이 알아서 올라와야 한다.
            //
            // 예전에는 여기서 다섯 번 시도하고 그만두었다. 그래서 한 번 열린 뒤에
            // 몇 시간 지나 조용히 닫히면 아무도 다시 열어 주지 않았다. 사람이 없는
            // PC 라면 그대로 끝이다. 이제는 지킴이가 계속 지켜본다.
            RemoteTunnel.SyncSettings();
            TunnelGuard.Enable(
                delegate { return Config.Port; },
                delegate { return Config.HasPassword && server.Port != 0 && Config.Get("extauto") == "1"; },
                delegate (string s) { SvcLog.Write(s); });

            // 상태를 남기고, 창에서 바꾼 설정을 따라간다.
            string pw = Config.Get("pwhash");

            while (true)
            {
                WriteStatus(server);
                Thread.Sleep(2000);

                // 창은 다른 프로세스다. 거기서 바꾼 것을 알려면 파일을 다시 봐야 한다.
                Config.Reload();
                RemoteTunnel.SyncSettings();
                TailscaleMcp.Tick();

                int want = Config.Port;
                if (want != server.Port && Config.HasPassword)
                {
                    SvcLog.Write("포트가 " + server.Port + " 에서 " + want + " 로 바뀌었습니다.");

                    server.Stop();
                    Thread.Sleep(300);

                    if (server.Start(want, true)) SvcLog.Write("포트 " + want + " 에서 대기 중입니다.");
                    else SvcLog.Write("포트를 열지 못했습니다: " + server.LastError);

                    // 방화벽도 같이 옮겨 준다. 안 그러면 옛 포트만 열린 채 남는다.
                    SyncFirewall();

                    // 터널도 옮겨야 한다. 터널은 "127.0.0.1:옛포트" 로 넘겨 주도록
                    // 만들어져 있어서, 그대로 두면 밖에서 들어온 사람은 502 만 본다.
                    // (실제로 그렇게 됐다. 안에서는 되는데 밖에서만 안 되는 상태였다)
                    //
                    // 접기만 한다. 다시 여는 것은 지킴이가 몇 초 안에 알아서 한다.
                    // 여기서 직접 열면 지킴이와 둘이 열게 되고, 그러면 같은 상태 폴더를
                    // 두고 중계 둘이 서로 밀어낸다.
                    if (RemoteTunnel.IsAlive())
                    {
                        SvcLog.Write("외부망을 접습니다. 새 포트로 곧 다시 엽니다.");
                        RemoteTunnel.Stop();
                    }
                }

                string now = Config.Get("pwhash");
                if (now != pw)
                {
                    pw = now;
                    Auth.DropAll();      // 비밀번호가 바뀌면 기존 접속은 모두 끊는다
                    SvcLog.Write("비밀번호가 바뀌었습니다. 기존 접속을 모두 끊었습니다.");
                }

                KeepScreenSize();
            }
        }

        /// <summary>
        /// 화면 크기를 계속 지킨다.
        ///
        /// 한 번 맞추는 것으로는 부족하다. 원격 데스크톱을 끊고 콘솔로 되돌리면
        /// (tscon) 윈도우가 그 세션의 화면을 새로 만드는데, 모니터가 없으면
        /// 가장 작은 크기로 잡아 버린다. 대리 프로세스는 그때 다시 뜨지 않으므로
        /// 시작할 때 한 번만 맞춰서는 작아진 채로 남는다. 실제로 그렇게 됐다.
        /// </summary>
        private static DateTime lastScreenCheck;

        private static void KeepScreenSize()
        {
            if ((DateTime.Now - lastScreenCheck).TotalSeconds < 5) return;
            lastScreenCheck = DateTime.Now;

            try
            {
                string note = Display.ApplySaved();
                if (!string.IsNullOrEmpty(note)) SvcLog.Write(note);
            }
            catch { }
        }

        /// <summary>
        /// 방화벽 규칙을 지금 포트에 맞춘다.
        /// 대리 프로세스는 SYSTEM 이라 권한을 따로 물어보지 않고 바꿀 수 있다.
        /// </summary>
        private static void SyncFirewall()
        {
            try
            {
                string exe = System.Reflection.Assembly.GetExecutingAssembly().Location;
                if (Firewall.Ensure(exe, Config.Port))
                    SvcLog.Write("방화벽을 포트 " + Config.Port + " 에 맞췄습니다.");
            }
            catch { }
        }

        internal static string StatusPath { get { return Path.Combine(SvcLog.Dir, "status.txt"); } }

        private static void WriteStatus(Server server)
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.Append("time=").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append("\r\n");
                sb.Append("port=").Append(server.Port).Append("\r\n");
                sb.Append("mcpnote=").Append(server.McpNote.Replace("\r"," ").Replace("\n"," ")).Append("\r\n");
                sb.Append("mcpweburl=").Append(TailscaleMcp.CurrentUrl).Append("\r\n");
                sb.Append("mcpwebready=").Append(TailscaleMcp.Ready ? "1" : "0").Append("\r\n");
                sb.Append("mcpwebnote=").Append(TailscaleMcp.Note.Replace("\r"," ").Replace("\n"," ")).Append("\r\n");
                sb.Append("viewers=").Append(server.Viewers).Append("\r\n");

                // 브릿지로 하위 PC 에 붙어 있는 사람. 이 PC 화면을 보는 것은 아니라서 따로 센다.
                sb.Append("relays=").Append(server.Relays).Append("\r\n");
                sb.Append("url=").Append(RemoteTunnel.IsAlive() ? RemoteTunnel.CurrentUrl : "").Append("\r\n");
                sb.Append("desktop=").Append(Desktops.InputDesktopName()).Append("\r\n");
                sb.Append("session=").Append(Win.MySession()).Append("\r\n");

                // 주소를 만드는 중인지 알려 준다. 창이 "여는 중" 을 보여 줄 수 있게.
                sb.Append("opening=").Append(RemoteTunnel.IsOpening ? 1 : 0).Append("\r\n");

                // 외부망이 왜 이 상태인지 한 줄. 그냥 "닫힘" 만 보면 뭘 해야 할지 모른다.
                // 줄바꿈이 섞이면 이 파일 형식이 깨진다.
                sb.Append("extnote=")
                  .Append((TunnelGuard.Note ?? "").Replace("\r", " ").Replace("\n", " "))
                  .Append("\r\n");

                string p = StatusPath;
                string tmp = p + ".tmp";

                File.WriteAllText(tmp, sb.ToString(), Encoding.UTF8);

                try { File.Copy(tmp, p, true); } catch { }
                try { File.Delete(tmp); } catch { }
            }
            catch { }
        }

        /// <summary>창 쪽에서 읽는다. 대리 프로세스가 살아 있지 않으면 null.</summary>
        internal static AgentStatus ReadStatus()
        {
            try
            {
                if (!File.Exists(StatusPath)) return null;

                AgentStatus st = new AgentStatus();
                foreach (string line in File.ReadAllLines(StatusPath, Encoding.UTF8))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;

                    string k = line.Substring(0, eq).Trim();
                    string v = line.Substring(eq + 1).Trim();

                    if (k == "time") { DateTime d; if (DateTime.TryParse(v, out d)) st.Time = d; }
                    else if (k == "port") int.TryParse(v, out st.Port);
                    else if (k == "viewers") int.TryParse(v, out st.Viewers);
                    else if (k == "relays") int.TryParse(v, out st.Relays);
                    else if (k == "url") st.Url = v;
                    else if (k == "desktop") st.Desktop = v;
                    else if (k == "opening") st.Opening = v == "1";
                    else if (k == "extnote") st.Note = v;
                    else if (k == "mcpnote") st.McpNote = v;
                    else if (k == "mcpweburl") st.McpWebUrl = v;
                    else if (k == "mcpwebready") st.McpWebReady = v == "1";
                    else if (k == "mcpwebnote") st.McpWebNote = v;
                }

                // 6초 넘게 갱신이 없으면 죽은 것으로 본다 (2초마다 쓴다).
                return (DateTime.Now - st.Time).TotalSeconds > 6 ? null : st;
            }
            catch { return null; }
        }
    }

    internal sealed class AgentStatus
    {
        internal DateTime Time;
        internal int Port;
        internal int Viewers;
        internal int Relays;
        internal string Url = "";
        internal string Desktop = "";
        internal bool Opening;
        internal string Note = "";
        internal string McpNote = "";
        internal string McpWebUrl = "", McpWebNote = "";
        internal bool McpWebReady;
    }
}

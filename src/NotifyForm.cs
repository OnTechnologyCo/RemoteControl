// 알림 설정 창.
//
// 본 창에 끼워 넣지 않고 따로 뺐다. 알림 설정은 한 번 하고 다시 안 보는 것인데,
// 그걸 늘 보이는 자리에 두면 정작 매번 봐야 하는 주소와 상태가 밀려난다.
//
// 봇 토큰은 설치 화면에서 입력하거나 구버전에서 이전하여 PC별로 암호화한다.
// 여기서 정하는 것은 "어디로 보낼까"(chat_id) 하나뿐이다.
//
// 그 하나도 손으로 적게 하면 안 된다. 그러려면 브라우저로 getUpdates 를 열어
// 눈으로 숫자를 찾아 옮겨 적어야 하는데, 거기서 대부분 포기한다.
// [내 chat_id 찾기] 가 이 창의 전부라고 봐도 된다.
using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace RemoteControl
{
    internal sealed class NotifyForm : Form
    {
        private readonly TextBox txtChat;
        private readonly Label lblMsg, lblBot;
        private readonly Button btnFind, btnTest, btnSave, btnAddresses;
        private string botName = "";

        internal NotifyForm()
        {
            Font fN = new Font("맑은 고딕", 9);
            Font fS = new Font("맑은 고딕", 8.5f);

            Text = "알림 설정 (텔레그램)";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(520, 332);
            BackColor = Color.FromArgb(15, 23, 42);
            ForeColor = Color.FromArgb(226, 232, 240);
            Font = fN;

            Controls.Add(new Label
            {
                Text = "외부망 주소가 바뀌거나 오래 닫혀 있으면 휴대폰으로 알립니다.\n" +
                       "주소가 바뀌는 순간에는 이 PC 앞에 없기 때문에, 알림은 밖으로 나가야 합니다.",
                Font = fS,
                ForeColor = Color.FromArgb(148, 163, 184),
                Location = new Point(16, 14),
                Size = new Size(490, 34)
            });

            // 봇 이름은 물어봐야 안다. 이름을 모르면 텔레그램에서 찾아갈 수가 없다.
            lblBot = new Label
            {
                Text = Notify.HasBot ? "봇 이름을 확인하는 중…" : "이 PC에 봇 토큰이 등록되지 않았거나 읽을 수 없습니다.\n설치 프로그램의 [텔레그램 알림] 단계에서 등록할 수 있습니다.",
                Font = fS,
                ForeColor = Color.FromArgb(100, 116, 139),
                Location = new Point(16, 56),
                Size = new Size(490, 50)
            };
            Controls.Add(lblBot);

            Controls.Add(Lbl("chat_id", 16, 116, fS));
            txtChat = Txt(96, 113, 160, Notify.ChatId, false, fN);
            Controls.Add(txtChat);

            btnFind = Btn("내 chat_id 찾기", 262, 112, 130, Color.FromArgb(51, 65, 85), fS);
            btnFind.Click += delegate { Find(); };
            Controls.Add(btnFind);

            Button btnOpen = Btn("봇 열기", 402, 112, 70, Color.FromArgb(51, 65, 85), fS);
            btnOpen.Click += delegate
            {
                if (string.IsNullOrEmpty(botName)) { Msg("봇 이름을 아직 못 받았습니다.", true); return; }

                try { System.Diagnostics.Process.Start("https://t.me/" + botName); }
                catch (Exception ex) { Msg("열지 못했습니다: " + ex.Message, true); }
            };
            Controls.Add(btnOpen);

            lblMsg = new Label
            {
                Text = "",
                Font = fS,
                ForeColor = Color.FromArgb(148, 163, 184),
                Location = new Point(16, 150),
                Size = new Size(490, 66)
            };
            Controls.Add(lblMsg);

            btnTest = Btn("시험 발송", 16, 226, 120, Color.FromArgb(51, 65, 85), fN);
            btnTest.Click += delegate { Test(); };
            Controls.Add(btnTest);

            btnAddresses = Btn("접속 주소 보내기",16,267,228,Color.FromArgb(20,130,120),fN);
            btnAddresses.Name="SendConnectionAddresses";
            btnAddresses.Click += delegate { SendAddresses(); };
            Controls.Add(btnAddresses);

            Button btnOff = Btn("알림 끄기", 144, 226, 100, Color.FromArgb(90, 40, 40), fN);
            btnOff.Click += delegate
            {
                Notify.TurnOff();
                txtChat.Text = "";
                Msg("알림을 껐습니다.", false);
            };
            Controls.Add(btnOff);

            btnSave = Btn("저장", 288, 226, 100, Color.FromArgb(20, 130, 120), fN);
            btnSave.Click += delegate { Keep(); };
            Controls.Add(btnSave);

            Button btnClose = Btn("닫기", 396, 226, 100, Color.FromArgb(51, 65, 85), fN);
            btnClose.Click += delegate { Close(); };
            Controls.Add(btnClose);

            if (Notify.IsConfigured) Msg("알림이 켜져 있습니다.", false);

            if (Notify.HasBot) AskBotName();
        }

        /// <summary>
        /// 봇의 @이름을 물어 온다. 창을 여는 순간 붙잡고 있으면 안 되므로 따로 돌린다.
        /// </summary>
        private void AskBotName()
        {
            Thread th = new Thread(delegate ()
            {
                string n = Notify.BotName();

                try
                {
                    if (!IsHandleCreated || IsDisposed) return;

                    BeginInvoke((MethodInvoker)delegate
                    {
                        botName = n;

                        lblBot.Text = string.IsNullOrEmpty(n)
                            ? "봇 이름을 받지 못했습니다. 인터넷을 확인해 주세요."
                            : "1.  텔레그램에서 @" + n + " 을 찾아 아무 말이나 한 번 보냅니다. ([봇 열기])\n" +
                              "2.  [내 chat_id 찾기] → [저장] → [시험 발송].\n" +
                              "     이 PC에 등록된 봇으로 알림을 보냅니다.";

                        lblBot.ForeColor = string.IsNullOrEmpty(n)
                            ? Color.FromArgb(251, 146, 60)
                            : Color.FromArgb(100, 116, 139);
                    });
                }
                catch { }
            });

            th.IsBackground = true;
            th.Start();
        }

        // ---------------------------------------------------------------- 동작

        private void Keep()
        {
            string c = txtChat.Text.Trim();

            if (string.IsNullOrEmpty(c)) { Msg("chat_id 를 넣어야 합니다.", true); return; }

            Notify.SaveChat(c);
            Msg("저장했습니다. [시험 발송] 으로 한 번 확인해 보세요.", false);
        }

        /// <summary>
        /// 텔레그램에 다녀오는 일은 몇 초 걸린다. UI 스레드에서 그대로 하면 창이
        /// 굳어 버려서 사람은 고장으로 본다. 따로 돌리고 버튼을 잠가 둔다.
        /// </summary>
        private void Work(Button b, string busy, Func<string> job)
        {
            b.Enabled = false;
            Msg(busy, false);

            Thread th = new Thread(delegate ()
            {
                string r;
                try { r = job(); }
                catch (Exception ex) { r = "오류: " + ex.Message; }

                try
                {
                    if (!IsHandleCreated || IsDisposed) return;

                    BeginInvoke((MethodInvoker)delegate
                    {
                        b.Enabled = true;
                        Msg(r, r.StartsWith("✓") ? false : true);
                    });
                }
                catch { }
            });

            th.IsBackground = true;
            th.Start();
        }

        private void Find()
        {
            Work(btnFind, "찾는 중…", delegate
            {
                string who;
                string id = Notify.FindChatId(out who);
                if (string.IsNullOrEmpty(id)) return Notify.LastError;

                try { BeginInvoke((MethodInvoker)delegate { txtChat.Text = id; }); }
                catch { }

                // 누구 것인지 반드시 보여 준다. 봇 하나를 여럿이 쓰면 여기서 남의 것이
                // 잡힐 수 있고, 그대로 저장하면 내 원격제어 주소가 남에게 간다.
                return "✓ " + (who.Length > 0 ? who : "(이름 없음)") + "  →  " + id + "\r\n" +
                       "내가 맞으면 [저장]. 아니면 봇에게 한 번 더 보내고 다시 누르세요.";
            });
        }

        private void Test()
        {
            string c = txtChat.Text.Trim();
            if (string.IsNullOrEmpty(c)) { Msg("chat_id 를 넣어야 합니다.", true); return; }

            // 시험은 지금 칸에 있는 값으로 한다. 저장된 것으로 하면 방금 고친 값이
            // 아니라 옛 값을 시험하게 되어, 틀린 값을 맞다고 믿게 된다.
            Notify.SaveChat(c);

            Work(btnTest, "보내는 중…", delegate
            {
                string body = "🔔 원격제어 알림 시험\n\n이 글이 보이면 설정이 끝났습니다.\n" +
                              DateTime.Now.ToString("MM-dd HH:mm");

                return Notify.Send(body)
                    ? "✓ 보냈습니다. 텔레그램을 확인해 보세요."
                    : Notify.LastError;
            });
        }

        private void SendAddresses()
        {
            string c=txtChat.Text.Trim();
            if(string.IsNullOrEmpty(c)){Msg("chat_id를 입력하고 받을 대상을 확인하세요.",true);return;}
            Notify.SaveChat(c);
            Work(btnAddresses,"현재 접속 주소를 보내는 중…",delegate {
                return Notify.Send(Notify.CurrentAddresses())
                    ? "✓ 화면·MCP 접속 주소와 포트를 보냈습니다. 준비되지 않은 연결은 상태로 표시됩니다."
                    : Notify.LastError;
            });
        }

        private void Msg(string s, bool bad)
        {
            lblMsg.Text = s;
            lblMsg.ForeColor = bad ? Color.FromArgb(251, 146, 60) : Color.FromArgb(52, 211, 153);
        }

        // ---------------------------------------------------------------- 부품

        private static Label Lbl(string s, int x, int y, Font f)
        {
            return new Label
            {
                Text = s,
                Font = f,
                ForeColor = Color.FromArgb(148, 163, 184),
                Location = new Point(x, y),
                AutoSize = true
            };
        }

        private static TextBox Txt(int x, int y, int w, string v, bool pass, Font f)
        {
            return new TextBox
            {
                Location = new Point(x, y),
                Size = new Size(w, 24),
                Text = v ?? "",
                UseSystemPasswordChar = pass,
                BackColor = Color.FromArgb(30, 41, 59),
                ForeColor = Color.FromArgb(34, 211, 238),
                BorderStyle = BorderStyle.FixedSingle,
                Font = f
            };
        }

        private static Button Btn(string s, int x, int y, int w, Color bg, Font f)
        {
            Button b = new Button
            {
                Text = s,
                Location = new Point(x, y),
                Size = new Size(w, 28),
                BackColor = bg,
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = f
            };
            b.FlatAppearance.BorderSize = 0;
            return b;
        }
    }
}

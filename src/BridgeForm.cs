// 하위 PC 관리 창 (브릿지).
//
// 본 창에 끼워 넣지 않고 따로 뺐다. 알림 설정과 같은 이유다 - 한 번 정해 놓고 다시
// 안 보는 것인데 늘 보이는 자리를 차지하면, 정작 매번 봐야 하는 주소와 상태가 밀린다.
//
// 여기서 정하는 것은 하위 PC 의 주소와 로그인 정보다. 비밀번호는 DPAPI 로 봉인해
// 두 벌(내 계정 / 이 기계)로 저장한다. 실제로 로그인하는 것은 SYSTEM 대리 프로세스라
// 기계용 사본이 없으면 밖에서 들어왔을 때 조용히 실패한다.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace RemoteControl
{
    internal sealed class BridgeForm : Form
    {
        private readonly ListView list;
        private readonly Label lblMsg;
        private readonly Button btnEdit, btnDel, btnTest;

        private List<Peer> peers;

        internal BridgeForm()
        {
            Font fN = new Font("맑은 고딕", 9);
            Font fS = new Font("맑은 고딕", 8.5f);

            Text = "하위 PC (브릿지)";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(600, 400);
            BackColor = Color.FromArgb(15, 23, 42);
            ForeColor = Color.FromArgb(226, 232, 240);
            Font = fN;

            Controls.Add(new Label
            {
                Text = "여기에 넣은 PC 는 이 PC 를 거쳐 접속됩니다.\n" +
                       "브라우저로 들어오면 어느 PC 를 볼지 고르는 창이 먼저 뜹니다.",
                Font = fS,
                ForeColor = Color.FromArgb(148, 163, 184),
                Location = new Point(16, 14),
                Size = new Size(570, 34)
            });

            list = new ListView
            {
                Location = new Point(16, 54),
                Size = new Size(430, 300),
                View = View.Details,
                FullRowSelect = true,
                MultiSelect = false,
                HideSelection = false,
                BackColor = Color.FromArgb(11, 15, 24),
                ForeColor = Color.FromArgb(203, 213, 225),
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("맑은 고딕", 8.5f)
            };

            list.Columns.Add("번호", 44);
            list.Columns.Add("이름", 130);
            list.Columns.Add("주소", 140);
            list.Columns.Add("아이디", 70);
            list.Columns.Add("비밀번호", 60);

            list.SelectedIndexChanged += delegate { SyncButtons(); };
            list.DoubleClick += delegate { Edit(); };
            Controls.Add(list);

            int x = 458, y = 54;

            Controls.Add(Btn("추가", x, y, delegate { Add(); }, Color.FromArgb(20, 130, 120)));
            btnEdit = Btn("고치기", x, y + 34, delegate { Edit(); }, Color.FromArgb(51, 65, 85));
            Controls.Add(btnEdit);
            btnDel = Btn("지우기", x, y + 68, delegate { Del(); }, Color.FromArgb(51, 65, 85));
            Controls.Add(btnDel);

            btnTest = Btn("연결 시험", x, y + 112, delegate { Test(); }, Color.FromArgb(51, 65, 85));
            Controls.Add(btnTest);

            Controls.Add(Btn("랜에서 찾기", x, y + 146, delegate { ScanLan(); }, Color.FromArgb(120, 60, 160)));

            Controls.Add(Btn("닫기", x, y + 266, delegate { Close(); }, Color.FromArgb(51, 65, 85)));

            lblMsg = new Label
            {
                Text = "",
                Font = fS,
                ForeColor = Color.FromArgb(148, 163, 184),
                Location = new Point(16, 362),
                Size = new Size(570, 30)
            };
            Controls.Add(lblMsg);

            // 창(내 계정)과 대리 프로세스(SYSTEM)는 다른 프로세스다.
            // 저쪽이 고친 것을 못 보고 덮어쓰지 않도록 열 때 파일에서 다시 읽는다.
            Config.Reload();
            Reload();
        }

        private Button Btn(string s, int x, int y, EventHandler on, Color bg)
        {
            Button b = new Button
            {
                Text = s,
                Location = new Point(x, y),
                Size = new Size(126, 28),
                BackColor = bg,
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("맑은 고딕", 8.5f),
                UseVisualStyleBackColor = false
            };
            b.FlatAppearance.BorderSize = 0;
            b.Click += on;
            return b;
        }

        // ---------------------------------------------------------------- 목록

        private void Reload()
        {
            peers = Bridge.List();

            list.BeginUpdate();
            list.Items.Clear();

            foreach (Peer p in peers)
            {
                ListViewItem it = new ListViewItem(p.Id.ToString());
                it.SubItems.Add(p.Name);
                it.SubItems.Add(p.Addr);
                it.SubItems.Add(p.User);
                it.SubItems.Add(Bridge.HasPassword(p.Id) ? "있음" : "없음");
                it.Tag = p;

                // 비밀번호가 없으면 붙을 수 없다. 목록에서 바로 보이게 색을 뺀다.
                if (!Bridge.HasPassword(p.Id)) it.ForeColor = Color.FromArgb(248, 113, 113);

                list.Items.Add(it);
            }

            list.EndUpdate();
            SyncButtons();
        }

        private void SyncButtons()
        {
            bool any = list.SelectedItems.Count > 0;
            btnEdit.Enabled = any;
            btnDel.Enabled = any;
            btnTest.Enabled = any;
        }

        private Peer Selected()
        {
            if (list.SelectedItems.Count == 0) return null;
            return list.SelectedItems[0].Tag as Peer;
        }

        private void Msg(string s, bool bad)
        {
            lblMsg.ForeColor = bad ? Color.FromArgb(248, 113, 113) : Color.FromArgb(148, 163, 184);
            lblMsg.Text = s;
        }

        // ---------------------------------------------------------------- 조작

        private void Add()
        {
            using (PeerForm f = new PeerForm(null))
            {
                if (f.ShowDialog(this) != DialogResult.OK) return;

                peers.Add(f.Result);
                Bridge.Save(peers);

                // 번호는 Save 가 정해 준다. 그 번호로 비밀번호를 봉인한다.
                if (f.Result.Id > 0 && f.Password != null)
                    Bridge.SetPassword(f.Result.Id, f.Password);

                Reload();
                Msg(f.Result.Name + " 을(를) 넣었습니다. 번호 " + f.Result.Id + " (주소에 ?pc=" + f.Result.Id + ")", false);
            }
        }

        private void Edit()
        {
            Peer p = Selected();
            if (p == null) return;

            using (PeerForm f = new PeerForm(p))
            {
                if (f.ShowDialog(this) != DialogResult.OK) return;

                Bridge.Save(peers);

                // 비어 있으면 손대지 않은 것이다. 고치기에서 빈칸이 곧 "지우기" 가 되면
                // 이름만 바꾸려다 비밀번호를 날린다.
                if (f.Password != null) Bridge.SetPassword(p.Id, f.Password);

                Reload();
                Msg(p.Name + " 을(를) 고쳤습니다.", false);
            }
        }

        private void Del()
        {
            Peer p = Selected();
            if (p == null) return;

            if (MessageBox.Show(this, p.Name + " (" + p.Addr + ") 을(를) 목록에서 지울까요?\n\n" +
                                      "번호 " + p.Id + " 는 다시 쓰지 않습니다.",
                                "하위 PC 지우기", MessageBoxButtons.YesNo, MessageBoxIcon.Question)
                != DialogResult.Yes) return;

            peers.Remove(p);
            Bridge.Save(peers);
            Reload();
            Msg(p.Name + " 을(를) 지웠습니다.", false);
        }

        private void Test()
        {
            Peer p = Selected();
            if (p == null) return;

            Msg(p.Addr + " 에 붙어 보는 중…", false);
            Enabled = false;

            // 붙는 데 최대 5초가 걸린다. 창을 여는 스레드에서 하면 그동안 얼어붙는다.
            Thread th = new Thread(delegate ()
            {
                string why;
                bool ok = Bridge.Test(p, null, out why);

                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        Enabled = true;
                        Msg(ok ? p.Name + " 에 정상적으로 붙었습니다." : why, !ok);
                    });
                }
                catch { }
            });

            th.IsBackground = true;
            th.Start();
        }

        private void ScanLan()
        {
            using (ScanForm f = new ScanForm())
            {
                if (f.ShowDialog(this) != DialogResult.OK) return;
                if (f.Picked.Count == 0) return;

                foreach (Peer np in f.Picked)
                {
                    peers.Add(np);
                    Bridge.Save(peers);                       // 번호를 받으려고 하나씩 저장한다

                    if (np.Id > 0) Bridge.SetPassword(np.Id, f.Password);
                }

                Reload();
                Msg(f.Picked.Count + "대를 넣었습니다. [연결 시험] 으로 확인해 보세요.", false);
            }
        }
    }

    // ==================================================================== 한 대 편집

    internal sealed class PeerForm : Form
    {
        private readonly TextBox txtName, txtHost, txtUser, txtPass;
        private readonly NumericUpDown numPort;
        private readonly Peer target;

        internal Peer Result;

        /// <summary>비워 두면 null. "손대지 않았다" 와 "빈 비밀번호" 를 구별해야 한다.</summary>
        internal string Password;

        internal PeerForm(Peer edit)
        {
            target = edit;

            Font fN = new Font("맑은 고딕", 9);
            Font fS = new Font("맑은 고딕", 8.5f);

            Text = edit == null ? "하위 PC 넣기" : "하위 PC 고치기";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(400, 280);
            BackColor = Color.FromArgb(15, 23, 42);
            ForeColor = Color.FromArgb(226, 232, 240);
            Font = fN;

            Controls.Add(Lbl("이름", 16, 20, fS));
            txtName = Txt(110, 16, 260, edit == null ? "" : edit.Name, false, fN);
            Controls.Add(txtName);

            Controls.Add(Lbl("주소 (IP)", 16, 56, fS));
            txtHost = Txt(110, 52, 180, edit == null ? "" : edit.Host, false, fN);
            Controls.Add(txtHost);

            Controls.Add(Lbl("포트", 16, 92, fS));
            numPort = new NumericUpDown
            {
                Minimum = 1,
                Maximum = 65535,
                Value = edit == null ? 5900 : edit.Port,
                Location = new Point(110, 88),
                Size = new Size(80, 24),
                BackColor = Color.FromArgb(30, 41, 59),
                ForeColor = Color.FromArgb(34, 211, 238),
                BorderStyle = BorderStyle.FixedSingle,
                Font = fN
            };
            Controls.Add(numPort);

            Controls.Add(Lbl("아이디", 16, 128, fS));
            txtUser = Txt(110, 124, 180, edit == null ? "admin" : edit.User, false, fN);
            Controls.Add(txtUser);

            Controls.Add(Lbl("비밀번호", 16, 164, fS));
            txtPass = Txt(110, 160, 180, "", true, fN);
            Controls.Add(txtPass);

            Controls.Add(new Label
            {
                Text = edit == null
                    ? "그 PC 의 원격제어 로그인 정보입니다. 이 PC 에 봉인해서 보관합니다."
                    : "비워 두면 지금 비밀번호를 그대로 씁니다.",
                Font = fS,
                ForeColor = Color.FromArgb(100, 116, 139),
                Location = new Point(16, 192),
                Size = new Size(360, 32)
            });

            Button ok = new Button
            {
                Text = "저장",
                Location = new Point(200, 234),
                Size = new Size(84, 28),
                BackColor = Color.FromArgb(20, 130, 120),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = fS,
                UseVisualStyleBackColor = false
            };
            ok.FlatAppearance.BorderSize = 0;
            ok.Click += delegate { Save(); };
            Controls.Add(ok);

            Button no = new Button
            {
                Text = "취소",
                Location = new Point(292, 234),
                Size = new Size(84, 28),
                BackColor = Color.FromArgb(51, 65, 85),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = fS,
                UseVisualStyleBackColor = false,
                DialogResult = DialogResult.Cancel
            };
            no.FlatAppearance.BorderSize = 0;
            Controls.Add(no);

            AcceptButton = ok;
            CancelButton = no;
        }

        private void Save()
        {
            string host = txtHost.Text.Trim();
            if (host.Length == 0)
            {
                MessageBox.Show(this, "주소를 넣어 주세요.", "하위 PC", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string name = txtName.Text.Trim();
            if (name.Length == 0) name = host;

            string user = txtUser.Text.Trim();
            if (user.Length == 0) user = "admin";

            Peer p = target ?? new Peer();
            p.Name = name;
            p.Host = host;
            p.Port = (int)numPort.Value;
            p.User = user;

            Result = p;
            Password = txtPass.Text.Length > 0 ? txtPass.Text : null;

            // 새로 넣는 것인데 비밀번호가 없으면 붙을 수가 없다. 지금 말해 주는 편이 낫다.
            if (target == null && Password == null)
            {
                MessageBox.Show(this, "비밀번호를 넣어 주세요. 없으면 그 PC 에 로그인할 수 없습니다.",
                                "하위 PC", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            DialogResult = DialogResult.OK;
            Close();
        }

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
                Text = v,
                UseSystemPasswordChar = pass,
                BackColor = Color.FromArgb(30, 41, 59),
                ForeColor = Color.FromArgb(34, 211, 238),
                BorderStyle = BorderStyle.FixedSingle,
                Font = f
            };
        }
    }

    // ==================================================================== 랜에서 찾기

    internal sealed class ScanForm : Form
    {
        private readonly ComboBox cboNet;
        private readonly NumericUpDown numPort;
        private readonly CheckedListBox found;
        private readonly TextBox txtUser, txtPass;
        private readonly Label lblMsg;
        private readonly Button btnScan, btnAdd;

        internal readonly List<Peer> Picked = new List<Peer>();
        internal string Password = "";

        private volatile bool busy;

        internal ScanForm()
        {
            Font fN = new Font("맑은 고딕", 9);
            Font fS = new Font("맑은 고딕", 8.5f);

            Text = "랜에서 원격제어 PC 찾기";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(460, 400);
            BackColor = Color.FromArgb(15, 23, 42);
            ForeColor = Color.FromArgb(226, 232, 240);
            Font = fN;

            Controls.Add(new Label
            {
                Text = "같은 대역을 훑어 원격제어가 떠 있는 PC 를 찾습니다.\n" +
                       "5900 은 VNC 도 쓰는 자리라, 실제로 응답하는 것만 골라 냅니다.",
                Font = fS,
                ForeColor = Color.FromArgb(148, 163, 184),
                Location = new Point(16, 12),
                Size = new Size(430, 34)
            });

            cboNet = new ComboBox
            {
                Location = new Point(16, 54),
                Size = new Size(180, 24),
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Color.FromArgb(30, 41, 59),
                ForeColor = Color.FromArgb(34, 211, 238),
                FlatStyle = FlatStyle.Flat,
                Font = fN
            };

            foreach (string ip in Bridge.LocalIps()) cboNet.Items.Add(ip);
            if (cboNet.Items.Count > 0) cboNet.SelectedIndex = 0;
            Controls.Add(cboNet);

            numPort = new NumericUpDown
            {
                Minimum = 1,
                Maximum = 65535,
                Value = 5900,
                Location = new Point(206, 54),
                Size = new Size(74, 24),
                BackColor = Color.FromArgb(30, 41, 59),
                ForeColor = Color.FromArgb(34, 211, 238),
                BorderStyle = BorderStyle.FixedSingle,
                Font = fN
            };
            Controls.Add(numPort);

            btnScan = Btn("찾기", 290, 53, 156, Color.FromArgb(120, 60, 160));
            btnScan.Click += delegate { Go(); };
            Controls.Add(btnScan);

            found = new CheckedListBox
            {
                Location = new Point(16, 90),
                Size = new Size(430, 180),
                BackColor = Color.FromArgb(11, 15, 24),
                ForeColor = Color.FromArgb(203, 213, 225),
                BorderStyle = BorderStyle.FixedSingle,
                CheckOnClick = true,
                Font = new Font("맑은 고딕", 8.5f)
            };
            Controls.Add(found);

            Controls.Add(new Label
            {
                Text = "찾은 PC 는 로그인 정보가 같다고 보고 한 번만 받습니다.",
                Font = fS,
                ForeColor = Color.FromArgb(100, 116, 139),
                Location = new Point(16, 276),
                AutoSize = true
            });

            Controls.Add(new Label
            {
                Text = "아이디",
                Font = fS,
                ForeColor = Color.FromArgb(148, 163, 184),
                Location = new Point(16, 302),
                AutoSize = true
            });

            txtUser = new TextBox
            {
                Location = new Point(70, 298),
                Size = new Size(120, 24),
                Text = "admin",
                BackColor = Color.FromArgb(30, 41, 59),
                ForeColor = Color.FromArgb(34, 211, 238),
                BorderStyle = BorderStyle.FixedSingle,
                Font = fN
            };
            Controls.Add(txtUser);

            Controls.Add(new Label
            {
                Text = "비밀번호",
                Font = fS,
                ForeColor = Color.FromArgb(148, 163, 184),
                Location = new Point(204, 302),
                AutoSize = true
            });

            txtPass = new TextBox
            {
                Location = new Point(266, 298),
                Size = new Size(120, 24),
                UseSystemPasswordChar = true,
                BackColor = Color.FromArgb(30, 41, 59),
                ForeColor = Color.FromArgb(34, 211, 238),
                BorderStyle = BorderStyle.FixedSingle,
                Font = fN
            };
            Controls.Add(txtPass);

            lblMsg = new Label
            {
                Text = "",
                Font = fS,
                ForeColor = Color.FromArgb(148, 163, 184),
                Location = new Point(16, 334),
                Size = new Size(430, 20)
            };
            Controls.Add(lblMsg);

            btnAdd = Btn("고른 것 넣기", 270, 360, 96, Color.FromArgb(20, 130, 120));
            btnAdd.Click += delegate { Take(); };
            btnAdd.Enabled = false;
            Controls.Add(btnAdd);

            Button no = Btn("닫기", 372, 360, 74, Color.FromArgb(51, 65, 85));
            no.DialogResult = DialogResult.Cancel;
            Controls.Add(no);

            CancelButton = no;

            if (cboNet.Items.Count == 0)
                Msg("이 PC 에서 랜 주소를 찾지 못했습니다. 주소를 직접 넣어 주세요.", true);
        }

        private Button Btn(string s, int x, int y, int w, Color bg)
        {
            Button b = new Button
            {
                Text = s,
                Location = new Point(x, y),
                Size = new Size(w, 26),
                BackColor = bg,
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("맑은 고딕", 8.5f),
                UseVisualStyleBackColor = false
            };
            b.FlatAppearance.BorderSize = 0;
            return b;
        }

        private void Msg(string s, bool bad)
        {
            lblMsg.ForeColor = bad ? Color.FromArgb(248, 113, 113) : Color.FromArgb(148, 163, 184);
            lblMsg.Text = s;
        }

        private void Go()
        {
            if (busy || cboNet.SelectedItem == null) return;

            busy = true;
            btnScan.Enabled = false;
            btnAdd.Enabled = false;
            found.Items.Clear();

            string local = cboNet.SelectedItem.ToString();
            int port = (int)numPort.Value;

            // 254개를 훑는 동안 창이 멈추면 안 된다. 진행 상황을 초 대신 개수로 보여 준다.
            Thread th = new Thread(delegate ()
            {
                List<string> hits = Bridge.Scan(local, port, delegate (int done, int all)
                {
                    if (done % 16 != 0 && done != all) return;

                    try { BeginInvoke((MethodInvoker)delegate { Msg("훑는 중… " + done + "/" + all, false); }); }
                    catch { }
                });

                // 이름은 하나씩 물어봐야 안다. 훑기가 끝난 뒤에 한다.
                List<string> names = new List<string>();
                foreach (string ip in hits) names.Add(Bridge.NameOf(ip));

                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        for (int i = 0; i < hits.Count; i++)
                            found.Items.Add(names[i] + "   (" + hits[i] + ":" + port + ")", true);

                        busy = false;
                        btnScan.Enabled = true;
                        btnAdd.Enabled = hits.Count > 0;

                        Msg(hits.Count == 0
                            ? "원격제어가 떠 있는 PC 를 찾지 못했습니다. 포트와 방화벽을 확인하세요."
                            : hits.Count + "대를 찾았습니다.", hits.Count == 0);

                        tags = hits;
                        tagNames = names;
                        tagPort = port;
                    });
                }
                catch { busy = false; }
            });

            th.IsBackground = true;
            th.Start();
        }

        private List<string> tags = new List<string>();
        private List<string> tagNames = new List<string>();
        private int tagPort = 5900;

        private void Take()
        {
            if (txtPass.Text.Length == 0)
            {
                MessageBox.Show(this, "비밀번호를 넣어 주세요. 없으면 그 PC 에 로그인할 수 없습니다.",
                                "랜에서 찾기", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string user = txtUser.Text.Trim();
            if (user.Length == 0) user = "admin";

            Picked.Clear();

            for (int i = 0; i < found.Items.Count && i < tags.Count; i++)
            {
                if (!found.GetItemChecked(i)) continue;

                Peer p = new Peer();
                p.Name = tagNames[i];
                p.Host = tags[i];
                p.Port = tagPort;
                p.User = user;
                Picked.Add(p);
            }

            if (Picked.Count == 0)
            {
                Msg("넣을 PC 를 골라 주세요.", true);
                return;
            }

            Password = txtPass.Text;
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}

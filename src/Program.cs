// 원격제어 - 본체.

//

// 한 exe 가 네 가지로 동작한다. 어느 쪽인지는 실행 인자로 갈린다.

//   (없음)       창. 설정하고 상태를 본다.

//   --agent      대리 프로세스. 실제로 화면을 잡고 입력을 넣는다. 창이 없다.

//   --service    서비스. 대리 프로세스를 사람이 쓰는 세션에 밀어 넣는 일만 한다.

//   --install    서비스 등록 (관리자 권한으로 자기를 다시 띄워 여기로 들어온다)

//   --uninstall  서비스 제거

//

// 창은 설정과 상태 확인용이다. 접속자가 있으면 분명히 표시한다 -

// 원격제어는 이 PC 를 쓰는 사람이 누가 보고 있는지 알 수 있어야 한다.

using System;

using System.Drawing;

using System.IO;

using System.Reflection;

using System.Threading;

using System.Windows.Forms;



namespace RemoteControl

{

    internal sealed class MainForm : Form

    {

        private readonly Server server = new Server();



        private Label lblState, lblViewers;

        private TextBox txtLan, txtExt;

        private Button btnCopyLan, btnCopyExt, btnOpenExt;

        private ListBox lstLog;

        private NumericUpDown numPort;

        private ComboBox cboScreen;

        private TextBox txtUser, txtPass;

        private Button btnApply, btnStart, btnTunnel, btnNotify, btnBridge;

        private Label lblTunnelProvider;

        private Label lblExt;

        private CheckBox chkKeep;

        private Button btnSvc;

        private Label lblSvc;

        private NotifyIcon tray;

        private ToolStripMenuItem miLan, miExt, miTunnel, miSvc;

        private System.Windows.Forms.Timer tick;



        private Icon icoIdle, icoBusy;

        private bool trayBusy;

        private bool exiting;

        private bool startHidden;



        private static bool administratorWindow;

        private bool svcMode;          // 서비스가 돌고 있어 우리가 서버를 열지 않는 상태



        [STAThread]

        static int Main(string[] args)

        {

            string mode = args != null && args.Length > 0 ? args[0].ToLowerInvariant() : "";



            switch (mode)

            {

                case "--admin-ui":
                    if (!RemoteControlSecrets.ConfigProtection.IsAdministrator) return 5;
                    Config.EnsureWritable();
                    administratorWindow = true;
                    Native.MakeDpiAware(); Application.EnableVisualStyles();
                    Application.Run(new MainForm(false)); return 0;

                case "--mcp-client":

                    return McpClient.Run(args);

                case "--mcp-config":

                    Native.MakeDpiAware(); Application.EnableVisualStyles();

                    Application.Run(new McpClientForm()); return 0;

                case "--mcp-job":

                    return McpJobRunner.Run(args);

                case "--service":

                    ServiceHost.RunAsService();

                    return 0;



                case "--agent":

                {

                    // 서비스가 재시작을 겹쳐 부르면 대리 프로세스가 둘이 될 수 있다.

                    // 둘이 되면 포트를 서로 뺏어 접속이 됐다 안 됐다 한다.

                    bool first;

                    using (Mutex only = new Mutex(true, "Global\\RemoteControl.Agent", out first))

                    {

                        if (!first) return 0;



                        Native.MakeDpiAware();

                        Agent.Run();

                    }

                    return 0;

                }



                case "--install":

                {

                    string e = ServiceHost.Install();

                    if (!string.IsNullOrEmpty(e)) { SvcLog.Write("설치 실패: " + e); return 1; }

                    SvcLog.Write("서비스 설치 완료");

                    return 0;

                }



                case "--uninstall":

                {

                    string e = ServiceHost.Uninstall();

                    if (!string.IsNullOrEmpty(e)) { SvcLog.Write("제거 실패: " + e); return 1; }

                    SvcLog.Write("서비스 제거 완료");

                    return 0;

                }



                case "--startsvc":

                {

                    string e = ServiceHost.StartNow();

                    if (!string.IsNullOrEmpty(e)) { SvcLog.Write("서비스 시작 실패: " + e); return 1; }

                    return 0;

                }

            }



            if (RemoteControlSecrets.ConfigProtection.IsAdministrator) Config.EnsureWritable();
            else if (!ServiceHost.IsRunning())
            {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Assembly.GetExecutingAssembly().Location, "--admin-ui") { UseShellExecute = true, Verb = "runas" }); return 0; }
                catch { return 5; }
            }

            // 배율이 걸린 화면에서 좌표가 어긋나지 않게 가장 먼저 선언한다.

            Native.MakeDpiAware();



            // 두 번 켜면 둘 다 같은 포트를 물려다 하나가 실패한다. 그러면 프로그램은

            // 떠 있는데 접속은 안 되는, 원인을 찾기 어려운 상태가 된다. 실제로 그렇게 됐다.

            // (Local\ 인 이유: 로그온 세션마다 창 하나면 충분하고, Global\ 은 권한이 필요하다)

            bool fresh;

            using (Mutex only = new Mutex(true, "Local\\RemoteControl.Ui", out fresh))

            {

                if (!fresh)

                {

                    // 이미 떠 있다. 두 번째는 조용히 물러나되, 먼저 있던 창을 띄워 준다.

                    //

                    // 예전에는 "이미 실행 중" 이라고만 알리고 끝냈다. 그런데 창 없이

                    // 트레이에서 시작하도록 해 둔 탓에, 사용자 눈에는 아이콘을 눌러도

                    // 아무 일도 안 일어나는 것처럼 보였다. 몇 번을 눌러야 겨우 뭔가

                    // 나오는 상태였다. 누르면 창이 나오는 게 맞다.

                    Wake();

                    return 0;

                }



                Application.EnableVisualStyles();

                Application.SetCompatibleTextRenderingDefault(false);



                // --tray 로 뜨면 창 없이 트레이에서 시작한다.

                // 로그인할 때 자동으로 뜨는 경우가 그것이다. 사람이 직접 아이콘을

                // 눌렀을 때는 무조건 창을 보여 준다.

                Application.Run(new MainForm(mode == "--tray"));

            }



            return 0;

        }



        internal const string WakeEvent = "Local\\RemoteControl.Show";



        /// <summary>이미 떠 있는 쪽에 "창 좀 띄워" 라고 알린다.</summary>

        private static void Wake()

        {

            try

            {

                using (EventWaitHandle h = EventWaitHandle.OpenExisting(WakeEvent)) h.Set();

            }

            catch { }

        }



        /// <summary>이 프로그램의 판 번호. 창 제목과 머리글에 보인다.</summary>

        internal const string Version = "1.9.5";



        internal MainForm(bool toTray)

        {

            Text = "원격제어 v" + Version;

            ClientSize = new Size(560, 756);

            BackColor = Color.FromArgb(15, 23, 42);

            ForeColor = Color.White;

            FormBorderStyle = FormBorderStyle.FixedSingle;

            MaximizeBox = false;

            StartPosition = FormStartPosition.CenterScreen;



            LoadIcons();

            BuildUi();

            BuildTray();
            if (!RemoteControlSecrets.ConfigProtection.IsAdministrator) {
                txtUser.ReadOnly = true; txtPass.Enabled = false; numPort.Enabled = false;
                cboScreen.Enabled = false; chkKeep.Enabled = false; btnApply.Text = "관리자 설정";
            }



            server.Notice += OnNotice;



            if (RemoteControlSecrets.ConfigProtection.IsAdministrator) Tunnel.EnsureMachineKey();

            svcMode = ServiceHost.IsRunning();



            if (svcMode)

            {

                Log("서비스가 실행 중입니다. 화면과 입력은 서비스 쪽에서 처리합니다.");

                btnStart.Enabled = false;

                btnTunnel.Enabled = true;

            }

            else if (!Config.HasPassword)

            {

                Log("먼저 비밀번호를 정해 주세요. 정하기 전에는 시작할 수 없습니다.");

                btnStart.Enabled = false;

            }

            else

            {

                Tunnel.KillOrphans();

                StartServer();

            }



            // 창 없이 시작하는 것은 "로그인할 때 자동으로 뜬 경우" 뿐이다 (--tray).

            // 사람이 아이콘을 눌러 켰는데 아무것도 안 보이면 고장으로 보인다.

            startHidden = toTray && Config.HasPassword;



            if (!administratorWindow) StartWakeWatcher();



            tick = new System.Windows.Forms.Timer();

            tick.Interval = 1000;

            tick.Tick += delegate { UpdateStatus(); };

            tick.Start();



            if (!svcMode) EnableTunnelGuard();

            int savedX, savedY;
            if (UiPreferences.LoadPosition(out savedX, out savedY) && Screen.AllScreens.Length > 0 &&
                Array.Exists(Screen.AllScreens, delegate(Screen screen) { return screen.WorkingArea.Contains(new Point(savedX, savedY)); }))
            { StartPosition = FormStartPosition.Manual; Location = new Point(savedX, savedY); }
            Shown += delegate { UpdateStatus(); };



            FormClosing += delegate (object s, FormClosingEventArgs e)

            {

                try { UiPreferences.SavePosition(Location.X, Location.Y); } catch { }
                if (administratorWindow) exiting = true;
                // [X] 는 종료가 아니라 트레이로 숨기기. 원격제어는 창을 닫아도

                // 계속 대기해야 쓸모가 있다.

                if (!exiting && e.CloseReason == CloseReason.UserClosing)

                {

                    e.Cancel = true;

                    Hide();

                    ShowInTaskbar = false;

                    tray.ShowBalloonTip(3000, "원격제어", "트레이에서 계속 실행 중입니다.", ToolTipIcon.Info);

                    return;

                }



                TunnelGuard.Disable();

                server.Stop();

                RemoteTunnel.Stop();

                InputInjector.ReleaseAll();

                if (tray != null) { tray.Visible = false; tray.Dispose(); }

            };

        }



        /// <summary>

        /// Application.Run 은 넘겨받은 창을 무조건 한 번 띄운다.

        /// 트레이에서 시작하려면 그 첫 번째만 가로채야 한다.

        /// </summary>

        protected override void SetVisibleCore(bool value)

        {

            if (startHidden && value)

            {

                startHidden = false;

                ShowInTaskbar = false;



                // 자동 시작은 알림 팝업 없이 트레이 아이콘과 상태 표시만 유지한다.



                base.SetVisibleCore(false);

                return;

            }



            base.SetVisibleCore(value);

        }






        // ---------------------------------------------------------------- 아이콘



        private void LoadIcons()

        {

            icoIdle = LoadIcon("remotecontrol.ico");

            icoBusy = LoadIcon("rc-busy.ico");



            if (icoIdle != null) Icon = icoIdle;

        }



        private static Icon LoadIcon(string name)

        {

            try

            {

                using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))

                {

                    if (s == null) return null;



                    // 트레이는 작은 아이콘을 쓴다. 화면 배율에 맞는 크기를 골라 달라고 한다.

                    return new Icon(s, SystemInformation.SmallIconSize);

                }

            }

            catch { return null; }

        }



        // ---------------------------------------------------------------- 화면 구성



        private void BuildUi()

        {

            Font fB = new Font("맑은 고딕", 9, FontStyle.Bold);

            Font fN = new Font("맑은 고딕", 9);

            Font fS = new Font("맑은 고딕", 8);



            Label t = new Label

            {

                Text = "🖥️  원격제어",

                Font = new Font("맑은 고딕", 13, FontStyle.Bold),

                ForeColor = Color.FromArgb(34, 211, 238),

                Location = new Point(18, 14),

                AutoSize = true

            };

            Controls.Add(t);



            Controls.Add(new Label

            {

                Text = "v" + Version,

                Font = new Font("맑은 고딕", 8.5f),

                ForeColor = Color.FromArgb(100, 116, 139),

                Location = new Point(122, 24),

                AutoSize = true

            });



            lblState = new Label

            {

                Text = "대기 중",

                Font = fB,

                ForeColor = Color.FromArgb(148, 163, 184),

                Location = new Point(18, 46),

                Size = new Size(520, 20)

            };

            Controls.Add(lblState);



            // 주소는 눈으로만 보라고 두면 쓸모가 없다. 휴대폰으로 보내려면 복사가 돼야 한다.

            // 그래서 읽기 전용 입력칸에 담는다 - 끌어서 고를 수도 있고 Ctrl+C 도 먹는다.

            Controls.Add(Lbl("내부망", 18, 74, fS));

            txtLan = Addr(70, 71, 330);

            Controls.Add(txtLan);



            btnCopyLan = Btn("복사", 406, 70, 62, Color.FromArgb(51, 65, 85), fS);

            btnCopyLan.Click += delegate { Copy(txtLan.Text); };

            Controls.Add(btnCopyLan);



            Controls.Add(Lbl("외부망", 18, 106, fS));

            txtExt = Addr(70, 103, 330);

            Controls.Add(txtExt);



            btnCopyExt = Btn("복사", 406, 102, 62, Color.FromArgb(20, 130, 120), fS);

            btnCopyExt.Click += delegate { Copy(txtExt.Text); };

            Controls.Add(btnCopyExt);



            btnOpenExt = Btn("열기", 474, 102, 62, Color.FromArgb(51, 65, 85), fS);

            btnOpenExt.Click += delegate { Open(txtExt.Text); };

            Controls.Add(btnOpenExt);



            lblViewers = new Label

            {

                Text = "",

                Font = fB,

                ForeColor = Color.FromArgb(251, 146, 60),

                Location = new Point(18, 136),

                Size = new Size(520, 20)

            };

            Controls.Add(lblViewers);



            // ---- 설정 ----

            GroupBox g = new GroupBox

            {

                Text = "설정",

                Font = fN,

                ForeColor = Color.FromArgb(203, 213, 225),

                Location = new Point(16, 164),

                Size = new Size(524, 156)

            };

            Controls.Add(g);



            g.Controls.Add(Lbl("포트", 14, 28, fS));

            numPort = new NumericUpDown

            {

                Minimum = 1,

                Maximum = 65535,

                Value = Config.Port,

                Location = new Point(70, 25),

                Size = new Size(80, 24),

                BackColor = Color.FromArgb(30, 41, 59),

                ForeColor = Color.FromArgb(34, 211, 238),

                BorderStyle = BorderStyle.FixedSingle,

                Font = fN

            };

            g.Controls.Add(numPort);



            g.Controls.Add(Lbl("아이디", 170, 28, fS));

            txtUser = Txt(230, 25, 110, Config.User, false, fN);

            g.Controls.Add(txtUser);



            g.Controls.Add(Lbl("비밀번호", 14, 66, fS));

            txtPass = Txt(70, 63, 270, "", true, fN);

            g.Controls.Add(txtPass);



            g.Controls.Add(new Label

            {

                Text = Config.HasPassword ? "(바꿀 때만 입력)" : "(반드시 정해야 합니다)",

                Font = fS,

                ForeColor = Config.HasPassword ? Color.FromArgb(100, 116, 139) : Color.FromArgb(251, 146, 60),

                Location = new Point(348, 66),

                AutoSize = true

            });



            // 모니터 없는 PC 는 윈도우가 1024x768 로 잡아 둔다. 여기서 바꿀 수 있게 한다.

            g.Controls.Add(Lbl("화면 크기", 14, 104, fS));



            cboScreen = new ComboBox

            {

                Location = new Point(70, 101),

                Size = new Size(270, 24),

                DropDownStyle = ComboBoxStyle.DropDownList,

                BackColor = Color.FromArgb(30, 41, 59),

                ForeColor = Color.FromArgb(34, 211, 238),

                FlatStyle = FlatStyle.Flat,

                Font = fN

            };



            cboScreen.Items.Add("자동 (작으면 1920x1080 으로)");



            try

            {

                foreach (Size s in Display.Modes()) cboScreen.Items.Add(s.Width + " x " + s.Height);

            }

            catch { }



            int sw, sh;

            if (int.TryParse(Config.Get("screenw"), out sw) && int.TryParse(Config.Get("screenh"), out sh))

            {

                int idx = cboScreen.Items.IndexOf(sw + " x " + sh);

                cboScreen.SelectedIndex = idx >= 0 ? idx : 0;

            }

            else cboScreen.SelectedIndex = 0;



            cboScreen.SelectedIndexChanged += delegate { ApplyScreenSize(); };

            g.Controls.Add(cboScreen);



            btnApply = Btn("적용", 356, 22, 70, Color.FromArgb(51, 65, 85), fN);

            btnApply.Click += delegate { Apply(); };

            g.Controls.Add(btnApply);



            btnStart = Btn("중지", 436, 22, 70, Color.FromArgb(99, 102, 241), fN);

            btnStart.Click += delegate { Toggle(); };

            g.Controls.Add(btnStart);



            // 브릿지 - 이 PC 를 문으로 삼아 랜 안의 다른 PC 를 제어한다.

            // 단추 글씨가 곧 상태다. 몇 대가 걸려 있는지 적을 자리를 따로 두면 한쪽이 낡는다.

            btnBridge = Btn("", 356, 100, 150, Color.FromArgb(51, 65, 85), fS);

            btnBridge.Click += delegate

            {

                if (!RequireAdminSettings()) return;
                using (BridgeForm f = new BridgeForm()) f.ShowDialog(this);



                SyncBridgeButton();



                int n = Bridge.List().Count;

                Log(n > 0

                    ? "하위 PC " + n + "대. 브라우저로 들어오면 고르는 창이 먼저 뜹니다."

                    : "하위 PC 가 없습니다. 이 PC 만 보입니다.");

            };

            g.Controls.Add(btnBridge);

            SyncBridgeButton();



            // ---- 외부망 ----

            var mcpLink = new LinkLabel { Text = "LLM / MCP 설정 · LAN 및 웹 ChatGPT / Claude 연결", Location = new Point(14,132), Size = new Size(490,20), Font = fS, LinkColor = Color.FromArgb(34,211,238) };
            mcpLink.LinkClicked += delegate { if (!RequireAdminSettings()) return; using(var form=new McpForm(delegate {

                if(!svcMode)return server.McpNote;

                var st=Agent.ReadStatus();if(st==null)return "원격제어 서비스 상태를 확인하세요.";

                return string.IsNullOrEmpty(st.McpNote)?"실행 중인 서비스에 MCP 상태 정보가 없습니다. 새 setup으로 업데이트하세요.":st.McpNote;

            }))form.ShowDialog(this); };

            g.Controls.Add(mcpLink);



            GroupBox gx = new GroupBox

            {

                Text = "외부망 (인터넷에서 접속)",

                Font = fN,

                ForeColor = Color.FromArgb(203, 213, 225),

                Location = new Point(16, 326),

                Size = new Size(524, 148)

            };

            Controls.Add(gx);



            lblTunnelProvider = Lbl(RemoteTunnel.Name, 14, 28, fN);

            lblTunnelProvider.Size = new Size(325, 26);

            gx.Controls.Add(lblTunnelProvider);

            Button settings = Btn("연결 설정", 350, 24, 156, Color.FromArgb(51, 65, 85), fS);

            settings.Click += delegate {
                if (!RequireAdminSettings()) return;
                Config.Reload();

                using (TunnelForm form = new TunnelForm())

                    if (form.ShowDialog(this) != DialogResult.OK) return;

                lastExt = "";

                if (!svcMode) RemoteTunnel.SyncSettings();

                Log("외부망 설정 저장: " + RemoteTunnel.Name);

                UpdateStatus();

            };

            gx.Controls.Add(settings);



            btnTunnel = Btn("외부망 열기", 350, 56, 156, Color.FromArgb(20, 130, 120), fN);

            btnTunnel.Click += delegate { ToggleTunnel(); };

            gx.Controls.Add(btnTunnel);



            // 단추 글씨가 곧 상태다. 켜졌는지 알려 줄 자리를 따로 두면 한쪽이 낡는다.

            btnNotify = Btn("", 350, 90, 156, Color.FromArgb(51, 65, 85), fS);

            btnNotify.Click += delegate

            {

                if (!RequireAdminSettings()) return;
                using (NotifyForm f = new NotifyForm()) f.ShowDialog(this);



                SyncNotifyButton();

                Log(Notify.IsConfigured

                    ? "주소가 바뀌면 텔레그램으로 알립니다."

                    : "주소 변경 알림이 꺼져 있습니다.");

            };

            gx.Controls.Add(btnNotify);

            SyncNotifyButton();



            lblExt = new Label

            {

                Text = "[연결 설정]에서 방식을 선택하세요.",

                Font = fS,

                ForeColor = Color.FromArgb(100, 116, 139),

                Location = new Point(14, 58),

                Size = new Size(330, 34)

            };

            gx.Controls.Add(lblExt);



            // ---- 잠금 화면 ----

            GroupBox gs = new GroupBox

            {

                Text = "잠금 화면 제어",

                Font = fN,

                ForeColor = Color.FromArgb(203, 213, 225),

                Location = new Point(16, 480),

                Size = new Size(524, 108)

            };

            Controls.Add(gs);



            lblSvc = new Label

            {

                Text = "",

                Font = fS,

                ForeColor = Color.FromArgb(148, 163, 184),

                Location = new Point(14, 26),

                Size = new Size(330, 48)

            };

            gs.Controls.Add(lblSvc);



            btnSvc = Btn("켜기", 350, 28, 156, Color.FromArgb(120, 60, 160), fN);

            btnSvc.Click += delegate { ToggleService(); };

            gs.Controls.Add(btnSvc);



            // 모니터 없는 PC 를 원격 데스크톱으로 쓰다가 창을 닫으면 화면이 검게 나온다.

            // 끊긴 세션을 콘솔로 되돌려 놓으면 계속 보인다.

            chkKeep = new CheckBox

            {

                Text = "원격 데스크톱을 끊어도 화면 유지 (모니터 없는 PC 에 필요)",

                Checked = Config.Get("keepdesktop") != "0",

                Location = new Point(14, 78),

                Size = new Size(500, 22),

                ForeColor = Color.FromArgb(148, 163, 184),

                Font = fS,

                FlatStyle = FlatStyle.Flat

            };

            chkKeep.CheckedChanged += delegate

            {

                if (!RequireAdminSettings()) return;
                Config.Set("keepdesktop", chkKeep.Checked ? "1" : "0");

                Log(chkKeep.Checked

                    ? "원격 데스크톱을 끊어도 화면을 유지합니다."

                    : "원격 데스크톱을 끊으면 화면이 검게 나올 수 있습니다.");

            };

            gs.Controls.Add(chkKeep);



            // ---- 기록 ----

            Controls.Add(new Label

            {

                Text = "접속 기록",

                Font = fS,

                ForeColor = Color.FromArgb(148, 163, 184),

                Location = new Point(18, 596),

                AutoSize = true

            });



            lstLog = new ListBox

            {

                Location = new Point(16, 616),

                Size = new Size(524, 120),

                BackColor = Color.FromArgb(11, 15, 24),

                ForeColor = Color.FromArgb(203, 213, 225),

                BorderStyle = BorderStyle.FixedSingle,

                Font = new Font("Consolas", 8.5f)

            };

            Controls.Add(lstLog);

        }



        private static Label Lbl(string s, int x, int y, Font f)

        {

            return new Label { Text = s, Font = f, ForeColor = Color.FromArgb(148, 163, 184), Location = new Point(x, y), AutoSize = true };

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



        /// <summary>주소를 담는 칸. 고쳐 쓰지는 못하고 고르고 복사만 된다.</summary>

        private static TextBox Addr(int x, int y, int w)

        {

            TextBox t = new TextBox

            {

                Location = new Point(x, y),

                Size = new Size(w, 24),

                ReadOnly = true,

                BackColor = Color.FromArgb(11, 15, 24),

                ForeColor = Color.FromArgb(34, 211, 238),

                BorderStyle = BorderStyle.FixedSingle,

                Font = new Font("Consolas", 9.5f),

                Cursor = Cursors.IBeam

            };



            // 한 번 누르면 통째로 잡히게 한다. 주소는 부분만 복사하면 못 쓴다.

            t.Enter += delegate { t.SelectAll(); };

            t.Click += delegate { if (t.SelectionLength == 0) t.SelectAll(); };

            return t;

        }



        private void Open(string url)

        {

            if (string.IsNullOrEmpty(url) || !url.StartsWith("http")) { Log("열 주소가 없습니다."); return; }



            try { System.Diagnostics.Process.Start(url); }

            catch (Exception ex) { Log("열지 못했습니다: " + ex.Message); }

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



        // ---------------------------------------------------------------- 트레이



        private void BuildTray()

        {

            ContextMenuStrip menu = new ContextMenuStrip

            {

                BackColor = Color.FromArgb(30, 41, 59),

                ForeColor = Color.FromArgb(226, 232, 240),

                Font = new Font("맑은 고딕", 9)

            };



            ToolStripMenuItem miShow = new ToolStripMenuItem("창 열기");

            miShow.Font = new Font("맑은 고딕", 9, FontStyle.Bold);

            miShow.Click += delegate { Pop(); };

            menu.Items.Add(miShow);



            menu.Items.Add(new ToolStripSeparator());



            miLan = new ToolStripMenuItem("집·회사망 주소 복사");

            miLan.Click += delegate { Copy("http://" + LocalIp() + ":" + CurrentPort() + "/"); };

            menu.Items.Add(miLan);



            miExt = new ToolStripMenuItem("외부망 주소 복사");

            miExt.Click += delegate { Copy(CurrentUrl()); };

            menu.Items.Add(miExt);
            var notifyAddresses = new ToolStripMenuItem("텔레그램으로 접속 주소 보내기");
            notifyAddresses.Click += delegate
            {
                ThreadPool.QueueUserWorkItem(delegate
                {
                    string result = NotifyBroker.Request("send-addresses");
                    try { BeginInvoke((MethodInvoker)delegate { Log(result == "sent" ? "접속 주소를 전송했습니다." : "주소 전송 결과: " + result); }); } catch { }
                });
            };
            menu.Items.Add(notifyAddresses);



            miTunnel = new ToolStripMenuItem("외부망 열기");

            miTunnel.Click += delegate { Pop(); ToggleTunnel(); };

            menu.Items.Add(miTunnel);



            menu.Items.Add(new ToolStripSeparator());



            miSvc = new ToolStripMenuItem("잠금 화면 제어 켜기");

            miSvc.Click += delegate { Pop(); ToggleService(); };

            menu.Items.Add(miSvc);



            menu.Items.Add(new ToolStripSeparator());



            ToolStripMenuItem miExit = new ToolStripMenuItem("종료");

            miExit.Click += delegate

            {

                int v = Viewers();

                if (v > 0 &&

                    MessageBox.Show(this, "지금 " + v + "명이 이 PC 를 보고 있습니다.\r\n정말 종료할까요?",

                        "종료 확인", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,

                        MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;



                if (svcMode &&

                    MessageBox.Show(this,

                        "창만 닫힙니다. 잠금 화면 제어 서비스는 계속 돌고, 원격 접속도 계속 됩니다.\r\n" +

                        "완전히 끄려면 [잠금 화면 제어] 를 먼저 끄세요.\r\n\r\n창을 닫을까요?",

                        "종료 확인", MessageBoxButtons.YesNo, MessageBoxIcon.Information) != DialogResult.Yes) return;



                exiting = true;

                Close();

                Application.Exit();

            };

            menu.Items.Add(miExit);



            tray = new NotifyIcon

            {

                Icon = icoIdle ?? SystemIcons.Application,

                Text = "원격제어",

                Visible = true,

                ContextMenuStrip = menu

            };

            tray.DoubleClick += delegate { Pop(); };

        }



        /// <summary>

        /// 두 번째로 실행된 쪽이 보내는 신호를 기다린다.

        /// 신호가 오면 창을 띄운다. 아이콘을 눌렀는데 아무 반응이 없는 일이 없어야 한다.

        /// </summary>

        private void StartWakeWatcher()

        {

            Thread t = new Thread(delegate ()

            {

                EventWaitHandle h;

                try { h = new EventWaitHandle(false, EventResetMode.AutoReset, WakeEvent); }

                catch { return; }



                while (true)

                {

                    try

                    {

                        if (!h.WaitOne()) continue;

                        if (IsDisposed) return;



                        BeginInvoke((MethodInvoker)delegate { Pop(); });

                    }

                    catch { return; }

                }

            });



            t.IsBackground = true;

            t.Start();

        }



        private void Pop()

        {

            Show();

            ShowInTaskbar = true;

            WindowState = FormWindowState.Normal;

            Activate();

        }



        private void Copy(string s)

        {

            if (string.IsNullOrEmpty(s)) { Log("복사할 주소가 없습니다."); return; }



            try

            {

                Clipboard.SetText(s);

                tray.ShowBalloonTip(2500, "주소 복사됨", s, ToolTipIcon.Info);

            }

            catch { }

        }



        // ---------------------------------------------------------------- 잠금 화면 제어



        private void ToggleService()

        {

            // 등록은 돼 있는데 멈춘 상태. 지우는 게 아니라 다시 돌리는 게 맞다.

            if (ServiceHost.IsInstalled() && !ServiceHost.IsRunning())

            {

                Log("멈춰 있는 서비스를 다시 시작합니다. 관리자 권한을 물어봅니다.");

                Application.DoEvents();



                if (!ServiceHost.Elevate("--startsvc")) { Log("시작하지 못했습니다. " + LastSvcLog()); return; }



                server.Stop();          // 서비스가 포트를 가져가야 한다

                btnStart.Text = "시작";



                Log("서비스를 다시 시작했습니다.");

                UpdateStatus();

                return;

            }



            if (ServiceHost.IsInstalled())

            {

                if (MessageBox.Show(this,

                        "잠금 화면 제어를 끕니다.\r\n\r\n" +

                        "끄면 이 PC 가 잠겨 있을 때는 화면도 안 보이고 조작도 안 됩니다.\r\n" +

                        "계속할까요?",

                        "잠금 화면 제어", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)

                    return;



                Log("잠금 화면 제어를 끄는 중입니다. 관리자 권한을 물어봅니다.");

                Application.DoEvents();



                if (!ServiceHost.Elevate("--uninstall"))

                {

                    Log("끄지 못했습니다. " + LastSvcLog());

                    return;

                }



                svcMode = false;

                btnStart.Enabled = Config.HasPassword;

                btnTunnel.Enabled = true;



                Log("잠금 화면 제어를 껐습니다.");



                Thread.Sleep(500);

                if (Config.HasPassword) StartServer();



                UpdateStatus();

                return;

            }



            if (!Config.HasPassword)

            {

                MessageBox.Show(this, "먼저 비밀번호를 정해 주세요.", "잠금 화면 제어",

                    MessageBoxButtons.OK, MessageBoxIcon.Warning);

                return;

            }



            if (MessageBox.Show(this,

                    "잠금 화면 제어를 켭니다.\r\n\r\n" +

                    "이걸 켜면 이 PC 가 잠겨 있어도 원격에서 화면을 보고 로그인할 수 있습니다.\r\n" +

                    "그러려면 윈도우 서비스로 등록해야 하고, 그래서 관리자 권한을 한 번 물어봅니다.\r\n\r\n" +

                    "켜진 뒤에는 이 창을 닫아도, 로그아웃해도, PC 를 다시 켜도 원격 접속이 됩니다.\r\n" +

                    "비밀번호를 아는 사람은 언제든 이 PC 에 들어올 수 있다는 뜻이기도 합니다.\r\n\r\n" +

                    "계속할까요?",

                    "잠금 화면 제어", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,

                    MessageBoxDefaultButton.Button2) != DialogResult.Yes)

                return;



            // 서비스가 같은 포트를 열어야 하므로 창 쪽 서버와 터널을 먼저 접는다.

            if (RemoteControlSecrets.ConfigProtection.IsAdministrator) Tunnel.EnsureMachineKey();

            TunnelGuard.Disable();

            server.Stop();

            RemoteTunnel.Stop();

            btnStart.Text = "시작";



            Log("잠금 화면 제어를 켜는 중입니다. 관리자 권한을 물어봅니다.");

            Application.DoEvents();



            if (!ServiceHost.Elevate("--install"))

            {

                Log("켜지 못했습니다. " + LastSvcLog());

                if (Config.HasPassword) StartServer();      // 되돌린다

                UpdateStatus();

                return;

            }



            svcMode = true;

            btnStart.Enabled = false;

            btnTunnel.Enabled = true;



            // 서비스가 터널을 처음부터 다시 연다. 그동안 주소 칸이 비어 보이므로

            // 미리 알려 준다 (초를 세는 표시도 같이 나온다).

            tunnelSince = DateTime.Now;



            Log("잠금 화면 제어를 켰습니다. 이제 잠긴 화면에서도 조작할 수 있습니다.");

            Log("외부망 주소는 서비스가 다시 엽니다. 20~40초 걸립니다 (주소는 그대로).");

            UpdateStatus();

        }



        private static string LastSvcLog()

        {

            string[] t = SvcLog.Tail(1);

            return t.Length > 0 ? t[0] : "";

        }



        // ---------------------------------------------------------------- 동작



        private bool RequireAdminSettings()
        {
            if (RemoteControlSecrets.ConfigProtection.IsAdministrator) return true;
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Assembly.GetExecutingAssembly().Location, "--admin-ui") { UseShellExecute = true, Verb = "runas" });
                Log("관리자 설정 창에서 변경해 주세요.");
            }
            catch (Exception ex) { MessageBox.Show(this, "관리자 설정 창을 열지 못했습니다: " + ex.Message); }
            return false;
        }

        private void Apply()

        {
            if (!RequireAdminSettings()) return;

            string pw = txtPass.Text;

            if (!string.IsNullOrEmpty(pw))

            {

                // 원격제어는 뚫리면 이 PC 전체가 넘어간다. 그래서 짧은 비밀번호는

                // 그냥 통과시키지 않고, 무엇을 감수하는지 알린 뒤에만 받는다.

                if (pw.Length < 6)

                {

                    MessageBox.Show(this,

                        "비밀번호는 최소 6자 이상이어야 합니다.",

                        "비밀번호", MessageBoxButtons.OK, MessageBoxIcon.Warning);

                    return;

                }



                if (pw.Length < 8 || IsAllDigits(pw))

                {

                    string why = IsAllDigits(pw)

                        ? "숫자만 " + pw.Length + "자리"

                        : pw.Length + "자";



                    if (MessageBox.Show(this,

                            "약한 비밀번호입니다 (" + why + ").\r\n\r\n" +

                            "이 비밀번호를 아는 사람은 이 PC 를 마음대로 조작할 수 있습니다.\r\n" +

                            "5회 실패 시 잠금이 걸리지만, 외부망에 열어 둘 계획이라면\r\n" +

                            "영문·숫자를 섞어 10자 이상을 권합니다.\r\n\r\n그래도 이대로 쓰시겠습니까?",

                            "약한 비밀번호", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,

                            MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;

                }



                Config.SetPassword(pw);

                txtPass.Text = "";

                Log("비밀번호를 바꿨습니다. 기존 접속은 모두 끊깁니다.");



                if (svcMode) Log("서비스 쪽에도 곧 반영됩니다.");

            }



            Config.User = string.IsNullOrEmpty(txtUser.Text.Trim()) ? "admin" : txtUser.Text.Trim();



            int p = (int)numPort.Value;

            bool portChanged = p != Config.Port;

            Config.Port = p;



            if (!Config.HasPassword)

            {

                btnStart.Enabled = false;

                Log("비밀번호를 정해야 시작할 수 있습니다.");

                return;

            }



            if (!svcMode)

            {

                btnStart.Enabled = true;

                if (portChanged)

                {

                    server.Stop();

                    StartServer();

                    SyncFirewall(p);

                }

            }

            else if (portChanged)

            {

                // 서비스가 알아서 따라온다 (설정 파일을 2초마다 다시 본다).

                // 방화벽도 서비스가 SYSTEM 권한으로 함께 옮긴다.

                Log("포트를 " + p + " 로 바꿨습니다. 서비스가 곧 따라갑니다 (몇 초).");

            }



            UpdateStatus();

        }



        private void SyncNotifyButton()

        {

            if (btnNotify == null) return;



            bool on = Notify.IsConfigured;

            btnNotify.Text = on ? "알림: 켜짐" : "알림: 꺼짐";

            btnNotify.ForeColor = on ? Color.FromArgb(52, 211, 153) : Color.White;

        }



        private void SyncBridgeButton()

        {

            if (btnBridge == null) return;



            int n = 0;

            try { n = Bridge.List().Count; }

            catch { }



            btnBridge.Text = n > 0 ? "하위 PC: " + n + "대" : "하위 PC 없음";

            btnBridge.ForeColor = n > 0 ? Color.FromArgb(52, 211, 153) : Color.White;

        }



        private void ToggleTunnel()

        {
            if (!RequireAdminSettings()) return;

            try

            {

                Config.Reload();

                if (Config.Get("extauto") == "1")

                {

                    Config.SetMany(new System.Collections.Generic.Dictionary<string, string> { { "extauto", "0" } });

                    if (!svcMode) RemoteTunnel.SyncSettings();

                    Log("외부망 연결을 중지했습니다.");

                }

                else

                {

                    if (!Config.HasPassword) { Log("비밀번호를 먼저 정해 주세요."); return; }

                    if (!RemoteTunnel.IsConfigured) { Log(RemoteTunnel.ConfigurationError + " [연결 설정]을 확인하세요."); return; }

                    Config.SetMany(new System.Collections.Generic.Dictionary<string, string> {

                        { "extauto", "1" }, { "tunnelrevision", Guid.NewGuid().ToString("N") }

                    });

                    if (!svcMode) RemoteTunnel.SyncSettings();

                    Log(RemoteTunnel.Name + " 연결을 시작합니다.");

                }

                UpdateStatus();

            }

            catch (Exception ex) { MessageBox.Show(this, "설정을 저장하지 못했습니다: " + ex.Message); }

        }



        private void EnableTunnelGuard()

        {

            RemoteTunnel.SyncSettings();

            TunnelGuard.Enable(delegate { return Config.Port; },

                delegate { return Config.HasPassword && server.Port != 0 && Config.Get("extauto") == "1"; },

                delegate(string message) { try { BeginInvoke((MethodInvoker)delegate { Log(message); }); } catch { } });

        }



        /// <summary>

        /// 방화벽 규칙을 새 포트로 옮긴다.

        ///

        /// 서비스 없이 창만 띄운 경우다. 방화벽을 고치려면 관리자 권한이 필요한데

        /// 창은 보통 권한으로 돈다. 그래서 실패할 수 있고, 그때는 조용히 넘어가지 않고

        /// 무엇을 해야 하는지 알려 준다. 안 그러면 같은 공유기 안에서 접속이 안 되는데

        /// 이유를 찾을 방법이 없다.

        /// </summary>

        private void SyncFirewall(int port)

        {

            string exe = System.Reflection.Assembly.GetExecutingAssembly().Location;



            if (Firewall.Ensure(exe, port))

            {

                Log("방화벽을 포트 " + port + " 에 맞췄습니다.");

                return;

            }



            Log("방화벽 규칙을 바꾸지 못했습니다 (관리자 권한 필요).");

            Log("  같은 공유기 안에서 접속이 안 되면, [잠금 화면 제어] 를 켜면 자동으로 처리됩니다.");

        }



        /// <summary>

        /// 고른 화면 크기를 저장하고 지금 바로 맞춘다.

        ///

        /// 창은 사람이 앉은 세션에서 돌기 때문에 여기서 바로 바꿀 수 있다.

        /// 다음에 대리 프로세스가 뜰 때도 같은 값으로 맞춘다.

        /// </summary>

        private void ApplyScreenSize()

        {
            if (!RequireAdminSettings()) return;

            if (cboScreen.SelectedIndex <= 0)

            {

                Config.Set("screenw", "");

                Config.Set("screenh", "");

                Log("화면 크기를 자동으로 두었습니다 (작을 때만 1920x1080 으로 올립니다).");

                return;

            }



            string[] p = (cboScreen.SelectedItem as string ?? "").Split('x');

            if (p.Length != 2) return;



            int w, h;

            if (!int.TryParse(p[0].Trim(), out w) || !int.TryParse(p[1].Trim(), out h)) return;



            Config.Set("screenw", w.ToString());

            Config.Set("screenh", h.ToString());



            if (Display.Apply(w, h)) Log("화면 크기를 " + w + "x" + h + " 로 바꿨습니다.");

            else Log("화면 크기를 바꾸지 못했습니다. 서비스가 다음에 다시 시도합니다.");

        }



        private static bool IsAllDigits(string s)

        {

            foreach (char c in s) if (c < '0' || c > '9') return false;

            return s.Length > 0;

        }



        private void Toggle()

        {

            if (svcMode) return;



            if (server.Port != 0 && btnStart.Text == "중지")

            {

                server.Stop();

                btnStart.Text = "시작";

                Log("중지했습니다.");

            }

            else StartServer();



            UpdateStatus();

        }



        private void StartServer()

        {

            if (!Config.HasPassword) { Log("비밀번호를 먼저 정해 주세요."); return; }



            if (!svcMode) EnableTunnelGuard();

            if (server.Start(Config.Port, true))

            {

                btnStart.Text = "중지";

                Log("포트 " + Config.Port + " 에서 대기 중입니다.");

            }

            else

            {

                btnStart.Text = "시작";

                Log("포트를 열지 못했습니다: " + server.LastError);

            }

        }



        // ---------------------------------------------------------------- 상태 표시



        private AgentStatus agentStatus;



        private int Viewers() { return svcMode ? (agentStatus != null ? agentStatus.Viewers : 0) : server.Viewers; }

        private int Relays() { return svcMode ? (agentStatus != null ? agentStatus.Relays : 0) : server.Relays; }

        private int CurrentPort() { return svcMode && agentStatus != null && agentStatus.Port > 0 ? agentStatus.Port : Config.Port; }



        private string CurrentUrl()

        {

            if (svcMode) return agentStatus != null && !string.IsNullOrEmpty(agentStatus.Url) ? agentStatus.Url + "/" : "";

            return RemoteTunnel.IsAlive() ? RemoteTunnel.CurrentUrl + "/" : "";

        }



        private string lastExt = "";     // 마지막으로 받은 외부망 주소 (다시 여는 동안 보여 준다)

        private DateTime tunnelSince;

        private DateTime svcCheckedAt;

        private bool svcInstalled, svcRunning;



        private void UpdateStatus()

        {

            Config.Reload();

            if (!svcMode) RemoteTunnel.SyncSettings();

            lblTunnelProvider.Text = RemoteTunnel.Name;

            btnTunnel.Text = Config.Get("extauto") == "1" ? "외부망 닫기" : "외부망 열기";

            // 서비스 목록을 뒤지는 일은 생각보다 비싸다. 1초마다 하면 창이 굼떠진다.

            // 몇 초에 한 번이면 충분하다 (사람이 켜고 끄는 것을 따라가는 용도다).

            if ((DateTime.Now - svcCheckedAt).TotalSeconds >= 4)

            {

                svcCheckedAt = DateTime.Now;

                svcInstalled = ServiceHost.IsInstalled();

                svcRunning = svcInstalled && ServiceHost.IsRunning();

            }



            bool installed = svcInstalled;

            bool running = svcRunning;



            if (running != svcMode)

            {

                svcMode = running;

                btnStart.Enabled = !svcMode && Config.HasPassword;

                btnTunnel.Enabled = true;



                // 서비스가 막 올라왔다. 창이 물고 있던 포트를 놓아 줘야 한다.

                // 둘이 같은 포트를 다투면 접속이 됐다 안 됐다 한다.

                if (svcMode)

                {

                    server.Stop();



                    // 터널 주인이 서비스로 넘어갔다. 창 쪽 지킴이가 계속 살아 있으면

                    // 둘이 같은 상태 폴더를 두고 중계를 서로 밀어낸다.

                    TunnelGuard.Disable();

                    RemoteTunnel.Stop();

                    btnStart.Text = "시작";

                    Log("서비스가 올라왔습니다. 화면과 입력은 서비스 쪽에서 처리합니다.");

                }

                else if (Config.HasPassword) StartServer();

            }



            agentStatus = svcMode ? Agent.ReadStatus() : null;



            bool on = svcMode ? agentStatus != null && agentStatus.Port > 0 : btnStart.Text == "중지";



            // 맨 윗줄은 "지금 무엇을 해야 하는가" 를 말해야 한다.

            // 단순히 "중지됨" 이라고만 하면, 왜 멈췄고 뭘 해야 하는지 알 수가 없다.

            if (!Config.HasPassword)

            {

                lblState.Text = "⚠  비밀번호를 정해야 시작됩니다 — 아래 [비밀번호] 칸에 넣고 [적용]";

                lblState.ForeColor = Color.FromArgb(251, 146, 60);

            }

            else if (svcMode && agentStatus == null)

            {

                lblState.Text = "🟡 서비스 준비 중…";

                lblState.ForeColor = Color.FromArgb(251, 191, 36);

            }

            else if (svcMode && agentStatus != null && agentStatus.Port == 0)

            {

                lblState.Text = "🟡 서비스는 떠 있는데 대기 중입니다 (설정 확인 필요)";

                lblState.ForeColor = Color.FromArgb(251, 191, 36);

            }

            else

            {

                lblState.Text = on ? (svcMode ? "🟢 실행 중 (잠금 화면 포함)" : "🟢 실행 중") : "⚪ 중지됨";

                lblState.ForeColor = on ? Color.FromArgb(52, 211, 153) : Color.FromArgb(148, 163, 184);

            }



            // 주소를 만드는 데 20~40초 걸린다. 그동안 아무 표시가 없으면 멈춘 줄 안다.

            // 초를 세어 보여 주면 "돌아가고는 있구나" 를 알 수 있다.

            //

            // 서비스로 넘기는 순간에도 이 길로 온다. 창이 열어 둔 터널을 접고 서비스가

            // 처음부터 다시 열기 때문이다. 그때 주소 칸이 그냥 비면 없어진 줄 안다.

            bool opening = RemoteTunnel.IsOpening || (svcMode && agentStatus != null && agentStatus.Opening);



            if (opening)

            {

                if (tunnelSince == DateTime.MinValue) tunnelSince = DateTime.Now;



                int sec = (int)(DateTime.Now - tunnelSince).TotalSeconds;

                btnTunnel.Text = "연결 취소";

                lblExt.ForeColor = Color.FromArgb(251, 191, 36);

                lblExt.Text = RemoteTunnel.Name + " 연결 중 (" + sec + "초)";



                // 예전에 받은 주소가 있으면 같이 보여 준다. 장비 이름은 그대로라

                // 십중팔구 같은 주소가 다시 나온다. 사라진 게 아니라는 것을 알려야 한다.

                SetAddr(txtExt, string.IsNullOrEmpty(lastExt)

                    ? "만드는 중… " + sec + "초"

                    : lastExt + "   (다시 여는 중 " + sec + "초)");



                txtExt.ForeColor = Color.FromArgb(251, 191, 36);



                btnCopyExt.Enabled = false;

                btnOpenExt.Enabled = false;



                tray.Text = Trim63("원격제어 - 외부망 여는 중 " + sec + "초");

                return;

            }



            tunnelSince = DateTime.MinValue;



            string lan = on ? "http://" + LocalIp() + ":" + CurrentPort() + "/" : "";

            string ext = CurrentUrl();

            if (!string.IsNullOrEmpty(ext)) lastExt = ext;



            // 고쳐 쓰는 중일 수 있으니 값이 실제로 달라졌을 때만 건드린다.

            // 매 초 덮어쓰면 사용자가 잡아 놓은 선택이 풀린다.

            //

            // 비어 있을 때는 그냥 "없음" 이 아니라 왜 없는지를 적는다.

            // "(중지됨)" 만 보면 무엇을 해야 할지 알 수 없다.

            SetAddr(txtLan, !string.IsNullOrEmpty(lan) ? lan

                          : !Config.HasPassword ? "비밀번호를 정하면 주소가 나옵니다"

                          : "중지됨 — [시작] 을 누르세요");



            // 지킴이가 무언가 하고 있으면 그 말을 그대로 보여 준다.

            // "닫힘" 만 보이면 사람이 눌러야 하는 줄 알고 서비스와 둘이 열게 된다.

            string extWhy = svcMode ? (agentStatus != null ? agentStatus.Note : "") : TunnelGuard.Note;



            SetAddr(txtExt, !string.IsNullOrEmpty(ext) ? ext

                          : !Config.HasPassword ? "비밀번호를 먼저 정하세요"

                          : !RemoteTunnel.IsConfigured ? RemoteTunnel.ConfigurationError

                          : !string.IsNullOrEmpty(extWhy) ? extWhy

                          : Config.Get("extauto") == "1" ? "자동 연결 대기 중"

                          : "닫힘 — [외부망 열기] 를 누르세요");



            lblExt.Text = !string.IsNullOrEmpty(extWhy) ? extWhy

                : !string.IsNullOrEmpty(ext) ? "터널 연결됨 · 브라우저 주소 확인 중"

                : Config.Get("extauto") == "1" ? "자동 연결 대기 중" : "[연결 설정]에서 방식을 선택하세요.";

            lblExt.ForeColor = Color.FromArgb(148, 163, 184);

            bool hasLan = !string.IsNullOrEmpty(lan);

            bool hasExt = !string.IsNullOrEmpty(ext);



            txtLan.ForeColor = hasLan ? Color.FromArgb(34, 211, 238) : Color.FromArgb(100, 116, 139);

            txtExt.ForeColor = hasExt ? Color.FromArgb(52, 211, 153) : Color.FromArgb(100, 116, 139);



            btnCopyLan.Enabled = hasLan;

            btnCopyExt.Enabled = hasExt;

            btnOpenExt.Enabled = hasExt;



            int v = Viewers();

            int rl = Relays();



            // 두 가지를 구별해서 적는다. 브릿지로 지나가는 사람은 이 PC 화면을

            // 보는 것이 아니라 하위 PC 를 보는 것이다. 뭉뚱그리면 "누가 내 화면을

            // 보고 있다" 고 잘못 읽힌다.

            if (v > 0 && rl > 0)

                lblViewers.Text = "👁  이 화면 " + v + "명 · 🔀 하위 PC 로 " + rl + "명 지나가는 중";

            else if (v > 0)

                lblViewers.Text = "👁  지금 " + v + "명이 이 화면을 보고 있습니다";

            else if (rl > 0)

                lblViewers.Text = "🔀  " + rl + "명이 이 PC 를 거쳐 하위 PC 에 붙어 있습니다";

            else

                lblViewers.Text = "";



            // ---- 잠금 화면 제어 칸 ----

            if (running)

            {

                string desk = agentStatus != null ? agentStatus.Desktop : "";

                btnSvc.Text = "끄기";

                btnSvc.BackColor = Color.FromArgb(120, 60, 160);

                lblSvc.ForeColor = Color.FromArgb(52, 211, 153);

                lblSvc.Text = "켜져 있습니다. 잠긴 화면에서도 보이고 조작됩니다.\r\n" +

                              "PC 를 껐다 켜도, 로그아웃해도 계속 열려 있습니다." +

                              (string.IsNullOrEmpty(desk) ? "" : "\r\n지금 화면: " + DeskName(desk));

            }

            else if (installed)

            {

                btnSvc.Text = "다시 시작";

                btnSvc.BackColor = Color.FromArgb(180, 120, 30);

                lblSvc.ForeColor = Color.FromArgb(251, 191, 36);

                lblSvc.Text = "등록은 되어 있는데 서비스가 멈춰 있습니다.\r\n" +

                              "[다시 시작] 을 누르면 잠금 화면 제어가 다시 켜집니다.";

            }

            else

            {

                btnSvc.Text = "켜기";

                btnSvc.BackColor = Color.FromArgb(120, 60, 160);

                lblSvc.ForeColor = Color.FromArgb(148, 163, 184);

                lblSvc.Text = "꺼져 있습니다. 지금은 PC 가 잠기면 화면이 멈추고 조작도 안 됩니다.\r\n" +

                              "켜면 잠금 화면에서 로그인까지 할 수 있습니다 (관리자 권한 필요).";

            }



            // ---- 트레이 ----

            miExt.Enabled = !string.IsNullOrEmpty(ext);

            miLan.Enabled = on;

            miTunnel.Enabled = true;

            miTunnel.Text = Config.Get("extauto") == "1" ? "외부망 닫기" : "외부망 열기";

            miSvc.Text = installed ? "잠금 화면 제어 끄기" : "잠금 화면 제어 켜기";



            tray.Text = Trim63(v > 0 ? "원격제어 - " + v + "명 접속 중"

                                     : (on ? "원격제어 - 대기 중" + (svcMode ? " (잠금 화면 포함)" : "")

                                           : "원격제어 - 중지됨"));



            // 누가 보고 있으면 아이콘 색을 바꾼다. 작업 표시줄만 봐도 알 수 있어야 한다.

            bool busy = v > 0;

            if (busy != trayBusy)

            {

                trayBusy = busy;

                Icon want = busy ? icoBusy : icoIdle;

                if (want != null) tray.Icon = want;

            }

        }



        private static void SetAddr(TextBox t, string v)

        {

            if (t.Text == v) return;



            int sel = t.SelectionStart;

            t.Text = v;

            try { t.SelectionStart = Math.Min(sel, v.Length); } catch { }

        }



        private static string DeskName(string raw)

        {

            if (string.Equals(raw, "Winlogon", StringComparison.OrdinalIgnoreCase)) return "잠금·로그인 화면";

            if (string.Equals(raw, "Default", StringComparison.OrdinalIgnoreCase)) return "바탕화면";

            return raw;   // UAC 동의 창 등

        }



        /// <summary>트레이 툴팁은 64자를 넘기면 통째로 사라진다.</summary>

        private static string Trim63(string s)

        {

            return s != null && s.Length > 63 ? s.Substring(0, 63) : s;

        }



        private static string LocalIp()

        {

            try

            {

                foreach (System.Net.NetworkInformation.NetworkInterface ni in

                         System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())

                {

                    if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;

                    if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;



                    foreach (var ua in ni.GetIPProperties().UnicastAddresses)

                        if (ua.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&

                            !ua.Address.ToString().StartsWith("169.254."))

                            return ua.Address.ToString();

                }

            }

            catch { }

            return "127.0.0.1";

        }



        private void OnNotice(string s)

        {

            if (IsHandleCreated && !IsDisposed)

                BeginInvoke((MethodInvoker)delegate { Log(s); UpdateStatus(); });

        }



        private void Log(string s)

        {

            lstLog.Items.Insert(0, DateTime.Now.ToString("HH:mm:ss") + "  " + s);

            while (lstLog.Items.Count > 300) lstLog.Items.RemoveAt(lstLog.Items.Count - 1);

        }

    }

}


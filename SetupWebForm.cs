using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

namespace RemoteControlSetup
{
    // 설치와 설치 후 설정이 같은 입력/검사 경로를 사용한다.
    internal sealed class WebConnectionControls
    {
        private readonly CheckBox enabled;
        private readonly CheckBox defaultPort;
        private readonly TextBox address;
        private readonly NumericUpDown port;
        private readonly NumericUpDown gateway;
        private readonly ComboBox mode;
        private readonly Label help,addressTitle,result;
        private readonly System.Windows.Forms.Timer timer=new System.Windows.Forms.Timer();
        private bool busy,changingPort;
        private string directAddress;
        private int directPort;
        private int customPort=WebMcp.DefaultPort;
        private int tailscalePort,tailscaleCustomPort=8443,previousMode=-1,tailscaleGateway,privateGateway;
        private readonly Label gatewayTitle;
        private readonly Button certificate;
        private string certificatePath, privateKeyPath;

        internal WebConnectionControls(Action<Control[]> row, bool optional)
        {
            enabled = new CheckBox {Name="WebConnectNow", Text="웹 ChatGPT / Claude 연결을 지금 설정 (선택 사항)", Checked=!optional,
                Font=Ui.F(9,FontStyle.Regular), ForeColor=Color.White, FlatStyle=FlatStyle.Flat};
            if (optional) row(new Control[] {enabled});
            row(new Control[] {new Label {Text=optional
                ? "선택하지 않아도 설치됩니다. 설치 후 [LLM / MCP 설정] → [웹 ChatGPT / Claude 설정]에서 설정할 수 있습니다. 기존 웹 연결은 유지됩니다."
                : "원격제어 ID·비밀번호, 잠금 화면 제어 서비스, LAN MCP를 먼저 켜고 저장하세요. 공개 HTTPS 주소와 기본 포트(443) 또는 전용 포트를 선택하세요.",
                Font=Ui.F(8,FontStyle.Regular), ForeColor=Ui.Sub}});
            string saved = WebMcp.ReadSetting("mcp_web_url", ""); Uri uri;
            if(WebMcp.ReadSetting("mcp_web_mode","")=="tailscale"||WebMcp.ReadSetting("mcp_web_mode","")=="private")saved=WebMcp.ReadSetting("mcp_ddns_url","");
            if (Uri.TryCreate(saved, UriKind.Absolute, out uri)) saved = new UriBuilder(uri) {Port=-1}.Uri.GetLeftPart(UriPartial.Authority);
            directAddress=saved;directPort=WebMcp.PreferredPort();
            mode=new ComboBox{Name="WebConnectionMode",DropDownStyle=ComboBoxStyle.DropDownList,Font=Ui.F(9,FontStyle.Regular)};
            mode.Items.AddRange(new object[]{"주소 직접 입력 (자동 변경 안 함)","Tailscale 주소 자동 연결","프라이빗 터널 주소 자동 연결"});
            row(new Control[]{mode});
            addressTitle=new Label {Text="DDNS 주소 (https://내주소)",Font=Ui.F(8,FontStyle.Regular),ForeColor=Ui.Sub};
            row(new Control[] {addressTitle});
            address = new TextBox {Name="WebAddress",Text=saved,Font=Ui.F(9,FontStyle.Regular),BackColor=Ui.Input,ForeColor=Color.White,BorderStyle=BorderStyle.FixedSingle};
            port = new NumericUpDown {Name="WebHttpsPort",Minimum=443,Maximum=65535,Value=WebMcp.PreferredPort(),Font=Ui.F(9,FontStyle.Regular),BackColor=Ui.Input,ForeColor=Color.White};
            row(new Control[] {address,new Label {Text="HTTPS 포트",Font=Ui.F(8,FontStyle.Regular),ForeColor=Ui.Sub},port});
            if(directPort>=1024)customPort=directPort;
            defaultPort=new CheckBox{Name="WebDefaultHttpsPort",Text="기본 HTTPS 포트(443) 사용 · 주소에서 포트 번호 생략",Checked=directPort==443,
                Font=Ui.F(9,FontStyle.Regular),ForeColor=Color.White,FlatStyle=FlatStyle.Flat};
            row(new Control[]{defaultPort});
            certificate = Ui.Btn("HTTPS 인증서 선택",Ui.Panel,148,28); certificate.Name="WebCertificate";
            certificate.Click += delegate {
                using (var cert = new OpenFileDialog {Title="공개 HTTPS 인증서 (체인 포함 PEM)",Filter="PEM 인증서|*.pem;*.crt|모든 파일|*.*"}) {
                    if (cert.ShowDialog(certificate.FindForm()) != DialogResult.OK) return;
                    using (var key = new OpenFileDialog {Title="해당 인증서의 개인 키",Filter="PEM 개인 키|*.key;*.pem|모든 파일|*.*"}) {
                        if (key.ShowDialog(certificate.FindForm()) != DialogResult.OK) return;
                        certificatePath=cert.FileName; privateKeyPath=key.FileName; certificate.Text="인증서 선택됨";
                    }
                }
            };
            help=new Label {Font=Ui.F(8,FontStyle.Regular),ForeColor=Ui.Sub};
            row(new Control[] {certificate,help});
            int local; if(!int.TryParse(WebMcp.ReadSetting("mcp_ts_gateway","59335"),out local)||local<1024||local>65535)local=59335;
            tailscaleGateway=local;
            if(!int.TryParse(WebMcp.ReadSetting("mcp_private_gateway","59336"),out privateGateway)||privateGateway<1024||privateGateway>65535)privateGateway=59336;
            gatewayTitle=new Label{Text="자동 연결 로컬 중계 포트",Font=Ui.F(8,FontStyle.Regular),ForeColor=Ui.Sub};
            gateway=new NumericUpDown{Name="TailscaleGatewayPort",Minimum=1024,Maximum=65535,Value=local,Font=Ui.F(9,FontStyle.Regular),BackColor=Ui.Input,ForeColor=Color.White};
            row(new Control[]{gatewayTitle,gateway});
            result=new Label{Name="GeneratedMcpAddress",Font=Ui.F(8,FontStyle.Regular),ForeColor=Ui.Cyan};row(new Control[]{result});
            if(!int.TryParse(WebMcp.ReadSetting("mcp_ts_port","443"),out tailscalePort)||(tailscalePort!=443&&tailscalePort!=8443&&tailscalePort!=10000))tailscalePort=443;
            if(tailscalePort!=443)tailscaleCustomPort=tailscalePort;
            mode.SelectedIndexChanged+=delegate{
                changingPort=true;
                if(previousMode==0){directAddress=address.Text;directPort=(int)port.Value;}
                if(previousMode==1){tailscalePort=(int)port.Value;tailscaleGateway=(int)gateway.Value;}
                if(previousMode==2)privateGateway=(int)gateway.Value;
                if(mode.SelectedIndex==1){port.Value=tailscalePort;gateway.Value=tailscaleGateway;}
                else if(mode.SelectedIndex==2){port.Value=443;gateway.Value=privateGateway;}
                else{address.Text=directAddress;port.Value=WebMcp.IsHttpsPort(directPort)?directPort:WebMcp.DefaultPort;}
                defaultPort.Checked=port.Value==443;previousMode=mode.SelectedIndex;
                changingPort=false;
                SetBusy(busy);RefreshAddress();
            };
            defaultPort.CheckedChanged+=delegate{
                if(changingPort)return;
                changingPort=true;
                if(defaultPort.Checked){if(port.Value>=1024){if(mode.SelectedIndex==1)tailscaleCustomPort=(int)port.Value;else customPort=(int)port.Value;}port.Value=443;}
                else port.Value=mode.SelectedIndex==1?tailscaleCustomPort:customPort;
                changingPort=false;SetBusy(busy);RefreshAddress();
            };
            port.ValueChanged+=delegate{
                if(!changingPort){if(mode.SelectedIndex==1){if(port.Value==8443||port.Value==10000)tailscaleCustomPort=(int)port.Value;}else if(port.Value>=1024)customPort=(int)port.Value;changingPort=true;defaultPort.Checked=port.Value==443;changingPort=false;SetBusy(busy);}
                RefreshAddress();
            };
            address.TextChanged+=delegate{if(mode.SelectedIndex==0)RefreshAddress();};
            mode.SelectedIndex=WebMcp.ReadSetting("mcp_web_mode","")=="private"?2:WebMcp.ReadSetting("mcp_web_mode","")=="tailscale"?1:0;
            enabled.CheckedChanged += delegate { SetBusy(false); };
            timer.Interval=1000;timer.Tick+=delegate{if(mode.SelectedIndex>0)RefreshAddress();};timer.Start();address.Disposed+=delegate{timer.Dispose();};
            SetBusy(false);
        }
        internal void Apply(Options options)
        {
            options.WebMcp=enabled.Checked;
            options.WebUrl=null; options.CertificatePath=null; options.PrivateKeyPath=null;
            if (!options.WebMcp) return;
            options.WebTailscale=mode.SelectedIndex==1;options.WebPrivate=mode.SelectedIndex==2;
            if(options.WebTailscale||options.WebPrivate){options.WebPublicPort=(int)port.Value;options.WebGatewayPort=(int)gateway.Value;return;}
            options.WebUrl=WebMcp.WithPort(address.Text.Trim(),(int)port.Value);
            options.CertificatePath=certificatePath; options.PrivateKeyPath=privateKeyPath;
        }
        internal void SetBusy(bool busy)
        {
            this.busy=busy;
            enabled.Enabled=!busy;
            bool on=!busy && enabled.Checked,ts=mode.SelectedIndex==1,pr=mode.SelectedIndex==2,auto=ts||pr;
            mode.Enabled=on;port.Enabled=on&&!pr&&!defaultPort.Checked;defaultPort.Enabled=on&&!pr;gateway.Enabled=on&&auto;
            address.Enabled=on;address.ReadOnly=auto;certificate.Enabled=on&&!auto;
            gatewayTitle.Text=pr?"프라이빗 터널 로컬 중계 포트":"Tailscale 로컬 중계 포트";
            addressTitle.Text=pr?"프라이빗 발급 주소 (연결되면 자동 입력)":ts?"Tailscale 주소 (연결되면 자동 입력)":"DDNS 주소 (https://내주소)";
            help.Text=pr?"발급 HTTPS 주소 하나로 화면·MCP·파일 전송을 연결합니다.\n외부망 설정에서 프라이빗 터널을 켜세요. 인증서·공유기 설정은 필요 없습니다.":ts?"기본 443에서 화면과 MCP를 함께 연결합니다.\n별도 포트는 8443 또는 10000을 선택하세요.":"기존 인증서는 자동 사용 · 새 인증서는 버튼으로 선택\n443 사용 시 기존 서비스와 충돌하는지 적용 전에 검사합니다.";
        }
        private void RefreshAddress()
        {
            if(mode.SelectedIndex==2){string current=WebMcp.ReadPrivateUrl();address.Text=current;result.Text=current.Length==0?"프라이빗 터널 연결 대기 · 발급 주소를 MCP에 자동 반영합니다.":current+"/mcp · 재연결로 주소가 바뀌면 앱에서 새 주소로 로그인하세요.";}
            else if(mode.SelectedIndex==1){string current=WebMcp.ReadTailscaleUrl();address.Text=current;int p=(int)port.Value;if(p!=443&&p!=8443&&p!=10000){result.Text="Tailscale 공개 포트는 443, 8443, 10000 중 선택하세요.";return;}result.Text=current.Length==0?"Tailscale 연결 대기 · 주소가 생성되면 자동 반영하고 접속 확인 후 텔레그램으로 알립니다.":WebMcp.WithPort(current,p)+"/mcp · 적용 후 접속 확인 시 텔레그램으로 알립니다.";}
            else {
                string preview="";try{preview="MCP 주소: "+WebMcp.WithPort(address.Text.Trim(),(int)port.Value)+"/mcp\n";}catch{}
                result.Text=preview+"직접 입력 주소·포트는 화면 터널을 사용해도 자동 변경하지 않습니다.";
            }
        }
    }

    // 설치된 uninstall.exe의 별도 진입점. 본체 재설치/제거는 실행하지 않는다.
    internal sealed class WebSettingsForm : InstallerForm
    {
        private readonly WebConnectionControls connection;
        private readonly TextBox log;
        private readonly Button apply, close;
        private bool busy;
        internal WebSettingsForm()
        {
            Text="웹 ChatGPT / Claude 설정";
            Row(new Label {Text=Text,Font=Ui.F(14,FontStyle.Bold),ForeColor=Ui.Cyan});
            connection=new WebConnectionControls(Row,false);
            log=new TextBox {Name="WebSettingsLog",Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical,Font=Ui.F(9,FontStyle.Regular),BackColor=Ui.Input,ForeColor=Color.White};
            Row(log);
            apply=Ui.Btn("적용",Ui.Accent,110,34); apply.Name="ApplyWebSettings"; apply.Click+=delegate { Apply(); };
            close=Ui.Btn("닫기",Ui.Panel,90,34); close.Click+=delegate { Close(); };
            FormClosing+=delegate(object sender,FormClosingEventArgs e) { if (busy) e.Cancel=true; };
            FinishLayout(580,540,apply,close);
        }
        internal Options ReadOptions()
        {
            var options=new Options {Dir=Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),Port=Installer.ReadPort(),LockScreen=true};
            connection.Apply(options); WebMcp.Validate(options); return options;
        }
        private void Say(string line)
        {
            if (InvokeRequired) {BeginInvoke((MethodInvoker)delegate {Say(line);});return;}
            log.AppendText(line+"\r\n");log.SelectionStart=log.TextLength;log.ScrollToCaret();
        }
        private void Apply()
        {
            Options options;
            try { options=ReadOptions(); } catch(Exception error) {MessageBox.Show(this,error.Message,Text);return;}
            busy=true;apply.Enabled=close.Enabled=false;connection.SetBusy(true);ShowProgress(log);
            new Thread(delegate() {
                try {WebMcp.ConfigureExisting(options,Say);}
                catch(Exception error) {Say("설정 실패: "+error.Message);}
                finally {try {BeginInvoke((MethodInvoker)delegate {busy=false;apply.Enabled=close.Enabled=true;connection.SetBusy(false);});} catch {} }
            }) {IsBackground=true}.Start();
        }
    }
}

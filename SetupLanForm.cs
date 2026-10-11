using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

namespace RemoteControlSetup
{
    internal sealed class LanConnectionControls
    {
        private readonly CheckBox enabled;
        private readonly TextBox address,networks;
        private readonly NumericUpDown port,gateway;
        private readonly Label result;
        internal LanConnectionControls(Action<Control[]> row,bool optional)
        {
            enabled=new CheckBox{Name="LanWebMcpEnabled",Text=optional?"LAN IP 직접 연결 / 인터넷 없는 내부망 WebMCP 설정 (선택 사항)":"LAN WebMCP 사용",Checked=!optional&&WebMcp.ReadSetting("mcp_lan_enabled","0")=="1",Font=Ui.F(9,FontStyle.Regular),ForeColor=Color.White,FlatStyle=FlatStyle.Flat};row(new Control[]{enabled});
            row(new Control[]{new Label{Text="DDNS·터널·인터넷 없이 대상 PC의 HTTPS IP 주소로 연결합니다. 외부 웹 연결과 함께 사용할 수 있습니다.",Font=Ui.F(8,FontStyle.Regular),ForeColor=Ui.Sub}});
            row(new Control[]{new Label{Text="이 PC의 LAN IPv4 주소 / HTTPS 포트",Font=Ui.F(8,FontStyle.Regular),ForeColor=Ui.Sub}});
            address=new TextBox{Name="LanWebMcpAddress",Text=LanWebMcp.DefaultAddress(),Font=Ui.F(9,FontStyle.Regular),BackColor=Ui.Input,ForeColor=Color.White};
            port=new NumericUpDown{Name="LanWebMcpHttpsPort",Minimum=443,Maximum=65535,Value=LanWebMcp.ReadPort("mcp_lan_https_port",LanWebMcp.DefaultPort),Font=Ui.F(9,FontStyle.Regular),BackColor=Ui.Input,ForeColor=Color.White};row(new Control[]{address,port});
            row(new Control[]{new Label{Text="접속 허용 클라이언트 IPv4 / 대역 (쉼표 구분)",Font=Ui.F(8,FontStyle.Regular),ForeColor=Ui.Sub}});
            networks=new TextBox{Name="LanWebMcpNetworks",Text=LanWebMcp.DefaultNetworks(address.Text),Font=Ui.F(9,FontStyle.Regular),BackColor=Ui.Input,ForeColor=Color.White};row(new Control[]{networks});
            gateway=new NumericUpDown{Name="LanWebMcpGateway",Minimum=1024,Maximum=65535,Value=LanWebMcp.ReadPort("mcp_lan_gateway",LanWebMcp.DefaultGateway),Font=Ui.F(9,FontStyle.Regular),BackColor=Ui.Input,ForeColor=Color.White};row(new Control[]{new Label{Text="LAN 전용 내부 중계 포트",Font=Ui.F(8,FontStyle.Regular),ForeColor=Ui.Sub},gateway});
            result=new Label{Name="LanWebMcpUrl",Font=Ui.F(8,FontStyle.Regular),ForeColor=Ui.Cyan};row(new Control[]{result});
            row(new Control[]{new Label{Text="최초 1회: 대상 PC에서 공개 CA·SHA256을 확인한 후 클라이언트 PC에 인증서를 등록하세요.\n로컬 MCP 클라이언트에서 URL을 등록하고 기존 원격제어 ID·비밀번호로 로그인합니다. 클라우드 ChatGPT는 폐쇄망 IP에 직접 접속하지 못합니다.",Font=Ui.F(8,FontStyle.Regular),ForeColor=Ui.Sub}});
            address.TextChanged+=delegate{Refresh();};port.ValueChanged+=delegate{Refresh();};enabled.CheckedChanged+=delegate{SetBusy(false);};SetBusy(false);Refresh();
        }
        private void Refresh(){result.Text="LAN MCP 주소: https://"+address.Text.Trim()+(port.Value==443?"":":"+port.Value)+"/mcp";}
        internal void Apply(Options o){o.LanWebMcp=enabled.Checked;o.LanAddress=address.Text.Trim();o.LanHttpsPort=(int)port.Value;o.LanGatewayPort=(int)gateway.Value;o.LanNetworks=networks.Text.Trim();}
        internal void SetBusy(bool busy){enabled.Enabled=!busy;address.Enabled=networks.Enabled=port.Enabled=gateway.Enabled=!busy&&enabled.Checked;}
    }
    internal sealed class LanSettingsForm:InstallerForm
    {
        private readonly LanConnectionControls connection;
        private readonly TextBox log;
        private readonly Button apply,close;
        private bool busy;
        internal LanSettingsForm()
        {
            Text="LAN / 내부망 WebMCP 설정";Row(new Label{Text=Text,Font=Ui.F(14,FontStyle.Bold),ForeColor=Ui.Cyan});connection=new LanConnectionControls(Row,false);
            log=new TextBox{Name="LanSettingsLog",Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical,Font=Ui.F(9,FontStyle.Regular),BackColor=Ui.Input,ForeColor=Color.White};Row(log);
            apply=Ui.Btn("적용",Ui.Accent,110,34);apply.Click+=delegate{Apply();};close=Ui.Btn("닫기",Ui.Panel,90,34);close.Click+=delegate{Close();};
            FormClosing+=delegate(object sender,FormClosingEventArgs e){if(busy)e.Cancel=true;};FinishLayout(620,600,apply,close);
        }
        internal Options ReadOptions(){var o=new Options{Dir=Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),Port=Installer.ReadPort(),LockScreen=true};connection.Apply(o);LanWebMcp.Validate(o);return o;}
        private void Say(string line){if(InvokeRequired){BeginInvoke((MethodInvoker)delegate{Say(line);});return;}log.AppendText(line+"\r\n");log.SelectionStart=log.TextLength;log.ScrollToCaret();}
        private void Apply(){Options o;try{o=ReadOptions();}catch(Exception e){MessageBox.Show(this,e.Message,Text);return;}busy=true;apply.Enabled=close.Enabled=false;connection.SetBusy(true);ShowProgress(log);
            new Thread(delegate(){try{LanWebMcp.ConfigureExisting(o,Say);}catch(Exception e){Say("설정 실패: "+e.Message);}finally{try{BeginInvoke((MethodInvoker)delegate{busy=false;apply.Enabled=close.Enabled=true;connection.SetBusy(false);});}catch{}}}){IsBackground=true}.Start();}
    }
}

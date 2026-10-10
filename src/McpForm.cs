using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

namespace RemoteControl
{
    internal sealed class McpForm : Form
    {
        private CheckBox enabled, commands, screen;
        private NumericUpDown port;
        private TextBox roots, networks;
        private Label status;
        private readonly Func<string> readStatus;
        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        internal McpForm(Func<string> getStatus)
        {
            readStatus=getStatus;Text="LLM / MCP 설정 · 이 PC에서 작업 허용";ClientSize=new Size(620,570);
            Font=new Font("맑은 고딕",9);StartPosition=FormStartPosition.CenterParent;FormBorderStyle=FormBorderStyle.FixedDialog;MaximizeBox=false;MinimizeBox=false;
            Config.Reload();McpOptions o;try{o=McpOptions.Load();}catch{o=new McpOptions{Roots=new[]{McpOptions.DefaultRoot}};}
            LabelAt("인터넷 없는 대상 PC에 Claude·GPT용 로컬 MCP 클라이언트가 접속합니다.\r\n로그인은 원격제어의 ID/PW를 그대로 사용합니다. 외부 계정 인증은 없습니다.",20,18,580,48);
            enabled=new CheckBox{Text="이 PC의 LAN MCP 사용",Checked=o.Enabled,Location=new Point(20,76),AutoSize=true};Controls.Add(enabled);
            LabelAt("MCP 포트",350,78,85,25);port=new NumericUpDown{Minimum=1,Maximum=65535,Value=o.Port,Location=new Point(440,75),Width=140};Controls.Add(port);
            LabelAt("LLM PC의 허용 IPv4 / 대역 (쉼표로 구분)",20,118,580,23);
            networks=new TextBox{Text=o.AllowedNetworks,Location=new Point(20,144),Width=580};Controls.Add(networks);
            LabelAt("파일 작업 폴더 (한 줄에 하나, 절대 경로)",20,183,580,23);
            roots=new TextBox{Text=string.Join("\r\n",o.Roots),Multiline=true,ScrollBars=ScrollBars.Vertical,Location=new Point(20,209),Size=new Size(580,94)};Controls.Add(roots);
            commands=new CheckBox{Text="프로그램·PowerShell 실행 허용",Checked=o.AllowCommands,Location=new Point(20,320),AutoSize=true};Controls.Add(commands);
            screen=new CheckBox{Text="화면 캡처 허용",Checked=o.AllowScreen,Location=new Point(355,320),AutoSize=true};Controls.Add(screen);
            LabelAt("명령은 원격제어 실행 계정의 권한으로 실행됩니다 (서비스 모드는 SYSTEM).\r\n작업 폴더 제한은 파일 도구에 적용되며 PowerShell 명령을 제한하지 않습니다.\r\nMCP는 별도 LAN 포트를 사용합니다. 신뢰하는 망에서 LLM PC IP를 지정하세요.",20,357,580,68);
            status=LabelAt("",20,435,580,38);RefreshStatus();timer.Interval=2000;timer.Tick+=delegate{RefreshStatus();};timer.Start();
            AddButton("연결 패키지 폴더",20,482,155,delegate{
                string here=Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),folder=Path.Combine(here,"plugins");
                if(!Directory.Exists(folder))folder=here;Process.Start(new ProcessStartInfo("explorer.exe",McpProcesses.Quote(folder)){UseShellExecute=true});
            });
            AddButton("LLM PC 연결 설정",185,482,155,delegate{using(var f=new McpClientForm())f.ShowDialog(this);});
            AddButton("웹 ChatGPT / Claude 설정",350,482,250,delegate{
                try {
                    string setup=Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),"uninstall.exe");
                    if(!File.Exists(setup))throw new IOException("새 Setup으로 설치한 뒤 웹 연결 설정을 열 수 있습니다.");
                    Process.Start(new ProcessStartInfo(setup,"/web-settings"){UseShellExecute=true,Verb="runas"});
                }catch(Exception ex){MessageBox.Show(this,ex.Message,"웹 연결 설정",MessageBoxButtons.OK,MessageBoxIcon.Information);}
            });
            AddButton("저장",400,524,95,Save);AddButton("닫기",505,524,95,delegate{Close();});
            LabelAt("패키지는 설치 폴더의 plugins에 있습니다. LLM PC로 복사해 수동 등록하세요.",20,530,370,28).Font=new Font(Font.FontFamily,8);
        }
        private Label LabelAt(string text,int x,int y,int w,int h){var c=new Label{Text=text,Location=new Point(x,y),Size=new Size(w,h)};Controls.Add(c);return c;}
        private void AddButton(string text,int x,int y,int w,Action action){var b=new Button{Text=text,Location=new Point(x,y),Size=new Size(w,30)};b.Click+=delegate{action();};Controls.Add(b);}
        private void RefreshStatus(){var state=Agent.ReadStatus();status.Text=readStatus()+(state!=null && state.McpWebNote.Length>0 ? "\r\n"+state.McpWebNote : "");}
        private void Save()
        {
            try{
                var o=new McpOptions{Enabled=enabled.Checked,Port=(int)port.Value,Roots=roots.Text.Split(new[]{'\r','\n'},StringSplitOptions.RemoveEmptyEntries),AllowedNetworks=networks.Text.Trim(),AllowCommands=commands.Checked,AllowScreen=screen.Checked};o.Validate();
                if(o.Enabled&&!Config.HasPassword)throw new ArgumentException("원격제어 기본 설정에서 로그인 비밀번호를 먼저 적용하세요.");
                if(o.Port==Config.Port)throw new ArgumentException("MCP 포트는 화면 접속 포트와 다르게 지정하세요.");
                foreach(string root in o.Roots){McpPath.Check(root,o);if(o.Enabled)Directory.CreateDirectory(root);}
                Config.SetMany(new Dictionary<string,string>{{"mcp_enabled",o.Enabled?"1":"0"},{"mcp_port",o.Port.ToString()},{"mcp_roots",string.Join("|",o.Roots)},{"mcp_networks",o.AllowedNetworks},{"mcp_commands",o.AllowCommands?"1":"0"},{"mcp_screen",o.AllowScreen?"1":"0"}});
                status.Text="저장했습니다. 실행 중인 원격제어에 수 초 이내 반영됩니다.";
            }catch(Exception ex){MessageBox.Show(this,ex.Message,"MCP 설정",MessageBoxButtons.OK,MessageBoxIcon.Information);}
        }
        protected override void Dispose(bool disposing){if(disposing)timer.Dispose();base.Dispose(disposing);}
    }
    internal sealed class McpClientForm : Form
    {
        private TextBox profile, host, port, user, password;
        private Label result;
        internal McpClientForm()
        {
            Text="LLM PC · Remote MCP 연결 설정";ClientSize=new Size(540,400);Font=new Font("맑은 고딕",9);StartPosition=FormStartPosition.CenterParent;
            FormBorderStyle=FormBorderStyle.FixedDialog;MaximizeBox=false;
            Controls.Add(new Label{Text="이 창은 Claude·Codex 등을 사용하는 LLM PC에서 설정하세요.\r\n비밀번호는 현재 Windows 계정으로 암호화해 이 PC에 저장합니다.",Location=new Point(20,16),Size=new Size(500,45)});
            profile=Field("연결 이름",76,"default",false);host=Field("대상 PC IPv4",116,"",false);port=Field("MCP 포트",156,"5901",false);
            user=Field("Remote ID",196,"admin",false);password=Field("Remote PW",236,"",true);
            ButtonAt("불러오기",20,280,100,delegate{LoadProfile();});ButtonAt("연결 시험",130,280,110,Test);
            ButtonAt("저장",250,280,110,delegate{try{using(var c=Client()){}McpProfile.Save(profile.Text.Trim(),host.Text.Trim(),port.Text.Trim(),user.Text,password.Text);result.Text="저장했습니다. MCP 클라이언트를 다시 연결하세요.";}catch(Exception ex){result.Text=ex.Message;}});
            ButtonAt("MCP JSON 복사",370,280,145,delegate{
                var entry=new Dictionary<string,object>{{"remotecontrol-lan",new{command=Assembly.GetExecutingAssembly().Location,args=new[]{"--mcp-client","--profile",profile.Text.Trim()}}}};
                try{Clipboard.SetText(McpJson.Write(new{mcpServers=entry}));result.Text="stdio MCP 설정 JSON을 복사했습니다. 비밀번호는 포함하지 않습니다.";}catch(Exception ex){result.Text=ex.Message;}
            });
            result=new Label{Location=new Point(20,329),Size=new Size(500,56)};Controls.Add(result);LoadProfile();
        }
        private TextBox Field(string text,int y,string value,bool secret){Controls.Add(new Label{Text=text,Location=new Point(20,y+4),Size=new Size(125,25)});var t=new TextBox{Text=value,UseSystemPasswordChar=secret,Location=new Point(150,y),Width=365};Controls.Add(t);return t;}
        private void ButtonAt(string text,int x,int y,int w,Action action){var b=new Button{Text=text,Location=new Point(x,y),Size=new Size(w,32)};b.Click+=delegate{action();};Controls.Add(b);}
        private McpClient Client(){return new McpClient(host.Text.Trim(),port.Text.Trim(),user.Text,password.Text);}
        private void LoadProfile(){try{var p=McpProfile.Load(profile.Text.Trim());host.Text=Json.Str(p,"host");port.Text=Json.Str(p,"port");user.Text=Json.Str(p,"user");password.Text=Json.Str(p,"password");result.Text="저장된 연결을 불러왔습니다.";}catch{result.Text="대상 PC의 MCP 설정값과 Remote ID/PW를 입력하세요.";}}
        private void Test()
        {
            McpClient client;try{client=Client();}catch(Exception ex){result.Text=ex.Message;return;}result.Text="연결 확인 중…";
            new Thread(delegate(){string message;using(client){try{client.Login();message="연결 및 Remote ID/PW 인증 성공";}catch(Exception ex){message=ex.Message;}}try{BeginInvoke((Action)delegate{result.Text=message;});}catch{}}){IsBackground=true}.Start();
        }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace RemoteControl
{
    internal sealed class McpForm : Form
    {
        private CheckBox enabled, commands, screen;
        private TextBox workspaces;
        private Label status;
        private readonly Func<string> readStatus;
        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        internal McpForm(Func<string> getStatus)
        {
            readStatus=getStatus;Text="Web MCP 설정";ClientSize=new Size(620,550);
            Font=new Font("맑은 고딕",9);StartPosition=FormStartPosition.CenterParent;FormBorderStyle=FormBorderStyle.FixedDialog;MaximizeBox=false;MinimizeBox=false;
            Config.Reload();McpOptions o=McpOptions.Load();
            LabelAt("ChatGPT, Claude, Codex는 HTTPS /mcp 주소로 연결합니다.\r\n연결 화면에서 원격제어 ID와 비밀번호로 로그인하세요.",20,18,580,48);
            enabled=new CheckBox{Text="이 PC의 Web MCP 사용 · 프로그램 시작 시 자동 실행",Checked=o.Enabled,Location=new Point(20,76),AutoSize=true};Controls.Add(enabled);
            LabelAt("기본 작업공간 (접근 제한 아님 · 첫 줄은 기본 실행 위치)",20,118,580,23);
            workspaces=new TextBox{Text=string.Join("\r\n",o.Workspaces),Multiline=true,ScrollBars=ScrollBars.Vertical,Location=new Point(20,146),Size=new Size(580,94)};Controls.Add(workspaces);
            commands=new CheckBox{Text="프로그램·PowerShell 실행 허용",Checked=o.AllowCommands,Location=new Point(20,258),AutoSize=true};Controls.Add(commands);
            screen=new CheckBox{Text="화면 캡처 허용",Checked=o.AllowScreen,Location=new Point(355,258),AutoSize=true};Controls.Add(screen);
            LabelAt("작업공간 밖의 경로도 지정할 수 있습니다. 폴더 목록은 권한 설정이 아닙니다.\r\n파일·명령은 실행 계정의 Windows 권한을 따릅니다 (서비스: SYSTEM).\r\n외부망과 내부망 모두 아래에서 HTTPS 연결을 설정하세요.",20,295,580,68);
            status=LabelAt("",20,369,580,40);RefreshStatus();timer.Interval=2000;timer.Tick+=delegate{RefreshStatus();};timer.Start();
            AddButton("Web MCP 주소 복사",20,418,185,delegate{CopyAddress("mcp_web_url","웹 연결을 먼저 설정하세요.");});
            AddButton("웹 HTTPS 연결 설정",215,418,185,delegate{OpenSetup("/web-settings");});
            AddButton("연결 파일 폴더",410,418,190,delegate{
                string folder=Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),"plugins");
                if(!Directory.Exists(folder)){MessageBox.Show(this,"웹 연결 설정을 적용하면 연결 파일이 생성됩니다.");return;}
                Process.Start(new ProcessStartInfo("explorer.exe",McpProcesses.Quote(folder)){UseShellExecute=true});
            });
            AddButton("내부망 HTTPS 설정",20,460,185,delegate{OpenSetup("/lan-web-settings");});
            AddButton("내부망 HTTPS 주소 복사",215,460,185,delegate{CopyAddress("mcp_lan_url","내부망 HTTPS 연결을 먼저 설정하세요.");});
            AddButton("내부망 공개 CA 폴더",410,460,190,delegate{
                try{string folder=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"RemoteControlLanWebMcp","certificates");
                    if(!Directory.Exists(folder))throw new IOException("내부망 HTTPS 연결을 먼저 설정하세요.");
                    Process.Start(new ProcessStartInfo("explorer.exe",McpProcesses.Quote(folder)){UseShellExecute=true});
                }catch(Exception ex){MessageBox.Show(this,ex.Message,"공개 CA");}
            });
            AddButton("저장",400,509,95,Save);AddButton("닫기",505,509,95,delegate{Close();});
        }
        private void OpenSetup(string mode)
        {
            try{
                string setup=Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),"uninstall.exe");
                if(!File.Exists(setup))throw new IOException("새 Setup으로 설치한 뒤 HTTPS 연결을 설정하세요.");
                Process.Start(new ProcessStartInfo(setup,mode){UseShellExecute=true,Verb="runas"});
            }catch(Exception ex){MessageBox.Show(this,ex.Message,"Web MCP 설정");}
        }
        private void CopyAddress(string key,string missing)
        {
            try{Config.Reload();string url=Config.Get(key);if(string.IsNullOrEmpty(url)||(key=="mcp_lan_url"&&Config.Get("mcp_lan_enabled")!="1"))throw new IOException(missing);
                Clipboard.SetText(url.TrimEnd('/')+"/mcp");status.Text="HTTPS 주소를 복사했습니다. MCP 연결 화면에 등록하세요.";
            }catch(Exception ex){MessageBox.Show(this,ex.Message,"Web MCP 주소");}
        }
        private Label LabelAt(string text,int x,int y,int w,int h){var c=new Label{Text=text,Location=new Point(x,y),Size=new Size(w,h)};Controls.Add(c);return c;}
        private void AddButton(string text,int x,int y,int w,Action action){var b=new Button{Text=text,Location=new Point(x,y),Size=new Size(w,30)};b.Click+=delegate{action();};Controls.Add(b);}
        private void RefreshStatus(){var state=Agent.ReadStatus();status.Text=readStatus()+(state!=null&&state.McpWebNote.Length>0?"\r\n"+state.McpWebNote:"");}
        private void Save()
        {
            try{
                var o=McpOptions.Load();o.Enabled=enabled.Checked;o.Workspaces=workspaces.Text.Split(new[]{'\r','\n'},StringSplitOptions.RemoveEmptyEntries);o.AllowCommands=commands.Checked;o.AllowScreen=screen.Checked;o.Validate();
                if(o.Enabled&&!Config.HasPassword)throw new ArgumentException("원격제어 기본 설정에서 로그인 비밀번호를 먼저 적용하세요.");
                foreach(string root in o.Workspaces){McpPath.Check(root);if(o.Enabled)Directory.CreateDirectory(root);}
                Config.SetMany(new Dictionary<string,string>{{"mcp_enabled",o.Enabled?"1":"0"},{"mcp_roots",string.Join("|",o.Workspaces)},{"mcp_commands",o.AllowCommands?"1":"0"},{"mcp_screen",o.AllowScreen?"1":"0"}});
                status.Text="저장했습니다. 실행 중인 Web MCP에 수 초 이내 반영됩니다.";
            }catch(Exception ex){MessageBox.Show(this,ex.Message,"Web MCP 설정",MessageBoxButtons.OK,MessageBoxIcon.Information);}
        }
        protected override void Dispose(bool disposing){if(disposing)timer.Dispose();base.Dispose(disposing);}
    }
}

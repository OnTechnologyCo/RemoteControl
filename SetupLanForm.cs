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
        private readonly TextBox address, additional, networks;
        private readonly NumericUpDown port, gateway;
        private readonly ComboBox interfaces;
        private readonly Button refreshInterfaces, usePrimary, addAddress, useNetworks, scripts;
        private readonly Label result, interfaceStatus, copyStatus;
        private LanInterfaceAddress[] available=new LanInterfaceAddress[0];
        private Options applied;
        private string caSha256="", caError="";
        private bool busy, failed, changingNetworks, networksEdited, changingAddresses;

        internal LanConnectionControls(Action<Control[]> row,bool optional)
        {
            enabled=new CheckBox{Name="LanWebMcpEnabled",Text=optional?"LAN IP 직접 연결 / 인터넷 없는 내부망 WebMCP 설정 (선택 사항)":"LAN WebMCP 사용",Checked=!optional&&WebMcp.ReadSetting("mcp_lan_enabled","0")=="1",Font=Ui.F(9,FontStyle.Regular),ForeColor=Color.White,FlatStyle=FlatStyle.Flat};
            row(new Control[]{enabled});
            row(new Control[]{Label("DDNS·터널·인터넷 없이 대상 PC의 HTTPS IP 주소로 연결합니다. 외부 웹 연결과 함께 사용할 수 있습니다.",Ui.Sub)});
            row(new Control[]{Label("이 PC의 랜카드 / IPv4 목록",Ui.Sub)});
            interfaces=new ComboBox{Name="LanWebMcpInterfaces",DropDownStyle=ComboBoxStyle.DropDownList,Font=Ui.F(9,FontStyle.Regular),BackColor=Ui.Input,ForeColor=Color.White};
            refreshInterfaces=Ui.Btn("IP 새로고침",Ui.Panel,112,30); refreshInterfaces.Name="LanWebMcpRefreshInterfaces";
            row(new Control[]{interfaces,refreshInterfaces});
            usePrimary=Ui.Btn("기본 IP로 사용",Ui.Panel,128,30); usePrimary.Name="LanWebMcpUsePrimary";
            addAddress=Ui.Btn("추가 IP에 넣기",Ui.Panel,128,30); addAddress.Name="LanWebMcpAddAddress";
            row(new Control[]{usePrimary,addAddress});
            row(new Control[]{Label("기본 서버 IPv4 / HTTPS 포트",Ui.Sub)});
            address=new TextBox{Name="LanWebMcpAddress",Text=LanWebMcp.DefaultAddress(),Font=Ui.F(9,FontStyle.Regular),BackColor=Ui.Input,ForeColor=Color.White};
            port=new NumericUpDown{Name="LanWebMcpHttpsPort",Minimum=443,Maximum=65535,Value=LanWebMcp.ReadPort("mcp_lan_https_port",LanWebMcp.DefaultPort),Font=Ui.F(9,FontStyle.Regular),BackColor=Ui.Input,ForeColor=Color.White};
            row(new Control[]{address,port});
            row(new Control[]{Label("추가 서버 IPv4 (쉼표 또는 줄바꿈으로 구분 · 기본 IP 포함 최대 32개)",Ui.Sub)});
            additional=new TextBox{Name="LanWebMcpAdditionalAddresses",Text=LanWebMcp.DefaultAdditionalAddresses(address.Text),Multiline=true,ScrollBars=ScrollBars.Vertical,Font=Ui.F(9,FontStyle.Regular),BackColor=Ui.Input,ForeColor=Color.White};
            row(new Control[]{additional});
            interfaceStatus=Label("",Ui.Sub); interfaceStatus.Name="LanWebMcpInterfaceStatus"; row(new Control[]{interfaceStatus});
            row(new Control[]{Label("접속 허용 클라이언트 IPv4 / 대역 (쉼표 구분)",Ui.Sub)});
            networks=new TextBox{Name="LanWebMcpNetworks",Text=LanWebMcp.DefaultNetworks(address.Text),Font=Ui.F(9,FontStyle.Regular),BackColor=Ui.Input,ForeColor=Color.White};
            useNetworks=Ui.Btn("선택 IP 대역 제안",Ui.Panel,142,30); useNetworks.Name="LanWebMcpSuggestNetworks";
            row(new Control[]{networks,useNetworks});
            row(new Control[]{Label("위 서버 IP는 이 PC의 접속 주소입니다. 허용 대역에는 연결할 클라이언트 PC의 IP·대역을 입력하세요. 기존 허용 대역을 바꾸려면 [선택 IP 대역 제안]을 누르거나 직접 입력하세요.",Ui.Sub)});
            gateway=new NumericUpDown{Name="LanWebMcpGateway",Minimum=1024,Maximum=65535,Value=LanWebMcp.ReadPort("mcp_lan_gateway",LanWebMcp.DefaultGateway),Font=Ui.F(9,FontStyle.Regular),BackColor=Ui.Input,ForeColor=Color.White};
            row(new Control[]{Label("LAN 전용 내부 중계 포트",Ui.Sub),gateway});
            result=Label("",Ui.Cyan); result.Name="LanWebMcpUrl"; row(new Control[]{result});
            scripts=Ui.Btn("연결 스크립트 / CA 복사",Ui.Accent,204,34); scripts.Name="LanWebMcpScripts"; row(new Control[]{scripts});
            copyStatus=Label("",Ui.Sub); copyStatus.Name="LanWebMcpCopyStatus"; row(new Control[]{copyStatus});
            row(new Control[]{Label("최초 1회: 대상 PC에서 공개 CA·SHA256을 확인한 후 클라이언트 PC에 인증서를 등록하세요.\n로컬 MCP 클라이언트에서 URL을 등록하고 기존 원격제어 ID·비밀번호로 로그인합니다. 클라우드 ChatGPT는 폐쇄망 IP에 직접 접속하지 못합니다.",Ui.Sub)});
            networksEdited=WebMcp.ReadSetting("mcp_lan_networks","").Length>0;
            applied=LanWebMcp.ReadAppliedConnection(); ReadCa();
            address.TextChanged+=delegate{AddressesChanged();}; additional.TextChanged+=delegate{AddressesChanged();};
            networks.TextChanged+=delegate{if(!changingNetworks)networksEdited=true;Refresh();};
            port.ValueChanged+=delegate{Refresh();}; gateway.ValueChanged+=delegate{Refresh();};
            enabled.CheckedChanged+=delegate{SetBusy(busy);};
            refreshInterfaces.Click+=delegate{RefreshInterfaces();};
            usePrimary.Click+=delegate{UseSelectedPrimary();};
            addAddress.Click+=delegate{AddSelectedAddress();};
            useNetworks.Click+=delegate{ProposeNetworks();};
            scripts.Click+=delegate{OpenScripts();};
            RefreshInterfaces(); SetBusy(false);
        }

        private static Label Label(string text,Color color)
        { return new Label{Text=text,Font=Ui.F(8,FontStyle.Regular),ForeColor=color}; }

        private Options Current()
        { var o=new Options(); Apply(o); return o; }

        private void ReadCa()
        {
            caSha256=""; caError="";
            try { caSha256=LanWebMcp.ReadPublicCaHash(); }
            catch(Exception error) { caError=error.Message; }
        }

        private void RefreshInterfaces()
        {
            LanInterfaceAddress prior=interfaces.SelectedItem as LanInterfaceAddress;
            available=LanWebMcp.Interfaces(); interfaces.Items.Clear();
            foreach(LanInterfaceAddress item in available) interfaces.Items.Add(item);
            int selected=-1;
            for(int i=0;i<available.Length;i++) if(available[i].Address==(prior==null?address.Text.Trim():prior.Address)) {selected=i;break;}
            if(selected<0 && available.Length>0) selected=0;
            interfaces.SelectedIndex=selected; ReadCa(); SetBusy(busy);
        }

        private void AddSelectedAddress()
        {
            LanInterfaceAddress item=interfaces.SelectedItem as LanInterfaceAddress; if(item==null) return;
            try {
                string[] values=LanWebMcp.Addresses(address.Text.Trim(),additional.Text+","+item.Address);
                additional.Text=LanWebMcp.AdditionalAddresses(address.Text.Trim(),string.Join(",",values));
            } catch(ArgumentException error) { MessageBox.Show(addAddress.FindForm(),error.Message,"LAN 서버 IP"); }
        }

        private void UseSelectedPrimary()
        {
            LanInterfaceAddress item=interfaces.SelectedItem as LanInterfaceAddress;if(item==null)return;
            try {
                string[] values=LanWebMcp.PromoteAddress(address.Text.Trim(),additional.Text,item.Address);
                changingAddresses=true;
                try {address.Text=values[0];additional.Text=LanWebMcp.AdditionalAddresses(values[0],string.Join(",",values));}
                finally {changingAddresses=false;}
                AddressesChanged();
            } catch(ArgumentException error) {MessageBox.Show(usePrimary.FindForm(),error.Message,"기본 서버 IP");}
        }

        private void AddressesChanged()
        {
            if(changingAddresses)return;
            if(!networksEdited) {
                try {
                    string suggestion=LanWebMcp.SuggestedNetworks(LanWebMcp.Addresses(address.Text.Trim(),additional.Text),available);
                    if(suggestion.Length>0) {changingNetworks=true;networks.Text=suggestion;changingNetworks=false;}
                } catch(ArgumentException) { }
            }
            Refresh();
        }

        private void ProposeNetworks()
        {
            try {
                string suggestion=LanWebMcp.SuggestedNetworks(LanWebMcp.Addresses(address.Text.Trim(),additional.Text),available);
                if(suggestion.Length==0) throw new ArgumentException("현재 연결된 랜카드에서 선택한 서버 IP의 대역을 확인할 수 없습니다.");
                // 기존 사용자 대역은 자동 교체하지 않고 명시한 제안 버튼에서만 바꾼다.
                networks.Text=suggestion; networksEdited=true;
            } catch(ArgumentException error) { MessageBox.Show(useNetworks.FindForm(),error.Message,"클라이언트 허용 대역"); }
        }

        private void Refresh()
        {
            Options current=Current(); bool matches=LanWebMcp.SameConnection(applied,current);
            string missing="", inputError="";
            try {
                string[] addresses=LanWebMcp.Addresses(current.LanAddress,current.LanAdditionalAddresses);
                string[] unavailable=LanWebMcp.MissingAddresses(addresses,available);
                missing=string.Join(", ",unavailable);
                string[] urls=new string[addresses.Length];
                for(int i=0;i<addresses.Length;i++) urls[i]=LanWebMcp.Origin(addresses[i],current.LanHttpsPort)+"/mcp";
                result.Text=(matches&&!failed?"적용된 LAN MCP 주소: ":"적용할 LAN MCP 주소: ")+string.Join("\n",urls);
            } catch(ArgumentException error) {inputError=error.Message;result.Text="LAN 서버 IPv4를 입력하세요.";}
            interfaceStatus.ForeColor=missing.Length>0||inputError.Length>0?Color.Orange:Ui.Sub;
            interfaceStatus.Text=inputError.Length>0?inputError:missing.Length>0?
                "현재 연결된 랜카드에 없는 IP: "+missing+"\n저장한 IP는 유지됩니다. 랜카드를 연결하거나 IP를 수정한 후 적용하세요.":
                available.Length+"개 IPv4 감지 · 기본 IP와 추가 IP를 적용하면 각 IP의 HTTPS·인증 주소가 함께 설정됩니다.";
            bool canCopy=!busy&&!failed&&enabled.Checked&&matches&&caSha256.Length==64&&missing.Length==0&&inputError.Length==0;
            scripts.Enabled=canCopy;
            copyStatus.Text=busy?"LAN 설정 적용 중에는 연결 스크립트를 복사할 수 없습니다.":
                failed?"이번 설정 적용이 실패했습니다. 원인을 확인하고 다시 적용하면 복사할 수 있습니다.":
                !enabled.Checked?"LAN WebMCP를 켜고 적용하면 연결 스크립트·공개 CA 정보를 복사할 수 있습니다.":
                !matches?"변경한 설정을 적용한 후 연결 스크립트를 복사하세요.":
                missing.Length>0?"연결되지 않은 서버 IP를 확인한 후 복사하세요.":
                caSha256.Length!=64?caError:
                "적용된 IP를 골라 Claude Code·VS Code·Cursor·Codex 설정과 공개 CA·SHA256을 복사할 수 있습니다.";
        }

        private void OpenScripts()
        {
            RefreshInterfaces();
            if(!scripts.Enabled) return;
            using(var form=new LanScriptForm(LanWebMcp.CopyConnection(applied),caSha256)) form.ShowDialog(scripts.FindForm());
        }

        internal void Apply(Options o)
        { o.LanWebMcp=enabled.Checked;o.LanAddress=address.Text.Trim();o.LanAdditionalAddresses=additional.Text.Trim();o.LanHttpsPort=(int)port.Value;o.LanGatewayPort=(int)gateway.Value;o.LanNetworks=networks.Text.Trim(); }

        internal void SetBusy(bool value)
        {
            busy=value; enabled.Enabled=!busy; bool on=!busy&&enabled.Checked;
            address.Enabled=additional.Enabled=networks.Enabled=port.Enabled=gateway.Enabled=interfaces.Enabled=useNetworks.Enabled=on;
            refreshInterfaces.Enabled=!busy; usePrimary.Enabled=addAddress.Enabled=on&&available.Length>0; Refresh();
        }

        internal void Applied(Options options)
        {
            failed=false; applied=options.LanWebMcp?LanWebMcp.CopyConnection(options):null;
            ReadCa(); RefreshInterfaces(); Refresh();
            if(options.LanWebMcp) OpenScripts();
        }

        internal void ApplyFailed() {failed=true;Refresh();}
    }

    internal sealed class LanScriptForm:InstallerForm
    {
        private readonly Options applied;
        private readonly string caSha256;
        private readonly ComboBox address,format;
        private readonly TextBox serverName,preview,mcpUrl,authorizeUrl,caUrl,caHash;
        private readonly Label status;
        private readonly Button copy,copyAll;

        internal LanScriptForm(Options configured,string caSha256)
        {
            this.applied=LanWebMcp.CopyConnection(configured); this.caSha256=caSha256;
            Text="LAN WebMCP 연결 스크립트 / 공개 CA";
            Row(Label(Text,Ui.Cyan,14,FontStyle.Bold));
            Row(Label("클라이언트 PC에서 접속할 IP를 선택하세요. 입력할 때도 적용된 IP만 사용할 수 있습니다.",Ui.Sub,8,FontStyle.Regular));
            address=new ComboBox{Name="LanScriptAddress",DropDownStyle=ComboBoxStyle.DropDown,Font=Ui.F(9,FontStyle.Regular),BackColor=Ui.Input,ForeColor=Color.White};
            address.Items.AddRange(LanWebMcp.Addresses(applied.LanAddress,applied.LanAdditionalAddresses));
            serverName=TextBox("LanScriptServerName",false);serverName.Text=LanConnectionInstructions.DefaultServerName;
            Row(Label("접속 IP",Ui.Sub,8,FontStyle.Regular),address);
            Row(Label("MCP 서버 이름",Ui.Sub,8,FontStyle.Regular),serverName);
            mcpUrl=TextBox("LanScriptMcpUrl",true);authorizeUrl=TextBox("LanScriptAuthorizeUrl",true);caUrl=TextBox("LanScriptCaUrl",true);caHash=TextBox("LanScriptCaHash",true);
            caHash.Text=caSha256;
            Row(Label("MCP 주소",Ui.Sub,8,FontStyle.Regular),mcpUrl);
            Row(Label("인증 주소",Ui.Sub,8,FontStyle.Regular),authorizeUrl);
            Row(Label("공개 CA",Ui.Sub,8,FontStyle.Regular),caUrl);
            Row(Label("공개 CA SHA256",Ui.Sub,8,FontStyle.Regular),caHash);
            Row(Label("최초 연결 전에 공개 CA를 등록하세요. 등록 후 클라이언트가 표시하는 로그인 링크에서 원격제어 ID·비밀번호로 로그인합니다.",Ui.Sub,8,FontStyle.Regular));
            format=new ComboBox{Name="LanScriptFormat",DropDownStyle=ComboBoxStyle.DropDownList,Font=Ui.F(9,FontStyle.Regular),BackColor=Ui.Input,ForeColor=Color.White};
            format.Items.AddRange(new object[]{"전체 안내 (모든 IP·인증·공개 CA 포함)","Claude Code — 명령 한 줄","VS Code — .vscode/mcp.json","Cursor — .cursor/mcp.json","Codex — 등록 / 로그인 명령","Codex — ~/.codex/config.toml","공개 CA URL / SHA256","Claude Code — 안전 등록 (PowerShell 권장)"});
            copyAll=Ui.Btn("전체 안내 복사",Ui.Panel,132,34);copyAll.Name="LanScriptCopyAll";copyAll.Click+=delegate{Copy(true);};
            Row(format,copyAll);
            preview=TextBox("LanScriptPreview",true);preview.Multiline=true;preview.WordWrap=false;preview.ScrollBars=ScrollBars.Both;Row(preview);
            status=Label("",Ui.Sub,8,FontStyle.Regular);status.Name="LanScriptStatus";Row(status);
            // 큰 글자 배율에서 긴 버튼 세 개를 고정하면 본문이 사라진다. 보조 복사는 본문에 둔다.
            copy=Ui.Btn("복사",Ui.Accent,90,34);copy.Name="LanScriptCopy";copy.Click+=delegate{Copy(false);};
            Button close=Ui.Btn("닫기",Ui.Panel,90,34);close.Click+=delegate{Close();};
            address.TextChanged+=delegate{RefreshPreview();};serverName.TextChanged+=delegate{RefreshPreview();};format.SelectedIndexChanged+=delegate{RefreshPreview();};
            address.Text=applied.LanAddress;format.SelectedIndex=(int)LanSnippetKind.ClaudeSafe;
            FinishLayout(760,690,copy,close);RefreshPreview();
        }

        private static Label Label(string text,Color color,float size,FontStyle style)
        { return new Label{Text=text,Font=Ui.F(size,style),ForeColor=color}; }
        private static TextBox TextBox(string name,bool readOnly)
        { return new TextBox{Name=name,ReadOnly=readOnly,Font=Ui.F(9,FontStyle.Regular),BackColor=Ui.Input,ForeColor=Color.White}; }
        private string Content(bool all)
        { return LanConnectionInstructions.Build(applied,address.Text.Trim(),serverName.Text.Trim(),caSha256,all?LanSnippetKind.Guide:(LanSnippetKind)Math.Max(0,format.SelectedIndex)); }

        private void RefreshPreview()
        {
            try {
                string content=Content(false), origin=LanWebMcp.Origin(address.Text.Trim(),applied.LanHttpsPort);
                mcpUrl.Text=origin+"/mcp";authorizeUrl.Text=origin+"/authorize";caUrl.Text=LanWebMcp.CaUrl(origin);preview.Text=content;
                copy.Enabled=copyAll.Enabled=true;
                status.ForeColor=Ui.Sub;status.Text="선택한 형식의 명령·설정만 복사합니다. 전체 안내에는 모든 IP와 공개 CA·SHA256이 포함됩니다.";
            } catch(ArgumentException error) {
                mcpUrl.Text=authorizeUrl.Text=caUrl.Text=preview.Text="";copy.Enabled=copyAll.Enabled=false;
                status.ForeColor=Color.Orange;status.Text=error.Message;
            }
        }

        private void Copy(bool all)
        {
            try {
                string content=Content(all);
                if(LanWebMcp.ReadPublicCaHash()!=caSha256) throw new IOException("공개 CA가 변경되었습니다. 이 창을 닫고 연결 스크립트 창을 다시 여세요.");
                Clipboard.SetDataObject(content,true,3,100);
                status.ForeColor=Ui.Cyan;status.Text=all?"모든 IP·인증 주소·공개 CA·SHA256을 포함한 안내를 복사했습니다.":"선택한 내용을 복사했습니다.";
            } catch(Exception error) {
                status.ForeColor=Color.Orange;status.Text="복사하지 못했습니다: "+error.Message;
                if(error is IOException) {copy.Enabled=copyAll.Enabled=false;preview.Text="";}
            }
        }
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
            apply=Ui.Btn("적용",Ui.Accent,110,34);apply.Name="ApplyLanSettings";apply.Click+=delegate{Apply();};close=Ui.Btn("닫기",Ui.Panel,90,34);close.Click+=delegate{Close();};
            FormClosing+=delegate(object sender,FormClosingEventArgs e){if(busy)e.Cancel=true;};FinishLayout(700,680,apply,close);
        }
        internal Options ReadOptions()
        { var o=new Options{Dir=Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),Port=Installer.ReadPort(),LockScreen=true};connection.Apply(o);LanWebMcp.Validate(o);return o; }
        private void Say(string line)
        { if(InvokeRequired){BeginInvoke((MethodInvoker)delegate{Say(line);});return;}log.AppendText(line+"\r\n");log.SelectionStart=log.TextLength;log.ScrollToCaret(); }
        private void Apply()
        {
            Options o;try{o=ReadOptions();}catch(Exception e){MessageBox.Show(this,e.Message,Text);return;}
            busy=true;apply.Enabled=close.Enabled=false;connection.SetBusy(true);ShowProgress(log);
            new Thread(delegate() {
                bool ok=false;
                try{LanWebMcp.ConfigureExisting(o,Say);ok=true;}
                catch(Exception e){Say("설정 실패: "+e.Message);}
                finally { try { BeginInvoke((MethodInvoker)delegate {
                    busy=false;apply.Enabled=close.Enabled=true;connection.SetBusy(false);
                    if(ok){Say(o.LanWebMcp?"LAN 설정을 적용했습니다. 연결 스크립트와 공개 CA 정보를 확인하세요.":"LAN WebMCP를 해제했습니다.");connection.Applied(o);}
                    else connection.ApplyFailed();
                }); } catch { } }
            }){IsBackground=true}.Start();
        }
    }
}

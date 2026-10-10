using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace RemoteControl
{
    internal sealed class TunnelForm : Form
    {
        private readonly ComboBox provider = new ComboBox();
        private readonly CheckBox auto = new CheckBox();
        private readonly TextBox host, port, user, key, known, remote, url, tailscale;
        private readonly Panel ssh = new Panel(), ts = new Panel();

        internal TunnelForm()
        {
            Text = "외부망 연결 설정";
            ClientSize = new Size(660, 520);
            Font = new Font("맑은 고딕", 9F);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            AddLabel(this, "연결 방식", 18, 23, 110);
            provider.SetBounds(140, 18, 495, 28);
            provider.DropDownStyle = ComboBoxStyle.DropDownList;
            provider.Items.AddRange(new object[] { "private-tunnel (SSH)", "Tailscale Funnel", "사용 안 함 (LAN만)" });
            Controls.Add(provider);
            auto.Text = "외부망 연결 유지 · 다음 실행 때 자동 연결";
            auto.SetBounds(140, 56, 480, 28);
            auto.Checked = Config.Get("extauto") == "1";
            Controls.Add(auto);
            ssh.SetBounds(0, 90, 660, 330); ts.SetBounds(0, 90, 660, 330);
            Controls.Add(ssh); Controls.Add(ts);
            host = Field(ssh, "SSH 허브 주소", "sshhost", 0);
            port = Field(ssh, "SSH 접속 포트", "sshport", 36);
            user = Field(ssh, "SSH 계정", "sshuser", 72);
            key = Field(ssh, "개인키 파일", "sshkeyfile", 108);
            known = Field(ssh, "known_hosts 파일", "sshknownhosts", 144);
            remote = Field(ssh, "허브 수신 포트", "sshremoteport", 180);
            url = Field(ssh, "브라우저 접속 주소", "sshurl", 216);
            Browse(key, 108); Browse(known, 144);
            AddLabel(ssh, "허브 주소·포트가 미정이면 비워 두고 자동 연결을 끈 채 저장할 수 있습니다.\r\n" +
                "허브 수신은 127.0.0.1에 요청합니다. 브라우저 주소에는 허브 HTTPS 중계 주소를 넣으세요.\r\n" +
                "known_hosts는 허브 관리자가 확인한 서버 키 파일입니다. 개인키와 다릅니다.", 18, 260, 620, 65);
            tailscale = Field(ts, "Tailscale 키", "", 0);
            tailscale.Text = Tunnel.LoadKey(); tailscale.UseSystemPasswordChar = true;
            CheckBox show = new CheckBox { Text = "키 보기", Left = 140, Top = 39, Width = 180 };
            show.CheckedChanged += delegate { tailscale.UseSystemPasswordChar = !show.Checked; };
            ts.Controls.Add(show);
            AddLabel(ts, "기존 Tailscale 키와 장비 신원은 방식 변경 후에도 유지됩니다.\r\n다른 키로 바꾸면 새 장비로 등록되어 공개 주소가 바뀔 수 있습니다.", 18, 80, 620, 60);
            AddLabel(this, "실행 중인 연결 설정을 바꾸면 외부 접속이 끊긴 뒤 새 설정으로 연결됩니다.\r\n서비스 모드에서도 저장한 설정이 적용됩니다. LAN 주소는 계속 사용할 수 있습니다.", 18, 423, 620, 46);
            Button save = new Button { Text = "저장", Left = 443, Top = 477, Width = 92, Height = 30 };
            Button cancel = new Button { Text = "취소", Left = 543, Top = 477, Width = 92, Height = 30, DialogResult = DialogResult.Cancel };
            save.Click += delegate { SaveSettings(); };
            Controls.Add(save); Controls.Add(cancel); AcceptButton = save; CancelButton = cancel;
            provider.SelectedIndexChanged += delegate {
                ssh.Visible = provider.SelectedIndex == 0; ts.Visible = provider.SelectedIndex == 1;
                auto.Enabled = provider.SelectedIndex != 2;
            };
            provider.SelectedIndex = RemoteTunnel.Provider == "tailscale" ? 1 : RemoteTunnel.Provider == "off" ? 2 : 0;
        }

        private static void AddLabel(Control parent, string text, int x, int y, int w, int h = 26)
        { parent.Controls.Add(new Label { Text = text, Left = x, Top = y, Width = w, Height = h }); }
        private static TextBox Field(Control parent, string label, string setting, int y)
        {
            AddLabel(parent, label, 18, y + 5, 122);
            TextBox box = new TextBox { Text = setting.Length == 0 ? "" : Config.Get(setting), Left = 140, Top = y, Width = 495 };
            parent.Controls.Add(box); return box;
        }
        private void Browse(TextBox target, int y)
        {
            target.Width = 419;
            Button browse = new Button { Text = "찾기", Left = 565, Top = y - 1, Width = 70, Height = 28 };
            browse.Click += delegate { using (OpenFileDialog dialog = new OpenFileDialog()) {
                dialog.CheckFileExists = true;
                if (dialog.ShowDialog(this) == DialogResult.OK) target.Text = dialog.FileName;
            } };
            ssh.Controls.Add(browse);
        }
        private void SaveSettings()
        {
            string p = provider.SelectedIndex == 0 ? "private" : provider.SelectedIndex == 1 ? "tailscale" : "off";
            SshSettings s = new SshSettings { Host = host.Text.Trim(), Port = port.Text.Trim(), User = user.Text.Trim(),
                KeyFile = key.Text.Trim(), KnownHosts = known.Text.Trim(), RemotePort = remote.Text.Trim(), Url = url.Text.Trim().TrimEnd('/') };
            if (p == "private" && auto.Checked)
            {
                string error = s.Validate(true);
                if (error.Length != 0) { MessageBox.Show(this, error, "SSH 설정"); return; }
            }
            string newKey = tailscale.Text.Trim();
            if (p == "tailscale" && (auto.Checked || newKey.Length > 0) && !newKey.StartsWith("tskey-", StringComparison.Ordinal))
            { MessageBox.Show(this, "Tailscale 키는 tskey-로 시작합니다."); return; }
            try
            {
                Dictionary<string, string> changes = new Dictionary<string, string> {
                    { "tunnel", p }, { "extauto", p != "off" && auto.Checked ? "1" : "0" },
                    { "sshhost", s.Host }, { "sshport", s.Port }, { "sshuser", s.User },
                    { "sshkeyfile", s.KeyFile }, { "sshknownhosts", s.KnownHosts },
                    { "sshremoteport", s.RemotePort }, { "sshurl", s.Url },
                    // 키 파일을 같은 경로에서 고친 뒤 저장해도 영구 오류를 해제한다.
                    { "tunnelrevision", Guid.NewGuid().ToString("N") }
                };
                if (p == "tailscale" && newKey != Tunnel.LoadKey())
                {
                    if (Tunnel.IsConfigured && MessageBox.Show(this,
                        "Tailscale 키를 바꾸면 장비 신원과 공개 주소가 바뀝니다. 계속할까요?", "키 변경",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
                    Tunnel.AddKeySettings(changes, newKey);
                }
                Config.SetMany(changes);
                DialogResult = DialogResult.OK; Close();
            }
            catch (Exception ex) { MessageBox.Show(this, "설정을 저장하지 못했습니다: " + ex.Message); }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace RemoteControl
{
    internal sealed class GeminiSettingsForm : Form
    {
        private readonly DataGridView grid;
        private readonly Label status;
        private string revision;
        internal GeminiSettingsForm()
        {
            RemoteControlSecrets.ConfigProtection.RequireAdministrator();
            Text = "Gemini 커넥터 설정 · readOnlyHint";
            Font = new Font("맑은 고딕", 9);
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(820, 610); MinimumSize = new Size(680, 480);
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(14) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(layout);
            var help = new Label { AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(0, 0, 0, 12), Text =
                "readOnlyHint는 조회 전용 여부를 알리는 메타데이터입니다. true로 바꿔도 실제 동작과 실행 권한은 그대로입니다.\r\n" +
                "적용 범위: 검증된 Google 콜백만 등록한 OAuth 연결. 특정 Gemini 제품의 신원을 증명하지는 않습니다.\r\n" +
                "저장 후 Gemini의 Tool schemas → Sync schema와 App actions 권한을 확인하세요.\r\n" +
                "확인창 생략 여부는 Gemini 정책에 따릅니다. 서버를 시작하지 않고도 설정할 수 있습니다." };
            layout.Controls.Add(help, 0, 0);
            grid = new DataGridView { Dock = DockStyle.Fill, AutoGenerateColumns = false, AllowUserToAddRows = false,
                AllowUserToDeleteRows = false, RowHeadersVisible = false, BackgroundColor = SystemColors.Window,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, SelectionMode = DataGridViewSelectionMode.CellSelect };
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "tool", HeaderText = "도구 이름", ReadOnly = true, FillWeight = 48 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "default", HeaderText = "서버 기본값", ReadOnly = true, FillWeight = 17 });
            var choice = new DataGridViewComboBoxColumn { Name = "choice", HeaderText = "저장값 / 편집", FillWeight = 20 };
            choice.Items.AddRange("기본값", "true", "false"); grid.Columns.Add(choice);
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "effective", HeaderText = "유효값", ReadOnly = true, FillWeight = 14 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "difference", HeaderText = "기본값과 다름", ReadOnly = true, FillWeight = 20 });
            Dictionary<string, bool> saved;
            string loadError = null;
            try { saved = GeminiSettings.Read(out revision); }
            catch (Exception ex) { saved = new Dictionary<string, bool>(); loadError = "설정을 읽지 못했습니다: " + ex.Message + " 모두 기본값으로 선택한 뒤 저장하여 복구할 수 있습니다."; }
            foreach (var tool in GeminiSettings.Catalog())
            {
                string name = (string)tool["name"]; bool value;
                int row = grid.Rows.Add(name, ((bool)McpJson.Obj(tool, "annotations")["readOnlyHint"]) ? "true" : "false",
                    saved.TryGetValue(name, out value) ? (value ? "true" : "false") : "기본값", "");
                grid.Rows[row].Cells[0].ToolTipText = (string)tool["description"];
                if (name == GeminiSettings.Setter) {
                    grid.Rows[row].Cells["choice"].ReadOnly = true;
                    grid.Rows[row].Cells["choice"].Style.BackColor = SystemColors.Control;
                    grid.Rows[row].Cells["choice"].ToolTipText = "저장 도구는 false로 고정되어 수정할 수 없습니다.";
                }
            }
            grid.CurrentCellDirtyStateChanged += delegate { if (grid.IsCurrentCellDirty) grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
            grid.CellValueChanged += delegate { MarkDifferences(); };
            MarkDifferences(); layout.Controls.Add(grid, 0, 1);
            status = new Label { AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(0, 10, 0, 10),
                Text = loadError ?? "선택 후 저장하세요. 모두 기본값으로는 저장 전까지 확정되지 않습니다." };
            layout.Controls.Add(status, 0, 2);
            var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
            var cancel = new Button { Text = "취소", AutoSize = true, DialogResult = DialogResult.Cancel };
            var save = new Button { Text = "저장", AutoSize = true, Enabled = loadError == null };
            var reset = new Button { Text = "모두 기본값으로", AutoSize = true };
            var reload = new Button { Text = "다시 불러오기", AutoSize = true };
            Action refresh = delegate {
                var latest = GeminiSettings.Read(out revision);
                foreach (DataGridViewRow row in grid.Rows) {
                    bool value; row.Cells["choice"].Value = latest.TryGetValue((string)row.Cells["tool"].Value, out value) ? (value ? "true" : "false") : "기본값";
                }
                save.Enabled = true;
            };
            reload.Click += delegate { try { refresh(); status.Text = "현재 설정을 다시 불러왔습니다."; } catch (Exception ex) { save.Enabled = false; status.Text = "조회 실패: " + ex.Message; } };
            cancel.Click += delegate { Close(); };
            reset.Click += delegate { foreach (DataGridViewRow row in grid.Rows) row.Cells["choice"].Value = "기본값"; save.Enabled = revision != null; status.Text = "기본값 복원 대기 중입니다. 저장해야 확정됩니다."; };
            save.Click += delegate {
                try {
                    grid.EndEdit(); var values = new Dictionary<string, bool>(StringComparer.Ordinal);
                    foreach (DataGridViewRow row in grid.Rows) {
                        string selected = (string)row.Cells["choice"].Value;
                        if (selected != "기본값") values.Add((string)row.Cells["tool"].Value, selected == "true");
                    }
                    GeminiSettings.Save(McpJson.Write(values), revision);
                    status.Text = "저장했습니다. 다음 도구 목록 조회부터 반영됩니다. Gemini에서 Sync schema 후 Raw schema를 확인하세요.";
                    try { refresh(); } catch (Exception ex) { save.Enabled = false; status.Text = "저장했지만 재조회하지 못했습니다. 다시 불러오세요: " + ex.Message; }
                } catch (Exception ex) { status.Text = "저장 실패: " + ex.Message; }
            };
            buttons.Controls.Add(cancel); buttons.Controls.Add(save); buttons.Controls.Add(reset); buttons.Controls.Add(reload);
            layout.Controls.Add(buttons, 0, 3); CancelButton = cancel;
        }
        private void MarkDifferences()
        {
            foreach (DataGridViewRow row in grid.Rows) {
                string choice = (string)row.Cells["choice"].Value;
                bool differs = choice != "기본값" && choice != (string)row.Cells["default"].Value;
                row.Cells["effective"].Value = choice == "기본값" ? row.Cells["default"].Value : choice;
                row.Cells["difference"].Value = (string)row.Cells["tool"].Value == GeminiSettings.Setter ? "false 고정" : differs ? "다름 · 동작 유지" : "";
                row.Cells["difference"].Style.ForeColor = differs ? Color.DarkRed : SystemColors.ControlText;
            }
        }
    }
}

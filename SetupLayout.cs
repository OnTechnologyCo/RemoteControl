using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace RemoteControlSetup
{
    // 단일 EXE/.NET Framework에서도 같은 계산을 사용한다. 고정 좌표나
    // WinForms의 암묵적인 폰트 확대와 섞지 않고 현재 DPI의 픽셀로 배치한다.
    internal class InstallerForm : Form
    {
        private readonly Panel viewport = new Panel {Name="SetupViewport", AutoScroll=true};
        private readonly Panel content = new Panel {Name="SetupContent"};
        private readonly Panel footer = new Panel {Name="SetupFooter"};
        private readonly List<Control[]> rows = new List<Control[]>();
        private readonly Dictionary<Control, Font> originalFonts = new Dictionary<Control, Font>();
        private readonly List<Font> displayFonts = new List<Font>();
        private Control[] actions = new Control[0];
        private bool ready, arranging;
        private int dpi = 96;
        private float textScale = 1;

        [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
        [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }

        protected InstallerForm()
        {
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Ui.Bg;
            viewport.BackColor = content.BackColor = Ui.Bg;
            footer.BackColor = Ui.Panel;
            viewport.Controls.Add(content);
            Controls.Add(viewport); Controls.Add(footer);
        }

        protected void Row(params Control[] controls)
        {
            rows.Add(controls);
            foreach (Control control in controls) {
                Label label = control as Label; if (label != null) label.AutoSize = false;
                TextBox text = control as TextBox; if (text != null) text.AutoSize = false;
                originalFonts.Add(control, control.Font);
                content.Controls.Add(control);
                control.TextChanged += delegate { Arrange(); };
            }
        }

        protected void FinishLayout(int width, int height, params Control[] buttons)
        {
            actions = buttons;
            foreach (Control control in buttons) {
                originalFonts.Add(control, control.Font);
                footer.Controls.Add(control);
                control.TextChanged += delegate { Arrange(); };
            }
            ready = true;
            using (Graphics graphics = CreateGraphics()) dpi = (int)graphics.DpiY;
            textScale = ReadTextScale();
            ApplyFonts();
            ClientSize = new Size(Px(width), Px(height));
            FitToWorkingArea(Screen.FromControl(this).WorkingArea);
            Arrange();
        }

        private int Px(int value) { return Math.Max(1, (int)Math.Round(value * dpi / 96.0)); }
        private static float ReadTextScale()
        {
            try {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Accessibility")) {
                    object value = key == null ? null : key.GetValue("TextScaleFactor");
                    if (value is int) return Math.Max(1f, Math.Min(2.25f, (int)value / 100f));
                }
            } catch { }
            return 1;
        }

        private void ApplyFonts()
        {
            var previous = new List<Font>(displayFonts); displayFonts.Clear();
            foreach (var entry in originalFonts) {
                Font font = new Font(entry.Value.FontFamily, entry.Value.SizeInPoints * dpi / 72f * textScale, entry.Value.Style, GraphicsUnit.Pixel);
                // WinForms는 같은 값의 Font를 지정하면 기존 인스턴스를 계속 쓴다.
                // 그 인스턴스를 버리면 다음 배치/그리기에서 GDI+ 예외가 난다.
                if (entry.Key.Font.Equals(font)) {
                    font.Dispose();
                    if (previous.Contains(entry.Key.Font)) displayFonts.Add(entry.Key.Font);
                } else {
                    entry.Key.Font = font; displayFonts.Add(font);
                }
            }
            foreach (Font font in previous) if (!displayFonts.Contains(font)) font.Dispose();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            try {
                int current = (int)GetDpiForWindow(Handle);
                if (current > 0 && current != dpi) { dpi = current; ApplyFonts(); }
            } catch (EntryPointNotFoundException) { }
            FitToWorkingArea(Screen.FromControl(this).WorkingArea); Arrange();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x02E0 && ready) { // WM_DPICHANGED
                int next = (int)((m.WParam.ToInt64() >> 16) & 0xffff);
                Rect bounds = (Rect)Marshal.PtrToStructure(m.LParam, typeof(Rect));
                base.WndProc(ref m);
                if (next > 0) { dpi = next; ApplyFonts(); }
                Bounds = Rectangle.FromLTRB(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);
                FitToWorkingArea(Screen.FromRectangle(Bounds).WorkingArea); Arrange();
                return;
            }
            base.WndProc(ref m);
            if (ready && (m.Msg == 0x001A || m.Msg == 0x007E)) { // text size / work area / resolution
                float next = ReadTextScale();
                if (next != textScale) { textScale = next; ApplyFonts(); }
                FitToWorkingArea(Screen.FromControl(this).WorkingArea); Arrange();
            }
        }

        private void FitToWorkingArea(Rectangle work)
        {
            MinimumSize = Size.Empty;
            Size = new Size(Math.Min(Width, work.Width), Math.Min(Height, work.Height));
            MinimumSize = new Size(Math.Min(Px(340), work.Width), Math.Min(Px(240), work.Height));
            if (Visible) Location = new Point(Math.Max(work.Left, Math.Min(Left, work.Right - Width)), Math.Max(work.Top, Math.Min(Top, work.Bottom - Height)));
        }

        // 시험도 실제 DPI/글자 배율 변경과 같은 경로를 쓴다. OS 설정은 변경하지 않는다.
        internal void SetDisplayMetrics(int displayDpi, float fontScale, Rectangle workArea)
        {
            if (displayDpi < 96 || displayDpi > 480 || fontScale < 1 || fontScale > 2.25f) throw new ArgumentOutOfRangeException();
            dpi = displayDpi; textScale = fontScale; ApplyFonts(); FitToWorkingArea(workArea); Arrange();
        }

        protected override void OnLayout(LayoutEventArgs e) { base.OnLayout(e); Arrange(); }

        private int NaturalWidth(Control control)
        {
            if (control is NumericUpDown) return Math.Max(Px(88), TextRenderer.MeasureText("65535", control.Font).Width + Px(30));
            if (control is Button) return Math.Max(Px(90), TextRenderer.MeasureText(control.Text, control.Font).Width + Px(30));
            return TextRenderer.MeasureText(control.Text, control.Font).Width + Px(4);
        }

        private bool Flexible(Control control, int index, int count)
        {
            return control is TextBox || control is ComboBox || control is CheckBox || (control is Label && (count == 1 || index == count - 1));
        }

        private int HeightFor(Control control, int width)
        {
            if (control is ProgressBar) return Px(6);
            TextBox text = control as TextBox;
            if (text != null && text.Multiline) return Math.Max(Px(84), control.Font.Height * 4 + Px(8));
            if (control is ComboBox) return ((ComboBox)control).PreferredHeight;
            if (control is TextBox || control is NumericUpDown) return control.PreferredSize.Height;
            if (control is Button) return Math.Max(Px(34), control.Font.Height + Px(18));
            int space = control is CheckBox ? Px(30) : Px(4);
            int height = TextRenderer.MeasureText(control.Text, control.Font, new Size(Math.Max(1, width - space), int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl).Height;
            return Math.Max(height + Px(6), control is CheckBox ? Px(24) : 1);
        }

        private void Arrange()
        {
            if (!ready || arranging || ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
            arranging = true;
            try {
                int pad = Px(18), gap = Px(8), width = Math.Max(1, ClientSize.Width - 2 * pad);
                int actionWidth = 0, actionHeight = 0;
                foreach (Control button in actions) { actionWidth += NaturalWidth(button) + gap; actionHeight = Math.Max(actionHeight, HeightFor(button, width)); }
                bool stack = actionWidth - gap > width;
                int footerHeight = 2 * gap + (stack ? actions.Length * actionHeight + Math.Max(0, actions.Length - 1) * gap : actionHeight);
                footer.SetBounds(0, Math.Max(0, ClientSize.Height - footerHeight), ClientSize.Width, footerHeight);
                int right = ClientSize.Width - pad, top = gap;
                foreach (Control button in actions) {
                    int w = stack ? width : NaturalWidth(button);
                    if (stack) { button.SetBounds(pad, top, w, actionHeight); top += actionHeight + gap; }
                    else { right -= w; button.SetBounds(right, top, w, actionHeight); right -= gap; }
                }
                viewport.SetBounds(0, 0, ClientSize.Width, Math.Max(1, ClientSize.Height - footerHeight));
                // 스크롤바 폭은 항상 남겨 두어 표시 여부에 따른 너비/줄바꿈 진동을 막는다.
                int contentWidth = Math.Max(1, viewport.ClientSize.Width - SystemInformation.VerticalScrollBarWidth);
                int available = Math.Max(1, contentWidth - 2 * pad), y = pad;
                foreach (Control[] row in rows) {
                    int required = gap * (row.Length - 1), flexible = 0;
                    int[] sizes = new int[row.Length];
                    for (int i = 0; i < row.Length; i++) {
                        bool fill = Flexible(row[i], i, row.Length);
                        sizes[i] = fill ? Px(130) : NaturalWidth(row[i]);
                        required += sizes[i]; if (fill) flexible++;
                    }
                    if (row.Length > 1 && required > available) {
                        foreach (Control control in row) { int h = HeightFor(control, available); control.SetBounds(pad, y, available, h); y += h + gap; }
                    } else {
                        int x = pad, rowHeight = 0;
                        for (int i = 0; i < row.Length; i++) {
                            int w = row.Length == 1 ? available : sizes[i] + (Flexible(row[i], i, row.Length) && flexible > 0 ? Math.Max(0, available - required) / flexible : 0);
                            int h = HeightFor(row[i], w); row[i].SetBounds(x, y, w, h); x += w + gap; rowHeight = Math.Max(rowHeight, h);
                        }
                        y += rowHeight + gap;
                    }
                }
                content.Size = new Size(contentWidth, y + gap);
                viewport.AutoScrollMinSize = new Size(0, content.Height);
            } finally { arranging = false; }
        }

        protected void ShowProgress(Control control) { viewport.ScrollControlIntoView(control); }
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) { foreach (Font font in displayFonts) font.Dispose(); displayFonts.Clear(); }
        }
    }
}

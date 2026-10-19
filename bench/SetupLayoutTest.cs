using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
namespace RemoteControlSetup
{
    internal static class SetupLayoutTest
    {
        private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
        private static Control Find(Control root, string name) {
            if (root.Name == name) return root;
            foreach (Control child in root.Controls) { Control found = Find(child, name); if (found != null) return found; }
            return null;
        }
        private static void SaveSnapshot(Form form,Panel view,Panel footer,string path)
        {
            using(Bitmap bitmap=new Bitmap(form.ClientSize.Width,form.ClientSize.Height)) {
                using(Graphics g=Graphics.FromImage(bitmap))g.Clear(form.BackColor);
                view.DrawToBitmap(bitmap,view.Bounds);footer.DrawToBitmap(bitmap,footer.Bounds);
                bitmap.Save(path,System.Drawing.Imaging.ImageFormat.Png);
            }
        }
        private static void CheckScriptPreview(Form form,Panel view,Panel footer,string label,string directory)
        {
            var preview=(TextBox)Find(form,"LanScriptPreview");
            Require(preview.ReadOnly&&preview.Multiline&&!preview.WordWrap&&preview.ScrollBars==ScrollBars.Both,label+": script preview lost scrolling or code formatting");
            Require(preview.TextLength>0,label+": empty script preview");
            Require(view.ClientSize.Height>=preview.Font.Height+4,label+": body cannot display one preview line");
            view.ScrollControlIntoView(preview);Application.DoEvents();
            Rectangle visible=Rectangle.Intersect(view.ClientRectangle,view.RectangleToClient(preview.RectangleToScreen(preview.ClientRectangle)));
            Require(visible.Height>=preview.Font.Height,label+": script preview cannot be reached in the body");
            // PreferredSize는 문서 전체 높이다. 긴 안내문은 실제 첫/끝 위치로 스크롤할 수 있는지 검사한다.
            foreach(int index in new[]{Math.Max(0,preview.TextLength-1),0}) {
                preview.Select(index,0);preview.ScrollToCaret();Application.DoEvents();
                Point caret=preview.GetPositionFromCharIndex(index);
                Require(caret.Y>=0&&caret.Y<preview.ClientSize.Height,label+": cannot scroll preview to "+(index==0?"start":"end"));
            }
            foreach(string name in new[]{"LanScriptCopy","LanScriptCopyAll"}) {
                var button=(Button)Find(form,name);
                Require(button!=null&&button.Visible&&button.Enabled&&button.CanSelect,label+": copy control unavailable: "+name);
                if(button.Parent==footer) Require(footer.ClientRectangle.Contains(button.Bounds),label+": copy control outside fixed footer: "+name);
                else {
                    view.ScrollControlIntoView(button);Application.DoEvents();
                    Rectangle buttonView=view.RectangleToClient(button.RectangleToScreen(button.ClientRectangle));
                    Require(view.ClientRectangle.Contains(buttonView),label+": cannot reach copy control in body: "+name);
                }
            }
            view.ScrollControlIntoView(preview);Application.DoEvents();
            SaveSnapshot(form,view,footer,Path.Combine(directory,label+"-preview.png"));
        }
        [STAThread] private static int Main(string[] args)
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            try {
                Directory.CreateDirectory(args[0]); int cases = 0;
                foreach (string kind in new[] {"SetupForm", "UninstallForm", "WebSettingsForm", "TailscaleSettingsForm", "LanSettingsForm", "LanScriptForm"}) {
                    bool uninstall=kind=="UninstallForm", tailscale=kind=="TailscaleSettingsForm", script=kind=="LanScriptForm", lanSettings=kind=="LanSettingsForm", webSettings=kind=="WebSettingsForm"||tailscale||lanSettings||script;
                    Assembly setup = args.Length > 1 ? Assembly.LoadFile(Path.GetFullPath(args[1])) : Assembly.GetExecutingAssembly();
                    Type formType=setup.GetType("RemoteControlSetup."+(tailscale?"WebSettingsForm":kind),true);
                    Form instance;
                    if(script) {
                        Type optionsType=setup.GetType("RemoteControlSetup.Options",true);
                        object options=Activator.CreateInstance(optionsType,true);
                        optionsType.GetField("LanWebMcp",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(options,true);
                        optionsType.GetField("LanAddress",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(options,"192.168.195.141");
                        optionsType.GetField("LanAdditionalAddresses",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(options,"10.0.0.10");
                        instance=(Form)Activator.CreateInstance(formType,BindingFlags.Instance|BindingFlags.NonPublic,null,new object[]{options,new string('A',64)},null);
                    } else instance=(Form)Activator.CreateInstance(formType,true);
                    using (Form form = instance) {
                        form.Opacity=0; form.ShowInTaskbar=false; form.StartPosition=FormStartPosition.Manual;
                        form.Show(); Application.DoEvents();
                        if(webSettings && !lanSettings && !script) {
                            ((ComboBox)Find(form,"WebConnectionMode")).SelectedIndex=tailscale?1:0;
                            if(!tailscale)((TextBox)Find(form,"WebAddress")).Text="https://najhna.dyndns.org";
                            ((CheckBox)Find(form,"WebDefaultHttpsPort")).Checked=true;
                        }
                        Panel view=(Panel)Find(form,"SetupViewport"), content=(Panel)Find(form,"SetupContent"), footer=(Panel)Find(form,"SetupFooter");
                        foreach (int[] sample in new[] {
                            new[] {96,100,1920,1040}, new[] {120,100,1366,728}, new[] {144,100,1366,728},
                            new[] {192,100,1920,1040}, new[] {96,150,1024,728}, new[] {144,150,1280,680},
                            new[] {192,225,1280,680}, new[] {96,100,640,440}, new[] {192,100,640,440}, new[] {288,225,800,560}
                        }) {
                            int dpi=sample[0], font=sample[1]; Rectangle work=new Rectangle(0,0,sample[2],sample[3]);
                            form.Size=new Size(Math.Min((uninstall?480:580)*dpi/96,work.Width),Math.Min((uninstall?340:webSettings?540:740)*dpi/96,work.Height));
                            form.GetType().BaseType.GetMethod("SetDisplayMetrics",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(form,new object[]{dpi,font/100f,work});
                            Application.DoEvents(); view.AutoScrollPosition=Point.Empty;
                            string label=(uninstall?"uninstall":script?"lan-scripts":lanSettings?"lan-settings":tailscale?"tailscale-settings":webSettings?"web-settings":"setup")+"-dpi"+dpi+"-font"+font+"-"+work.Width+"x"+work.Height;
                            Require(form.Width<=work.Width && form.Height<=work.Height,label+": window outside work area");
                            Require(view.Height>0 && view.Bottom<=footer.Top,label+": footer overlaps body");
                            foreach (Control button in footer.Controls) {
                                Require(footer.ClientRectangle.Contains(button.Bounds),label+": clipped action");
                                Require(button.Width>=button.GetPreferredSize(Size.Empty).Width && button.Height>=button.GetPreferredSize(Size.Empty).Height,label+": clipped button text");
                            }
                            var controls=new List<Control>(); foreach(Control control in content.Controls) controls.Add(control);
                            for(int i=0;i<controls.Count;i++) {
                                Control c=controls[i];
                                Require(c.Left>=0 && c.Right<=content.Width,label+": horizontal clipping: "+c.Text);
                                Require(c.Bottom<=content.Height,label+": unreachable bottom");
                                if(c is Label || c is CheckBox || c is Button) Require(c.GetPreferredSize(new Size(c.Width,0)).Height<=c.Height,label+": clipped text: "+c.Text);
                                TextBox text=c as TextBox;
                                bool scrollText=text!=null&&text.Multiline&&(text.ScrollBars==ScrollBars.Vertical||text.ScrollBars==ScrollBars.Both);
                                if(scrollText) Require(text.ClientSize.Height>=text.Font.Height*2,label+": multiline viewport cannot display two text lines: "+text.Name);
                                else if(c is TextBox || c is NumericUpDown) Require(c.Height>=c.PreferredSize.Height,label+": clipped input "+c.GetType().Name+" "+c.Text+" actual="+c.Height+" preferred="+c.PreferredSize.Height);
                                for(int j=i+1;j<controls.Count;j++) Require(!c.Bounds.IntersectsWith(controls[j].Bounds),label+": overlapping controls");
                                if(c.Visible && c.Height<=view.ClientSize.Height) {
                                    view.ScrollControlIntoView(c); Application.DoEvents();
                                    Rectangle visible=view.RectangleToClient(c.RectangleToScreen(c.ClientRectangle));
                                    Require(visible.Top>=0 && visible.Bottom<=view.ClientSize.Height,label+": cannot scroll to "+c.Text);
                                }
                            }
                            if(script)CheckScriptPreview(form,view,footer,label,args[0]);
                            view.AutoScrollPosition=Point.Empty; Application.DoEvents();
                            SaveSnapshot(form,view,footer,Path.Combine(args[0],label+".png"));
                            Console.WriteLine("PASS "+label);cases++;
                        }
                    }
                }
                File.WriteAllText(Path.Combine(args[0],"result.txt"),"PASS "+cases+" layout cases. No installation or system display settings changed.");
                return 0;
            } catch(Exception error) {Console.Error.WriteLine(error.ToString());return 1;}
        }
    }
}


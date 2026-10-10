using System;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
namespace RemoteControl
{
    internal static class TunnelFormTest
    {
        private static object Field(TunnelForm form, string name) { return typeof(TunnelForm).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(form); }
        private static void Save(TunnelForm form) { typeof(TunnelForm).GetMethod("SaveSettings", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(form, null); }
        [STAThread] public static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            try
            {
                Config.Reset();
                using (var form = new TunnelForm())
                {
                    form.Opacity = 0; form.ShowInTaskbar = false;
                    form.Show(); Application.DoEvents();
                    using (var bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(args[0]); }
                    form.Hide();
                    Save(form);
                    if (form.DialogResult != DialogResult.OK || Config.Get("tunnel") != "private" || Config.Get("extauto") != "0") throw new Exception("Blank SSH draft must save with automatic connection off");
                }
                Console.WriteLine("PASS blank private-tunnel settings can be saved before address/ports are assigned");
                Config.Set("tskey", "tskey-test-value"); Config.Set("sshhost", "draft.example");
                using (var form = new TunnelForm())
                {
                    ((ComboBox)Field(form, "provider")).SelectedIndex = 2; Save(form);
                    if (Config.Get("tunnel") != "off" || Config.Get("sshhost") != "draft.example" || Config.Get("tskey") != "tskey-test-value") throw new Exception("Switch must preserve both providers' credentials");
                }
                Console.WriteLine("PASS provider selection preserves SSH settings and Tailscale key");
                using (var form = new TunnelForm())
                {
                    ((ComboBox)Field(form, "provider")).SelectedIndex = 1;
                    ((CheckBox)Field(form, "auto")).Checked = true; Save(form);
                    if (Config.Get("tunnel") != "tailscale" || Config.Get("extauto") != "1") throw new Exception("Tailscale selection not saved");
                }
                Console.WriteLine("PASS Tailscale can be selected with automatic connection");
                return 0;
            }
            catch (Exception ex) { Console.WriteLine(ex); return 1; }
        }
    }
}

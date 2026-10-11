// 원격제어 설치 관리자. 한 파일로 끝난다 (본체를 안에 넣고 다닌다).

//

//   RemoteControl_Setup.exe             설치 창

//   RemoteControl_Setup.exe /S          묻지 않고 설치

//   RemoteControl_Setup.exe /uninstall  제거

//

// 설치 위치를 Program Files 로 고정하는 이유가 있다.

// 잠금 화면 제어를 켜면 이 exe 가 SYSTEM 권한 서비스로 실행된다.

// 그런데 사용자가 마음대로 쓸 수 있는 폴더에 두면, 그 폴더에 손댈 수 있는 사람이

// exe 를 바꿔치기해서 SYSTEM 권한을 가져갈 수 있다.

// 그래서 관리자만 쓸 수 있는 곳에 두고, 설치도 관리자 권한을 요구한다.

using System;

using System.Collections.Generic;

using System.Diagnostics;

using System.Drawing;

using System.IO;

using System.Reflection;

using System.Text;

using System.Threading;

using System.Windows.Forms;

using Microsoft.Win32;



// 이 값들이 비면 exe 속성 창의 [자세히] 탭이 통째로 빈칸이 되고, Defender 의 클라우드

// ML 이 그걸 크게 본다 (src/AssemblyInfo.cs 머리말 참고). 설치 파일은 받은 사람이

// 처음 보는 파일이라 평판이 0 이다 - 여기서 빈칸이면 더 쉽게 걸린다.

//

// 판 번호는 App.Version 하나만 고친다. 예전에는 여기에 따로 적어 두어서, 본체가

// 1.4.0 인데 설치 파일과 [프로그램 추가/제거] 는 1.2.3 으로 남아 있었다.

[assembly: AssemblyTitle("원격제어 설치 관리자")]

[assembly: AssemblyDescription("원격제어를 설치하고 서비스로 등록한다")]

[assembly: AssemblyProduct("원격제어")]

[assembly: AssemblyCompany("ontech")]

[assembly: AssemblyCopyright("Copyright (C) 2026 ontech")]

[assembly: AssemblyVersion("1.0.0.0")]

[assembly: AssemblyFileVersion(RemoteControlSetup.App.Version + ".0")]

[assembly: AssemblyInformationalVersion(RemoteControlSetup.App.Version)]



namespace RemoteControlSetup

{

    internal static class App

    {

        internal const string Product = "원격제어";

        internal const string Version = "1.8.17";

        internal const string ExeName = "RemoteControl.exe";

        internal const string IconName = "remotecontrol.ico";

        internal const string UninstExe = "uninstall.exe";

        internal const string ServiceName = "RemoteControlSvc";

        internal const string RegUninstall = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\RemoteControl";

        internal const string RegRun = @"Software\Microsoft\Windows\CurrentVersion\Run";

        internal const string RegRunValue = "RemoteControl";

        internal const string FirewallRule = "원격제어";



        internal static string DefaultDir

        {

            get

            {

                string pf = Environment.GetEnvironmentVariable("ProgramW6432");

                if (string.IsNullOrEmpty(pf))

                    pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);



                return Path.Combine(pf, "RemoteControl");

            }

        }

    }



    internal static class Ui

    {

        internal static readonly Color Bg = Color.FromArgb(15, 23, 42);

        internal static readonly Color Panel = Color.FromArgb(30, 41, 59);

        internal static readonly Color Input = Color.FromArgb(51, 65, 85);

        internal static readonly Color Accent = Color.FromArgb(99, 102, 241);

        internal static readonly Color Cyan = Color.FromArgb(34, 211, 238);

        internal static readonly Color Sub = Color.FromArgb(148, 163, 184);

        internal static readonly Color Ok = Color.FromArgb(52, 211, 153);

        internal static readonly Color Warn = Color.FromArgb(251, 191, 36);

        internal static readonly Color Err = Color.FromArgb(248, 113, 113);



        internal static Font F(float size, FontStyle st)

        {

            try { return new Font("맑은 고딕", size, st); }

            catch { return new Font(FontFamily.GenericSansSerif, size, st); }

        }



        internal static Label Text(string s, float size, FontStyle st, Color c, int x, int y, int w)

        {

            Font font = F(size, st);

            int height = w > 0 ? TextRenderer.MeasureText(s, font, new Size(w, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding).Height + 4 : 0;

            return new Label

            {

                Text = s,

                Font = font,

                ForeColor = c,

                Location = new Point(x, y),

                Size = new Size(w, height),

                AutoSize = w <= 0

            };

        }



        internal static Button Btn(string s, Color back, int w, int h)

        {

            Button b = new Button

            {

                Text = s,

                Size = new Size(w, h),

                BackColor = back,

                ForeColor = Color.White,

                FlatStyle = FlatStyle.Flat,

                Font = F(9.5f, FontStyle.Bold),

                Cursor = Cursors.Hand

            };

            b.FlatAppearance.BorderSize = 0;

            return b;

        }



        internal static CheckBox Check(string s, bool on, int x, int y, int w)

        {

            return new CheckBox

            {

                Text = s,

                Checked = on,

                Location = new Point(x, y),

                Size = new Size(w, 24),

                ForeColor = Color.FromArgb(226, 232, 240),

                Font = F(9, FontStyle.Regular),

                FlatStyle = FlatStyle.Flat,

                Cursor = Cursors.Hand

            };

        }

    }



    internal static class Sys

    {

        internal static bool IsAdmin()

        {

            try

            {

                using (System.Security.Principal.WindowsIdentity id =

                       System.Security.Principal.WindowsIdentity.GetCurrent())

                    return new System.Security.Principal.WindowsPrincipal(id)

                        .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);

            }

            catch { return false; }

        }



        /// <summary>관리자 권한으로 자기를 다시 띄우고, 이 판은 접는다.</summary>

        internal static bool Relaunch()

        {

            try

            {

                ProcessStartInfo psi = new ProcessStartInfo

                {

                    FileName = Assembly.GetExecutingAssembly().Location,

                    Arguments = string.Join(" ", Args()),

                    UseShellExecute = true,

                    Verb = "runas"

                };

                Process.Start(psi);

                return true;

            }

            catch { return false; }

        }



        private static string[] Args()

        {

            string[] a = Environment.GetCommandLineArgs();

            string[] r = new string[Math.Max(0, a.Length - 1)];

            Array.Copy(a, 1, r, 0, r.Length);

            return r;

        }



        internal static bool HasArg(string name)

        {

            foreach (string a in Environment.GetCommandLineArgs())

                if (string.Equals(a, name, StringComparison.OrdinalIgnoreCase)) return true;

            return false;

        }



        internal static void Extract(string resource, string target)

        {

            using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource))

            {

                if (s == null) throw new IOException("설치 파일 안에 " + resource + " 가 없습니다.");



                string dir = Path.GetDirectoryName(target);

                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);



                using (FileStream f = new FileStream(target, FileMode.Create, FileAccess.Write))

                {

                    byte[] buf = new byte[256 * 1024];

                    int n;

                    while ((n = s.Read(buf, 0, buf.Length)) > 0) f.Write(buf, 0, n);

                }

            }

        }



        internal static string Run(string exe, string args, int waitMs)

        {

            try

            {

                ProcessStartInfo psi = new ProcessStartInfo

                {

                    FileName = exe,

                    Arguments = args,

                    UseShellExecute = false,

                    RedirectStandardOutput = true,

                    RedirectStandardError = true,

                    CreateNoWindow = true,

                    StandardOutputEncoding = Encoding.UTF8

                };



                using (Process p = Process.Start(psi))

                {

                    string o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();

                    p.WaitForExit(waitMs);

                    return o;

                }

            }

            catch (Exception ex) { return ex.Message; }

        }



        /// <summary>돌고 있는 원격제어를 접는다. 서비스가 있으면 그것부터.</summary>

        internal static void StopApp()

        {

            Run("sc.exe", "stop " + App.ServiceName, 15000);



            for (int i = 0; i < 20; i++)

            {

                bool any = false;

                foreach (Process p in Process.GetProcessesByName("RemoteControl"))

                {

                    any = true;

                    try { p.Kill(); p.WaitForExit(3000); } catch { }

                    finally { try { p.Dispose(); } catch { } }

                }



                // 터널 중계는 이름만 보고 죽이면 안 된다.

                // 옆 프로그램(FileSharer)도 같은 이름을 쓴다. 실제로 남의 공유 터널을

                // 끊어 버린 적이 있다. 우리가 푼 폴더의 것만 고른다.

                foreach (Process p in Process.GetProcessesByName("tsproxy"))

                {

                    try

                    {

                        if (p.MainModule != null)

                        {

                            string path = p.MainModule.FileName;



                            foreach (string d in OurTsDirs)

                            {

                                if (string.IsNullOrEmpty(d)) continue;

                                if (!path.StartsWith(d, StringComparison.OrdinalIgnoreCase)) continue;



                                p.Kill();

                                break;

                            }

                        }

                    }

                    catch { }

                    finally { try { p.Dispose(); } catch { } }

                }



                if (!any) break;

                Thread.Sleep(300);

            }

        }



        /// <summary>

        /// 우리가 터널 중계 프로그램(tsproxy.exe)을 두는 자리 전부.

        ///

        /// v1.5.0 부터 %TEMP% 를 떠났다 - "실행 파일을 %TEMP% 에 풀어 실행" 이 백신

        /// 기계학습이 크게 보는 신호라 Bearfoos 계열로 오탐이 났다 (src/Tunnel.cs 주석).

        /// 옛 자리도 계속 본다. 판을 올린 PC 에는 v1.4.0 때 푼 것이 남아 있다.

        ///

        /// 본체(src/Tunnel.cs 의 BinDirs)와 같은 목록이어야 한다. 설치 관리자는 별도

        /// 어셈블리라 그 코드를 부를 수 없어 여기에 한 벌 더 적는다 - 한쪽만 고치면

        /// 제거할 때 중계 프로세스가 살아남아 파일이 지워지지 않는다.

        /// </summary>

        internal static string[] OurTsDirs

        {

            get

            {

                string common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);



                return new string[]

                {

                    string.IsNullOrEmpty(common) ? null : Path.Combine(common, "RemoteControl", "bin"),

                    string.IsNullOrEmpty(local)  ? null : Path.Combine(local,  "RemoteControl", "bin"),

                    Path.Combine(Path.GetTempPath(), "RemoteControlTs"),      // v1.4.0 까지

                };

            }

        }



        internal static void Firewall(string exe, int port, bool add)

        {

            Run("netsh.exe", "advfirewall firewall delete rule name=\"" + App.FirewallRule + "\"", 10000);

            if (!add) return;



            Run("netsh.exe", "advfirewall firewall add rule name=\"" + App.FirewallRule +

                "\" dir=in action=allow program=\"" + exe + "\" enable=yes profile=private,domain", 10000);



            Run("netsh.exe", "advfirewall firewall add rule name=\"" + App.FirewallRule +

                "\" dir=in action=allow protocol=TCP localport=" + port +

                " enable=yes profile=private,domain", 10000);

        }



        /// <summary>

        /// 프로그램을 관리자 권한 없이 띄운다.

        ///

        /// 설치 관리자는 관리자 권한으로 돈다. 여기서 그냥 실행하면 프로그램도

        /// 관리자 권한을 물려받는다. 그러면 나중에 사용자가 보통 권한으로 아이콘을

        /// 눌렀을 때 서로 다른 권한의 두 개가 되어 엉킨다.

        /// 탐색기(바탕화면 껍데기)에게 대신 띄워 달라고 하면 보통 권한으로 뜬다.

        /// </summary>

        internal static void RunAsUser(string exe, string workDir)

        {

            try

            {

                Process.Start(new ProcessStartInfo

                {

                    FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),

                                            "explorer.exe"),

                    Arguments = "\"" + exe + "\"",

                    UseShellExecute = true,

                    WorkingDirectory = workDir

                });

            }

            catch

            {

                try

                {

                    Process.Start(new ProcessStartInfo

                    {

                        FileName = exe,

                        WorkingDirectory = workDir,

                        UseShellExecute = true

                    });

                }

                catch { }

            }

        }



        internal static void Shortcut(string lnk, string target, string icon, string desc)

        {

            // WSH 를 늦은 바인딩으로 부른다. 참조 DLL 을 늘리지 않으려고.

            try

            {

                Type t = Type.GetTypeFromProgID("WScript.Shell");

                if (t == null) return;



                object sh = Activator.CreateInstance(t);

                object sc = t.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod,

                                           null, sh, new object[] { lnk });



                Type ts = sc.GetType();

                ts.InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, sc, new object[] { target });

                ts.InvokeMember("WorkingDirectory", System.Reflection.BindingFlags.SetProperty, null, sc,

                                new object[] { Path.GetDirectoryName(target) });

                ts.InvokeMember("Description", System.Reflection.BindingFlags.SetProperty, null, sc, new object[] { desc });



                if (!string.IsNullOrEmpty(icon) && File.Exists(icon))

                    ts.InvokeMember("IconLocation", System.Reflection.BindingFlags.SetProperty, null, sc, new object[] { icon });



                ts.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, sc, null);

            }

            catch { }

        }

    }



    // ------------------------------------------------------------------ 설치



    internal sealed class Options

    {

        internal string Dir = App.DefaultDir;

        internal bool LockScreen = true;    // 서비스 등록 (잠금 화면 제어)

        internal bool AutoRun = true;       // 로그인하면 트레이에 뜨게

        internal bool Firewall = true;

        internal bool Desktop = true;

        internal bool CarryKey = true;    // 설치 파일에 키가 들어 있으면 그것을 쓴다

        internal bool VirtualDisplay;     // 가상 모니터 드라이버까지 설치 (모니터 없는 PC 전용)

        internal int Port = 5900;

        internal bool LanWebMcp;
        internal string LanAddress, LanNetworks;
        internal int LanHttpsPort=443, LanGatewayPort=59338;
        internal bool WebMcp;
        internal string WebUrl;
        internal bool WebTailscale, WebPrivate;
        internal int WebPublicPort=443, WebGatewayPort=59335;
        internal string CertificatePath, PrivateKeyPath;

    }



    internal sealed class Installer

    {

        internal delegate void Say(string line);



        internal void Install(Options o, Say say)

        {

            WebMcp.Validate(o);
            LanWebMcp.Validate(o);

            say("폴더를 만듭니다: " + o.Dir);

            Directory.CreateDirectory(o.Dir);



            say("연결 패키지를 준비합니다…");
            WebMcp.ExtractPackages(o.Dir, o.WebMcp && !o.WebTailscale && !o.WebPrivate ? o.WebUrl : null);
            WebMcp.Preflight(o, say);
            LanWebMcp.Preflight(o, say);

            say("원격제어를 멈춥니다…");

            Sys.StopApp();



            string exe = Path.Combine(o.Dir, App.ExeName);

            string ico = Path.Combine(o.Dir, App.IconName);



            say("본체를 풀어 놓습니다…");

            Sys.Extract(App.ExeName, exe);

            try { Sys.Extract(App.IconName, ico); } catch { }



            say("LLM MCP 연결 패키지를 복사합니다…");

            string plugins = Path.Combine(o.Dir, "plugins");

            Directory.CreateDirectory(plugins);

            say(o.WebMcp ? "  웹 ChatGPT 업로드: " + Path.Combine(plugins, WebMcp.PluginName)
                : "  웹 연결은 설치 후 [LLM / MCP 설정] → [웹 ChatGPT / Claude 설정]에서 설정할 수 있습니다.");


            say("제거 프로그램을 넣습니다…");

            try { File.Copy(Assembly.GetExecutingAssembly().Location, Path.Combine(o.Dir, App.UninstExe), true); }

            catch { }



            say("설정 폴더를 준비합니다…");

            PrepareDataDir(say);



            ApplySettings(o, say);



            if (o.VirtualDisplay) InstallVirtualDisplay(o.Dir, say);



            if (o.Firewall)

            {

                say("방화벽에서 허용합니다 (사설·도메인망)…");

                Sys.Firewall(exe, o.Port, true);

            }



            say("시작 메뉴에 등록합니다…");

            string menu = Path.Combine(

                Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), App.Product);

            Directory.CreateDirectory(menu);

            Sys.Shortcut(Path.Combine(menu, App.Product + ".lnk"), exe, ico, App.Product);

            Sys.Shortcut(Path.Combine(menu, App.Product + " 제거.lnk"),

                         Path.Combine(o.Dir, App.UninstExe), ico, App.Product + " 제거");



            if (o.Desktop)

            {

                say("바탕화면에 바로가기를 만듭니다…");

                Sys.Shortcut(Path.Combine(

                    Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),

                    App.Product + ".lnk"), exe, ico, App.Product);

            }



            if (o.AutoRun)

            {

                say("로그인하면 트레이에 뜨도록 등록합니다…");

                try

                {

                    // --tray 를 붙인다. 이게 있어야 창 없이 트레이에서만 뜬다.

                    // 사람이 아이콘을 눌러 켤 때는 인자가 없으므로 창이 나온다.

                    using (RegistryKey k = Registry.LocalMachine.CreateSubKey(App.RegRun))

                        if (k != null) k.SetValue(App.RegRunValue, "\"" + exe + "\" --tray");

                }

                catch { }

            }

            else

            {

                try

                {

                    using (RegistryKey k = Registry.LocalMachine.OpenSubKey(App.RegRun, true))

                        if (k != null) k.DeleteValue(App.RegRunValue, false);

                }

                catch { }

            }



            say("프로그램 목록에 올립니다…");

            try

            {

                using (RegistryKey k = Registry.LocalMachine.CreateSubKey(App.RegUninstall))

                {

                    if (k != null)

                    {

                        k.SetValue("DisplayName", App.Product);

                        k.SetValue("DisplayVersion", App.Version);

                        k.SetValue("Publisher", App.Product);

                        k.SetValue("InstallLocation", o.Dir);

                        k.SetValue("DisplayIcon", ico);

                        k.SetValue("UninstallString", "\"" + Path.Combine(o.Dir, App.UninstExe) + "\" /uninstall");

                        k.SetValue("NoModify", 1, RegistryValueKind.DWord);

                        k.SetValue("NoRepair", 1, RegistryValueKind.DWord);



                        try { k.SetValue("EstimatedSize", (int)(new FileInfo(exe).Length / 1024), RegistryValueKind.DWord); }

                        catch { }

                    }

                }

            }

            catch { }



            if (o.LockScreen)

            {

                say("잠금 화면 제어를 켭니다 (윈도우 서비스 등록)…");



                string r = Sys.Run(exe, "--install", 60000);

                bool ok = false;



                for (int i = 0; i < 20; i++)

                {

                    if (ServiceRunning()) { ok = true; break; }

                    Thread.Sleep(500);

                }



                say(ok ? "  잠금 화면 제어가 켜졌습니다."

                       : "  켜지 못했습니다. 프로그램 창에서 다시 시도할 수 있습니다. " + r.Trim());

            }

            else

            {

                Sys.Run(exe, "--uninstall", 30000);

            }



            say("");

            if (o.WebMcp) WebMcp.Activate(o, say);
            if (o.LanWebMcp) LanWebMcp.Activate(o, say);

            say("설치가 끝났습니다.");

        }



        /// <summary>

        /// 설정과 기록을 둘 공용 폴더를 만들고, 보통 사용자도 쓸 수 있게 해 준다.

        ///

        /// 프로그램 자체는 Program Files 에 있어 아무나 못 고친다 (고칠 수 있으면

        /// SYSTEM 권한 서비스가 그 파일을 실행하므로 권한을 통째로 내주는 셈이다).

        /// 하지만 설정은 창에서 바꿔야 하므로, 설정 폴더에만 쓰기를 허용한다.

        /// </summary>

        private static void PrepareDataDir(Say say)

        {

            try

            {

                string dir = Path.Combine(

                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RemoteControl");



                Directory.CreateDirectory(dir);



                System.Security.AccessControl.DirectorySecurity sec = Directory.GetAccessControl(dir);



                sec.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(

                    new System.Security.Principal.SecurityIdentifier(

                        System.Security.Principal.WellKnownSidType.BuiltinUsersSid, null),

                    System.Security.AccessControl.FileSystemRights.Modify,

                    System.Security.AccessControl.InheritanceFlags.ContainerInherit |

                    System.Security.AccessControl.InheritanceFlags.ObjectInherit,

                    System.Security.AccessControl.PropagationFlags.None,

                    System.Security.AccessControl.AccessControlType.Allow));



                Directory.SetAccessControl(dir, sec);

            }

            catch (Exception ex) { say("  설정 폴더 권한을 주지 못했습니다: " + ex.Message); }

        }



        /// <summary>

        /// 설치 파일 안에 넣어 둔 값들. 없으면 빈 목록.

        ///

        ///   tskey   Tailscale 인증 키

        ///   tgchat  텔레그램 알림을 받을 곳 (봇 토큰은 프로그램 안에 박혀 있다)

        ///

        /// 예전 판은 이 파일에 키 하나만 통째로 넣었다. 그때 만든 설치 파일도 읽히도록

        /// 앞이 tskey- 로 시작하면 옛 방식으로 본다. (키 자체에 '=' 가 들어 있어서

        /// 줄 모양만으로는 두 방식을 가릴 수 없다 — ?ephemeral=true 같은 것이 붙는다)

        /// </summary>

        private static Dictionary<string, string> ReadPreset()

        {

            Dictionary<string, string> d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);



            try

            {

                using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("preset.txt"))

                {

                    if (s == null) return d;



                    string all;

                    using (StreamReader r = new StreamReader(s, Encoding.UTF8)) all = r.ReadToEnd().Trim();

                    if (all.Length == 0) return d;



                    if (all.StartsWith("tskey-", StringComparison.OrdinalIgnoreCase))

                    {

                        d["tskey"] = all;

                        return d;

                    }



                    foreach (string line in all.Split('\n'))

                    {

                        string t = line.Trim();

                        int eq = t.IndexOf('=');

                        if (eq <= 0) continue;



                        d[t.Substring(0, eq).Trim()] = t.Substring(eq + 1).Trim();

                    }

                }

            }

            catch { }



            return d;

        }



        private static string Val(Dictionary<string, string> d, string k)

        {

            string v;

            return d != null && d.TryGetValue(k, out v) && v != null ? v.Trim() : "";

        }



        /// <summary>이 PC 기준으로 잠근다. SYSTEM 서비스도 풀 수 있어야 한다.</summary>

        private static string SealForMachine(string v)

        {

            byte[] p = System.Security.Cryptography.ProtectedData.Protect(

                Encoding.UTF8.GetBytes(v), null,

                System.Security.Cryptography.DataProtectionScope.LocalMachine);



            return Convert.ToBase64String(p);

        }



        internal static string SettingsPath

        {

            get

            {

                return Path.Combine(

                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),

                    "RemoteControl", "settings.txt");

            }

        }



        /// <summary>이미 정해져 있는 포트를 읽는다. 없으면 5900.</summary>

        internal static int ReadPort()

        {

            try

            {

                if (!File.Exists(SettingsPath)) return 5900;



                foreach (string line in File.ReadAllLines(SettingsPath, Encoding.UTF8))

                {

                    string t = line.Trim();

                    if (!t.StartsWith("port=", StringComparison.OrdinalIgnoreCase)) continue;



                    int v;

                    if (int.TryParse(t.Substring(5).Trim(), out v) && v > 0 && v < 65536) return v;

                }

            }

            catch { }

            return 5900;

        }



        /// <summary>

        /// 설치 창에서 정한 것을 설정 파일에 넣는다.

        ///

        ///   포트  - 고른 값을 그대로 쓴다. 방화벽만 그 번호로 열고 프로그램은 5900 을

        ///           쓰면 앞뒤가 안 맞는다 (실제로 그렇게 되어 있었다).

        ///   키    - 설치 파일 안에 넣어 둔 것이 있고, 이 PC 에 아직 없을 때만 넣는다.

        ///

        /// 비밀번호처럼 이미 쓰던 값은 건드리지 않는다.

        /// </summary>

        private static void ApplySettings(Options o, Say say)

        {

            try

            {

                string f = SettingsPath;

                Directory.CreateDirectory(Path.GetDirectoryName(f));



                List<string> lines = new List<string>();

                bool hasKey = false, hasTg = false;



                if (File.Exists(f))

                {

                    foreach (string line in File.ReadAllLines(f, Encoding.UTF8))

                    {

                        string t = line.Trim();

                        if (t.Length == 0) continue;



                        // 포트는 새로 쓴다.

                        if (t.StartsWith("port=", StringComparison.OrdinalIgnoreCase)) continue;

                        if ((o.WebMcp || o.LanWebMcp) && t.StartsWith("mcp_enabled=", StringComparison.OrdinalIgnoreCase)) continue;
                        if (o.WebMcp && !o.WebTailscale && !o.WebPrivate && t.StartsWith("mcp_web_url=", StringComparison.OrdinalIgnoreCase)) continue;


                        if (t.StartsWith("tskey=", StringComparison.OrdinalIgnoreCase) ||

                            t.StartsWith("tskeym=", StringComparison.OrdinalIgnoreCase))

                        {

                            // 값이 있으면 이미 쓰던 키다. 그대로 둔다.

                            if (t.IndexOf('=') < t.Length - 1) hasKey = true;

                        }



                        if (t.StartsWith("tgchat=", StringComparison.OrdinalIgnoreCase))

                        {

                            if (t.IndexOf('=') < t.Length - 1) hasTg = true;

                        }



                        lines.Add(line);

                    }

                }



                lines.Add("port=" + o.Port);

                if(o.LanWebMcp && !o.WebMcp)lines.Add("mcp_enabled=1");
                if (o.WebMcp) { lines.Add("mcp_enabled=1"); if(!o.WebTailscale && !o.WebPrivate)lines.Add("mcp_web_url=" + WebMcp.NormalizeUrl(o.WebUrl)); }
                say("  접속 포트를 " + o.Port + " 로 정했습니다.");



                Dictionary<string, string> preset = o.CarryKey

                    ? ReadPreset()

                    : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);



                string key = Val(preset, "tskey");



                if (!hasKey && !string.IsNullOrEmpty(key))

                {

                    lines.Add("tskeym=" + SealForMachine(key));



                    bool hasAuto = false;

                    foreach (string line in lines)

                        if (line.TrimStart().StartsWith("extauto=", StringComparison.OrdinalIgnoreCase)) hasAuto = true;



                    if (!hasAuto) lines.Add("extauto=1");



                    say("  Tailscale 키를 넣었습니다. 비밀번호만 정하면 외부망까지 자동으로 열립니다.");

                }

                else if (hasKey) say("  이 PC 에는 이미 키가 있어 그대로 둡니다.");



                // 알림 받을 곳만 옮긴다. 봇 토큰은 프로그램 안에 박혀 있어서 옮길 것이 없다.

                string chat = Val(preset, "tgchat");



                if (!hasTg && chat.Length > 0)

                {

                    lines.Add("tgchat=" + chat);

                    say("  텔레그램 알림을 넣었습니다. 주소가 바뀌면 알려 줍니다.");

                }

                else if (hasTg) say("  이 PC 에는 이미 알림 설정이 있어 그대로 둡니다.");



                File.WriteAllLines(f, lines.ToArray(), Encoding.UTF8);

            }

            catch (Exception ex) { if (o.WebMcp) throw; say("  키를 넣지 못했습니다: " + ex.Message); }

        }



        internal const string VddDirName = "vdd";



        /// <summary>설치 파일 안에 가상 모니터 드라이버가 들어 있는가.</summary>

        /// <summary>

        /// 설치 파일 안에 들어 있는 가상 모니터 드라이버 파일 이름들.

        ///

        /// 압축해서 넣지 않는다. zip 을 풀려면 Shell.Application 을 써야 하는데

        /// 그건 STA 스레드에서만 제대로 돈다. 설치는 창을 막지 않으려고 배경 스레드에서

        /// 하므로 STA 가 아니고, 그래서 조용히 실패했다. 파일 몇 개뿐이라 하나씩 넣는다.

        /// </summary>

        internal static string[] VddFiles()

        {

            List<string> r = new List<string>();



            try

            {

                foreach (string n in Assembly.GetExecutingAssembly().GetManifestResourceNames())

                    if (n.StartsWith("vdd/", StringComparison.OrdinalIgnoreCase)) r.Add(n);

            }

            catch { }



            return r.ToArray();

        }



        internal static bool HasVirtualDisplay()

        {

            return VddFiles().Length > 0;

        }



        /// <summary>

        /// 가상 모니터를 만든다.

        ///

        /// 모니터를 안 꽂으면 윈도우가 GPU 드라이버를 못 쓰고 기본 화면 장치로 잡는다.

        /// 그 장치는 640x480 하나만 내놓는다. 어떤 프로그램도 그걸 넘길 수 없다 -

        /// 장치가 못 하는 일이다. 가상 모니터를 만들어 주면 그제서야 FHD 가 열린다.

        ///

        /// 모니터가 달린 PC 에 넣으면 쓰지도 않는 두 번째 화면이 생겨 거슬린다.

        /// 그래서 기본은 꺼져 있고, 모니터 없는 PC 에만 켠다.

        /// </summary>

        private static void InstallVirtualDisplay(string dir, Say say)

        {

            try

            {

                say("가상 모니터 드라이버를 넣습니다…");



                string vdd = Path.Combine(dir, VddDirName);

                Directory.CreateDirectory(vdd);



                string[] res = VddFiles();

                if (res.Length == 0) { say("  드라이버가 들어 있지 않습니다."); return; }



                foreach (string r in res)

                {

                    string name = r.Substring(4);          // "vdd/" 를 뗀다

                    Sys.Extract(r, Path.Combine(vdd, name));

                }



                say("  파일 " + res.Length + "개를 풀었습니다.");



                // 어떤 드라이버를 넣었는지에 따라 설치 방법이 다르다.

                // 종류를 미리 정해 두면 다른 걸 쓰고 싶을 때 코드를 고쳐야 한다.

                // 폴더에 뭐가 들었는지 보고 알아서 고른다.



                // 1) 자체 설치 프로그램이 있으면 그것에 맡긴다 (alluno-vdd 등).

                foreach (string name in new string[] { "install.bat", "install.cmd",

                                                       "Installer.exe", "AllunoVDDInstaller.exe" })

                {

                    string p = Path.Combine(vdd, name);

                    if (!File.Exists(p)) continue;



                    say("  " + name + " 으로 설치합니다…");

                    say("  " + Squash(RunIn(vdd, p, "install")));

                    say("  설치했습니다. 잠시 뒤 1920x1080 으로 맞춰집니다.");

                    return;

                }



                // 2) usbmmidd 방식 (deviceinstaller + enableidd).

                string di = Path.Combine(vdd, "deviceinstaller64.exe");

                if (!File.Exists(di)) di = Path.Combine(vdd, "deviceinstaller.exe");



                if (File.Exists(di))

                {

                    say("  " + Squash(RunIn(vdd, di, "install usbmmidd.inf usbmmidd")));

                    say("  " + Squash(RunIn(vdd, di, "enableidd 1")));

                    say("  가상 모니터를 켰습니다. 잠시 뒤 1920x1080 으로 맞춰집니다.");

                    return;

                }



                // 3) inf 만 있으면 윈도우 기본 도구로 넣는다.

                string[] infs = Directory.GetFiles(vdd, "*.inf");

                if (infs.Length == 0)

                {

                    say("  드라이버를 알아보지 못했습니다. vdd 폴더 내용을 확인하세요.");

                    return;

                }



                // 같이 들어 있는 인증서가 있으면 먼저 넣는다 (자체 서명 드라이버용).

                // 정식 서명된 드라이버에는 없다.

                foreach (string cer in Directory.GetFiles(vdd, "*.cer"))

                {

                    RunIn(vdd, "certutil", "-addstore -f Root \"" + cer + "\"");

                    RunIn(vdd, "certutil", "-addstore -f TrustedPublisher \"" + cer + "\"");

                    say("  인증서를 넣었습니다: " + Path.GetFileName(cer));

                }



                // 설정 파일은 드라이버가 정해진 자리에서 읽는다.

                string xml = Path.Combine(vdd, "vdd_settings.xml");

                if (File.Exists(xml))

                {

                    try

                    {

                        Directory.CreateDirectory(Vdd.SettingsDir);

                        File.Copy(xml, Path.Combine(Vdd.SettingsDir, "vdd_settings.xml"), true);

                        say("  설정을 " + Vdd.SettingsDir + " 에 두었습니다 (1920x1080 우선).");

                    }

                    catch (Exception ex) { say("  설정을 두지 못했습니다: " + ex.Message); }

                }



                string outp = RunIn(vdd, "pnputil.exe", "/add-driver \"" + infs[0] + "\" /install");

                say("  " + Squash(outp));



                if (outp.IndexOf("서명", StringComparison.Ordinal) >= 0 ||

                    outp.IndexOf("signature", StringComparison.OrdinalIgnoreCase) >= 0)

                {

                    say("  서명 문제로 막혔습니다. 서명된 드라이버가 필요합니다.");

                    return;

                }



                // 드라이버만 등록해서는 화면이 안 생긴다. 꽂을 장치가 없기 때문이다.

                // 루트 장치 노드를 직접 만들어 붙인다.

                say("  가상 모니터 장치를 만듭니다…");



                string err = Vdd.Create(infs[0]);



                if (string.IsNullOrEmpty(err))

                    say("  가상 모니터가 생겼습니다. 잠시 뒤 1920x1080 으로 맞춰집니다.");

                else

                    say("  장치를 만들지 못했습니다: " + err);

            }

            catch (Exception ex) { say("  가상 모니터를 넣지 못했습니다: " + ex.Message); }

        }



        private static void RemoveVirtualDisplay(string dir, Say say)

        {

            try

            {

                string vdd = Path.Combine(dir, VddDirName);

                if (!Directory.Exists(vdd)) return;



                say("가상 모니터를 끄고 지웁니다…");



                foreach (string name in new string[] { "uninstall.bat", "uninstall.cmd",

                                                       "Installer.exe", "AllunoVDDInstaller.exe" })

                {

                    string p = Path.Combine(vdd, name);

                    if (!File.Exists(p)) continue;



                    RunIn(vdd, p, "uninstall");

                    return;

                }



                string di = Path.Combine(vdd, "deviceinstaller64.exe");

                if (!File.Exists(di)) di = Path.Combine(vdd, "deviceinstaller.exe");



                if (File.Exists(di))

                {

                    RunIn(vdd, di, "enableidd 0");

                    RunIn(vdd, di, "remove usbmmidd");

                    return;

                }



                // 장치부터 뗀다. 장치가 붙어 있으면 드라이버가 안 지워진다.

                int gone = Vdd.RemoveAll();

                if (gone > 0) say("  가상 모니터 " + gone + "개를 뗐습니다.");



                foreach (string inf in Directory.GetFiles(vdd, "*.inf"))

                    RunIn(vdd, "pnputil.exe", "/delete-driver \"" + Path.GetFileName(inf) + "\" /uninstall /force");



                try

                {

                    if (Directory.Exists(Vdd.SettingsDir)) Directory.Delete(Vdd.SettingsDir, true);

                }

                catch { }

            }

            catch { }

        }



        private static string RunIn(string workDir, string exe, string args)

        {

            try

            {

                ProcessStartInfo psi = new ProcessStartInfo

                {

                    FileName = exe,

                    Arguments = args,

                    WorkingDirectory = workDir,

                    UseShellExecute = false,

                    RedirectStandardOutput = true,

                    RedirectStandardError = true,

                    CreateNoWindow = true

                };



                using (Process p = Process.Start(psi))

                {

                    string o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();

                    p.WaitForExit(60000);

                    return o;

                }

            }

            catch (Exception ex) { return ex.Message; }

        }



        private static string Squash(string s)

        {

            if (string.IsNullOrEmpty(s)) return "(출력 없음)";



            s = s.Replace("\r", " ").Replace("\n", " ").Trim();

            while (s.Contains("  ")) s = s.Replace("  ", " ");

            return s.Length > 120 ? s.Substring(0, 120) + "…" : s;

        }





        internal static bool ServiceRunning()

        {

            try

            {

                using (System.ServiceProcess.ServiceController s =

                       new System.ServiceProcess.ServiceController(App.ServiceName))

                    return s.Status == System.ServiceProcess.ServiceControllerStatus.Running;

            }

            catch { return false; }

        }



        internal void Uninstall(string dir, Say say)

        {

            string exe = Path.Combine(dir, App.ExeName);

            say("웹 MCP 자동 실행을 끕니다 (인증서와 연결 설정은 보존)…");

            LanWebMcp.Disable(say);
            WebMcp.Disable(say);



            // 파일을 지우기 전에 드라이버부터 뗀다. 지운 뒤에는 설치기를 못 쓴다.

            RemoveVirtualDisplay(dir, say);



            say("잠금 화면 제어를 끕니다…");

            if (File.Exists(exe)) Sys.Run(exe, "--uninstall", 60000);

            Sys.Run("sc.exe", "delete " + App.ServiceName, 15000);



            say("원격제어를 멈춥니다…");

            Sys.StopApp();



            say("방화벽 규칙을 지웁니다…");

            Sys.Firewall(exe, 0, false);



            say("등록 정보를 지웁니다…");

            try

            {

                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(App.RegRun, true))

                    if (k != null) k.DeleteValue(App.RegRunValue, false);

            }

            catch { }



            try { Registry.LocalMachine.DeleteSubKeyTree(App.RegUninstall, false); } catch { }



            say("바로가기를 지웁니다…");

            try

            {

                string menu = Path.Combine(

                    Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), App.Product);

                if (Directory.Exists(menu)) Directory.Delete(menu, true);

            }

            catch { }



            try

            {

                string d = Path.Combine(

                    Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),

                    App.Product + ".lnk");

                if (File.Exists(d)) File.Delete(d);

            }

            catch { }



            say("남은 파일을 지웁니다…");



            // 터널 중계부터 지운다. **차례가 중요하다.**

            //   v1.5.0 부터 중계 프로그램은 ProgramData\RemoteControl\bin 에 있다.

            //   즉 설정 폴더 안이다. 중계가 아직 돌고 있으면 그 exe 가 잠겨 있어서

            //   설정 폴더를 통째로 지우려는 시도가 그 자리에서 실패하고, 설정·기록·

            //   장비 신원이 몽땅 남는다. (bin 이 %TEMP% 이던 v1.4.0 까지는 상관없었다)

            foreach (string d in Sys.OurTsDirs)

            {

                try { if (!string.IsNullOrEmpty(d) && Directory.Exists(d)) Directory.Delete(d, true); }

                catch { }

            }



            // 설정·기록·장비 신원이 여기 있다.

            try

            {

                string common = Path.Combine(

                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RemoteControl");

                if (Directory.Exists(common)) Directory.Delete(common, true);

            }

            catch { }



            SelfDelete(dir);



            say("");

            say("제거가 끝났습니다.");

            say("※ Tailscale 관리 콘솔에는 이 PC 가 오프라인 장비로 남습니다. 거기서 지우세요.");

        }



        /// <summary>지금 돌고 있는 제거 프로그램은 자기 폴더를 못 지운다. 나간 뒤에 지우게 시킨다.</summary>

        private static void SelfDelete(string dir)

        {

            try

            {

                string bat = Path.Combine(Path.GetTempPath(), "rc_cleanup.bat");



                // 제거 프로그램은 지우려는 그 폴더 안에서 돌고 있다.

                // 한 번만 시도하면 아직 안 끝난 사이에 실패해 폴더가 남는다.

                // 사라질 때까지 몇 번 다시 해 본다.

                File.WriteAllText(bat,

                    "@echo off\r\n" +

                    "for /l %%i in (1,1,20) do (\r\n" +

                    "  ping 127.0.0.1 -n 2 >nul\r\n" +

                    "  rd /s /q \"" + dir + "\" 2>nul\r\n" +

                    "  if not exist \"" + dir + "\" goto done\r\n" +

                    ")\r\n" +

                    ":done\r\n" +

                    "del \"%~f0\"\r\n", Encoding.Default);



                Process.Start(new ProcessStartInfo

                {

                    FileName = bat,

                    WindowStyle = ProcessWindowStyle.Hidden,

                    CreateNoWindow = true,

                    UseShellExecute = true

                });

            }

            catch { }

        }

    }



    // ------------------------------------------------------------------ 창



    internal sealed class SetupForm : InstallerForm
    {

        private TextBox txtDir;

        private CheckBox chkLock, chkRun, chkFw, chkDesk, chkVdd;
        private WebConnectionControls webConnection;
        private LanConnectionControls lanConnection;
        private Button btnGo, btnBrowse;

        private NumericUpDown numPort;
        private TextBox log;

        private ProgressBar bar;

        private bool installed;



        internal SetupForm()

        {

            Text = App.Product + " 설치";



            try

            {

                using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(App.IconName))

                    if (s != null) Icon = new Icon(s);

            }

            catch { }



            Build();

        }



        private void Build()

        {

            Row(Ui.Text("🖥️  " + App.Product, 15, FontStyle.Bold, Ui.Cyan, 24, 20, 0));
            Row(Ui.Text("버전 " + App.Version + " · 웹브라우저로 이 PC 를 보고 조작합니다",
                                 8.5f, FontStyle.Regular, Ui.Sub, 26, 52, 0));



            Row(Ui.Text("설치 위치", 9, FontStyle.Bold, Color.White, 24, 88, 0));


            txtDir = new TextBox

            {

                Text = App.DefaultDir,

                Location = new Point(24, 110),

                Size = new Size(410, 26),

                BackColor = Ui.Input,

                ForeColor = Color.White,

                BorderStyle = BorderStyle.FixedSingle,

                Font = Ui.F(9, FontStyle.Regular)

            };



            btnBrowse = Ui.Btn("찾아보기", Ui.Panel, 100, 26);

            btnBrowse.Location = new Point(440, 110);

            btnBrowse.Click += delegate

            {

                using (FolderBrowserDialog d = new FolderBrowserDialog())

                {

                    d.Description = "설치할 폴더를 고르세요";

                    d.SelectedPath = txtDir.Text;

                    if (d.ShowDialog(this) == DialogResult.OK)

                        txtDir.Text = Path.Combine(d.SelectedPath, "RemoteControl");

                }

            };

            Row(txtDir, btnBrowse);


            Label portLabel = Ui.Text("접속 포트", 9, FontStyle.Bold, Color.White, 24, 148, 0);


            numPort = new NumericUpDown

            {

                Minimum = 1,

                Maximum = 65535,

                Value = Installer.ReadPort(),

                Location = new Point(96, 145),

                Size = new Size(80, 24),

                BackColor = Ui.Input,

                ForeColor = Color.White,

                BorderStyle = BorderStyle.FixedSingle,

                Font = Ui.F(9, FontStyle.Regular)

            };



            Row(portLabel, numPort, Ui.Text("이 번호로 방화벽을 열어 둡니다. 나중에 프로그램에서 바꾸면 방화벽도 따라갑니다.",
                                 8, FontStyle.Regular, Ui.Sub, 186, 150, 354));


            Row(Ui.Text("설치 옵션", 9, FontStyle.Bold, Color.White, 24, 182, 0));


            chkLock = Ui.Check("잠금 화면 제어 켜기 (권장)", true, 24, 206, 500);

            Row(chkLock);
            Row(Ui.Text("     PC 가 잠겨 있어도 화면이 보이고, 원격에서 로그인까지 됩니다.\n" +
                                 "     끄면 잠긴 동안에는 화면이 멈추고 조작도 안 됩니다.",

                                 8, FontStyle.Regular, Ui.Sub, 26, 230, 500));



            chkRun = Ui.Check("윈도우에 로그인하면 트레이에 자동으로 띄우기", true, 24, 270, 500);

            Row(chkRun);


            chkFw = Ui.Check("방화벽에서 허용 (같은 공유기 안에서 접속)", true, 24, 298, 500);

            Row(chkFw);


            chkDesk = Ui.Check("바탕화면에 바로가기 만들기", true, 24, 326, 500);

            Row(chkDesk);


            // 모니터 없는 PC 는 윈도우가 640x480 밖에 못 낸다. 장치의 한계라 프로그램으로는

            // 못 늘린다. 가상 모니터를 만들어 줘야 FHD 가 열린다.

            // 모니터가 달린 PC 에 넣으면 안 쓰는 두 번째 화면이 생기므로 기본은 꺼 둔다.

            chkVdd = Ui.Check("가상 모니터 만들기 — 모니터 안 꽂은 PC 에만 체크", false, 24, 354, 500);

            chkVdd.Enabled = Installer.HasVirtualDisplay();

            Row(chkVdd);


            Row(Ui.Text(chkVdd.Enabled
                    ? "     모니터가 없으면 화면이 640x480 으로 잡힙니다. 이걸 켜면 1920x1080 이 됩니다."

                    : "     이 설치 파일에는 드라이버가 들어 있지 않습니다 (vdd 폴더를 넣고 다시 빌드).",

                8, FontStyle.Regular, chkVdd.Enabled ? Ui.Sub : Ui.Warn, 26, 378, 500));



            webConnection = new WebConnectionControls(Row, true);
            lanConnection = new LanConnectionControls(Row, true);



            Label warn = Ui.Text("⚠  이 프로그램은 이 PC 를 밖에서 조작할 수 있게 합니다.\n" +

                                 "    설치 후 반드시 긴 비밀번호를 정하세요.",

                                 8.5f, FontStyle.Regular, Ui.Warn, 26, 510, 500);

            Row(warn);


            bar = new ProgressBar

            {

                Location = new Point(24, 558),

                Size = new Size(516, 6),

                Style = ProgressBarStyle.Continuous,

                Visible = false

            };

            Row(bar);


            log = new TextBox

            {

                Location = new Point(24, 570),

                Size = new Size(516, 84),

                Multiline = true,

                ReadOnly = true,

                ScrollBars = ScrollBars.Vertical,

                BackColor = Color.FromArgb(11, 15, 24),

                ForeColor = Ui.Sub,

                BorderStyle = BorderStyle.FixedSingle,

                Font = new Font("Consolas", 8.5f)

            };

            Row(log);


            btnGo = Ui.Btn("설치", Ui.Accent, 130, 34);

            btnGo.Location = new Point(410, 660);

            btnGo.Click += delegate { Go(); };



            Button cancel = Ui.Btn("닫기", Ui.Panel, 90, 34);

            cancel.Location = new Point(310, 660);

            cancel.Click += delegate { Close(); };

            FinishLayout(580, 740, btnGo, cancel);
        }



        private void Say(string s)

        {

            if (InvokeRequired) { BeginInvoke((MethodInvoker)delegate { Say(s); }); return; }



            log.AppendText(s + "\r\n");

            log.SelectionStart = log.TextLength;

            log.ScrollToCaret();

        }



        internal Options ReadOptions()
        {
            Options o = new Options
            {

                Dir = txtDir.Text.Trim(),

                LockScreen = chkLock.Checked,

                AutoRun = chkRun.Checked,

                Firewall = chkFw.Checked,

                Desktop = chkDesk.Checked,

                Port = (int)numPort.Value,

                VirtualDisplay = chkVdd.Checked
            };
            if (string.IsNullOrEmpty(o.Dir)) throw new ArgumentException("설치 위치를 정해 주세요.");
            webConnection.Apply(o);
            lanConnection.Apply(o);
            WebMcp.Validate(o);
            LanWebMcp.Validate(o);
            return o;
        }
        private void Go()
        {
            if (installed) { Sys.RunAsUser(Path.Combine(txtDir.Text.Trim(), App.ExeName), txtDir.Text.Trim()); Close(); return; }
            Options o;
            try { o = ReadOptions(); } catch (Exception ex) { MessageBox.Show(this, ex.Message); return; }


            btnGo.Enabled = false;
            btnBrowse.Enabled = false;
            webConnection.SetBusy(true);lanConnection.SetBusy(true);
            bar.Visible = true;

            bar.Style = ProgressBarStyle.Marquee;
            ShowProgress(log);


            Thread t = new Thread(delegate ()

            {

                bool ok = true;

                try { new Installer().Install(o, Say); }

                catch (Exception ex) { ok = false; Say("실패: " + ex.Message); }



                try

                {

                    BeginInvoke((MethodInvoker)delegate

                    {

                        bar.Style = ProgressBarStyle.Continuous;

                        bar.Value = 100;

                        btnGo.Enabled = true;
                        btnBrowse.Enabled = true;
                        webConnection.SetBusy(false);lanConnection.SetBusy(false);


                        if (!ok) return;



                        btnGo.Text = "실행";

                        installed = true;

                        txtDir.ReadOnly = true;

                        btnBrowse.Enabled = false;

                    });

                }

                catch { }

            });

            t.IsBackground = true;

            t.Start();

        }

    }



    internal sealed class UninstallForm : InstallerForm
    {

        private TextBox log;

        private Button btnGo;

        private readonly string dir;



        internal UninstallForm()

        {

            dir = ReadDir();



            Text = App.Product + " 제거";



            Row(Ui.Text(App.Product + " 를 지웁니다", 13, FontStyle.Bold, Color.White, 24, 22, 0));
            Row(Ui.Text("잠금 화면 제어 서비스, 방화벽 규칙, 바로가기, 설정까지 모두 지웁니다.",
                                 8.5f, FontStyle.Regular, Ui.Sub, 26, 52, 420));

            Row(Ui.Text(dir, 8.5f, FontStyle.Regular, Ui.Sub, 26, 76, 420));


            log = new TextBox

            {

                Location = new Point(24, 106),

                Size = new Size(432, 132),

                Multiline = true,

                ReadOnly = true,

                ScrollBars = ScrollBars.Vertical,

                BackColor = Color.FromArgb(11, 15, 24),

                ForeColor = Ui.Sub,

                BorderStyle = BorderStyle.FixedSingle,

                Font = new Font("Consolas", 8.5f)

            };

            Row(log);


            btnGo = Ui.Btn("제거", Color.FromArgb(190, 60, 60), 110, 32);

            btnGo.Location = new Point(346, 250);

            btnGo.Click += delegate { Go(); };



            Button c = Ui.Btn("닫기", Ui.Panel, 90, 32);

            c.Location = new Point(246, 250);

            c.Click += delegate { Close(); };

            FinishLayout(480, 340, btnGo, c);
        }



        private static string ReadDir()

        {

            try

            {

                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(App.RegUninstall))

                {

                    if (k != null)

                    {

                        string v = k.GetValue("InstallLocation") as string;

                        if (!string.IsNullOrEmpty(v)) return v;

                    }

                }

            }

            catch { }



            return Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

        }



        private void Say(string s)

        {

            if (InvokeRequired) { BeginInvoke((MethodInvoker)delegate { Say(s); }); return; }

            log.AppendText(s + "\r\n");

            log.SelectionStart = log.TextLength;

            log.ScrollToCaret();

        }



        private void Go()

        {

            btnGo.Enabled = false;



            Thread t = new Thread(delegate ()

            {

                try { new Installer().Uninstall(dir, Say); }

                catch (Exception ex) { Say("실패: " + ex.Message); }



                try { BeginInvoke((MethodInvoker)delegate { btnGo.Text = "완료"; btnGo.Enabled = true; btnGo.Click += delegate { Close(); }; }); }

                catch { }

            });

            t.IsBackground = true;

            t.Start();

        }

    }



    internal static class Program

    {

        [STAThread]

        internal static int Main()

        {

            // 서비스를 등록하고 Program Files 에 쓰려면 관리자 권한이 필요하다.

            if (!Sys.IsAdmin())

            {

                if (Sys.Relaunch()) return 0;



                MessageBox.Show(

                    "관리자 권한이 필요합니다.\r\n\r\n" +

                    "잠금 화면 제어는 윈도우 서비스로 등록해야 하고,\r\n" +

                    "프로그램은 함부로 바꿀 수 없는 곳에 설치해야 안전합니다.\r\n\r\n" +

                    "설치 파일을 오른쪽 클릭 → [관리자 권한으로 실행] 해 주세요.",

                    App.Product, MessageBoxButtons.OK, MessageBoxIcon.Warning);

                return 1;

            }



            Application.EnableVisualStyles();

            Application.SetCompatibleTextRenderingDefault(false);

            if (Sys.HasArg("/lan-web-settings")) {Application.Run(new LanSettingsForm());return 0;}
            if (Sys.HasArg("/web-settings"))
            {
                Application.Run(new WebSettingsForm());
                return 0;
            }

            if (Sys.HasArg("/uninstall") || Sys.HasArg("-uninstall"))

            {

                Application.Run(new UninstallForm());

                return 0;

            }



            if (Sys.HasArg("/S") || Sys.HasArg("/s"))

            {

                try

                {

                    new Installer().Install(new Options(), delegate { });



                    Sys.RunAsUser(Path.Combine(App.DefaultDir, App.ExeName), App.DefaultDir);

                    return 0;

                }

                catch { return 1; }

            }



            Application.Run(new SetupForm());

            return 0;

        }

    }

}

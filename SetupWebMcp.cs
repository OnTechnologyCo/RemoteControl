using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace RemoteControlSetup
{
    internal static class WebMcp
    {
        internal const string PluginName = "RemoteControl_WebPlugin.zip";
        internal const int DefaultPort = 443;
        internal const string DefaultUrl = "https://najhna.dyndns.org";
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

        internal static string ReadSetting(string key, string fallback)
        {
            try { foreach (string line in File.ReadAllLines(Installer.SettingsPath, Encoding.UTF8))
                if (line.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase)) return line.Substring(key.Length + 1).Trim(); }
            catch { }
            return fallback;
        }
        internal static int LanPort()
        {
            int port; return int.TryParse(ReadSetting("mcp_port", "5901"), out port) && port > 0 && port <= 65535 ? port : 5901;
        }
        internal static string NormalizeUrl(string text)
        {
            Uri uri;
            if (!Uri.TryCreate(text, UriKind.Absolute, out uri) || uri.Scheme != "https" || uri.HostNameType != UriHostNameType.Dns ||
                (!uri.IsDefaultPort && uri.Port < 1024) || uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.UserInfo.Length > 0)
                throw new ArgumentException("웹 연결 주소는 경로 없는 https://DDNS주소 형식으로 입력하세요. 기본 HTTPS 포트는 443입니다.");
            return uri.GetLeftPart(UriPartial.Authority);
        }
        internal static int PreferredPort()
        {
            string key=(ReadSetting("mcp_web_mode","")=="tailscale"||ReadSetting("mcp_web_mode","")=="private")?"mcp_ddns_url":"mcp_web_url";
            Uri uri; return Uri.TryCreate(ReadSetting(key, DefaultUrl), UriKind.Absolute, out uri) && IsHttpsPort(uri.Port) ? uri.Port : DefaultPort;
        }
        internal static bool IsHttpsPort(int port) { return port==443 || (port>=1024 && port<=65535); }
        internal static string WithPort(string text, int port)
        {
            if (!IsHttpsPort(port)) throw new ArgumentException("기본 HTTPS 포트 443 또는 전용 포트 1024~65535를 지정하세요.");
            return new UriBuilder(NormalizeUrl(text)) {Port=port}.Uri.GetLeftPart(UriPartial.Authority);
        }
        internal static void Validate(Options o)
        {
            if (!o.WebMcp) return;
            if (!o.LockScreen) throw new ArgumentException("웹 연결 자동 실행에는 잠금 화면 제어 서비스가 필요합니다.");
            if (ReadSetting("pwhash", "").Length == 0) throw new ArgumentException("먼저 웹 연결을 선택하지 않고 설치하세요. 원격제어에서 ID와 비밀번호를 설정한 뒤 [LLM / MCP 설정] → [웹 ChatGPT / Claude 설정]에서 연결할 수 있습니다.");
            if(o.WebTailscale || o.WebPrivate) {
                if(LanPort()<1024)throw new ArgumentException("웹 MCP에 사용할 LAN MCP 포트는 1024 이상으로 지정하세요.");
                if(!o.WebPrivate && o.WebPublicPort!=443 && o.WebPublicPort!=8443 && o.WebPublicPort!=10000)throw new ArgumentException("Tailscale MCP 공개 포트는 기본 443 또는 8443, 10000을 선택하세요.");
                if(o.WebGatewayPort<1024||o.WebGatewayPort>65535||o.WebGatewayPort==LanPort()||o.WebGatewayPort==o.Port||o.Port==LanPort())throw new ArgumentException("로컬 중계 포트는 화면 및 LAN MCP 포트와 다르게 지정하세요.");
                return;
            }
            o.WebUrl = NormalizeUrl(o.WebUrl);
            int port = new Uri(o.WebUrl).Port;
            if (!IsHttpsPort(port)) throw new ArgumentException("기본 HTTPS 포트 443 또는 전용 포트 1024~65535를 지정하세요.");
            if (o.Port == LanPort() || o.Port == port || LanPort() == port || port == 59332 || o.Port == 59332 || LanPort() == 59332)
                throw new ArgumentException("화면, LAN MCP, HTTPS, 내부 중계(59332) 포트는 각각 달라야 합니다.");
        }
        internal static void ExtractPackages(string dir, string publicUrl)
        {
            for (string current = Path.GetFullPath(dir); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
                if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("설치 경로에 연결 폴더가 있습니다.");
            string plugins = Path.Combine(dir, "plugins"); Directory.CreateDirectory(plugins);
            Sys.Extract("RemoteControl_MCP.zip", Path.Combine(plugins, "RemoteControl_MCP.zip"));
            Sys.Extract("RemoteControl_Claude.mcpb", Path.Combine(plugins, "RemoteControl_Claude.mcpb"));
            // 공개 서버 주소는 설치할 PC마다 다를 수 있어 플러그인 연결 정보를 설치 시 생성한다.
            if (!string.IsNullOrWhiteSpace(publicUrl)) WritePlugin(Path.Combine(plugins, PluginName), publicUrl);
            File.WriteAllText(Path.Combine(plugins, "웹-ChatGPT-업로드-안내.txt"),
                string.IsNullOrWhiteSpace(publicUrl)
                ? "웹 연결 설정을 건너뛰었습니다. 기존 웹 연결과 플러그인이 있으면 유지됩니다.\r\n" +
                  "원격제어 → LLM / MCP 설정 → 웹 ChatGPT / Claude 설정에서 주소·포트·인증서를 지정하세요.\r\n" +
                  "설정을 적용하면 이 폴더에 " + PluginName + "이 생성됩니다.\r\n"
                : "웹 ChatGPT 업로드 파일: " + PluginName + "\r\n" +
                "MCP 주소: " + NormalizeUrl(publicUrl) + "/mcp\r\n" +
                "공유기 TCP " + new Uri(publicUrl).Port + "을 이 PC의 같은 포트로 연결하세요.\r\n" +
                "ChatGPT 연결 화면에서 기존 원격제어 ID/비밀번호로 로그인합니다.\r\n" +
                "Claude는 사용자 지정 커넥터에 같은 MCP 주소를 입력합니다. OAuth client ID/secret은 비워 둡니다.\r\n" +
                "RemoteControl_MCP.zip은 LAN/로컬 클라이언트용입니다.\r\n" +
                "ZIP 생성은 공개 HTTPS 개통이나 ChatGPT 연결 성공을 의미하지 않습니다.\r\n", Utf8);
            string root = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("RemoteControl_WebMCP.zip"))
            {
                if (stream == null) throw new IOException("웹 MCP 설치 리소스가 없습니다.");
                using (ZipArchive zip = new ZipArchive(stream, ZipArchiveMode.Read))
                    foreach (ZipArchiveEntry entry in zip.Entries)
                    {
                        if (!entry.FullName.StartsWith("web-mcp/", StringComparison.Ordinal) || entry.FullName.Contains("\\")) throw new IOException("잘못된 웹 MCP 패키지 경로입니다.");
                        string target = Path.GetFullPath(Path.Combine(root, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
                        if (!target.StartsWith(root + "web-mcp" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("패키지가 설치 폴더를 벗어납니다.");
                        string parent = Path.GetDirectoryName(target);
                        for (string current = parent; current != null && current.StartsWith(root, StringComparison.OrdinalIgnoreCase); current = Path.GetDirectoryName(current))
                            if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("설치 폴더의 연결 폴더는 따라가지 않습니다.");
                        Directory.CreateDirectory(parent);
                        if (File.Exists(target) && (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0) throw new IOException("설치 파일의 연결 경로는 따라가지 않습니다.");
                        using (Stream input = entry.Open()) using (FileStream output = File.Create(target)) input.CopyTo(output);
                    }
            }
        }
        internal static void WritePlugin(string destination, string publicUrl)
        {
            string origin = NormalizeUrl(publicUrl);
            var json = new JavaScriptSerializer();
            var manifest = new Dictionary<string, object> { {"$schema", "https://agent-plugins.org/schemas/1.0.0/plugin.schema.json"},
                {"name", "remotecontrol-web"}, {"version", App.Version}, {"description", "원격제어 PC에 HTTPS와 기존 원격제어 로그인으로 연결합니다."}, {"author", new {name="ontech"}} };
            var mcp = new Dictionary<string, object> { {"$schema", "https://agent-plugins.org/schemas/1.0.0/mcp.schema.json"},
                {"mcpServers", new Dictionary<string, object> { {"remotecontrol-web", new {type="streamable-http",url=origin + "/mcp"}} }} };
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination)));
            using (FileStream output = File.Create(destination)) using (ZipArchive zip = new ZipArchive(output, ZipArchiveMode.Create))
            {
                Entry(zip, "plugin.json", json.Serialize(manifest));
                Entry(zip, "mcp.json", json.Serialize(mcp));
                Entry(zip, "skills/remotecontrol-web/SKILL.md", "---\nname: remotecontrol-web\ndescription: Inspect and manage the connected RemoteControl PC over HTTPS MCP.\n---\nCall pc_get_status first and verify the target hostname before changes. File paths are on that PC. Commands run with the remote agent Windows rights. Keep output concise. Never retry an execution after an uncertain transport failure. Reconnect through OAuth when authentication expires. Use pc_open_file_transfer for the authenticated browser file/folder picker and streaming downloads. Direct MCP binary transfers use pc_begin_upload, pc_write_upload_chunk, pc_finish_upload, pc_prepare_download and pc_read_download_chunk with SHA-256. Local and external clients use the same HTTPS /mcp address; no local connector installation is required.\n");
                Entry(zip, "README.md", "# RemoteControl Web\n\nMCP URL: " + origin + "/mcp\n\nSetup에서 웹 연결을 켜고 TCP " + new Uri(origin).Port + "과 공개 HTTPS 인증서가 정상인지 확인하세요. 연결 화면에서 원격제어 ID/비밀번호로 로그인합니다. 이 ZIP을 생성한 것만으로 외부 개통이 확인된 것은 아닙니다.\n");
            }
        }
        private static void Entry(ZipArchive zip, string name, string text)
        {
            using (Stream output = zip.CreateEntry("remotecontrol-web/" + name, CompressionLevel.Optimal).Open())
            { byte[] bytes = Utf8.GetBytes(text); output.Write(bytes, 0, bytes.Length); }
        }
        internal static void Activate(Options o, Installer.Say say)
        {
            if(o.WebPrivate){
                RunScript(Path.Combine(o.Dir,"web-mcp","Install-PrivateMcp.ps1"),PrivateArguments(o),say);
                SaveWebMode(o);
                say("프라이빗 터널의 발급 HTTPS 주소에 /mcp를 자동 연결합니다. 외부망 연결 설정에서 프라이빗 터널을 켜세요.");
                say("재연결로 주소가 바뀌면 새 MCP 주소를 앱에 등록하고 로그인하세요.");
                return;
            }
            if(o.WebTailscale){
                RunScript(Path.Combine(o.Dir,"web-mcp","Install-TailscaleMcp.ps1"),TailscaleArguments(o),say);
                SaveWebMode(o);
                say("Tailscale 주소가 생성되면 MCP 주소에 자동 반영합니다. 공개 포트: "+o.WebPublicPort);
                say("MCP 접속 확인 후 기존 텔레그램 알림으로 주소를 보냅니다. 아직 Tailscale을 켜지 않았다면 [외부망 연결 설정]에서 선택하세요.");
                return;
            }
            say("웹 ChatGPT / Claude HTTPS와 자동 실행을 설치합니다…");
            RunScript(Path.Combine(o.Dir, "web-mcp", "Install-WebMcp.ps1"),
                Arguments(o), say);
            SaveWebMode(o);
            say("웹 업로드 파일: " + Path.Combine(o.Dir, "plugins", PluginName));
            say("연결 주소: " + NormalizeUrl(o.WebUrl) + "/mcp · 외부 TCP " + new Uri(o.WebUrl).Port + " 연결을 확인하세요.");
        }
        internal static void Preflight(Options o, Installer.Say say)
        {
            if (o.WebMcp) RunScript(Path.Combine(o.Dir,"web-mcp",o.WebPrivate?"Install-PrivateMcp.ps1":o.WebTailscale?"Install-TailscaleMcp.ps1":"Install-WebMcp.ps1"), (o.WebPrivate?PrivateArguments(o):o.WebTailscale?TailscaleArguments(o):Arguments(o)) + " -CheckOnly", say);
        }
        internal static void ConfigureExisting(Options o, Installer.Say say)
        {
            Validate(o);
            if (!o.WebMcp) throw new ArgumentException("웹 연결 설정을 선택하세요.");
            if (!Installer.ServiceRunning()) throw new ArgumentException("원격제어에서 잠금 화면 제어 서비스를 먼저 켜세요.");
            if (!o.WebTailscale && !o.WebPrivate && ReadSetting("mcp_enabled", "0") != "1") throw new ArgumentException("[LLM / MCP 설정]에서 LAN MCP 사용을 켜고 저장한 뒤 다시 적용하세요.");
            Preflight(o, say);
            Activate(o, say);
            if(o.WebTailscale || o.WebPrivate)return;
            WritePlugin(Path.Combine(o.Dir, "plugins", PluginName), o.WebUrl);
            SaveConfiguredUrl(Installer.SettingsPath, o.WebUrl);
            say("웹 연결 설정을 저장했습니다. 플러그인은 설치 폴더의 plugins에 있습니다.");
        }
        internal static void SaveConfiguredUrl(string path, string publicUrl)
        {
            string origin = NormalizeUrl(publicUrl);
            SaveSettings(path,new Dictionary<string,string>{{"mcp_web_url",origin},{"mcp_ddns_url",origin},{"mcp_web_mode","ddns"}});
        }
        private static void SaveWebMode(Options o)
        {
            var changes=new Dictionary<string,string>{{"mcp_web_mode",o.WebPrivate?"private":o.WebTailscale?"tailscale":"ddns"}};
            if(o.WebTailscale || o.WebPrivate){
                changes["mcp_enabled"]="1";
                if(o.WebPrivate){changes["mcp_private_gateway"]=o.WebGatewayPort.ToString();changes["mcp_private_revision"]=Guid.NewGuid().ToString("N");}
                else{changes["mcp_ts_port"]=o.WebPublicPort.ToString();changes["mcp_ts_gateway"]=o.WebGatewayPort.ToString();changes["mcp_ts_revision"]=Guid.NewGuid().ToString("N");}
                string prior=ReadSetting("mcp_web_url","");Uri u;
                changes["mcp_web_url"]="";
                if(ReadSetting("mcp_web_mode","")!="tailscale"&&ReadSetting("mcp_web_mode","")!="private"&&Uri.TryCreate(prior,UriKind.Absolute,out u))changes["mcp_ddns_url"]=prior;
            } else {changes["mcp_ddns_url"]=NormalizeUrl(o.WebUrl);changes["mcp_web_url"]=NormalizeUrl(o.WebUrl);}
            SaveSettings(Installer.SettingsPath,changes);
        }
        private static string PrivateArguments(Options o){return " -GatewayPort "+o.WebGatewayPort+" -LanPort "+LanPort();}
        internal static string ReadPrivateUrl()
        {
            try{
                string path=Path.Combine(Path.GetDirectoryName(Installer.SettingsPath),"status.txt");
                if(DateTime.UtcNow-File.GetLastWriteTimeUtc(path)>TimeSpan.FromSeconds(10)||ReadSetting("tunnel","")!="private")return "";
                foreach(string line in File.ReadAllLines(path,Encoding.UTF8))if(line.StartsWith("url=")){
                    string value=line.Substring(4);Uri u;
                    if(System.Text.RegularExpressions.Regex.IsMatch(value,@"\Ahttps://[a-zA-Z0-9.-]+/?\z")&&Uri.TryCreate(value,UriKind.Absolute,out u)&&u.HostNameType==UriHostNameType.Dns)return u.GetLeftPart(UriPartial.Authority);
                }
            }catch{}return "";
        }
        private static string TailscaleArguments(Options o){return " -PublicPort "+o.WebPublicPort+" -GatewayPort "+o.WebGatewayPort+" -LanPort "+LanPort();}
        internal static string ReadTailscaleUrl()
        {
            try{
                string path=Path.Combine(Path.GetDirectoryName(Installer.SettingsPath),"status.txt");
                if(DateTime.UtcNow-File.GetLastWriteTimeUtc(path)>TimeSpan.FromSeconds(10))return "";
                foreach(string line in File.ReadAllLines(path,Encoding.UTF8))if(line.StartsWith("url=")){
                    Uri u;if(Uri.TryCreate(line.Substring(4),UriKind.Absolute,out u)&&u.Scheme=="https"&&u.Host.EndsWith(".ts.net",StringComparison.OrdinalIgnoreCase))return new UriBuilder(u){Port=-1,Path="/",Query="",Fragment=""}.Uri.GetLeftPart(UriPartial.Authority);
                }
            }catch{}return "";
        }
        internal static void SaveSettings(string path, Dictionary<string,string> changes)
        {
            // 본체 Config.SetMany와 같은 잠금으로 최신 설정에 URL 한 항목만 합친다.
            FileStream lease = null; DateTime until = DateTime.UtcNow.AddSeconds(3);
            while (lease == null) {
                try { lease = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                catch (IOException) { if (DateTime.UtcNow >= until) throw; Thread.Sleep(30); }
            }
            using (lease) {
                var lines = new List<string>();
                foreach (string line in File.ReadAllLines(path, Encoding.UTF8)){
                    int eq=line.IndexOf('=');bool replaced=false;
                    if(eq>0)foreach(string key in changes.Keys)if(line.Substring(0,eq).Trim().Equals(key,StringComparison.OrdinalIgnoreCase)){replaced=true;break;}
                    if(!replaced)lines.Add(line);
                }
                foreach(var change in changes)lines.Add(change.Key+"="+change.Value);
                string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try { File.WriteAllLines(temp, lines.ToArray(), Utf8); File.Replace(temp, path, null); }
                finally { if (File.Exists(temp)) File.Delete(temp); }
            }
        }
        private static string Arguments(Options o)
        {
            string args = " -PublicUrl \"" + NormalizeUrl(o.WebUrl) + "\" -LanPort " + LanPort();
            if (!string.IsNullOrEmpty(o.CertificatePath)) args += " -CertificatePath \"" + o.CertificatePath + "\" -PrivateKeyPath \"" + o.PrivateKeyPath + "\"";
            return args;
        }
        internal static void Disable(Installer.Say say)
        {
            string privateScript=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"RemoteControlPrivateMcp","Disable-PrivateMcp.ps1");
            if(File.Exists(privateScript))RunScript(privateScript,"",say);
            string tailscale=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"RemoteControlTailscaleMcp","Disable-TailscaleMcp.ps1");
            if(File.Exists(tailscale))RunScript(tailscale,"",say);
            string script = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "RemoteControlWebMcp", "Disable-WebMcp.ps1");
            if (File.Exists(script)) RunScript(script, "", say);
        }
        internal static void RunScript(string script, string arguments, Installer.Say say)
        {
            var psi = new ProcessStartInfo { FileName = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
                Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + script + "\"" + arguments,
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using (Process process = new Process {StartInfo=psi})
            {
                StringBuilder errors = new StringBuilder();
                process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e) { if(e.Data != null) say(e.Data); };
                process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e) { if(e.Data != null) lock(errors) errors.AppendLine(e.Data); };
                process.Start(); process.BeginOutputReadLine(); process.BeginErrorReadLine();
                if(!process.WaitForExit(180000)) throw new IOException("웹 연결 설치가 아직 실행 중입니다. 완료 여부를 확인하기 전에는 설치를 다시 실행하지 마세요.");
                process.WaitForExit();
                if(process.ExitCode != 0) throw new IOException("웹 연결 설치 실패: " + errors.ToString());
            }
        }
    }
}

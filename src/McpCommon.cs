// 오프라인 대상 PC는 Remote의 ID/PW만 사용한다. MCP 설정에 인터넷 계정/토큰은 없다.
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Web.Script.Serialization;

namespace RemoteControl
{
    internal static class McpJson
    {
        internal static string Write(object value) { return new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 }.Serialize(value); }
        internal static Dictionary<string, object> Read(string value)
        {
            return new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024, RecursionLimit = 32 }.Deserialize<Dictionary<string, object>>(value);
        }
        internal static Dictionary<string, object> Obj(Dictionary<string, object> value, string key)
        { object v; return value != null && value.TryGetValue(key, out v) && v is Dictionary<string, object> ? (Dictionary<string, object>)v : new Dictionary<string, object>(); }
        internal static object Id(Dictionary<string, object> value) { object id; return value != null && value.TryGetValue("id", out id) ? id : null; }
        internal static string Str(Dictionary<string, object> value, string key, string fallback)
        { string text = Json.Str(value, key); return string.IsNullOrEmpty(text) ? fallback : text; }
        internal static object Result(object id, object result) { return new { jsonrpc = "2.0", id = id, result = result }; }
        internal static object Error(object id, int code, string message) { return new { jsonrpc = "2.0", id = id, error = new { code = code, message = message } }; }
        internal static object Text(object result) { return new { content = new[] { new { type = "text", text = Write(result) } } }; }
        internal static object ToolError(string message) { return new { isError = true, content = new[] { new { type = "text", text = message } } }; }
    }

    internal sealed class McpOptions
    {
        internal bool Enabled, AllowCommands = true, AllowScreen = true;
        internal int Port = 5901;
        internal string[] Roots = new string[0];
        internal string AllowedNetworks = "10.0.0.0/8,172.16.0.0/12,192.168.0.0/16";
        internal string CredentialStamp = "";
        // 배포한 사용자 프로그램은 RemoteControl 제거 시 지우는 설정 폴더 밖에 둔다.
        internal static string DefaultRoot { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments), "RemoteControlDeploy"); } }
        internal static McpOptions Load()
        {
            var o = new McpOptions(); o.Enabled = Config.Get("mcp_enabled") == "1";
            o.CredentialStamp = Config.User + "|" + Config.Get("pwhash");
            int port; if (int.TryParse(Config.Get("mcp_port"), out port)) o.Port = port;
            o.AllowCommands = Config.Get("mcp_commands") != "0"; o.AllowScreen = Config.Get("mcp_screen") != "0";
            string roots = Config.Get("mcp_roots"); o.Roots = (string.IsNullOrEmpty(roots) ? DefaultRoot : roots).Split('|');
            string networks = Config.Get("mcp_networks"); if (!string.IsNullOrEmpty(networks)) o.AllowedNetworks = networks;
            o.Validate(); return o;
        }
        internal void Validate()
        {
            if (Port < 1 || Port > 65535) throw new ArgumentException("MCP 포트는 1~65535여야 합니다.");
            if (Roots.Length < 1 || Roots.Length > 32) throw new ArgumentException("파일 작업 폴더를 1~32개 지정하세요.");
            for (int i = 0; i < Roots.Length; i++) Roots[i] = McpPath.Absolute(Roots[i]);
            foreach (string network in AllowedNetworks.Split(',')) Network(network, IPAddress.Loopback, true);
        }
        internal bool Accepts(IPAddress ip)
        {
            if (IPAddress.IsLoopback(ip)) return true;
            foreach (string network in AllowedNetworks.Split(',')) if (Network(network, ip, false)) return true;
            return false;
        }
        private static bool Network(string text, IPAddress ip, bool validate)
        {
            string[] parts = text.Trim().Split('/'); IPAddress address; int bits = 32;
            if (parts.Length > 2 || !IPAddress.TryParse(parts[0], out address) || address.GetAddressBytes().Length != 4 ||
                (parts.Length == 2 && (!int.TryParse(parts[1], out bits) || bits < 0 || bits > 32)))
                throw new ArgumentException("허용 IP/대역은 IPv4 주소 또는 CIDR을 쉼표로 구분하세요.");
            if (validate) return true;
            byte[] a = address.GetAddressBytes(), b = ip.GetAddressBytes(); if (b.Length != 4) return false;
            for (int n = 0; n < 4; n++) { int used = Math.Max(0, Math.Min(8, bits - n * 8)); int mask = 255 << (8 - used); if ((a[n] & mask) != (b[n] & mask)) return false; }
            return true;
        }
    }

    internal static class McpPath
    {
        internal static string Absolute(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("경로가 비어 있습니다.");
            path = path.Replace('/', '\\');
            if (path.Length < 3 || !char.IsLetter(path[0]) || path[1] != ':' || path[2] != '\\' || path.IndexOf(':', 2) >= 0)
                throw new ArgumentException("드라이브로 시작하는 로컬 절대 경로를 지정하세요.");
            string full = Path.GetFullPath(path);
            foreach (string part in full.Substring(3).Split('\\'))
            {
                if (part.Length == 0) continue;
                string stem = part.Split('.')[0].ToUpperInvariant();
                if (part.EndsWith(".") || part.EndsWith(" ") || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                    stem == "CON" || stem == "PRN" || stem == "AUX" || stem == "NUL" ||
                    System.Text.RegularExpressions.Regex.IsMatch(stem, "^(COM|LPT)[1-9¹²³]$"))
                    throw new ArgumentException("사용할 수 없는 경로 이름입니다.");
            }
            return full.Length > 3 ? full.TrimEnd('\\') : full;
        }
        internal static string Check(string path, McpOptions options)
        {
            string full = Absolute(path); bool allowed = false;
            foreach (string root in options.Roots)
                if (full.Equals(root, StringComparison.OrdinalIgnoreCase) || full.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) { allowed = true; break; }
            if (!allowed) throw new UnauthorizedAccessException("MCP 설정에서 허용한 작업 폴더 밖입니다.");
            string current = full;
            while (!string.IsNullOrEmpty(current))
            {
                try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new UnauthorizedAccessException("연결 폴더/junction/symlink는 파일 도구로 따라가지 않습니다."); }
                catch (FileNotFoundException) { } catch (DirectoryNotFoundException) { }
                current = Path.GetDirectoryName(current);
            }
            return full;
        }
    }
}

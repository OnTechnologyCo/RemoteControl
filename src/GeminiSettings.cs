using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

namespace RemoteControl
{
    internal static class GeminiSettings
    {
        internal const string Key = "mcp_gemini_readonly_hints";
        internal const string MetadataKey = "remotecontrol/internalGeminiReadOnlyHints";
        internal const int MaxBytes = 16 * 1024;

        internal static Dictionary<string, object>[] Catalog()
        {
            var tools = new List<Dictionary<string, object>>();
            foreach (object tool in McpTools.List()) tools.Add(McpJson.Read(McpJson.Write(tool)));
            // The gateway and the offline administrator UI use the same web tool definition.
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("web-tools.json"))
            {
                if (stream == null) throw new IOException("Web MCP 도구 리소스가 없습니다. 본체를 다시 설치하세요.");
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                    tools.Add(McpJson.Read(reader.ReadToEnd()));
            }
            return tools.ToArray();
        }

        internal static Dictionary<string, bool> Parse(string json, bool restoring)
        {
            var result = new Dictionary<string, bool>(StringComparer.Ordinal);
            if (restoring && string.IsNullOrEmpty(json)) return result;
            if (json == null || Encoding.UTF8.GetByteCount(json) > MaxBytes)
                throw new ArgumentException("readOnlyHint 설정은 UTF-8 16KiB 이하여야 합니다.");
            Dictionary<string, object> input;
            try { input = McpJson.Read(json); }
            catch { throw new ArgumentException("readOnlyHint 설정은 JSON 객체여야 합니다."); }
            if (input == null) throw new ArgumentException("readOnlyHint 설정은 JSON 객체여야 합니다.");
            if (input.Count == 0) return result;
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var tool in Catalog()) names.Add((string)tool["name"]);
            foreach (var pair in input)
            {
                if (!(pair.Value is bool)) throw new ArgumentException("readOnlyHint 값은 JSON true 또는 false여야 합니다.");
                if (!names.Contains(pair.Key))
                {
                    // Retired tools must not prevent opening the UI and restoring defaults.
                    if (restoring) continue;
                    throw new ArgumentException("현재 도구 목록에 없는 이름입니다: " + pair.Key);
                }
                result.Add(pair.Key, (bool)pair.Value);
            }
            return result;
        }

        internal static Dictionary<string, bool> Load()
        {
            Config.Reload();
            return Parse(Config.Get(Key), true);
        }

        internal static void Save(string json)
        {
            var values = Parse(json, false);
            // Config commits atomically under its policy lock. No mutable runtime map is
            // published first: the backend reads only the successfully committed snapshot.
            Config.Set(Key, McpJson.Write(values));
        }
    }
}

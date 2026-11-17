using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Linq;
using System.Text.RegularExpressions;

namespace RemoteControl
{
    internal sealed class GeminiSettingsException : IOException
    {
        internal readonly string Code;
        internal GeminiSettingsException(string code, string message) : base(message) { Code = code; }
    }

    internal static class GeminiSettings
    {
        internal const string Key = "mcp_gemini_readonly_hints";
        internal const string MetadataKey = "remotecontrol/internalGeminiReadOnlyHints";
        internal const int MaxBytes = 16 * 1024;
        internal const string Setter = "pc_set_gemini_settings";
        private const string ReceiptsKey = "mcp_gemini_requests";
        // Never evict a receipt and accidentally execute an old request again.
        private const int MaxOwnerReceipts = 256, MaxReceipts = 4096, MaxReceiptBytes = 8 * 1024 * 1024;

        internal static object[] Definitions()
        {
            var target = new { type = "string", minLength = 1, maxLength = 255, description = "Exact hostname verified with pc_get_status on this connection" };
            var request = new { type = "string", minLength = 1, maxLength = 128, pattern = "^[A-Za-z0-9][A-Za-z0-9_.:-]{0,127}$", description = "New ID for each intent; after timeout GET with this ID first. Receipts belong to this authenticated connection and survive restart." };
            return new object[] {
                new { name = "pc_get_gemini_settings", description = "Read this PC owner's Gemini readOnlyHint policy, defaults, effective annotations and editable flags. Supply request_id only to recover a prior SET receipt (historical snapshot); omit it for current settings. Google callback scope can cover several products. Does not change Gemini App actions permissions. Direct owner connection required; no screen or command capability needed.",
                    inputSchema = new { type = "object", required = new[] { "expected_hostname" }, additionalProperties = false,
                        properties = new { expected_hostname = target, request_id = request } },
                    annotations = new { readOnlyHint = true, destructiveHint = false, idempotentHint = true, openWorldHint = false } },
                new { name = Setter, description = "Save this PC owner's Gemini readOnlyHint policy atomically. mode=patch changes boolean read_only_hints and/or restores reset_tools; mode=reset_all accepts neither field. The setter itself is locked readOnlyHint=false. A matching request_id returns the original receipt without reapplying; changed payload conflicts. After a lost response query pc_get_gemini_settings with the SAME ID before retrying. Receipts are retained without eviction: 256 per connection, 4096 per target, 8MiB total; limit errors save nothing. Requires a direct owner login and agent Windows administrator rights. Saving does not grant execution rights or Gemini App actions permission. Refresh Gemini Tool schemas after success.",
                    inputSchema = new { type = "object", required = new[] { "request_id", "expected_hostname", "mode" }, additionalProperties = false,
                        properties = new { expected_hostname = target, request_id = request, mode = new { type = "string", @enum = new[] { "patch", "reset_all" } },
                            read_only_hints = new { type = "object", maxProperties = 64, additionalProperties = new { type = "boolean" } },
                            reset_tools = new { type = "array", maxItems = 64, uniqueItems = true, items = new { type = "string", minLength = 1, maxLength = 128 } } } },
                    annotations = new { readOnlyHint = false, destructiveHint = false, idempotentHint = true, openWorldHint = false } }
            };
        }

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
                if (!names.Contains(pair.Key) || pair.Key == Setter)
                {
                    // Retired tools must not prevent opening the UI and restoring defaults.
                    if (restoring) continue;
                    throw new ArgumentException("없는 도구이거나 수정할 수 없는 저장 도구입니다: " + pair.Key);
                }
                if (!(pair.Value is bool)) throw new ArgumentException("readOnlyHint 값은 JSON true 또는 false여야 합니다.");
                result.Add(pair.Key, (bool)pair.Value);
            }
            return result;
        }

        internal static Dictionary<string, bool> Load()
        {
            string revision; return Read(out revision);
        }

        internal static Dictionary<string, bool> Read(out string revision)
        {
            string raw = Config.GetMany(new[] { Key }, true)[0];
            revision = Revision(raw);
            return Parse(raw, true);
        }

        private static string Revision(string raw) { return McpTools.Hash(Encoding.UTF8.GetBytes(raw ?? "")); }
        private static string Value(Dictionary<string, string> policy, string key)
        { string value; return policy.TryGetValue(key, out value) ? value : ""; }

        internal static void Save(string json)
        { Save(json, null); }

        internal static void Save(string json, string expectedRevision)
        {
            var values = Parse(json, false);
            Commit(values, null, expectedRevision, null, null, null);
        }

        internal static Dictionary<string, object> Snapshot(Dictionary<string, bool> values, string revision)
        {
            var tools = new List<object>();
            foreach (var tool in Catalog()) {
                string name = (string)tool["name"];
                var annotations = McpJson.Obj(tool, "annotations");
                bool original = (bool)annotations["readOnlyHint"], value;
                bool overridden = name != Setter && values.TryGetValue(name, out value);
                if (overridden) annotations["readOnlyHint"] = values[name];
                tools.Add(new { tool_name = name, default_read_only_hint = original, overridden = overridden,
                    editable = name != Setter, annotations = annotations });
            }
            return new Dictionary<string, object> {
                {"target_id", Environment.MachineName}, {"scope", "google_connections_for_this_target"},
                {"scope_note", "Machine policy for the single RemoteControl owner. Registered Google callback sets identify scope, not a specific Gemini product."},
                {"revision", revision}, {"read_only_hints", new Dictionary<string, bool>(values)}, {"tools", tools},
                {"gemini_permission_updated", false}
            };
        }

        private static string Text(Dictionary<string, object> args, string key, int max)
        {
            object value;
            if (!args.TryGetValue(key, out value) || !(value is string) || string.IsNullOrWhiteSpace((string)value) || ((string)value).Length > max)
                throw new ArgumentException(key + "는 비어 있지 않은 문자열이어야 합니다 (최대 " + max + "자).");
            return (string)value;
        }

        private static void CheckPolicy(Dictionary<string, string> policy, McpOptions options)
        {
            if (options == null) return; // Local UI already requires the Windows administrator boundary.
            if (Value(policy, "mcp_enabled") != "1") throw new McpCapabilityDisabledException("MCP 사용 설정이 꺼져 있습니다.");
            string owner = Value(policy, "user"); if (owner == "") owner = "admin";
            if (!string.Equals(owner, options.SettingsOwner, StringComparison.OrdinalIgnoreCase) || owner + "|" + Value(policy, "pwhash") != options.CredentialStamp)
                throw new UnauthorizedAccessException("인증 정책이 변경되었습니다. 다시 연결하세요.");
        }

        internal static object Call(string name, Dictionary<string, object> args, McpOptions options, string user, string token)
        {
            if (!options.Enabled) throw new McpCapabilityDisabledException("MCP 사용 설정이 꺼져 있습니다.");
            if (string.IsNullOrEmpty(token) || !Regex.IsMatch(token, @"\A[A-Za-z0-9_-]{43}\z") ||
                string.IsNullOrEmpty(options.SettingsOwner) || !string.Equals(user, options.SettingsOwner, StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("대상 RemoteControl 소유자의 직접 인증 연결이 필요합니다. 브릿지 중계에서는 설정을 변경하지 않습니다.");
            bool write = name == Setter;
            if (args == null || Encoding.UTF8.GetByteCount(McpJson.Write(args)) > MaxBytes) throw new ArgumentException("설정 요청은 16KiB 이하여야 합니다.");
            var fields = new HashSet<string>(write ? new[] { "expected_hostname", "request_id", "mode", "read_only_hints", "reset_tools" } : new[] { "expected_hostname", "request_id" });
            if (args.Keys.Any(key => !fields.Contains(key))) throw new ArgumentException("예상하지 않은 설정 필드입니다.");
            if (!string.Equals(Text(args, "expected_hostname", 255), Environment.MachineName, StringComparison.OrdinalIgnoreCase))
                throw new GeminiSettingsException("TARGET_MISMATCH", "확인한 PC 이름과 현재 대상이 다릅니다. 실행하지 않았습니다.");
            string requestId = null;
            if (write || args.ContainsKey("request_id")) {
                requestId = Text(args, "request_id", 128);
                if (!Regex.IsMatch(requestId, @"\A[A-Za-z0-9][A-Za-z0-9_.:-]{0,127}\z")) throw new ArgumentException("request_id 형식이 잘못되었습니다.");
            }
            string ownerKey = Revision(options.CredentialStamp + "\n" + token);
            string receiptKey = requestId == null ? null : ownerKey + ":" + Revision(requestId);
            if (!write) {
                string[] keys = { Key, ReceiptsKey, "mcp_enabled", "user", "pwhash" };
                string[] raw = Config.GetMany(keys, true); var policy = new Dictionary<string, string>();
                for (int i = 0; i < keys.Length; i++) policy[keys[i]] = raw[i];
                CheckPolicy(policy, options);
                if (requestId == null) return Snapshot(Parse(raw[0], true), Revision(raw[0]));
                object receipt;
                if (!Receipts(raw[1]).TryGetValue(receiptKey, out receipt)) return new { target_id = Environment.MachineName, request_id = requestId, request_status = "not_found", gemini_permission_updated = false };
                return ReceiptResult((Dictionary<string, object>)receipt, requestId, Revision(raw[0]), true);
            }
            string mode = Text(args, "mode", 16);
            if (mode != "patch" && mode != "reset_all") throw new ArgumentException("mode는 patch 또는 reset_all이어야 합니다.");
            var patch = new Dictionary<string, bool>(StringComparer.Ordinal); var resets = new List<string>();
            if (mode == "reset_all") {
                if (args.ContainsKey("read_only_hints") || args.ContainsKey("reset_tools")) throw new ArgumentException("reset_all에는 변경/복원 필드를 넣을 수 없습니다.");
            } else {
                object input;
                if (args.TryGetValue("read_only_hints", out input)) {
                    if (!(input is Dictionary<string, object>) || ((Dictionary<string, object>)input).Count > 64) throw new ArgumentException("read_only_hints는 최대 64개 boolean 맵이어야 합니다.");
                    patch = Parse(McpJson.Write(input), false);
                }
                if (args.TryGetValue("reset_tools", out input)) {
                    var array = input as IList;
                    if (array == null || array.Count > 64) throw new ArgumentException("reset_tools는 최대 64개 도구 이름 배열이어야 합니다.");
                    foreach (object item in array) {
                        if (!(item is string) || resets.Contains((string)item) || patch.ContainsKey((string)item)) throw new ArgumentException("복원 이름 타입/중복/변경 충돌을 확인하세요.");
                        Parse(McpJson.Write(new Dictionary<string, bool> { { (string)item, false } }), false);
                        resets.Add((string)item);
                    }
                }
                if (patch.Count + resets.Count == 0) throw new ArgumentException("patch에는 하나 이상의 변경 또는 복원이 필요합니다.");
            }
            resets.Sort(StringComparer.Ordinal);
            string fingerprint = Revision(McpJson.Write(new { mode = mode, patch = new SortedDictionary<string, bool>(patch, StringComparer.Ordinal), reset = resets }));
            return Commit(mode == "reset_all" ? new Dictionary<string, bool>() : null, patch, null, resets,
                new[] { receiptKey, ownerKey, requestId, fingerprint }, options);
        }

        private static Dictionary<string, object> Receipts(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return new Dictionary<string, object>();
            if (Encoding.UTF8.GetByteCount(raw) > MaxReceiptBytes) throw new GeminiSettingsException("SETTINGS_READ_FAILED", "설정 요청 기록이 너무 큽니다.");
            try {
                var result = McpJson.Read(raw);
                if (result == null || result.Count > MaxReceipts || result.Values.Any(v => !(v is Dictionary<string, object>))) throw new FormatException();
                return result;
            } catch { throw new GeminiSettingsException("SETTINGS_READ_FAILED", "설정 요청 기록을 읽을 수 없습니다. 중복 실행 방지를 위해 저장하지 않았습니다."); }
        }

        private static Dictionary<string, object> ReceiptResult(Dictionary<string, object> receipt, string requestId, string currentRevision, bool replayed)
        {
            var result = Snapshot(Parse(McpJson.Write(receipt["values"]), true), (string)receipt["revision"]);
            result["saved"] = true; result["schema_refresh_required"] = true; result["request_id"] = requestId;
            result["request_status"] = "saved"; result["replayed"] = replayed;
            result["current_revision"] = currentRevision; result["result_is_current"] = currentRevision == (string)receipt["revision"];
            return result;
        }

        private static object Commit(Dictionary<string, bool> replacement, Dictionary<string, bool> patch, string expectedRevision,
            List<string> resets, string[] request, McpOptions options)
        {
            object result = null;
            try {
                Config.UpdateMany(delegate(Dictionary<string, string> policy) {
                    CheckPolicy(policy, options);
                    string raw = Value(policy, Key), revision = Revision(raw);
                    if (expectedRevision != null && expectedRevision != revision)
                        throw new GeminiSettingsException("SETTINGS_CONFLICT", "다른 화면 또는 MCP에서 설정을 변경했습니다. 다시 불러온 뒤 저장하세요.");
                    var receipts = request == null ? null : Receipts(Value(policy, ReceiptsKey)); object prior;
                    if (request != null && receipts.TryGetValue(request[0], out prior)) {
                        var receipt = (Dictionary<string, object>)prior;
                        if (Json.Str(receipt, "fingerprint") != request[3]) throw new GeminiSettingsException("REQUEST_ID_CONFLICT", "같은 request_id에 다른 변경을 지정할 수 없습니다.");
                        result = ReceiptResult(receipt, request[2], revision, true); return null;
                    }
                    var values = replacement == null ? Parse(raw, true) : new Dictionary<string, bool>(replacement);
                    if (patch != null) foreach (var pair in patch) values[pair.Key] = pair.Value;
                    if (resets != null) foreach (string reset in resets) values.Remove(reset);
                    string json = McpJson.Write(values); Parse(json, false);
                    // Verify a complete effective catalog BEFORE the atomic commit. There is
                    // no separate mutable runtime state which can fail after persistence.
                    try { result = Snapshot(values, Revision(json)); }
                    catch { throw new GeminiSettingsException("SETTINGS_APPLY_FAILED", "유효 도구 목록을 구성하지 못했습니다. 저장하지 않았습니다."); }
                    var changes = new Dictionary<string, string> { { Key, json } };
                    if (request != null) {
                        if (receipts.Count >= MaxReceipts || receipts.Keys.Count(k => k.StartsWith(request[1] + ":", StringComparison.Ordinal)) >= MaxOwnerReceipts)
                            throw new GeminiSettingsException("REQUEST_HISTORY_FULL", "요청 기록 한도에 도달했습니다. 이전 요청은 재실행하지 않습니다. 관리자가 기록 보존 정책을 점검해야 합니다.");
                        var receipt = new Dictionary<string, object> { { "fingerprint", request[3] }, { "values", values }, { "revision", Revision(json) } };
                        receipts.Add(request[0], receipt); string journal = McpJson.Write(receipts);
                        if (Encoding.UTF8.GetByteCount(journal) > MaxReceiptBytes) throw new GeminiSettingsException("REQUEST_HISTORY_FULL", "요청 기록 크기 한도에 도달했습니다.");
                        changes[ReceiptsKey] = journal;
                        result = ReceiptResult(receipt, request[2], Revision(json), false);
                    }
                    return changes;
                });
                return result;
            } catch (GeminiSettingsException) { throw; }
              catch (UnauthorizedAccessException) { throw; }
              catch (IOException) { throw new GeminiSettingsException("SETTINGS_SAVE_FAILED", "설정을 저장하지 못했습니다. 같은 request_id로 결과를 조회한 뒤 확인하세요."); }
        }
    }
}

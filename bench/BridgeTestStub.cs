// 시험용 껍데기. 진짜 Bridge.cs 를 그대로 쓰되 설정만 메모리에 둔다.
// 사용자의 실제 ProgramData\RemoteControl\settings.txt 를 건드리지 않기 위해서다.
using System;
using System.Collections.Generic;

namespace RemoteControl
{
    internal static class Config
    {
        private static readonly Dictionary<string, string> m =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        internal static string Get(string k) { string v; return m.TryGetValue(k, out v) ? v : ""; }
        internal static void Set(string k, string v) { m[k] = v ?? ""; }
    }
}

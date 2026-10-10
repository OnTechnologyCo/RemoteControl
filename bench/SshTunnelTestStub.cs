using System;
using System.Collections.Generic;
namespace RemoteControl
{
    internal static class PrivateMcp {internal static void Stop(){} internal static string RequestFields(){return "";} internal static void Address(string value){}}
    internal static class TailscaleMcp {internal static void Stop() {}}
    internal static class Config
    {
        private static readonly Dictionary<string, string> values = new Dictionary<string, string>();
        internal static string Get(string k) { lock (values) { string v; return values.TryGetValue(k, out v) ? v : ""; } }
        internal static void Set(string k, string v) { lock (values) values[k] = v; }
        internal static void SetMany(Dictionary<string, string> changes) { lock (values) foreach (var p in changes) values[p.Key] = p.Value; }
        internal static string[] GetMany(string[] keys) { lock (values) return Array.ConvertAll(keys, Get); }
        internal static void Reset() { lock (values) values.Clear(); }
        internal static int Port { get { int p; return int.TryParse(Get("port"), out p) ? p : 5900; } }
    }
    // 실제 Tailscale 계정과 알림에 접속하지 않는다. SSH/guard는 제품 코드를 그대로 컴파일한다.
    internal static class Tunnel
    {
        internal static bool IsOpening { get { return false; } }
        internal static bool Alive;
        internal static int Starts, Stops;
        internal static string CurrentUrl = "https://test.invalid", LastError = "";
        internal static bool IsConfigured { get { return Config.Get("tskey").Length > 0; } }
        internal static bool IsAlive() { return Alive; }
        internal static string Start(int port, Func<bool> canceled) { if (canceled()) return null; Starts++; Alive = true; return CurrentUrl; }
        internal static void Stop() { Stops++; Alive = false; }
        internal static void KillOrphans() { }
        internal static void ResetIdentity() { }
        internal static string LoadKey() { return Config.Get("tskey"); }
        internal static void AddKeySettings(Dictionary<string, string> changes, string key) { changes["tskey"] = key; }
        internal static string Sanitize(string s) { return s; }
    }
    internal static class Notify
    {
        internal static string LastUrl = "", LastError = "";
        internal static bool IsConfigured { get { return false; } }
        internal static bool Send(string s) { throw new Exception("Test must never send notifications"); }
        internal static string CameBack(string s) { return s; }
        internal static string AddressChanged(string a, string b) { return b; }
        internal static string WentDown(int n, string s) { return s; }
    }
    internal static class Https { internal static void EnsureTls() { } }
}

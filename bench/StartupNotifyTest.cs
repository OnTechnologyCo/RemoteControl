// Synthetic startup-notification tests. No production configuration or Telegram API calls.
using System;
using System.Collections.Generic;
using System.Threading;
namespace RemoteControl
{
    internal static class Config
    {
        static readonly Dictionary<string, string> Values = new Dictionary<string, string>();
        internal static string Get(string key) { string v; return Values.TryGetValue(key, out v) ? v : ""; }
        internal static void Set(string key, string value) { Values[key] = value; }
    }
    internal static class Notify
    {
        internal static bool IsConfigured = true;
        internal static string ChatId = "subscriber-one";
        internal static string LastUrl = "https://previous.example/";
        internal static bool Succeeds = true;
        internal static int Sent;
        internal static string LastText = "";
        internal static string CurrentAddressesWithScreen(string url) { return "PC: TEST-PC\n원격제어 ID: alice\n터널 계정: sshuser\n화면 주소: " + url; }
        internal static string NoticeAddress(string url) { return url; }
        internal static bool Send(string text)
        {
            if (!Succeeds) return false;
            Interlocked.Increment(ref Sent);
            LastText = text;
            return true;
        }
    }
    internal static class SvcLog { internal static void Write(string value) {} }

    internal static class StartupNotifyTest
    {
        static int tests;
        static void Check(bool okay, string description)
        {
            if (!okay) throw new Exception(description);
            Console.WriteLine("PASS " + description);
            tests++;
        }
        static void Main()
        {
            Check(!StartupNotify.OnVerifiedTunnel(""), "No URL does not send");
            Check(StartupNotify.OnVerifiedTunnel("https://first.example/"), "First verified connection notifies");
            Check(Notify.Sent == 1 && Notify.LastText.Contains("TEST-PC") &&
                  Notify.LastText.Contains("https://first.example/") &&
                  Notify.LastText.Contains("이전 화면 주소: https://previous.example/"), "First message includes PC, current and previous URLs");
            Check(Notify.LastUrl == "https://first.example/" && Config.Get("tg_boot_notice").EndsWith("|ready"), "Successful send persists current boot and address");
            Check(!StartupNotify.OnVerifiedTunnel("https://first.example/") && Notify.Sent == 1, "Agent and tunnel restart on same boot do not re-notify");
            Notify.ChatId = "subscriber-two";
            Notify.IsConfigured = false;
            Check(!StartupNotify.OnVerifiedTunnel("https://first.example/"), "Disabled notification never sends");
            Notify.IsConfigured = true;
            Check(StartupNotify.TrySendPending(), "Offline startup warns after timeout");
            Check(Notify.LastText.Contains("연결 확인 대기") && !Notify.LastText.Contains("https://first.example/"), "Pending notice does not claim unverified URL");
            Check(!StartupNotify.TrySendPending() && Notify.Sent == 2, "Waiting notice is sent once");
            Check(StartupNotify.OnVerifiedTunnel("https://second.example/"), "Verified recovery after pending notice sends completion");
            Check(Notify.LastText.Contains("연결 완료") && Notify.LastText.Contains("https://second.example/"), "Recovery notice includes working URL");
            Check(!StartupNotify.OnVerifiedTunnel("https://second.example/") && Notify.Sent == 3, "Recovered state suppresses repeated notifications");
            Notify.ChatId = "subscriber-three"; Notify.Succeeds = false;
            Check(!StartupNotify.OnVerifiedTunnel("https://third.example/") && Notify.Sent == 3, "Failed delivery is not recorded");
            Notify.Succeeds = true;
            Check(StartupNotify.OnVerifiedTunnel("https://third.example/") && Notify.Sent == 4, "Failed delivery is retried");
            Notify.ChatId = "subscriber-four";
            int initial = Notify.Sent;
            Exception firstFailure = null, secondFailure = null;
            Thread a = new Thread(delegate() { try { StartupNotify.OnVerifiedTunnel("https://fourth.example/"); } catch(Exception ex) { firstFailure=ex; } });
            Thread b = new Thread(delegate() { try { StartupNotify.OnVerifiedTunnel("https://fourth.example/"); } catch(Exception ex) { secondFailure=ex; } });
            a.Start(); b.Start(); a.Join(); b.Join();
            Check(firstFailure==null && secondFailure==null && Notify.Sent==initial+1, "Concurrent verification sends exactly once");
            Console.WriteLine("ALL " + tests + " STARTUP CHECKS PASSED. No Telegram messages sent.");
        }
    }
}

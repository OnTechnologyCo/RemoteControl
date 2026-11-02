// Synthetic privacy-mode state test. Never calls Windows overlay APIs or changes displays.
using System;
using RemoteControl;

namespace RemoteControl
{
    internal static class SvcLog { internal static void Write(string value) {} }
    internal static class Desktops { internal static string InputDesktopName() { return "Default"; } }
    internal sealed class FakeCover : IPrivacyCover
    {
        internal static int Started, Stopped;
        internal static bool Fail;
        internal bool Active;
        public bool IsRunning { get { return Active; } }
        public bool Start(out string error)
        {
            if (Fail) { error = "Synthetic failure"; return false; }
            error = ""; Started++; Active = true; return true;
        }
        public void Stop() { if (Active) { Stopped++; Active = false; } }
    }
    internal static class PrivacyScreenTest
    {
        private static int passed;
        static void Check(bool condition, string name)
        {
            if (!condition) throw new Exception("FAIL " + name);
            passed++; Console.WriteLine("PASS " + name);
        }
        static void Main()
        {
            PrivacyScreen.CoverFactory = delegate { return new FakeCover(); };
            object a = new object(), b = new object();
            bool active; string error;
            Check(!PrivacyScreen.IsActive, "Privacy is off by default");
            Check(PrivacyScreen.Set(a, true, out active, out error) && active && error == "",
                  "Authenticated viewer can enable");
            Check(FakeCover.Started == 1 && PrivacyScreen.IsActive, "One overlay created");
            Check(PrivacyScreen.Set(a, true, out active, out error) && FakeCover.Started == 1,
                  "Repeat enable is idempotent");
            Check(!PrivacyScreen.Set(b, true, out active, out error) && active && error.Length > 0,
                  "Another viewer cannot silently steal the owner");
            PrivacyScreen.Release(b);
            Check(PrivacyScreen.IsActive && FakeCover.Stopped == 0,
                  "Other viewer disconnect does not uncover current owner");
            PrivacyScreen.Release(a);
            Check(!PrivacyScreen.IsActive && FakeCover.Stopped == 1,
                  "Owner disconnect immediately restores display");
            Check(PrivacyScreen.Set(b, true, out active, out error), "Next viewer can enable");
            Check(PrivacyScreen.Set(a, false, out active, out error) && !active && FakeCover.Stopped == 2,
                  "Any authenticated viewer may restore physical screen");
            FakeCover.Fail = true;
            Check(!PrivacyScreen.Set(a, true, out active, out error) && !active &&
                  error == "Synthetic failure", "Failed overlay never claims to be enabled");
            FakeCover.Fail = false;
            Check(PrivacyScreen.Set(a, true, out active, out error), "Successful retry after failure");
            PrivacyScreen.Reset();
            Check(!PrivacyScreen.IsActive && FakeCover.Stopped == 3,
                  "Server shutdown releases overlay");
            Check(PrivacyScreen.Set(a, false, out active, out error) && !active,
                  "Repeated restore is harmless");
            FakeCover cover = new FakeCover();
            PrivacyScreen.CoverFactory = delegate { return cover; };
            Check(PrivacyScreen.Set(a, true, out active, out error), "Overlay can be enabled again");
            cover.Active = false; // e.g. emergency hotkey closed its message loop
            Check(!PrivacyScreen.IsActive, "Emergency hotkey exit is reflected in mode status");
            PrivacyScreen.Release(a);
            Check(!PrivacyScreen.IsActive, "Disconnected owner after hotkey remains safe");
            Console.WriteLine("ALL " + passed + " PRIVACY STATE CHECKS PASSED; physical displays untouched");
        }
    }
}

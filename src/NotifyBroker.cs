using System;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using RemoteControlSecrets;

namespace RemoteControl
{
    // The tray sends an operation name only. Token, destination and message content stay in the service.
    internal static class NotifyBroker
    {
        internal static Action<string> Diagnostic { get; set; }
        private static void Report(Exception ex) { if (Diagnostic != null) Diagnostic(ex.GetType().Name + " " + ex.HResult.ToString("X")); }
        private static bool started;
        private static DateTime lastSend;
        private static readonly object sendGate = new object();
        private static string HandoffPath { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RemoteControl", "notify-pipe.txt"); } }

        internal static void Start()
        {
            if (started) return;
            using (var identity = WindowsIdentity.GetCurrent()) if (!identity.IsSystem) return;
            started = true;
            string name = "RemoteControl.Notify." + Guid.NewGuid().ToString("N");
            var thread = new Thread(delegate() { Serve(name, HandoffPath, Dispatch); });
            thread.IsBackground = true; thread.Start();
        }

        internal static bool AllowedCaller(WindowsIdentity identity)
        {
            if (identity == null || !identity.IsAuthenticated) return false;
            var principal = new WindowsPrincipal(identity);
            if (principal.IsInRole(new SecurityIdentifier(WellKnownSidType.NetworkSid, null))) return false;
            return identity.IsSystem || principal.IsInRole(WindowsBuiltInRole.Administrator) ||
                principal.IsInRole(new SecurityIdentifier(WellKnownSidType.InteractiveSid, null));
        }

        internal static string Dispatch(string operation)
        {
            if (operation == "status") return Notify.IsConfigured ? "configured" : "disabled";
            if (operation != "send-addresses") return "rejected";
            lock (sendGate)
            {
                if (DateTime.UtcNow - lastSend < TimeSpan.FromSeconds(30)) return "rate-limited";
                lastSend = DateTime.UtcNow;
            }
            return Notify.Send(Notify.CurrentAddresses()) ? "sent" : "failed";
        }

        internal static void Serve(string name, string handoff, Func<string, string> dispatch)
        {
            var acl = new PipeSecurity(); acl.SetAccessRuleProtection(true, false);
            acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
            acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
            // Do not grant CreateNewInstance, which is included in the broad Write mask.
            acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.InteractiveSid, null), PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, AccessControlType.Allow));
            while (true)
            {
                try
                {
                    using (var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 256, 256, acl))
                    {
                        File.WriteAllText(handoff, name, Encoding.UTF8);
                        ConfigProtection.ProtectFile(handoff, false);
                        pipe.WaitForConnection();
                        byte[] buffer = new byte[32];
                        var read = pipe.BeginRead(buffer, 0, buffer.Length, null, null);
                        int length;
                        using (var wait = read.AsyncWaitHandle) { if (!wait.WaitOne(3000)) continue; length = pipe.EndRead(read); }
                        bool allowed = false;
                        pipe.RunAsClient(delegate { using (var id = WindowsIdentity.GetCurrent(true)) allowed = AllowedCaller(id); });
                        if (!allowed) { if (Diagnostic != null) Diagnostic("caller rejected"); continue; }
                        string op = Encoding.ASCII.GetString(buffer, 0, length);
                        string response = op == "status" || op == "send-addresses" ? dispatch(op) : "rejected";
                        // Responses have a closed vocabulary; no exception, token or arbitrary upstream text crosses IPC.
                        if (response != "configured" && response != "disabled" && response != "sent" && response != "failed" && response != "rate-limited") response = "rejected";
                        byte[] data = Encoding.ASCII.GetBytes(response);
                        pipe.Write(data, 0, data.Length);
                    }
                }
                catch (Exception ex) { Report(ex); Thread.Sleep(250); }
            }
        }

        internal static string Request(string operation) { return RequestAt(HandoffPath, operation); }
        internal static string RequestAt(string handoff, string operation)
        {
            if (operation != "status" && operation != "send-addresses") return "rejected";
            try
            {
                ConfigProtection.Verify(Path.GetDirectoryName(handoff), true, false);
                ConfigProtection.Verify(handoff, false, false);
                string name = File.ReadAllText(handoff, Encoding.UTF8).Trim();
                if (!System.Text.RegularExpressions.Regex.IsMatch(name, "\\ARemoteControl\\.Notify\\.[a-f0-9]{32}\\z")) return "failed";
                using (var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification))
                {
                    pipe.Connect(1000);
                    byte[] data = Encoding.ASCII.GetBytes(operation); pipe.Write(data, 0, data.Length);
                    byte[] response = new byte[32]; var read = pipe.BeginRead(response, 0, response.Length, null, null);
                    using (var wait = read.AsyncWaitHandle) {
                        if (!wait.WaitOne(operation == "status" ? 1500 : 55000)) return "failed";
                        return Encoding.ASCII.GetString(response, 0, pipe.EndRead(read));
                    }
                }
            }
            catch (Exception ex) { Report(ex); return "failed"; }
        }
    }
}

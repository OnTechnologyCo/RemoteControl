using System;
using System.IO;
using System.Text;
namespace RemoteControl
{
    internal static class McpSessionsTest
    {
        private static void Check(bool value, string message)
        { if (!value) throw new Exception(message); Console.WriteLine("PASS " + message); }
        internal static int Run(string mode, string root)
        {
            Directory.CreateDirectory(root);
            string file = Path.Combine(root, "sessions.bin"), receipt = Path.Combine(root, "test-token.txt");
            var sessions = new McpSessions(file);
            if (mode == "--session-save")
            {
                Session created = sessions.Create("fixture", "127.0.0.1", "fixture-credentials");
                File.WriteAllText(receipt, created.Token);
                string bytes = Encoding.ASCII.GetString(File.ReadAllBytes(file));
                Check(!bytes.Contains(created.Token) && !bytes.Contains("fixture-credentials"), "LAN MCP session file is DPAPI encrypted");
                Check(created.Expires == DateTime.MaxValue, "LAN MCP authentication has no fixed eight-hour expiry");
            }
            else
            {
                string token = File.ReadAllText(receipt);
                Session restored = sessions.Find(token, "fixture-credentials");
                Check(restored != null && restored.User == "fixture" && restored.Ip == "127.0.0.1", "LAN MCP authentication survives a new Windows process");
                Check(sessions.Find(token, "changed-credentials") == null, "changed credentials reject old LAN MCP authentication");
                sessions.Drop(token, "fixture-credentials");
                Check(new McpSessions(file).Find(token, "fixture-credentials") == null, "LAN MCP logout remains revoked after reload");
                Session next = sessions.Create("fixture", "127.0.0.1", "fixture-credentials");
                sessions.Clear("changed-credentials");
                Check(sessions.Find(next.Token, "fixture-credentials") == null && sessions.Find(next.Token, "changed-credentials") == null, "credential-change revocation cannot revive the previous session");
            }
            return 0;
        }
    }
}

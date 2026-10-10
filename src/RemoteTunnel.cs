// 화면 서버와 외부망 방식을 분리한다. 방식 변경은 터널만 다시 열어 LAN 접속을 유지한다.
using System;

namespace RemoteControl
{
    internal static class RemoteTunnel
    {
        internal static readonly string[] SettingKeys = {
            "tunnel", "sshhost", "sshport", "sshuser", "sshkeyfile", "sshknownhosts",
            "sshremoteport", "sshurl", "tunnelrevision", "tskey", "tskeym", "tsreset", "extauto", "port",
            "mcp_web_mode", "mcp_ts_port", "mcp_ts_gateway", "mcp_ts_revision", "mcp_enabled", "mcp_port"
        };
        private static readonly object startGate = new object();
        private static readonly object stateGate = new object();
        private static volatile string active = "";
        private static string settings;
        private static int generation;

        internal static string Provider
        {
            get
            {
                string p = Config.Get("tunnel");
                // 이미 설치된 PC의 주소를 업그레이드만으로 끊지 않는다.
                if (p.Length == 0) return Config.Get("tskey").Length > 0 || Config.Get("tskeym").Length > 0
                    ? "tailscale" : "private";
                return p;
            }
        }
        internal static string Name { get { return Provider == "private" ? "private-tunnel (SSH)" : Provider == "tailscale" ? "Tailscale" : "사용 안 함"; } }
        internal static bool IsOpening { get { return Tunnel.IsOpening || SshTunnel.IsOpening; } }
        internal static bool IsAlive() { return active == "private" ? SshTunnel.IsAlive() : active == "tailscale" && Tunnel.IsAlive(); }
        internal static string CurrentUrl { get { return active == "private" ? SshTunnel.CurrentUrl : active == "tailscale" ? Tunnel.CurrentUrl : ""; } }
        internal static string LastError { get { return active == "private" ? SshTunnel.LastError : Tunnel.LastError; } }
        internal static bool RetryBlocked { get { return active == "private" && SshTunnel.RetryBlocked; } }
        internal static string ConfigurationError
        {
            get
            {
                if (Provider == "off") return "외부망 사용이 꺼져 있습니다.";
                if (Provider == "tailscale") return Tunnel.IsConfigured ? "" : "Tailscale 키를 설정하세요.";
                if (Provider == "private") return SshSettings.Load().Validate(false);
                return "외부망 방식을 다시 선택하세요.";
            }
        }
        internal static bool IsConfigured { get { return ConfigurationError.Length == 0; } }

        internal static string SettingsFingerprint()
        {
            string[] values=Config.GetMany(SettingKeys);
            // 직접 입력 MCP와 SSH는 Tailscale MCP 설정 변경의 영향을 받지 않는다.
            if(Provider!="tailscale"||Config.Get("mcp_web_mode")!="tailscale")
                for(int i=0;i<SettingKeys.Length;i++)if(SettingKeys[i].StartsWith("mcp_",StringComparison.Ordinal))values[i]="";
            return string.Join("\n",values);
        }

        // Agent는 2초마다, 창 모드는 상태 타이머에서 호출한다. Start의 긴 락을 기다리지 않는다.
        internal static bool SyncSettings()
        {
            string now = SettingsFingerprint();
            lock (stateGate)
            {
                if (settings == now) return false;
                settings = now;
                Stop();
                SshTunnel.ClearFailure();
                return true;
            }
        }

        internal static string Start(int port)
        {
            int gen;
            lock (stateGate) gen = generation;
            lock (startGate)
            {
                string selected;
                lock (stateGate)
                {
                    if (gen != generation) return null;
                    selected = Provider;
                    if (selected != "private" && selected != "tailscale") return null;
                    active = selected;
                }
                if (selected == "tailscale" && Config.Get("tsreset") != Config.Get("tsresetdone"))
                {
                    string reset = Config.Get("tsreset");
                    Tunnel.ResetIdentity();
                    Config.Set("tsresetdone", reset);
                }
                Func<bool> canceled = delegate { return gen != System.Threading.Thread.VolatileRead(ref generation); };
                string result = selected == "private" ? SshTunnel.Start(port, canceled) : Tunnel.Start(port, canceled);
                lock (stateGate)
                {
                    if (gen == generation) return result;
                    if (selected == "private") SshTunnel.Stop(); else Tunnel.Stop();
                    return null;
                }
            }
        }

        internal static void Stop()
        {
            lock (stateGate)
            {
                generation++;
                TailscaleMcp.Stop();
                Tunnel.Stop();
                SshTunnel.Stop();
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Threading;

namespace RemoteControl
{
    // 부팅 작업만으로는 앱 재실행 시 중지된 웹 MCP 중계가 돌아오지 않는다.
    internal sealed class McpGatewayStartup : IDisposable
    {
        private readonly Timer timer;
        private readonly Dictionary<string,string> errors = new Dictionary<string,string>();
        private int working;
        private volatile bool stopped;
        private DateTime nextCheck;
        private string lastSettings = "";

        internal McpGatewayStartup() { timer = new Timer(Tick, null, 0, 1000); }
        private void Tick(object state)
        {
            if (stopped || Interlocked.CompareExchange(ref working, 1, 0) != 0) return;
            try
            {
                if (stopped || !RemoteControlSecrets.ConfigProtection.IsAdministrator) return;
                Config.Reload();
                // 화면 초기화나 UI 타이머 지연이 MCP 주소 heartbeat를 막지 않게 한다.
                TailscaleMcp.Tick();
                PrivateMcp.Tick();
                string mode = Config.Get("mcp_web_mode");
                bool enabled = Config.Get("mcp_enabled") == "1", password = Config.HasPassword;
                bool lan = Config.Get("mcp_lan_enabled") == "1";
                string settings = mode + "|" + enabled + "|" + password + "|" + lan;
                if (settings == lastSettings && DateTime.UtcNow < nextCheck) return;
                lastSettings = settings; nextCheck = DateTime.UtcNow.AddSeconds(15);
                if (!enabled || !password) return;
                bool shared = false;
                if (lan)
                {
                    string file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RemoteControlLanWebMcp", "active.json");
                    try {
                        var active = McpJson.Read(File.ReadAllText(file));
                        lan = Json.Bool(active, "enabled", false);
                        shared = Json.Bool(active, "shared", false);
                    } catch (Exception ex) { lan = false; Failed("LAN MCP 설정", ex); }
                }
                EnsureConfigured(enabled, password, mode, lan, shared, StartTask, StartHttps);
            }
            catch (Exception ex) { Failed("MCP 자동 시작", ex); }
            finally { Interlocked.Exchange(ref working, 0); }
        }

        internal static void EnsureConfigured(bool enabled, bool password, string mode, bool lan, bool shared, Action<string> task, Action<string> service)
        {
            if (!enabled || !password) return;
            if (mode == "private") task("RemoteControlPrivateMcp");
            else if (mode == "tailscale") task("RemoteControlTailscaleMcp");
            else if (mode == "ddns") task("RemoteControlWebMcp");
            if (lan) {
                task("RemoteControlLanWebMcpGateway");
                if (!shared) task("RemoteControlLanWebMcpHttps");
            }
            if (mode == "ddns" || (lan && shared)) service("RemoteControlWebHttps");
        }

        private static object Invoke(object target, string member, BindingFlags flags, params object[] args)
        { return target.GetType().InvokeMember(member, flags, null, target, args); }
        private static void Release(object value)
        { if (value != null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value); }

        private void StartTask(string name)
        {
            object scheduler = null, folder = null, task = null, running = null;
            try
            {
                if (stopped) return;
                scheduler = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", true));
                Invoke(scheduler, "Connect", BindingFlags.InvokeMethod);
                folder = Invoke(scheduler, "GetFolder", BindingFlags.InvokeMethod, "\\");
                task = Invoke(folder, "GetTask", BindingFlags.InvokeMethod, name);
                if (!stopped) running = StartReadyTask(task);
                if (running != null) SvcLog.Write("MCP 중계 시작 요청: " + name);
                errors.Remove(name);
            }
            catch (Exception ex) { Failed(name, ex); }
            finally { Release(running); Release(task); Release(folder); Release(scheduler); }
        }

        internal static object StartReadyTask(object task)
        {
            // Disabled/Queued/Running 작업은 재등록하거나 중복 실행하지 않는다.
            int state = Convert.ToInt32(Invoke(task, "State", BindingFlags.GetProperty));
            return state == 3 ? Invoke(task, "Run", BindingFlags.InvokeMethod, new object[] { null }) : null;
        }

        private void StartHttps(string name)
        {
            try
            {
                if (stopped) return;
                using (var service = new ServiceController(name))
                    if (service.Status == ServiceControllerStatus.Stopped && !stopped)
                    { service.Start(); SvcLog.Write("MCP HTTPS 시작 요청: " + name); }
                errors.Remove(name);
            }
            catch (Exception ex) { Failed(name, ex); }
        }

        private void Failed(string name, Exception error)
        {
            error = error.GetBaseException();
            string code = error.GetType().Name + " 0x" + error.HResult.ToString("X8"), prior;
            if (!errors.TryGetValue(name, out prior) || prior != code)
                SvcLog.Write(name + " 자동 시작 실패 (" + code + "). 설치 상태와 실행 권한을 확인하세요. 재시도합니다.");
            errors[name] = code;
        }
        public void Dispose() { stopped = true; timer.Dispose(); }
    }
}



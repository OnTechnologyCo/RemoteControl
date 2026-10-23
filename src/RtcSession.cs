using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Web.Script.Serialization;

namespace RemoteControl
{
    // Private pipes only; this helper never listens for unauthenticated commands.
    internal sealed class RtcSession : IRealtimeVideo
    {
        private static int count;
        private static readonly object prepareGate = new object();
        private static string helper, encoder;
        private readonly object gate = new object();
        private readonly ConcurrentQueue<string> outgoing = new ConcurrentQueue<string>();
        private readonly ConcurrentQueue<string> incoming = new ConcurrentQueue<string>();
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private Process process;
        private StreamWriter inputWriter;
        private Interop.ProcessJob job;
        private int closed, queuedIn, queuedOut;
        internal string FailureCode;
        private long heartbeat;
        private readonly string desktop;
        public int Id { get; private set; }
        public bool Active { get; set; }
        public bool Expired { get { return closed != 0 || clock.ElapsedMilliseconds - heartbeat > (Active ? 5000 : 20000) || Desktops.InputDesktopName() != desktop; } }
        internal static bool Available
        {
            get
            {
                if (Config.Get("video") == "off") return false;
                var names = Assembly.GetExecutingAssembly().GetManifestResourceNames();
                return Array.IndexOf(names, "video-helper.exe.gz") >= 0 && Array.IndexOf(names, "video-ffmpeg.exe.gz") >= 0;
            }
        }
        // The browser must use the same STUN policy as the sender. No TURN service
        // is provisioned by the application; blocked UDP safely stays on JPEG.
        internal static object[] IceServers()
        {
            string url = Config.Get("videostun");
            if (url == "off") return new object[0];
            if (url.Length == 0) url = "stun:stun.l.google.com:19302";
            if (!url.StartsWith("stun:", StringComparison.OrdinalIgnoreCase) || url.Length > 256) return new object[0];
            return new object[] { new { urls = new[] { url } } };
        }
        internal static RtcSession Start(int id, string sdp, ScreenSource src)
        {
            if (!string.Equals(Desktops.InputDesktopName(), "Default", StringComparison.OrdinalIgnoreCase) || !Available || id <= 0 || sdp == null || sdp.Length < 32 || sdp.Length > 128 * 1024) return null;
            if (Interlocked.Increment(ref count) > 2) { Interlocked.Decrement(ref count); return null; }
            try { return new RtcSession(id, sdp, src); }
            catch { Interlocked.Decrement(ref count); return null; }
        }
        private RtcSession(int id, string sdp, ScreenSource src)
        {
            Id = id; desktop = Desktops.InputDesktopName();
            var area = src.Area;
            int adapter, output;
            DesktopDup.FindOutput(area, out adapter, out output);
            var config = new Dictionary<string, object> {
                {"type", "start"}, {"sdp", sdp}, {"X", area.X}, {"Y", area.Y},
                {"Width", area.Width}, {"Height", area.Height}, {"OutputWidth", src.FrameWidth}, {"OutputHeight", src.FrameHeight},
                {"Adapter", adapter}, {"Output", output}, {"FPS", 30},
                {"InitialBitrate", (long)src.FrameWidth * src.FrameHeight >= 3840L * 2160 ? 4000000 : 2000000},
                {"Bitrate", (long)src.FrameWidth * src.FrameHeight >= 3840L * 2160 ? 12000000 : 6000000}, {"ice", IceServers()}
            };
            new Thread(delegate() { Run(config); }) { IsBackground = true, Name = "WebRTC video" }.Start();
        }
        private void Run(Dictionary<string, object> config)
        {
            try
            {
                lock (prepareGate)
                {
                    if (helper == null) helper = SshHelper.Prepare("video-helper.exe.gz", "video-helper", "video");
                    if (encoder == null) encoder = SshHelper.Prepare("video-ffmpeg.exe.gz", "ffmpeg", "video");
                }
                lock (gate)
                {
                    if (closed != 0) return;
                    process = new Process { StartInfo = new ProcessStartInfo(helper) {
                        UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
                        RedirectStandardOutput = true, RedirectStandardError = true,
                        StandardOutputEncoding = System.Text.Encoding.UTF8
                    }};
                    process.Start();
                    job = new Interop.ProcessJob(process); // assign before allowing the child to spawn FFmpeg
                    process.ErrorDataReceived += delegate { }; process.BeginErrorReadLine();
                    config["ffmpeg"] = encoder;
                    inputWriter = new StreamWriter(process.StandardInput.BaseStream, new System.Text.UTF8Encoding(false));
                    inputWriter.AutoFlush = true;
                    inputWriter.WriteLine(new JavaScriptSerializer().Serialize(config));
                }
                Process p = process;
                new Thread(delegate() {
                    try {
                        while (closed == 0) {
                            string line;
                            if (incoming.TryDequeue(out line)) {
                                Interlocked.Decrement(ref queuedIn);
                                inputWriter.WriteLine(line);
                            } else Thread.Sleep(10);
                        }
                    } catch { Dispose(); }
                }) { IsBackground = true, Name = "WebRTC commands" }.Start();
                string message;
                while (closed == 0 && (message = p.StandardOutput.ReadLine()) != null)
                {
                    if (message.Length > 256 * 1024 || Interlocked.Increment(ref queuedOut) > 64) break;
                    outgoing.Enqueue(message);
                }
            }
            catch (Exception ex) {
                FailureCode = ex.GetType().Name;
                var win = ex as System.ComponentModel.Win32Exception;
                if (win != null) FailureCode += ":" + win.NativeErrorCode;
            } // browser receives a generic fallback, never paths or credentials
            finally { Dispose(); }
        }
        public void Heartbeat() { heartbeat = clock.ElapsedMilliseconds; }
        public bool TryRead(out string value)
        {
            if (!outgoing.TryDequeue(out value)) return false;
            Interlocked.Decrement(ref queuedOut); return true;
        }
        public void Send(string value)
        {
            if (closed != 0) return;
            if (value.Length > 16384 || Interlocked.Increment(ref queuedIn) > 64) { Dispose(); return; }
            incoming.Enqueue(value);
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref closed, 1) != 0) return;
            lock (gate)
            {
                if (job != null) { job.Dispose(); job = null; }
                if (process != null)
                {
                    try { if (!process.HasExited) process.Kill(); } catch { }
                    try { if (inputWriter != null) inputWriter.Dispose(); } catch { }
                    process.Dispose(); process = null;
                }
            }
            Interlocked.Decrement(ref count);
        }
    }
}

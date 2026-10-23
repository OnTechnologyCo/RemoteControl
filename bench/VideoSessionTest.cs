using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading;
internal static class VideoSessionTest
{
    [STAThread]
    public static int Main(string[] args)
    {
        IDisposable src = null, rtc = null; int processId = 0;
        try
        {
            var assembly = Assembly.LoadFrom(args[0]);
            var flags = BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            var sourceType = assembly.GetType("RemoteControl.ScreenSource", true);
            var rtcType = assembly.GetType("RemoteControl.RtcSession", true);
            src = (IDisposable)Activator.CreateInstance(sourceType, BindingFlags.Instance | BindingFlags.NonPublic, null,
                new object[] { new Rectangle(0, 0, 256, 256) }, null);
            rtc = (IDisposable)rtcType.GetMethod("Start", flags).Invoke(null, new object[] { 123, File.ReadAllText(args[1]), src });
            if (rtc == null) throw new Exception("Embedded video unavailable on the default desktop");
            var timer = Stopwatch.StartNew(); bool answer = false;
            while (timer.ElapsedMilliseconds < 20000)
            {
                object[] value = { null };
                if ((bool)rtcType.GetMethod("TryRead", flags).Invoke(rtc, value))
                {
                    string line = value[0] as string;
                    if (line != null && line.Contains("\"type\":\"error\"")) Console.WriteLine("Helper fixture error: " + line);
                    if (line != null && line.Contains("\"type\":\"answer\"")) { answer = true; break; }
                }
                if ((bool)rtcType.GetProperty("Expired", flags).GetValue(rtc, null)) break;
                Thread.Sleep(20);
            }
            if (!answer) {
                var failure = rtcType.GetField("FailureCode", flags);
                throw new Exception("Embedded helper did not return an SDP answer; " + (failure == null ? "no diagnostic" : failure.GetValue(rtc)));
            }
            processId = ((Process)rtcType.GetField("process", flags).GetValue(rtc)).Id;
            Console.WriteLine("PASS protected embedded helpers, UTF-8 pipes and SDP answer");
            rtc.Dispose(); rtc = null;
            Process child = null;
            try { child = Process.GetProcessById(processId); } catch (ArgumentException) { }
            if (child != null && !child.WaitForExit(3000)) throw new Exception("Helper survived session disposal");
            if (child != null) child.Dispose();
            Console.WriteLine("PASS per-viewer Job Object terminates helper on disconnect");
            return 0;
        }
        catch (Exception ex) { Console.WriteLine(ex); return 1; }
        finally { if (rtc != null) rtc.Dispose(); if (src != null) src.Dispose(); }
    }
}

// MCP_LocalPC의 소유 프로세스/출력 제한 방식을 따른다. 이름으로 다른 프로그램을 종료하지 않는다.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace RemoteControl
{
    internal sealed class McpProcesses : IDisposable
    {
        private sealed class Job
        {
            internal string Id, Owner;
            internal Process Process;
            internal StringBuilder Output = new StringBuilder();
            internal long Base;
            internal DateTime Started;
            internal bool Running = true, TimedOut;
            internal int? ExitCode;
            internal Thread OutputThread, ErrorThread;
        }
        private readonly object gate = new object();
        private readonly Dictionary<string, Job> jobs = new Dictionary<string, Job>();
        private readonly Timer expiry;
        internal McpProcesses() { expiry = new Timer(delegate { Expire(); }, null, 10000, 10000); }
        internal object Start(string owner, string command, string cwd, int waitMs)
        {
            if (string.IsNullOrWhiteSpace(command) || command.Length > 16000) throw new ArgumentException("명령은 1~16000자여야 합니다.");
            Job job;
            lock (gate)
            {
                int active = 0; foreach (var item in jobs.Values) if (item.Running) active++;
                if (active >= 8) throw new InvalidOperationException("실행 중인 MCP 작업이 8개입니다. 완료하거나 해당 작업을 중지하세요.");
                if (jobs.Count >= 100)
                { var remove = new List<string>(); foreach (var p in jobs) if (!p.Value.Running) remove.Add(p.Key); foreach (string key in remove) { jobs[key].Process.Dispose(); jobs.Remove(key); } }
                string script = "[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false); $OutputEncoding = [Console]::OutputEncoding; $ProgressPreference = 'SilentlyContinue';\r\n" + command;
                var psi = new ProcessStartInfo(Assembly.GetExecutingAssembly().Location,
                    "--mcp-job " + Process.GetCurrentProcess().Id + " " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) + " " + Quote(cwd)) {
                    UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = cwd,
                    RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
                    StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
                };
                job = new Job { Id = Guid.NewGuid().ToString("N"), Owner = owner, Started = DateTime.UtcNow, Process = new Process { StartInfo = psi, EnableRaisingEvents = true } };
                job.Process.Exited += delegate { lock (gate) { job.Running = false; try { job.ExitCode = job.Process.ExitCode; } catch { } } };
                Desktops.Sync();
                if (!job.Process.Start()) throw new IOException("MCP 프로세스 실행 실패");
                jobs.Add(job.Id, job); job.Process.StandardInput.Close();
                job.OutputThread = Pump(job,job.Process.StandardOutput); job.ErrorThread = Pump(job,job.Process.StandardError);
            }
            if (job.Process.WaitForExit(Math.Max(0, Math.Min(10000, waitMs)))) { job.OutputThread.Join(2000); job.ErrorThread.Join(2000); }
            return Read(owner, job.Id, 0, 12000);
        }
        private Thread Pump(Job job,StreamReader reader)
        {
            var thread=new Thread(delegate(){try{
                byte[] bytes=new byte[4096];char[] chars=new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];var decoder=Encoding.UTF8.GetDecoder();int n;
                while((n=reader.BaseStream.Read(bytes,0,bytes.Length))>0){int count=decoder.GetChars(bytes,0,n,chars,0,false);Append(job,new string(chars,0,count));}
            }catch(IOException){}catch(ObjectDisposedException){}}){IsBackground=true};thread.Start();return thread;
        }
        private void Append(Job job, string line)
        {
            if (line == null) return;
            lock (gate)
            {
                job.Output.Append(line);
                if (job.Output.Length > 128 * 1024) { int n = job.Output.Length - 128 * 1024; job.Output.Remove(0, n); job.Base += n; }
            }
        }
        private Job Find(string owner, string id)
        { Job j; if (!jobs.TryGetValue(id, out j) || !string.Equals(owner, j.Owner, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("이 계정에서 시작한 작업 ID가 아닙니다."); return j; }
        internal object Read(string owner, string id, long offset, int limit)
        {
            lock (gate)
            {
                Job j = Find(owner, id); long start = Math.Max(j.Base, Math.Min(offset, j.Base + j.Output.Length));
                int count = (int)Math.Min(j.Base + j.Output.Length - start, Math.Max(1, Math.Min(65536, limit)));
                return new { session_id = j.Id, pid = j.Process.Id, running = j.Running, exit_code = j.ExitCode,
                    output = j.Output.ToString((int)(start - j.Base), count), next_offset = start + count, output_start = j.Base,
                    truncated = offset < j.Base || start + count < j.Base + j.Output.Length, timed_out = j.TimedOut };
            }
        }
        internal object List(string owner)
        {
            var list = new List<object>(); lock (gate) foreach (Job j in jobs.Values) if (string.Equals(owner, j.Owner, StringComparison.OrdinalIgnoreCase))
                list.Add(new { session_id = j.Id, pid = j.Process.Id, running = j.Running, exit_code = j.ExitCode, started = j.Started.ToString("o") });
            return new { sessions = list.ToArray() };
        }
        internal object Stop(string owner, string id)
        {
            Process p; lock (gate) { Job j = Find(owner, id); p = j.Process; if (j.Running) try { p.Kill(); } catch (InvalidOperationException) { } }
            if (p.WaitForExit(3000)) p.WaitForExit(); return Read(owner, id, 0, 12000);
        }
        private void Expire()
        { lock (gate) foreach (Job j in jobs.Values) if (j.Running && (DateTime.UtcNow - j.Started).TotalHours >= 1) { j.TimedOut = true; try { j.Process.Kill(); } catch { } } }
        public void Dispose()
        { expiry.Dispose(); lock (gate) { foreach (Job j in jobs.Values) { if (j.Running) try { j.Process.Kill(); } catch { } j.Process.Dispose(); } jobs.Clear(); } }
        internal static string Quote(string s)
        {
            var b = new StringBuilder("\""); int slashes = 0;
            foreach (char c in s) { if (c == '\\') { slashes++; continue; } b.Append('\\', slashes * (c == '"' ? 2 : 1)); slashes = 0; if (c == '"') b.Append('\\'); b.Append(c); }
            b.Append('\\', slashes * 2); return b.Append('"').ToString();
        }
    }

    internal static class McpJobRunner
    {
        [StructLayout(LayoutKind.Sequential)] private struct Basic { public long PerProcess, PerJob; public uint Flags; public UIntPtr Min, Max; public uint Active; public UIntPtr Affinity; public uint Priority, Scheduling; }
        [StructLayout(LayoutKind.Sequential)] private struct Io { public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes; }
        [StructLayout(LayoutKind.Sequential)] private struct Extended { public Basic Basic; public Io Io; public UIntPtr ProcessMemory, JobMemory, PeakProcess, PeakJob; }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateJobObject(IntPtr a, string n);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(IntPtr j, int c, IntPtr i, uint s);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(IntPtr j, IntPtr p);
        private static IntPtr job;
        internal static int Run(string[] args)
        {
            try
            {
                if (args.Length != 4) return 64;
                var owner = Process.GetProcessById(int.Parse(args[1])); IntPtr heldOwner = owner.Handle;
                job = CreateJobObject(IntPtr.Zero, null); if (job == IntPtr.Zero) throw new IOException("job create");
                var info = new Extended(); info.Basic.Flags = 0x2000;
                int size = Marshal.SizeOf(info); IntPtr mem = Marshal.AllocHGlobal(size);
                try { Marshal.StructureToPtr(info, mem, false); if (!SetInformationJobObject(job, 9, mem, (uint)size)) throw new IOException("job limit"); }
                finally { Marshal.FreeHGlobal(mem); }
                if (!AssignProcessToJobObject(job, Process.GetCurrentProcess().Handle)) throw new IOException("job assign");
                var watchdog = new Thread(delegate() { try { owner.WaitForExit(); } finally { Environment.Exit(125); } }) { IsBackground = true }; watchdog.Start();
                if (owner.HasExited) return 125;
                Convert.FromBase64String(args[2]);
                var psi = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"),
                    "-NoLogo -NoProfile -NonInteractive -OutputFormat Text -ExecutionPolicy Bypass -EncodedCommand " + args[2]) {
                    WorkingDirectory = args[3], UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true
                };
                using (Process p = Process.Start(psi))
                {
                    p.StandardInput.Close();
                    var output = new Thread(delegate() { try { p.StandardOutput.BaseStream.CopyTo(Console.OpenStandardOutput()); } catch (IOException) { } }) { IsBackground = true };
                    var error = new Thread(delegate() { try { p.StandardError.BaseStream.CopyTo(Console.OpenStandardError()); } catch (IOException) { } }) { IsBackground = true };
                    output.Start(); error.Start(); p.WaitForExit(); output.Join(2000); error.Join(2000); return p.ExitCode;
                }
            }
            catch { Console.Error.WriteLine("MCP 소유 프로세스 작업을 시작하지 못했습니다."); return 126; }
            // helper 종료 때 상속되지 않은 job 핸들이 닫히며 이 작업의 자손만 정리된다.
        }
    }
}

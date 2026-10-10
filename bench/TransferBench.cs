// 파일 전송 실측 (v1.4.0 경로).
//
// 무엇을 알아내려는 것인가 — **병목이 어디인가.**
//   우리 코드인가, 디스크인가, 회선인가.
//   셋 중 어느 것인지에 따라 고칠 곳이 완전히 달라진다. 화면 전송 때도 "압축이
//   느릴 것" 이라 짐작하고 코덱부터 봤다가, 재 보니 병목은 BitBlt 36ms 였다.
//   같은 실수를 파일 전송에서 되풀이하지 않으려고 먼저 잰다.
//
// 이 프로그램이 답해야 할 것
//   1. 지금 코드(Files.Send / Tar.Write / Files.ReceiveMultipart)의 상한은 몇 MB/s 인가
//   2. 그 상한이 회선(랜 1Gbps=125MB/s, Funnel 10~30MB/s)보다 높은가 낮은가
//   3. 읽기와 쓰기를 직렬로 하는 지금 방식이 얼마나 손해인가 (겹치면 얼마나 버는가)
//   4. Chunk 256KB 는 맞는 값인가
//   5. tar 의 Plan() 이 첫 바이트를 얼마나 붙잡아 두는가 ("즉시 나간다" 가 사실인가)
//
// 디스크 읽기는 **캐시를 끄고**(FILE_FLAG_NO_BUFFERING) 잰다.
//   방금 쓴 파일을 그냥 읽으면 전부 page cache 에 있어서 GB/s 가 나온다.
//   그 숫자로 판단하면 "디스크는 넉넉하다" 는 잘못된 결론이 나온다 — 실제 전송은
//   대개 처음 읽는 파일이다.
//
// 빌드:
//   $csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
//   & $csc /nologo /target:exe /platform:x64 /unsafe /optimize+ /langversion:5 `
//         /out:$env:TEMP\tb.exe /r:System.dll /r:System.Core.dll /r:System.Drawing.dll `
//         /r:System.Windows.Forms.dll `
//         bench\TransferBench.cs src\Files.cs src\Tar.cs src\Json.cs src\Desktops.cs
//   & $env:TEMP\tb.exe
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace RemoteControl
{
    internal static class TransferBench
    {
        // 512MB. 이보다 작으면 재는 시간이 짧아 흔들리고, 이보다 크면 준비가 오래 걸린다.
        private const long BigSize = 512L * 1024 * 1024;

        private static string work;
        private static string bigFile;

        // ------------------------------------------------------------------ 도우미

        private static void Head(string s)
        {
            Console.WriteLine();
            Console.WriteLine(s);
            Console.WriteLine(new string('-', 66));
        }

        /// <summary>바이트/초를 사람이 읽는 단위로.</summary>
        private static string Rate(long bytes, double ms)
        {
            if (ms <= 0) return "  (너무 빨라 잴 수 없음)";
            double mbs = bytes / 1048576.0 / (ms / 1000.0);
            return string.Format("{0,9:N0} MB/s", mbs);
        }

        private static void Line(string what, long bytes, double ms)
        {
            Console.WriteLine(string.Format("  {0,-38} {1}   ({2,7:N0} ms)", what, Rate(bytes, ms), ms));
        }

        private static void Ms(string what, double ms, string tail)
        {
            Console.WriteLine(string.Format("  {0,-38} {1,9:N1} ms   {2}", what, ms, tail));
        }

        // ------------------------------------------------------------------ 삼키는 스트림

        /// <summary>받기만 하고 버린다. 소켓을 뺀 "우리 코드만" 의 상한을 재려고.</summary>
        private sealed class Sink : Stream
        {
            internal long Count;

            public override void Write(byte[] b, int o, int c) { Count += c; }
            public override bool CanRead { get { return false; } }
            public override bool CanSeek { get { return false; } }
            public override bool CanWrite { get { return true; } }
            public override long Length { get { return Count; } }
            public override long Position { get { return Count; } set { } }
            public override void Flush() { }
            public override int Read(byte[] b, int o, int c) { return 0; }
            public override long Seek(long o, SeekOrigin s) { return 0; }
            public override void SetLength(long v) { }
        }

        /// <summary>정해진 속도까지만 받아 준다. 느린 회선 흉내.</summary>
        private sealed class Throttle : Stream
        {
            private readonly double bytesPerMs;
            private readonly Stopwatch sw = Stopwatch.StartNew();
            internal long Count;

            internal Throttle(double mbPerSec) { bytesPerMs = mbPerSec * 1048576.0 / 1000.0; }

            public override void Write(byte[] b, int o, int c)
            {
                Count += c;

                // 이만큼 보내려면 회선이 이만큼 걸린다. 앞서 갔으면 기다린다.
                double due = Count / bytesPerMs;
                double waitMs = due - sw.Elapsed.TotalMilliseconds;
                if (waitMs > 1) Thread.Sleep((int)waitMs);
            }

            public override bool CanRead { get { return false; } }
            public override bool CanSeek { get { return false; } }
            public override bool CanWrite { get { return true; } }
            public override long Length { get { return Count; } }
            public override long Position { get { return Count; } set { } }
            public override void Flush() { }
            public override int Read(byte[] b, int o, int c) { return 0; }
            public override long Seek(long o, SeekOrigin s) { return 0; }
            public override void SetLength(long v) { }
        }

        // ------------------------------------------------------------------ 캐시 끄고 읽기

        private const uint GENERIC_READ = 0x80000000;
        private const uint FILE_SHARE_READ = 1;
        private const uint OPEN_EXISTING = 3;
        private const uint FILE_FLAG_NO_BUFFERING = 0x20000000;
        private const uint FILE_FLAG_SEQUENTIAL_SCAN = 0x08000000;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr sa,
                                                 uint disp, uint flags, IntPtr tmpl);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadFile(IntPtr h, IntPtr buf, uint count, out uint read, IntPtr ov);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr h);

        /// <summary>
        /// page cache 를 건너뛰고 진짜 디스크에서 읽어 처리량을 잰다.
        ///
        /// 버퍼 주소와 길이가 섹터 크기의 배수여야 한다. .NET 배열은 정렬을 보장하지
        /// 않으므로 AllocHGlobal 로 직접 잡아 맞춘다. 그냥 byte[] 를 쓰면
        /// ERROR_INVALID_PARAMETER(87) 로 조용히 실패한다.
        /// </summary>
        private static double ColdReadMs(string path, int chunk)
        {
            IntPtr h = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ, IntPtr.Zero, OPEN_EXISTING,
                                   FILE_FLAG_NO_BUFFERING | FILE_FLAG_SEQUENTIAL_SCAN, IntPtr.Zero);

            if (h == new IntPtr(-1))
                throw new Exception("캐시 없이 열지 못했습니다. 오류 " + Marshal.GetLastWin32Error());

            // 4KB 여유를 두고 잡아 4KB 경계로 밀어 올린다.
            IntPtr raw = Marshal.AllocHGlobal(chunk + 4096);
            long addr = raw.ToInt64();
            IntPtr buf = new IntPtr((addr + 4095) & ~4095L);

            Stopwatch sw = Stopwatch.StartNew();
            try
            {
                while (true)
                {
                    uint got;
                    if (!ReadFile(h, buf, (uint)chunk, out got, IntPtr.Zero))
                        throw new Exception("읽기 실패 " + Marshal.GetLastWin32Error());
                    if (got == 0) break;
                }
            }
            finally
            {
                sw.Stop();
                Marshal.FreeHGlobal(raw);
                CloseHandle(h);
            }

            return sw.Elapsed.TotalMilliseconds;
        }

        // ------------------------------------------------------------------ 준비

        private static void MakeBigFile()
        {
            bigFile = Path.Combine(work, "big.bin");

            if (File.Exists(bigFile) && new FileInfo(bigFile).Length == BigSize) return;

            Console.WriteLine("  " + (BigSize / 1048576) + "MB 시험 파일을 만드는 중...");

            byte[] block = new byte[1024 * 1024];
            new Random(7).NextBytes(block);      // 0 으로 채우면 압축·중복제거가 끼어들 수 있다

            using (FileStream fs = new FileStream(bigFile, FileMode.Create, FileAccess.Write,
                                                  FileShare.None, 1024 * 1024, FileOptions.WriteThrough))
            {
                for (long i = 0; i < BigSize; i += block.Length) fs.Write(block, 0, block.Length);
            }
        }

        /// <summary>작은 파일 여러 개를 만든다. tar 에서 파일 여는 비용을 재려고.</summary>
        private static string MakeManyFiles(int count, int sizeEach)
        {
            string dir = Path.Combine(work, "many" + count + "_" + sizeEach);

            if (Directory.Exists(dir) && Directory.GetFiles(dir).Length == count) return dir;

            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.CreateDirectory(dir);

            byte[] block = new byte[sizeEach];
            new Random(9).NextBytes(block);

            for (int i = 0; i < count; i++)
                File.WriteAllBytes(Path.Combine(dir, "파일" + i.ToString("D5") + ".bin"), block);

            return dir;
        }

        // ------------------------------------------------------------------ 루프백 소켓

        /// <summary>127.0.0.1 로 소켓 한 쌍. 받는 쪽은 계속 비운다.</summary>
        private static NetworkStream LoopbackSender(out Thread drain, out TcpListener lis, out TcpClient c)
        {
            lis = new TcpListener(IPAddress.Loopback, 0);
            lis.Start();

            int port = ((IPEndPoint)lis.LocalEndpoint).Port;

            c = new TcpClient();
            c.Connect(IPAddress.Loopback, port);
            c.NoDelay = true;
            c.SendBufferSize = 256 * 1024;

            TcpClient server = lis.AcceptTcpClient();
            server.ReceiveBufferSize = 256 * 1024;

            Thread t = new Thread(delegate ()
            {
                byte[] b = new byte[256 * 1024];
                NetworkStream ns = server.GetStream();
                try { while (ns.Read(b, 0, b.Length) > 0) { } }
                catch { }
                try { server.Close(); } catch { }
            });

            t.IsBackground = true;
            t.Start();

            drain = t;
            return c.GetStream();
        }

        // ------------------------------------------------------------------ 겹쳐 읽기 시제품

        /// <summary>
        /// 지금 코드: 읽고 → 쓰고 → 읽고 → 쓰고. 읽는 동안 회선은 놀고,
        /// 쓰는 동안 디스크는 논다. 둘을 겹치면 느린 쪽 속도까지 낼 수 있다.
        /// 그 차이가 실제로 얼마인지 재려고 만든 시제품.
        /// </summary>
        private static double OverlappedCopyMs(string path, Stream to, int chunk)
        {
            Stopwatch sw = Stopwatch.StartNew();

            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                                                  FileShare.ReadWrite | FileShare.Delete,
                                                  chunk, FileOptions.SequentialScan | FileOptions.Asynchronous))
            {
                byte[] a = new byte[chunk], b = new byte[chunk];

                IAsyncResult pending = fs.BeginRead(a, 0, chunk, null, null);

                while (true)
                {
                    int n = fs.EndRead(pending);
                    if (n <= 0) break;

                    // 다음 조각을 미리 시켜 두고, 그 사이에 방금 것을 내보낸다.
                    IAsyncResult next = fs.BeginRead(b, 0, chunk, null, null);
                    to.Write(a, 0, n);
                    pending = next;

                    byte[] t = a; a = b; b = t;
                }
            }

            sw.Stop();
            return sw.Elapsed.TotalMilliseconds;
        }

        /// <summary>지금 Files.Send 가 하는 것과 같은 직렬 복사. 견주기용.</summary>
        private static double SerialCopyMs(string path, Stream to, int chunk)
        {
            Stopwatch sw = Stopwatch.StartNew();

            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                                                  FileShare.ReadWrite | FileShare.Delete,
                                                  chunk, FileOptions.SequentialScan))
            {
                byte[] buf = new byte[chunk];

                while (true)
                {
                    int n = fs.Read(buf, 0, chunk);
                    if (n <= 0) break;
                    to.Write(buf, 0, n);
                }
            }

            sw.Stop();
            return sw.Elapsed.TotalMilliseconds;
        }

        // ------------------------------------------------------------------ 올리기용 본문

        /// <summary>브라우저가 FormData 로 만드는 본문을 흉내 내되, 메모리에 다 담지 않고 흘려 준다.</summary>
        private sealed class MultipartSource : Stream
        {
            private readonly byte[] head, tail, block;
            private long bodyLeft;
            private int headAt, tailAt;

            internal readonly long Total;

            internal MultipartSource(string boundary, string name, long size)
            {
                head = Encoding.UTF8.GetBytes(
                    "--" + boundary + "\r\n" +
                    "Content-Disposition: form-data; name=\"f\"; filename=\"" + name + "\"\r\n" +
                    "Content-Type: application/octet-stream\r\n\r\n");

                tail = Encoding.UTF8.GetBytes("\r\n--" + boundary + "--\r\n");

                block = new byte[64 * 1024];
                new Random(5).NextBytes(block);

                bodyLeft = size;
                Total = head.Length + size + tail.Length;
            }

            public override int Read(byte[] b, int off, int count)
            {
                if (headAt < head.Length)
                {
                    int n = Math.Min(count, head.Length - headAt);
                    Buffer.BlockCopy(head, headAt, b, off, n);
                    headAt += n;
                    return n;
                }

                if (bodyLeft > 0)
                {
                    int n = (int)Math.Min(Math.Min(count, block.Length), bodyLeft);
                    Buffer.BlockCopy(block, 0, b, off, n);
                    bodyLeft -= n;
                    return n;
                }

                if (tailAt < tail.Length)
                {
                    int n = Math.Min(count, tail.Length - tailAt);
                    Buffer.BlockCopy(tail, tailAt, b, off, n);
                    tailAt += n;
                    return n;
                }

                return 0;
            }

            public override bool CanRead { get { return true; } }
            public override bool CanSeek { get { return false; } }
            public override bool CanWrite { get { return false; } }
            public override long Length { get { return Total; } }
            public override long Position { get { return 0; } set { } }
            public override void Flush() { }
            public override long Seek(long o, SeekOrigin s) { return 0; }
            public override void SetLength(long v) { }
            public override void Write(byte[] b, int o, int c) { }
        }

        // ------------------------------------------------------------------ 본체

        internal static void Main()
        {
            Console.OutputEncoding = Encoding.UTF8;

            work = Path.Combine(Path.GetTempPath(), "rc-transfer-bench");
            Directory.CreateDirectory(work);

            Console.WriteLine("파일 전송 실측  —  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
            Console.WriteLine("작업 폴더: " + work);
            Console.WriteLine("시험 파일: " + (BigSize / 1048576) + "MB, Chunk(지금 코드) = 256KB");

            MakeBigFile();

            // ---------------------------------------------------------- [1] 바탕
            Head("[1] 이 PC 의 상한 — 여기보다 빨라질 수는 없다");

            {
                // 캐시 끄고 읽기. 진짜 디스크 속도.
                double ms = ColdReadMs(bigFile, 256 * 1024);
                Line("디스크 읽기 (캐시 끔, 256KB 씩)", BigSize, ms);

                // 캐시 켜고 읽기. 두 번째로 받는 파일이면 이쪽에 가깝다.
                double ms2 = SerialCopyMs(bigFile, new Sink(), 256 * 1024);
                double ms3 = SerialCopyMs(bigFile, new Sink(), 256 * 1024);
                Line("디스크 읽기 (page cache 더움)", BigSize, Math.Min(ms2, ms3));
            }

            {
                // 소켓만. 파일 없이 메모리에서 바로.
                Thread drain; TcpListener lis; TcpClient c;
                NetworkStream ns = LoopbackSender(out drain, out lis, out c);

                byte[] buf = new byte[256 * 1024];
                Stopwatch sw = Stopwatch.StartNew();

                for (long sent = 0; sent < BigSize; sent += buf.Length) ns.Write(buf, 0, buf.Length);

                sw.Stop();
                try { c.Close(); lis.Stop(); } catch { }

                Line("루프백 소켓 (메모리 → 소켓)", BigSize, sw.Elapsed.TotalMilliseconds);
            }

            Console.WriteLine();
            Console.WriteLine("  참고 — 회선:  랜 1Gbps = 119 MB/s,  100Mbps = 12 MB/s,  Funnel 실측 필요");

            // ---------------------------------------------------------- [2] 파일 하나 받기
            Head("[2] 파일 하나 받기 — Files.Send() 가 실제로 하는 일");

            {
                Sink sink = new Sink();
                Stopwatch sw = Stopwatch.StartNew();
                Files.Send(sink, new string[] { "GET /fs/get HTTP/1.1" }, bigFile, false);
                sw.Stop();

                Line("Files.Send → 삼키는 스트림", sink.Count, sw.Elapsed.TotalMilliseconds);
            }

            {
                Thread drain; TcpListener lis; TcpClient c;
                NetworkStream ns = LoopbackSender(out drain, out lis, out c);

                Stopwatch sw = Stopwatch.StartNew();
                Files.Send(ns, new string[] { "GET /fs/get HTTP/1.1" }, bigFile, false);
                sw.Stop();

                try { c.Close(); lis.Stop(); } catch { }
                Line("Files.Send → 루프백 소켓", BigSize, sw.Elapsed.TotalMilliseconds);
            }

            // ---------------------------------------------------------- [3] 버퍼 크기
            Head("[3] Chunk 는 몇이 맞는가 (지금 256KB) — 캐시 끄고, 디스크만");

            foreach (int kb in new int[] { 32, 64, 128, 256, 512, 1024, 4096 })
            {
                double ms = ColdReadMs(bigFile, kb * 1024);
                Line(kb + " KB 씩", BigSize, ms);
            }

            // ---------------------------------------------------------- [4] 겹치기
            Head("[4] 읽기와 쓰기를 겹치면 — 지금은 직렬이다");

            Console.WriteLine("  회선이 빠를 때 (루프백)");
            {
                Thread d1; TcpListener l1; TcpClient c1;
                NetworkStream n1 = LoopbackSender(out d1, out l1, out c1);
                double a = SerialCopyMs(bigFile, n1, 256 * 1024);
                try { c1.Close(); l1.Stop(); } catch { }

                Thread d2; TcpListener l2; TcpClient c2;
                NetworkStream n2 = LoopbackSender(out d2, out l2, out c2);
                double b = OverlappedCopyMs(bigFile, n2, 256 * 1024);
                try { c2.Close(); l2.Stop(); } catch { }

                Line("  지금 방식 (직렬)", BigSize, a);
                Line("  겹치기 (미리 읽기)", BigSize, b);
                Console.WriteLine(string.Format("    → {0:+0.0;-0.0;0.0}% ", (a / b - 1) * 100));
            }

            Console.WriteLine();
            Console.WriteLine("  회선이 랜 1Gbps 일 때 (119 MB/s 로 묶어 둠)");
            {
                // 캐시가 더워진 뒤라 디스크가 병목이 아닐 수 있다. 그래도 회선 쪽이
                // 훨씬 느리므로 결론은 같다 — 그 사실을 눈으로 확인하려고 잰다.
                double a = SerialCopyMs(bigFile, new Throttle(119), 256 * 1024);
                double b = OverlappedCopyMs(bigFile, new Throttle(119), 256 * 1024);

                Line("  지금 방식 (직렬)", BigSize, a);
                Line("  겹치기 (미리 읽기)", BigSize, b);
                Console.WriteLine(string.Format("    → {0:+0.0;-0.0;0.0}% ", (a / b - 1) * 100));
            }

            // ---------------------------------------------------------- [5] tar
            Head("[5] 폴더 묶어 받기 — Plan() 이 첫 바이트를 얼마나 붙잡는가");

            foreach (int n in new int[] { 100, 1000, 10000 })
            {
                string dir = MakeManyFiles(n, 4096);

                long total;
                Stopwatch sw = Stopwatch.StartNew();
                List<TarEntry> plan = Tar.Plan(dir, out total);
                sw.Stop();

                Ms("Plan() — 파일 " + n + "개", sw.Elapsed.TotalMilliseconds,
                   string.Format("파일당 {0:N0} µs", sw.Elapsed.TotalMilliseconds * 1000 / n));

                Sink sink = new Sink();
                Stopwatch sw2 = Stopwatch.StartNew();
                Tar.Write(sink, plan);
                sw2.Stop();

                Line("  Write() — " + n + "개 × 4KB", sink.Count, sw2.Elapsed.TotalMilliseconds);
                Console.WriteLine(string.Format("    파일 하나 여는 값 {0:N0} µs",
                                  sw2.Elapsed.TotalMilliseconds * 1000 / n));
            }

            Console.WriteLine();
            {
                // 큰 파일 하나짜리 폴더. tar 자체의 처리량 상한.
                string dir = Path.Combine(work, "onebig");
                Directory.CreateDirectory(dir);

                string link = Path.Combine(dir, "big.bin");
                if (!File.Exists(link)) File.Copy(bigFile, link);

                long total;
                List<TarEntry> plan = Tar.Plan(dir, out total);

                Sink sink = new Sink();
                Stopwatch sw = Stopwatch.StartNew();
                Tar.Write(sink, plan);
                sw.Stop();

                Line("Write() — 큰 파일 하나 (" + (BigSize / 1048576) + "MB)", sink.Count, sw.Elapsed.TotalMilliseconds);
            }

            // ---------------------------------------------------------- [6] 올리기
            Head("[6] 올리기 — Files.ReceiveMultipart() 의 경계 찾기가 비싼가");

            {
                const string B = "----WebKitFormBoundary7MA4YWxkTrZu0gW";

                string dir = Path.Combine(work, "up");
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
                Directory.CreateDirectory(dir);

                MultipartSource src = new MultipartSource(B, "big.bin", BigSize);

                Stopwatch sw = Stopwatch.StartNew();
                string why = Files.ReceiveMultipart(src, null, src.Total, dir, "받은것.bin", B);
                sw.Stop();

                if (why != "") Console.WriteLine("  실패: " + why);
                Line("경계 찾기 + 디스크 쓰기", BigSize, sw.Elapsed.TotalMilliseconds);

                // 견주기: 경계 찾기 없이 그냥 쓰기만
                MultipartSource src2 = new MultipartSource(B, "x", BigSize);
                string dir2 = Path.Combine(work, "up2");
                if (Directory.Exists(dir2)) Directory.Delete(dir2, true);
                Directory.CreateDirectory(dir2);

                Stopwatch sw2 = Stopwatch.StartNew();
                Files.Receive(src2, null, src2.Total, dir2, "날것.bin");
                sw2.Stop();

                Line("그냥 쓰기만 (Receive, 경계 없음)", BigSize, sw2.Elapsed.TotalMilliseconds);

                try { Directory.Delete(dir, true); Directory.Delete(dir2, true); } catch { }
            }

            Console.WriteLine();
            Console.WriteLine(new string('=', 66));
            Console.WriteLine("끝. 시험 파일은 " + work + " 에 남겨 둡니다 (다시 재려면 그대로 쓰임).");
        }
    }
}

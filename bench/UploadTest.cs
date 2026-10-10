// 올리기(multipart) 수신 시험.
//
// 확인하는 것:
//   1. 브라우저가 실제로 만드는 모양 그대로 받아 파일이 한 바이트도 안 틀리는가
//   2. 경계 표시가 읽기 조각 사이에 걸쳐도 제대로 찾는가 (여기서 깨지기 쉽다)
//   3. 내용 안에 경계와 비슷한 바이트가 있어도 잘리지 않는가
//   4. 중간에 끊기면 반쯤 받은 파일을 남기지 않는가
//   5. 이름에 경로가 섞여 와도 그 폴더 밖으로 나가지 않는가
using System;
using System.IO;
using System.Text;

namespace RemoteControl
{
    internal static class UploadTest
    {
        private static int fails;

        private static void Ok(string what, bool cond, string extra)
        {
            Console.WriteLine((cond ? "  OK   " : "  실패 ") + what + (cond || extra == null ? "" : "  → " + extra));
            if (!cond) fails++;
        }

        /// <summary>브라우저가 FormData 로 보낼 때 만드는 본문 그대로.</summary>
        private static byte[] Body(string boundary, string name, byte[] content)
        {
            MemoryStream ms = new MemoryStream();

            byte[] head = Encoding.UTF8.GetBytes(
                "--" + boundary + "\r\n" +
                "Content-Disposition: form-data; name=\"f\"; filename=\"" + name + "\"\r\n" +
                "Content-Type: application/octet-stream\r\n\r\n");

            byte[] tail = Encoding.UTF8.GetBytes("\r\n--" + boundary + "--\r\n");

            ms.Write(head, 0, head.Length);
            ms.Write(content, 0, content.Length);
            ms.Write(tail, 0, tail.Length);
            return ms.ToArray();
        }

        /// <summary>
        /// 소켓처럼 조금씩 내주는 흐름. 한 번에 다 주면 경계가 조각에 걸치는 경우를
        /// 시험할 수 없다 - 실제 소켓은 절대 한 번에 다 주지 않는다.
        /// </summary>
        private sealed class Drip : Stream
        {
            private readonly byte[] data;
            private readonly int step;
            private int at;

            internal Drip(byte[] d, int stepSize) { data = d; step = stepSize; }

            public override int Read(byte[] b, int off, int count)
            {
                if (at >= data.Length) return 0;

                int n = Math.Min(Math.Min(step, count), data.Length - at);
                Buffer.BlockCopy(data, at, b, off, n);
                at += n;
                return n;
            }

            public override bool CanRead { get { return true; } }
            public override bool CanSeek { get { return false; } }
            public override bool CanWrite { get { return false; } }
            public override long Length { get { return data.Length; } }
            public override long Position { get { return at; } set { } }
            public override void Flush() { }
            public override long Seek(long o, SeekOrigin s) { return 0; }
            public override void SetLength(long v) { }
            public override void Write(byte[] b, int o, int c) { }
        }

        private static bool Same(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        internal static void Main()
        {
            Console.OutputEncoding = Encoding.UTF8;

            string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "multipart-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);

            const string B = "----WebKitFormBoundary7MA4YWxkTrZu0gW";

            Console.WriteLine("[1] 여러 크기 · 여러 조각 크기로 받아 본다");

            int[] sizes = { 0, 1, 511, 512, 513, 65536, 700000 };
            int[] steps = { 1, 3, 17, 512, 65536 };

            Random rnd = new Random(11);

            foreach (int size in sizes)
            {
                byte[] content = new byte[size];
                rnd.NextBytes(content);

                foreach (int step in steps)
                {
                    // 큰 것을 1바이트씩 주면 너무 오래 걸린다. 뜻은 이미 작은 것에서 확인된다.
                    if (size > 70000 && step < 512) continue;

                    byte[] body = Body(B, "시험.bin", content);
                    string name = "s" + size + "_p" + step + ".bin";

                    string why = Files.ReceiveMultipart(new Drip(body, step), null, body.Length, dir, name, B);

                    string got = Path.Combine(dir, name);
                    bool ok = why == "" && File.Exists(got) && Same(File.ReadAllBytes(got), content);

                    if (!ok)
                        Ok(size + "바이트 / " + step + "씩 읽기", false,
                           why != "" ? why : (File.Exists(got) ? "내용이 다르다 (" + new FileInfo(got).Length + "바이트)" : "파일이 없다"));
                }
            }

            Ok("크기 " + sizes.Length + "종 × 조각 " + steps.Length + "종 전부 일치", fails == 0, null);

            Console.WriteLine();
            Console.WriteLine("[2] 머리글을 읽을 때 딸려 온 몫(preRead)을 먼저 쓴다");
            {
                byte[] content = new byte[40000];
                rnd.NextBytes(content);

                byte[] body = Body(B, "x.bin", content);

                // 실제로 흔한 상황: 첫 읽기에 머리글과 본문 앞부분이 같이 딸려 온다
                int cut = 9000;
                byte[] pre = new byte[cut];
                Buffer.BlockCopy(body, 0, pre, 0, cut);

                byte[] rest = new byte[body.Length - cut];
                Buffer.BlockCopy(body, cut, rest, 0, rest.Length);

                string why = Files.ReceiveMultipart(new Drip(rest, 1000), pre, body.Length, dir, "pre.bin", B);
                string got = Path.Combine(dir, "pre.bin");

                Ok("딸려 온 9000바이트 + 나머지", why == "" && File.Exists(got) && Same(File.ReadAllBytes(got), content), why);
            }

            Console.WriteLine();
            Console.WriteLine("[3] 내용 안에 경계와 닮은 바이트가 있어도 안 잘린다");
            {
                // 진짜 경계는 앞에 \r\n 이 붙는다. 그것만 빠진 가짜를 내용에 심는다.
                byte[] fake = Encoding.ASCII.GetBytes("--" + B + " 이건 내용입니다\r\n");

                MemoryStream ms = new MemoryStream();
                ms.Write(new byte[1000], 0, 1000);
                ms.Write(fake, 0, fake.Length);
                ms.Write(new byte[1000], 0, 1000);
                byte[] content = ms.ToArray();

                byte[] body = Body(B, "y.bin", content);
                string why = Files.ReceiveMultipart(new Drip(body, 7), null, body.Length, dir, "fake.bin", B);

                string got = Path.Combine(dir, "fake.bin");
                Ok("가짜 경계가 든 " + content.Length + "바이트",
                   why == "" && File.Exists(got) && Same(File.ReadAllBytes(got), content), why);
            }

            Console.WriteLine();
            Console.WriteLine("[4] 중간에 끊기면 반쯤 받은 파일을 안 남긴다");
            {
                byte[] content = new byte[50000];
                rnd.NextBytes(content);

                byte[] body = Body(B, "z.bin", content);

                // 회선이 끊긴 척: 실제로 오는 것은 절반뿐인데 Content-Length 는 전부라고 한다
                byte[] half = new byte[body.Length / 2];
                Buffer.BlockCopy(body, 0, half, 0, half.Length);

                string why = Files.ReceiveMultipart(new Drip(half, 4096), null, body.Length, dir, "cut.bin", B);

                Ok("실패로 알린다  \"" + why + "\"", why != "", null);
                Ok("온전한 이름의 파일이 안 남는다", !File.Exists(Path.Combine(dir, "cut.bin")), null);
                Ok("임시 파일도 안 남는다", !File.Exists(Path.Combine(dir, "cut.bin.올리는중")), null);
            }

            Console.WriteLine();
            Console.WriteLine("[5] 이름에 경로가 섞여 와도 폴더 밖으로 안 나간다");
            {
                byte[] content = Encoding.UTF8.GetBytes("나쁜 파일");
                byte[] body = Body(B, "x", content);

                string why = Files.ReceiveMultipart(new Drip(body, 64), null, body.Length, dir,
                                                    "..\\..\\Windows\\System32\\나쁜것.txt", B);

                string escaped = Path.Combine(Path.GetTempPath(), "..", "..", "Windows", "System32", "나쁜것.txt");

                Ok("받기는 성공한다", why == "", why);
                Ok("이름만 남고 폴더 안에 들어간다", File.Exists(Path.Combine(dir, "나쁜것.txt")), null);
                Ok("System32 로 안 나간다", !File.Exists(Path.GetFullPath(escaped)), null);
            }

            Console.WriteLine();
            Console.WriteLine("[6] 같은 이름을 또 올리면 비켜서 저장한다");
            {
                byte[] content = Encoding.UTF8.GetBytes("두 번째");
                byte[] body = Body(B, "x", content);

                Files.ReceiveMultipart(new Drip(body, 64), null, body.Length, dir, "나쁜것.txt", B);

                Ok("나쁜것 (2).txt 로 저장됐다", File.Exists(Path.Combine(dir, "나쁜것 (2).txt")), null);
                Ok("먼저 것이 안 덮였다",
                   File.ReadAllText(Path.Combine(dir, "나쁜것.txt"), Encoding.UTF8) == "나쁜 파일", null);
            }

            Console.WriteLine();
            Console.WriteLine("[7] Content-Type 에서 boundary 를 꺼낸다");
            {
                Ok("따옴표 없음", Files.Boundary("multipart/form-data; boundary=" + B) == B, null);
                Ok("따옴표 있음", Files.Boundary("multipart/form-data; boundary=\"" + B + "\"") == B, null);
                Ok("뒤에 것이 더 붙어도", Files.Boundary("multipart/form-data; boundary=" + B + "; charset=utf-8") == B, null);
                Ok("multipart 가 아니면 빈 값", Files.Boundary("application/octet-stream") == "", null);
                Ok("없으면 빈 값", Files.Boundary(null) == "", null);
            }

            Console.WriteLine();
            Console.WriteLine(fails == 0 ? "전부 통과" : fails + "건 실패");
            Environment.Exit(fails == 0 ? 0 : 1);
        }
    }
}

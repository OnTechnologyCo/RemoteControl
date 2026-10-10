// 폴더를 tar 로 묶어 흘려보낸다. 압축하지 않는다.
//
// 왜 zip 이 아니라 tar 인가.
//   zip 은 파일마다 끝에 목록(central directory)을 붙여야 해서, 다 만들기 전에는
//   전체 크기를 모른다. 크기를 모르면 브라우저가 진행률을 못 그리고 "얼마나 남았는지"
//   가 안 보인다. 옆 프로젝트(FileSharer)는 그래서 zip 을 통째로 만들어 임시 폴더에
//   캐시해 두고 그 파일을 내보냈다 - 디스크를 두 배로 쓰고, 큰 폴더는 첫 바이트가
//   나가기까지 몇 분이 걸린다.
//
//   tar 는 다르다. 머리글이 파일 앞에 붙고 크기가 정해져 있어서 **보내기 전에 전체
//   바이트 수를 정확히 셀 수 있다.** 임시 파일도, 기다림도 없다. 첫 바이트가 즉시 나간다.
//   압축을 안 하니 CPU 도 안 쓴다 - 사내망에서는 압축이 오히려 느리다.
//
// 크기를 미리 셈해 두었으므로 **중간에 파일이 바뀌어도 셈한 만큼만 보낸다.**
//   짧아졌으면 0 으로 채우고, 길어졌으면 잘라 낸다. 안 그러면 Content-Length 와
//   실제로 보낸 양이 어긋나 브라우저가 끝나지 않는 다운로드를 붙잡는다.
//   (FileSharer 에서 "공유한 뒤 파일이 바뀌면 다운로드가 안 끝난다" 로 겪었다)
//
// Range 이어받기는 지원하지 않는다.
//   흘려보내며 만드는 것이라 임의 위치로 건너뛰려면 어느 파일의 몇 번째인지 되짚어야
//   하는데, 그 사이 파일이 바뀌면 위치가 어긋나 내용이 섞인 tar 가 나간다.
//   조용히 깨진 파일을 주느니 처음부터 다시 받는 편이 낫다.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace RemoteControl
{
    /// <summary>tar 에 담을 파일 하나.</summary>
    internal sealed class TarEntry
    {
        internal string Path;        // 실제 파일 위치
        internal string Name;        // tar 안에서의 이름 (상대 경로, 구분자는 /)
        internal long Size;
        internal DateTime Mtime;

        // 미리 만들어 둔 머리글. 크기를 셀 때 한 번 만들고 보낼 때 그대로 쓴다.
        internal byte[] Header;
    }

    internal static class Tar
    {
        private const int Block = 512;

        /// <summary>
        /// 폴더를 훑어 담을 목록을 만들고 머리글까지 미리 짜 둔다.
        /// 여기서 잰 크기가 곧 Content-Length 가 되므로, 이 뒤로는 목록도 크기도 바뀌지 않는다.
        /// </summary>
        internal static List<TarEntry> Plan(string root, out long total)
        {
            List<TarEntry> list = new List<TarEntry>();

            string full = System.IO.Path.GetFullPath(root);
            Add(full, list);

            return Finish(list, out total);
        }

        /// <summary>
        /// 고른 것 여럿을 하나로 묶는다. 파일과 폴더가 섞여 있어도 된다.
        ///
        /// 푼 뒤에 고른 그대로 나와야 한다. 파일은 파일 이름으로, 폴더는 폴더 이름을
        /// 앞에 달고 그 안의 것들이 딸려 온다. 고른 것들의 공통 부모는 담지 않는다 -
        /// "D:\작업\2026\" 에서 셋을 골랐는데 풀었더니 작업/2026 부터 나오면 성가시다.
        /// </summary>
        internal static List<TarEntry> PlanMany(List<string> paths, out long total)
        {
            List<TarEntry> list = new List<TarEntry>();

            foreach (string p in paths)
            {
                string full;
                try { full = System.IO.Path.GetFullPath(p); }
                catch { continue; }

                Add(full, list);
            }

            return Finish(list, out total);
        }

        /// <summary>파일이면 한 개, 폴더면 그 아래 전부.</summary>
        private static void Add(string full, List<TarEntry> list)
        {
            try
            {
                if (File.Exists(full))
                {
                    FileInfo fi = new FileInfo(full);

                    TarEntry e = new TarEntry();
                    e.Path = full;
                    e.Name = fi.Name;
                    e.Size = fi.Length;
                    e.Mtime = fi.LastWriteTimeUtc;
                    list.Add(e);
                    return;
                }

                if (!Directory.Exists(full)) return;

                string baseName = System.IO.Path.GetFileName(full.TrimEnd('\\', '/'));

                // 드라이브 통째("D:\")면 이름이 비어 버린다. 받는 쪽에서 풀었을 때
                // 파일이 현재 폴더에 쏟아지지 않도록 이름을 하나 만들어 준다.
                if (string.IsNullOrEmpty(baseName))
                    baseName = full.Replace(":", "").Replace("\\", "").Replace("/", "");
                if (string.IsNullOrEmpty(baseName)) baseName = "files";

                Walk(full, baseName, list, 0);
            }
            catch { }
        }

        private static List<TarEntry> Finish(List<TarEntry> list, out long total)
        {
            total = 0;

            foreach (TarEntry e in list)
            {
                e.Header = MakeHeader(e);
                total += e.Header.Length;
                total += Pad(e.Size);
            }

            total += Block * 2;              // 끝을 알리는 빈 블록 두 개
            return list;
        }

        /// <summary>
        /// 폴더를 내려가며 파일을 모은다.
        ///
        /// 빈 폴더는 담지 않는다. 폴더 항목까지 넣으려면 머리글이 늘어나는데, 받는 쪽은
        /// 어차피 파일 경로를 보고 폴더를 만든다. 대신 **읽을 수 없는 폴더는 조용히 건너뛴다** -
        /// 전체 드라이브를 받을 때 시스템 폴더 하나 때문에 통째로 실패하면 안 된다.
        /// </summary>
        private static void Walk(string dir, string prefix, List<TarEntry> list, int depth)
        {
            if (depth > 64) return;          // 재분석 지점(junction)이 고리를 만들면 끝없이 내려간다

            string[] files;
            try { files = Directory.GetFiles(dir); }
            catch { return; }

            foreach (string f in files)
            {
                try
                {
                    FileInfo fi = new FileInfo(f);
                    if ((fi.Attributes & FileAttributes.ReparsePoint) != 0) continue;

                    TarEntry e = new TarEntry();
                    e.Path = f;
                    e.Name = prefix + "/" + fi.Name;
                    e.Size = fi.Length;
                    e.Mtime = fi.LastWriteTimeUtc;
                    list.Add(e);
                }
                catch { }
            }

            string[] subs;
            try { subs = Directory.GetDirectories(dir); }
            catch { return; }

            foreach (string s in subs)
            {
                try
                {
                    DirectoryInfo di = new DirectoryInfo(s);

                    // 바로가기(junction/symlink)를 따라가면 같은 곳을 무한히 돈다.
                    // C:\Documents and Settings 가 대표적이다.
                    if ((di.Attributes & FileAttributes.ReparsePoint) != 0) continue;

                    Walk(s, prefix + "/" + di.Name, list, depth + 1);
                }
                catch { }
            }
        }

        private static long Pad(long n)
        {
            long r = n % Block;
            return r == 0 ? n : n + (Block - r);
        }

        // ---------------------------------------------------------------- 머리글

        /// <summary>
        /// USTAR 머리글. 이름이 길거나 한글이면 앞에 PAX 확장 머리글을 하나 더 붙인다.
        ///
        /// USTAR 의 이름 칸은 100글자뿐이고, 거기 넣는 글자의 인코딩이 규격에 없다.
        /// 한글 이름을 그냥 밀어 넣으면 푸는 프로그램마다 다르게 깨진다.
        /// PAX 는 "UTF-8 로 적는다" 가 규격에 박혀 있어 안전하다.
        /// 7-Zip, 반디집, 윈도우 tar, GNU tar 모두 읽는다.
        /// </summary>
        private static byte[] MakeHeader(TarEntry e)
        {
            bool longName = Encoding.UTF8.GetByteCount(e.Name) > 100 || !IsAscii(e.Name);
            bool bigFile = e.Size > 8589934591L;      // 8진수 11자리로 담기는 한계

            MemoryStream ms = new MemoryStream();

            if (longName || bigFile)
            {
                StringBuilder recs = new StringBuilder();
                if (longName) recs.Append(PaxRecord("path", e.Name));
                if (bigFile) recs.Append(PaxRecord("size", e.Size.ToString()));

                byte[] data = Encoding.UTF8.GetBytes(recs.ToString());

                // PAX 머리글 자신의 이름은 아무거나 되지만, 옛 프로그램이 이걸 진짜 파일로
                // 풀어 버리는 일이 있어 관례대로 PaxHeaders 아래에 둔다.
                byte[] h = Ustar("PaxHeaders/" + Math.Abs(e.Name.GetHashCode() % 100000),
                                 data.Length, e.Mtime, 'x');

                ms.Write(h, 0, h.Length);
                ms.Write(data, 0, data.Length);

                int rest = (int)(Pad(data.Length) - data.Length);
                if (rest > 0) ms.Write(new byte[rest], 0, rest);
            }

            // 본 머리글. PAX 를 붙였으면 여기 값은 예비용이라 잘려도 된다.
            byte[] main = Ustar(Ascii(e.Name), bigFile ? 0 : e.Size, e.Mtime, '0');
            ms.Write(main, 0, main.Length);

            return ms.ToArray();
        }

        private static bool IsAscii(string s)
        {
            foreach (char c in s) if (c > 126 || c < 32) return false;
            return true;
        }

        /// <summary>PAX 를 못 읽는 옛 프로그램용 예비 이름. 뜻이 통할 정도만 남긴다.</summary>
        private static string Ascii(string name)
        {
            StringBuilder sb = new StringBuilder(name.Length);
            foreach (char c in name) sb.Append(c > 126 || c < 32 ? '_' : c);

            string s = sb.ToString();
            return s.Length <= 100 ? s : s.Substring(s.Length - 100);
        }

        /// <summary>
        /// PAX 기록 한 줄: "길이 키=값\n".
        /// 길이에는 그 길이 숫자 자체도 들어간다. 자릿수가 늘면 길이도 늘어나므로 맞을 때까지 돈다.
        /// </summary>
        private static string PaxRecord(string key, string value)
        {
            int len = Encoding.UTF8.GetByteCount(key) + Encoding.UTF8.GetByteCount(value) + 3;  // ' ' '=' '\n'

            int guess = len + 1;
            while (true)
            {
                int digits = guess.ToString().Length;
                int real = len + digits;
                if (real == guess) break;
                guess = real;
            }

            return guess + " " + key + "=" + value + "\n";
        }

        private static byte[] Ustar(string name, long size, DateTime mtime, char type)
        {
            byte[] h = new byte[Block];
            for (int i = 0; i < Block; i++) h[i] = 0;

            PutStr(h, 0, 100, name);
            PutOct(h, 100, 8, 0644);                    // mode
            PutOct(h, 108, 8, 0);                       // uid
            PutOct(h, 116, 8, 0);                       // gid
            PutOct(h, 124, 12, size);
            PutOct(h, 136, 12, Unix(mtime));

            h[156] = (byte)type;

            PutStr(h, 257, 6, "ustar");
            h[263] = (byte)'0'; h[264] = (byte)'0';     // version "00"

            // 검사합은 그 칸을 공백으로 친 채 셈한다. 셈한 뒤에 8진수로 적는다.
            for (int i = 148; i < 156; i++) h[i] = (byte)' ';

            int sum = 0;
            for (int i = 0; i < Block; i++) sum += h[i];

            string oct = Convert.ToString(sum, 8).PadLeft(6, '0');
            for (int i = 0; i < 6; i++) h[148 + i] = (byte)oct[i];
            h[154] = 0;
            h[155] = (byte)' ';

            return h;
        }

        private static long Unix(DateTime utc)
        {
            long t = (long)(utc - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
            return t < 0 ? 0 : t;
        }

        private static void PutStr(byte[] h, int off, int len, string s)
        {
            byte[] b = Encoding.UTF8.GetBytes(s ?? "");
            int n = Math.Min(b.Length, len - 1);        // 끝의 NUL 자리를 남긴다
            Buffer.BlockCopy(b, 0, h, off, n);
        }

        private static void PutOct(byte[] h, int off, int len, long v)
        {
            string s = Convert.ToString(v, 8).PadLeft(len - 1, '0');
            if (s.Length > len - 1) s = s.Substring(s.Length - (len - 1));

            for (int i = 0; i < len - 1; i++) h[off + i] = (byte)s[i];
            h[off + len - 1] = 0;
        }

        // ---------------------------------------------------------------- 내보내기

        /// <summary>
        /// 셈해 둔 목록을 그대로 흘려보낸다.
        /// 반드시 Plan() 이 돌려준 목록이어야 한다 - 크기가 그것에 맞춰 이미 알려졌기 때문이다.
        /// </summary>
        internal static void Write(Stream to, List<TarEntry> list)
        {
            byte[] pad = new byte[Block];
            byte[] buf = new byte[256 * 1024];

            foreach (TarEntry e in list)
            {
                to.Write(e.Header, 0, e.Header.Length);

                long left = e.Size;

                try
                {
                    using (FileStream fs = new FileStream(e.Path, FileMode.Open, FileAccess.Read,
                                                          FileShare.ReadWrite | FileShare.Delete,
                                                          buf.Length, FileOptions.SequentialScan))
                    {
                        while (left > 0)
                        {
                            int want = (int)Math.Min(buf.Length, left);
                            int n = fs.Read(buf, 0, want);
                            if (n <= 0) break;

                            to.Write(buf, 0, n);
                            left -= n;
                        }
                    }
                }
                catch { }

                // 셈한 만큼은 반드시 채운다. 파일이 짧아졌거나 읽다 실패했어도
                // 여기서 0 으로 메워야 Content-Length 와 어긋나지 않는다.
                while (left > 0)
                {
                    int n = (int)Math.Min(pad.Length, left);
                    to.Write(pad, 0, n);
                    left -= n;
                }

                int tail = (int)(Pad(e.Size) - e.Size);
                if (tail > 0) to.Write(pad, 0, tail);
            }

            to.Write(pad, 0, Block);
            to.Write(pad, 0, Block);
            to.Flush();
        }
    }
}

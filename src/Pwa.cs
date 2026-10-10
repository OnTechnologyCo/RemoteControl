// 앱으로 설치되게 하는 것들 (PWA).
//
// 브라우저 주소창과 탭을 걷어 내면 화면이 그만큼 넓어진다. 휴대폰에서는 이게 크다 -
// 1920 짜리 화면을 6인치에 욱여넣는 판에 위아래로 100px 씩 뺏기면 더 못 쓴다.
// 홈 화면에서 바로 열리는 것도 덤이 아니다. 주소를 매번 찾아 들어가는 것보다 낫다.
//
// 아이콘은 exe 안의 .ico 에서 그때그때 만들어 낸다. png 를 따로 넣지 않는 이유는
// 아이콘을 바꿀 때 고칠 곳이 둘로 늘고, 한쪽만 고치면 창과 앱의 그림이 달라지기 때문이다.
//
// 이 파일이 내주는 것은 로그인 없이 나간다. 브라우저가 manifest 와 서비스 워커를
// 가져갈 때 쿠키를 붙이지 않기 때문이다(규격이 그렇다). 대신 여기에는 비밀이 없다 -
// 프로그램 이름과 아이콘뿐이고, 그건 로그인 화면에 이미 크게 적혀 있다.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;

namespace RemoteControl
{
    internal static class Pwa
    {
        internal const string Manifest =
            "{\n" +
            "  \"name\": \"원격제어\",\n" +
            "  \"short_name\": \"원격제어\",\n" +
            "  \"description\": \"브라우저로 PC 를 보고 조작합니다.\",\n" +
            "  \"lang\": \"ko\",\n" +
            "  \"start_url\": \"/\",\n" +
            "  \"scope\": \"/\",\n" +
            "  \"display\": \"standalone\",\n" +
            "  \"orientation\": \"any\",\n" +
            "  \"background_color\": \"#0b0f18\",\n" +
            "  \"theme_color\": \"#0b0f18\",\n" +
            "  \"icons\": [\n" +
            "    { \"src\": \"/icon-192.png\", \"sizes\": \"192x192\", \"type\": \"image/png\" },\n" +
            "    { \"src\": \"/icon-512.png\", \"sizes\": \"512x512\", \"type\": \"image/png\" },\n" +
            "    { \"src\": \"/icon-mask.png\", \"sizes\": \"512x512\", \"type\": \"image/png\", \"purpose\": \"maskable\" }\n" +
            "  ]\n" +
            "}\n";

        /// <summary>
        /// 서비스 워커.
        ///
        /// 일부러 아무것도 캐시하지 않는다. 원격제어는 화면을 실시간으로 받는 것이라
        /// 회선이 끊기면 캐시가 있어도 할 수 있는 일이 없다. 반대로 캐시가 남으면
        /// 새 판을 올려도 옛 viewer.js 가 계속 돌아, 고쳤는데 안 고쳐지는 고장이 된다.
        /// 그 고장은 원인을 찾는 데만 하루가 걸린다.
        ///
        /// fetch 를 받아만 두고 아무 대답도 하지 않으면 브라우저가 평소대로 망에서
        /// 가져온다. 설치 조건은 채우면서 끼어들지는 않는 상태다.
        /// </summary>
        internal const string ServiceWorker =
            "// 원격제어 서비스 워커 - 설치되게만 하고 아무것도 캐시하지 않는다.\n" +
            "self.addEventListener('install', function () { self.skipWaiting(); });\n" +
            "self.addEventListener('activate', function (e) { e.waitUntil(self.clients.claim()); });\n" +
            "self.addEventListener('fetch', function () { });\n";

        // 아이콘은 만드는 데 시간이 든다. 한 번 만들고 들고 있는다.
        private static readonly Dictionary<string, byte[]> cache = new Dictionary<string, byte[]>();

        /// <summary>
        /// exe 안의 .ico 에서 png 를 만든다.
        ///
        /// maskable 은 안드로이드용이다. 그쪽은 아이콘을 동그랗게·모나게 잘라 내기 때문에
        /// 가장자리까지 그림이 차 있으면 잘려 나간다. 배경을 깔고 가운데 66% 안에만 그린다.
        /// </summary>
        internal static byte[] Icon(int size, bool maskable)
        {
            string key = size + (maskable ? "m" : "");

            lock (cache)
            {
                byte[] have;
                if (cache.TryGetValue(key, out have)) return have;

                byte[] made = Render(size, maskable);
                cache[key] = made;
                return made;
            }
        }

        private static byte[] Render(int size, bool maskable)
        {
            try
            {
                using (Image src = Largest())
                {
                    if (src == null) return new byte[0];

                    using (Bitmap dst = new Bitmap(size, size, PixelFormat.Format32bppArgb))
                    using (Graphics g = Graphics.FromImage(dst))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.SmoothingMode = SmoothingMode.HighQuality;
                        g.CompositingQuality = CompositingQuality.HighQuality;

                        if (maskable)
                        {
                            g.Clear(Color.FromArgb(11, 15, 24));      // #0b0f18 - 앱 바탕색

                            int inner = (int)(size * 0.66);
                            int off = (size - inner) / 2;
                            g.DrawImage(src, off, off, inner, inner);
                        }
                        else g.DrawImage(src, 0, 0, size, size);

                        using (MemoryStream ms = new MemoryStream())
                        {
                            dst.Save(ms, ImageFormat.Png);
                            return ms.ToArray();
                        }
                    }
                }
            }
            catch { return new byte[0]; }
        }

        /// <summary>
        /// .ico 안에서 가장 큰 그림을 꺼낸다.
        ///
        /// Icon.ToBitmap() 을 쓰면 안 된다. 요즘 .ico 는 큰 항목을 PNG 로 압축해 담는데
        /// (이 파일도 여덟 항목 전부 PNG 다), .NET Framework 의 Icon 은 그걸 옛 BMP 로
        /// 알고 읽다가 ArgumentOutOfRangeException 으로 터진다. 실측으로 확인했다 -
        /// 128x128 까지는 Icon 객체가 만들어지는데 ToBitmap() 에서 죽는다.
        /// 예외를 삼키고 있으면 아이콘이 조용히 0바이트로 나가고, 휴대폰 홈 화면에
        /// 빈 사각형이 박힌다. 원인을 찾기 어려운 종류의 고장이다.
        ///
        /// 그래서 디렉터리를 직접 읽어 PNG 조각을 그대로 꺼낸다. PNG 는 Image 가 잘 읽는다.
        /// </summary>
        private static Image Largest()
        {
            byte[] ico;

            using (Stream st = Assembly.GetExecutingAssembly().GetManifestResourceStream("remotecontrol.ico"))
            {
                if (st == null) return null;

                using (MemoryStream ms = new MemoryStream())
                {
                    byte[] buf = new byte[8192];
                    int n;
                    while ((n = st.Read(buf, 0, buf.Length)) > 0) ms.Write(buf, 0, n);
                    ico = ms.ToArray();
                }
            }

            if (ico.Length < 6 || ico[0] != 0 || ico[1] != 0 || ico[2] != 1) return null;

            int count = ico[4] | (ico[5] << 8);
            int bestW = 0, bestOff = 0, bestLen = 0;

            for (int i = 0; i < count; i++)
            {
                int e = 6 + i * 16;
                if (e + 16 > ico.Length) break;

                int w = ico[e];
                if (w == 0) w = 256;                 // 0 은 256 을 뜻한다 (한 바이트에 안 들어가서)

                int len = BitConverter.ToInt32(ico, e + 8);
                int off = BitConverter.ToInt32(ico, e + 12);

                if (off < 0 || len <= 0 || off + len > ico.Length) continue;
                if (w <= bestW) continue;

                bestW = w; bestOff = off; bestLen = len;
            }

            if (bestW == 0) return null;

            bool png = bestLen > 8 && ico[bestOff] == 0x89 && ico[bestOff + 1] == 0x50 &&
                       ico[bestOff + 2] == 0x4E && ico[bestOff + 3] == 0x47;

            if (png)
            {
                // 스트림을 살려 둬야 한다. Image 는 자기가 만들어진 스트림을 계속 붙들고 있어서
                // using 으로 닫으면 나중에 그릴 때 "일반적인 GDI+ 오류" 가 난다.
                return Image.FromStream(new MemoryStream(ico, bestOff, bestLen));
            }

            // 옛 방식(BMP)으로 담긴 .ico. 이건 Icon 이 제대로 읽는다.
            using (MemoryStream ms = new MemoryStream(ico))
            using (Icon ic = new Icon(ms, bestW, bestW))
                return ic.ToBitmap();
        }
    }
}

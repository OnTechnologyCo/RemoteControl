// 실제 ScreenSource의 4K 전체 지문 + JPEG 비용. ControlTest의 합성 DesktopDup만 사용한다.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
namespace RemoteControl
{
    internal static class FrameEncodeBench
    {
        public static int Main(string[] args)
        {
            using (var source = new ScreenSource(new Rectangle(0, 0, 3840, 2160)))
            {
                source.TileSize = args.Length > 1 ? int.Parse(args[1]) : 128;
                source.Grab(); // 합성 배경 생성 비용과 첫 JIT를 제외한다.
                var samples = new List<double>(); List<TilePatch> patches = null;
                for (int n = 0; n < 30; n++)
                {
                    source.InvalidateAll(); var clock = Stopwatch.StartNew(); patches = source.Grab();
                    samples.Add(clock.Elapsed.TotalMilliseconds);
                }
                var tiles = new LatestTiles(); tiles.Merge(patches); byte[] packet = tiles.Take(8 * 1024 * 1024);
                if (tiles.Count != 0) throw new Exception("Fixture packet did not contain all 4K tiles");
                File.WriteAllBytes(Path.Combine(args[0], "frame.bin"), packet);
                File.WriteAllText(Path.Combine(args[0], "frame.json"), "{\"tile\":" + source.TileSize + ",\"cols\":" + source.Cols + ",\"rows\":" + source.Rows + "}");
                double total = 0; foreach (double ms in samples) total += ms; samples.Sort();
                Console.WriteLine("4K full hash + JPEG: mean={0:F1}ms p95={1:F1}ms, tiles={2}, payload={3:F0}KiB",
                    total / samples.Count, samples[28], patches.Count, packet.Length / 1024.0);
                var original = (Bitmap)typeof(ScreenSource).GetField("frame", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(source);
                ImageCodecInfo codec = null; foreach (var c in ImageCodecInfo.GetImageEncoders()) if (c.FormatID == ImageFormat.Jpeg.Guid) codec = c;
                using (var parameters = new EncoderParameters(1))
                {
                    parameters.Param[0] = new EncoderParameter(Encoder.Quality, source.Quality);
                    foreach (var patch in patches)
                    {
                        int x = patch.Index % source.Cols * source.TileSize, y = patch.Index / source.Cols * source.TileSize;
                        var rect = new Rectangle(x, y, Math.Min(source.TileSize, 3840 - x), Math.Min(source.TileSize, 2160 - y));
                        using (var copy = original.Clone(rect, PixelFormat.Format32bppRgb))
                        using (var bytes = new MemoryStream())
                        {
                            copy.Save(bytes, codec, parameters); bytes.Position = 0;
                            using (var actualBytes = new MemoryStream(patch.Jpeg))
                            using (var a = new Bitmap(bytes)) using (var b = new Bitmap(actualBytes))
                            {
                                if (a.Width != b.Width || a.Height != b.Height) throw new Exception("Tile dimensions differ");
                                var area = new Rectangle(0, 0, a.Width, a.Height);
                                var left = a.LockBits(area, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                                var right = b.LockBits(area, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                                try
                                {
                                    var l = new byte[left.Stride * a.Height]; var r = new byte[right.Stride * b.Height];
                                    Marshal.Copy(left.Scan0, l, 0, l.Length); Marshal.Copy(right.Scan0, r, 0, r.Length);
                                    for (int py = 0; py < a.Height; py++) for (int px = 0; px < a.Width * 4; px++)
                                        if (l[py * left.Stride + px] != r[py * right.Stride + px]) throw new Exception("JPEG pixel mismatch at tile " + patch.Index);
                                }
                                finally { a.UnlockBits(left); b.UnlockBits(right); }
                            }
                        }
                    }
                }
                Console.WriteLine("PASS every decoded 4K pixel equals independently copied/encoded reference tiles");
            }
            return 0;
        }
    }
}

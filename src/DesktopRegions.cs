using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;

namespace RemoteControl
{
    // A move's destination is read from the new GPU texture. Copying old CPU pixels
    // would require ordered moves and a separate previous-frame buffer.
    internal static class DesktopRegions
    {
        internal static bool Append(IntPtr memory, uint bytes, int capacity, bool moves,
                                    int width, int height, List<Rectangle> result)
        {
            int stride = moves ? 24 : 16, offset = moves ? 8 : 0;
            if (bytes > capacity || bytes % stride != 0) return false;
            for (int i = 0; i < bytes; i += stride)
            {
                int l = Marshal.ReadInt32(memory, i + offset), t = Marshal.ReadInt32(memory, i + offset + 4);
                int r = Marshal.ReadInt32(memory, i + offset + 8), b = Marshal.ReadInt32(memory, i + offset + 12);
                if (l < 0 || t < 0 || r <= l || b <= t || r > width || b > height) return false;
                result.Add(Rectangle.FromLTRB(l, t, r, b));
            }
            return true;
        }
    }
}

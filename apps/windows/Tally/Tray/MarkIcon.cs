using System.Runtime.InteropServices;

namespace Tally;

/// <summary>
/// Tally's "T." for the notification area, drawn from the app icon's shapes (apps/mac/icon.svg's 64-unit
/// grid) at the exact pixel size the screen wants, in the taskbar's own colour: a tray icon is a bitmap, not a
/// template the system tints, as the Mac's menu bar image is. As there, the full stop is red while recording and a
/// ring in auto mode; an update adds a dot in the accent colour.
/// </summary>
internal static class MarkIcon
{
    public enum Dot { Solid, Ring }

    private const uint Red = 0xFFE0383E;

    public static IntPtr Create(int size, bool lightTaskbar, Dot dot, bool red, bool badge, uint accent)
    {
        var ink = lightTaskbar ? 0xFF1A1A1Au : 0xFFFFFFFFu;
        // the mark fills x 12..52.5 and y 14..50 of the grid (the badge reaches 54): scaled to fill the icon, centred
        var scale = size / 42.5;
        var dx = (size - 42 * scale) / 2 - 12 * scale;
        var dy = (size - 36 * scale) / 2 - 14 * scale;
        const int samples = 4;  // per side: 16 per pixel, so the edges are smooth at 16 px
        var pixels = new uint[size * size];
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            double a = 0, r = 0, g = 0, b = 0;
            for (var sy = 0; sy < samples; sy++)
            for (var sx = 0; sx < samples; sx++)
            {
                var gx = (x + (sx + 0.5) / samples - dx) / scale;
                var gy = (y + (sy + 0.5) / samples - dy) / scale;
                uint? colour = null;
                if (badge && Circle(gx, gy, 50, 18, 4)) colour = accent;
                else if ((gx >= 12 && gx < 44 && gy >= 14 && gy < 23.5) || (gx >= 23 && gx < 33 && gy >= 23.5 && gy < 50)) colour = ink;
                else if (dot == Dot.Solid ? Circle(gx, gy, 47.5, 45, 5) : Ring(gx, gy, 47.5, 45, 3.9, 1.1)) colour = red ? Red : ink;
                if (colour is not { } c) continue;
                a += 1; r += (c >> 16) & 0xFF; g += (c >> 8) & 0xFF; b += c & 0xFF;
            }
            if (a == 0) continue;
            var alpha = a / (samples * samples);
            // premultiplied, as icons with an alpha channel are
            pixels[y * size + x] = ((uint)(alpha * 255) << 24) | ((uint)(r / a * alpha) << 16) | ((uint)(g / a * alpha) << 8) | (uint)(b / a * alpha);
        }
        return ToIcon(pixels, size);
    }

    private static bool Circle(double x, double y, double cx, double cy, double radius) =>
        (x - cx) * (x - cx) + (y - cy) * (y - cy) <= radius * radius;

    private static bool Ring(double x, double y, double cx, double cy, double radius, double half)
    {
        var d = Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
        return Math.Abs(d - radius) <= half;
    }

    private static IntPtr ToIcon(uint[] pixels, int size)
    {
        var header = new Native.BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<Native.BITMAPINFOHEADER>(), biWidth = size, biHeight = -size, biPlanes = 1, biBitCount = 32,
        };
        var colour = Native.CreateDIBSection(IntPtr.Zero, ref header, 0, out var bits, IntPtr.Zero, 0);
        Marshal.Copy(Array.ConvertAll(pixels, p => unchecked((int)p)), 0, bits, pixels.Length);
        var mask = Native.CreateBitmap(size, size, 1, 1, IntPtr.Zero);
        var info = new Native.ICONINFO { fIcon = true, hbmColor = colour, hbmMask = mask };
        var icon = Native.CreateIconIndirect(ref info);
        Native.DeleteObject(colour);
        Native.DeleteObject(mask);
        return icon;
    }
}

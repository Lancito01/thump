using System.Runtime.InteropServices;

namespace Thump.Colors;

/// <summary>
/// Grabs a tiny downscaled copy of the primary monitor with GDI (cheap enough to run 30×/s) and samples it
/// per light position. Note: exclusive-fullscreen games and DRM video may show up black — use borderless mode.
/// </summary>
public sealed class ScreenSampler : IDisposable
{
    public const int W = 64, H = 36;

    readonly IntPtr _memDc;
    readonly IntPtr _dib;
    readonly IntPtr _bits;
    readonly IntPtr _oldObj;
    readonly byte[] _pixels = new byte[W * H * 4];

    public ScreenSampler()
    {
        var screenDc = GetDC(IntPtr.Zero);
        _memDc = CreateCompatibleDC(screenDc);
        ReleaseDC(IntPtr.Zero, screenDc);

        var bmi = new BITMAPINFO
        {
            biSize = Marshal.SizeOf<BITMAPINFO>(),
            biWidth = W,
            biHeight = -H, // top-down
            biPlanes = 1,
            biBitCount = 32,
        };
        _dib = CreateDIBSection(_memDc, ref bmi, 0, out _bits, IntPtr.Zero, 0);
        _oldObj = SelectObject(_memDc, _dib);
        SetStretchBltMode(_memDc, HALFTONE);
    }

    /// <summary>Captures the screen. Returns false if the capture failed (e.g. secure desktop / UAC prompt).</summary>
    public bool Capture()
    {
        int sw = GetSystemMetrics(SM_CXSCREEN), sh = GetSystemMetrics(SM_CYSCREEN);
        var screenDc = GetDC(IntPtr.Zero);
        try
        {
            if (!StretchBlt(_memDc, 0, 0, W, H, screenDc, 0, 0, sw, sh, SRCCOPY)) return false;
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, screenDc);
        }
        Marshal.Copy(_bits, _pixels, 0, _pixels.Length);
        return true;
    }

    /// <summary>
    /// Average colour around a normalised point (u: 0 = left … 1 = right, v: 0 = top … 1 = bottom).
    /// <paramref name="radius"/> is a fraction of the screen width.
    /// </summary>
    public Rgb Sample(double u, double v, double radius)
    {
        int cx = (int)Math.Round(Math.Clamp(u, 0, 1) * (W - 1));
        int cy = (int)Math.Round(Math.Clamp(v, 0, 1) * (H - 1));
        int rx = Math.Max(1, (int)(radius * W)), ry = Math.Max(1, (int)(radius * W * 0.75));
        double r = 0, g = 0, b = 0, n = 0;
        for (int y = Math.Max(0, cy - ry); y <= Math.Min(H - 1, cy + ry); y++)
        for (int x = Math.Max(0, cx - rx); x <= Math.Min(W - 1, cx + rx); x++)
        {
            int i = (y * W + x) * 4;
            // Weight by brightness so dark letterbox areas don't dilute the picture.
            double lum = (_pixels[i] + _pixels[i + 1] + _pixels[i + 2]) / 765.0;
            double w = 0.05 + lum;
            b += _pixels[i] / 255.0 * w;
            g += _pixels[i + 1] / 255.0 * w;
            r += _pixels[i + 2] / 255.0 * w;
            n += w;
        }
        return n > 0 ? new Rgb(r / n, g / n, b / n) : Rgb.Black;
    }

    /// <summary>Dominant colours of the whole frame, for palette-style use.</summary>
    public List<Rgb> Palette(int max) => Palettes.Extract(_pixels, max);

    public void Dispose()
    {
        SelectObject(_memDc, _oldObj);
        DeleteObject(_dib);
        DeleteDC(_memDc);
    }

    const int SM_CXSCREEN = 0, SM_CYSCREEN = 1, HALFTONE = 4, SRCCOPY = 0x00CC0020;

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFO
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        public int bmiColors;
    }

    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hWnd, IntPtr hDc);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] static extern int SetStretchBltMode(IntPtr hdc, int mode);
    [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] static extern bool StretchBlt(IntPtr dst, int dx, int dy, int dw, int dh, IntPtr src, int sx, int sy, int sw, int sh, int rop);
}

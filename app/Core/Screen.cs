using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace ValSpot.Core;

public readonly record struct Rect(int X, int Y, int W, int H)
{
    public string Key => $"{W}x{H}";
}

/// <summary>Grayscale float image, row-major.</summary>
public sealed class GrayImage
{
    public readonly int W, H;
    public readonly float[] P;

    public GrayImage(int w, int h)
    {
        W = w;
        H = h;
        P = new float[w * h];
    }

    public GrayImage(int w, int h, float[] p)
    {
        W = w;
        H = h;
        P = p;
    }

    public float this[int x, int y] => P[y * W + x];

    public double Mean()
    {
        double s = 0;
        foreach (float v in P) s += v;
        return P.Length == 0 ? 0 : s / P.Length;
    }

    public GrayImage Crop(int x, int y, int w, int h)
    {
        var o = new GrayImage(w, h);
        for (int r = 0; r < h; r++)
        {
            int sy = Math.Clamp(y + r, 0, H - 1);
            for (int c = 0; c < w; c++)
            {
                int sx = Math.Clamp(x + c, 0, W - 1);
                o.P[r * w + c] = P[sy * W + sx];
            }
        }
        return o;
    }

    /// <summary>Area-average resample (same as PIL's BOX filter).</summary>
    public GrayImage Resize(int nw, int nh)
    {
        if (nw == W && nh == H) return this;
        if (nw > 0 && nh > 0 && W % nw == 0 && H % nh == 0 && W / nw == H / nh) return BlockMean(W / nw);
        var (xi, xw) = CachedWeights(W, nw);
        var (yi, yw) = CachedWeights(H, nh);
        var tmp = new float[nw * H];
        for (int y = 0; y < H; y++)
        {
            int row = y * W;
            for (int x = 0; x < nw; x++)
            {
                float s = 0;
                var idx = xi[x];
                var wts = xw[x];
                for (int k = 0; k < idx.Length; k++) s += P[row + idx[k]] * wts[k];
                tmp[y * nw + x] = s;
            }
        }
        var o = new GrayImage(nw, nh);
        for (int y = 0; y < nh; y++)
        {
            var idx = yi[y];
            var wts = yw[y];
            for (int x = 0; x < nw; x++)
            {
                float s = 0;
                for (int k = 0; k < idx.Length; k++) s += tmp[idx[k] * nw + x] * wts[k];
                o.P[y * nw + x] = s;
            }
        }
        return o;
    }

    /// <summary>Exact integer downscale (e.g. 4K -> 1080p reference = 2x): plain block average.</summary>
    private GrayImage BlockMean(int f)
    {
        int nw = W / f, nh = H / f;
        var o = new GrayImage(nw, nh);
        float inv = 1f / (f * f);
        for (int y = 0; y < nh; y++)
        {
            for (int dy = 0; dy < f; dy++)
            {
                int row = (y * f + dy) * W;
                for (int x = 0; x < nw; x++)
                {
                    float s = 0;
                    int b = row + x * f;
                    for (int dx = 0; dx < f; dx++) s += P[b + dx];
                    o.P[y * nw + x] += s;
                }
            }
            for (int x = 0; x < nw; x++) o.P[y * nw + x] *= inv;
        }
        return o;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(int, int), (int[][], float[][])> WeightCache = new();

    private static (int[][], float[][]) CachedWeights(int src, int dst)
    {
        if (WeightCache.Count > 256) WeightCache.Clear();
        return WeightCache.GetOrAdd((src, dst), k => Weights(k.Item1, k.Item2));
    }

    private static (int[][], float[][]) Weights(int src, int dst)
    {
        double scale = (double)src / dst;
        var idx = new int[dst][];
        var wts = new float[dst][];
        for (int i = 0; i < dst; i++)
        {
            double a = i * scale, b = (i + 1) * scale;
            int j0 = (int)Math.Floor(a), j1 = Math.Min(src, (int)Math.Ceiling(b));
            var li = new List<int>();
            var lw = new List<float>();
            for (int j = j0; j < j1; j++)
            {
                double ov = Math.Min(b, j + 1) - Math.Max(a, j);
                if (ov > 1e-9)
                {
                    li.Add(Math.Clamp(j, 0, src - 1));
                    lw.Add((float)(ov / (b - a)));
                }
            }
            if (li.Count == 0)
            {
                li.Add(Math.Clamp(j0, 0, src - 1));
                lw.Add(1f);
            }
            idx[i] = li.ToArray();
            wts[i] = lw.ToArray();
        }
        return (idx, wts);
    }

    /// <summary>Keep only near-white pixels (HUD text) as 0..1, so whatever colour sits
    /// behind the prompt (abilities, smokes, flashes) doesn't matter.</summary>
    public GrayImage Bright(float lo = 130f, float span = 90f)
    {
        var o = new GrayImage(W, H);
        for (int i = 0; i < P.Length; i++)
        {
            float v = (P[i] - lo) / span;
            o.P[i] = v < 0 ? 0 : v > 1 ? 1 : v;
        }
        return o;
    }

    public static GrayImage FromBitmap(Bitmap bmp)
    {
        var g = new GrayImage(bmp.Width, bmp.Height);
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            unsafe
            {
                for (int y = 0; y < bmp.Height; y++)
                {
                    byte* row = (byte*)data.Scan0 + y * data.Stride;
                    for (int x = 0; x < bmp.Width; x++)
                    {
                        byte b = row[x * 4], gr = row[x * 4 + 1], r = row[x * 4 + 2];
                        g.P[y * bmp.Width + x] = 0.299f * r + 0.587f * gr + 0.114f * b;
                    }
                }
            }
        }
        finally { bmp.UnlockBits(data); }
        return g;
    }

    public static GrayImage FromRgb(byte[] rgb, int w, int h)
    {
        var g = new GrayImage(w, h);
        for (int i = 0, n = w * h; i < n; i++)
            g.P[i] = 0.299f * rgb[i * 3] + 0.587f * rgb[i * 3 + 1] + 0.114f * rgb[i * 3 + 2];
        return g;
    }

    public static GrayImage FromPngStream(Stream s)
    {
        using var bmp = new Bitmap(s);
        using var argb = new Bitmap(bmp.Width, bmp.Height, PixelFormat.Format32bppArgb);
        using (var gr = Graphics.FromImage(argb)) gr.DrawImage(bmp, 0, 0, bmp.Width, bmp.Height);
        return FromBitmap(argb);
    }
}

/// <summary>Something that can hand back screen pixels (the real screen, or a test frame).</summary>
public interface IScreen
{
    GrayImage Grab(Rect box);
    Bitmap GrabColor(Rect box);
}

/// <summary>GDI screenshot of a screen region: BitBlt into a reused DIB section (the same
/// method as Python's mss), so repeated same-size grabs allocate nothing.</summary>
public sealed class DesktopScreen : IScreen, IDisposable
{
    private readonly object _gate = new();
    private IntPtr _memDc, _bmp, _old, _bits;
    private int _w, _h;

    public Bitmap GrabColor(Rect box)
    {
        var bmp = new Bitmap(Math.Max(1, box.W), Math.Max(1, box.H), PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(box.X, box.Y, 0, 0, new Size(bmp.Width, bmp.Height), CopyPixelOperation.SourceCopy);
        return bmp;
    }

    public GrayImage Grab(Rect box)
    {
        int w = Math.Max(1, box.W), h = Math.Max(1, box.H);
        lock (_gate)
        {
            Ensure(w, h);
            IntPtr screen = GetDC(IntPtr.Zero);
            try { BitBlt(_memDc, 0, 0, w, h, screen, box.X, box.Y, 0x00CC0020); } // SRCCOPY
            finally { ReleaseDC(IntPtr.Zero, screen); }
            var g = new GrayImage(w, h);
            unsafe
            {
                byte* px = (byte*)_bits;
                for (int i = 0, n = w * h; i < n; i++, px += 4)
                    g.P[i] = 0.299f * px[2] + 0.587f * px[1] + 0.114f * px[0];
            }
            return g;
        }
    }

    private void Ensure(int w, int h)
    {
        if (_memDc != IntPtr.Zero && w == _w && h == _h) return;
        Free();
        IntPtr screen = GetDC(IntPtr.Zero);
        _memDc = CreateCompatibleDC(screen);
        ReleaseDC(IntPtr.Zero, screen);
        var bi = new BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = w,
            biHeight = -h,          // top-down rows
            biPlanes = 1,
            biBitCount = 32,
        };
        _bmp = CreateDIBSection(_memDc, ref bi, 0, out _bits, IntPtr.Zero, 0);
        _old = SelectObject(_memDc, _bmp);
        _w = w;
        _h = h;
    }

    private void Free()
    {
        if (_memDc == IntPtr.Zero) return;
        SelectObject(_memDc, _old);
        DeleteObject(_bmp);
        DeleteDC(_memDc);
        _memDc = _bmp = _bits = IntPtr.Zero;
    }

    public void Dispose()
    {
        lock (_gate) Free();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER bi, uint usage, out IntPtr bits, IntPtr section, uint offset);
}

/// <summary>Serves grabs out of one RGB frame (tests and clip evaluation).</summary>
public sealed class FrameScreen : IScreen
{
    private readonly byte[] _rgb;
    private readonly int _w, _h;
    private readonly GrayImage _gray;

    public FrameScreen(byte[] rgb, int w, int h)
    {
        _rgb = rgb;
        _w = w;
        _h = h;
        _gray = GrayImage.FromRgb(rgb, w, h);
    }

    public GrayImage Grab(Rect b) => _gray.Crop(b.X, b.Y, b.W, b.H);

    public Bitmap GrabColor(Rect b)
    {
        var bmp = new Bitmap(Math.Max(1, b.W), Math.Max(1, b.H), PixelFormat.Format24bppRgb);
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
            {
                int sx = Math.Clamp(b.X + x, 0, _w - 1), sy = Math.Clamp(b.Y + y, 0, _h - 1);
                int i = (sy * _w + sx) * 3;
                bmp.SetPixel(x, y, Color.FromArgb(_rgb[i], _rgb[i + 1], _rgb[i + 2]));
            }
        return bmp;
    }
}

/// <summary>Finds the VALORANT window and its client area in physical screen pixels.
/// Plain Win32 queries; nothing is sent to the game.</summary>
public sealed class GameWindow
{
    public const string GameExe = "VALORANT-Win64-Shipping.exe";
    private IntPtr _cacheHwnd;
    private bool _cacheOk;
    public string LastForegroundExe { get; private set; } = "";

    public Rect? FocusedRect()
    {
        IntPtr hwnd = GetForegroundWindow();
        if (hwnd != _cacheHwnd)
        {
            _cacheHwnd = hwnd;
            LastForegroundExe = hwnd == IntPtr.Zero ? "" : ExeOf(hwnd);
            _cacheOk = string.Equals(LastForegroundExe, GameExe, StringComparison.OrdinalIgnoreCase);
        }
        if (!_cacheOk || IsIconic(hwnd)) return null;
        return ClientRect(hwnd);
    }

    public Rect? FindRect()
    {
        Rect? best = null;
        EnumWindows((h, _) =>
        {
            if (IsWindowVisible(h) && string.Equals(ExeOf(h), GameExe, StringComparison.OrdinalIgnoreCase))
            {
                var r = ClientRect(h);
                if (r != null && (best == null || r.Value.W * r.Value.H > best.Value.W * best.Value.H)) best = r;
            }
            return true;
        }, IntPtr.Zero);
        return best;
    }

    public static bool GameRunning()
    {
        try { return System.Diagnostics.Process.GetProcessesByName("VALORANT-Win64-Shipping").Length > 0; }
        catch { return false; }
    }

    private static Rect? ClientRect(IntPtr hwnd)
    {
        if (!GetClientRect(hwnd, out RECT r)) return null;
        var p = new POINT();
        if (!ClientToScreen(hwnd, ref p)) return null;
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        return w > 100 && h > 100 ? new Rect(p.X, p.Y, w, h) : null;
    }

    private static string ExeOf(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out uint pid);
        IntPtr h = OpenProcess(0x1000, false, pid); // PROCESS_QUERY_LIMITED_INFORMATION
        if (h == IntPtr.Zero) return "";
        try
        {
            var sb = new StringBuilder(1024);
            int n = sb.Capacity;
            return QueryFullProcessImageNameW(h, 0, sb, ref n) ? Path.GetFileName(sb.ToString()) : "";
        }
        finally { CloseHandle(h); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    private delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out RECT r);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hwnd, ref POINT p);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageNameW(IntPtr h, int flags, StringBuilder sb, ref int size);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
}

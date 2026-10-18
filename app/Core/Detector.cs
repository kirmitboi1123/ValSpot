using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;

namespace ValSpot.Core;

public sealed class Template
{
    public string Name = "";
    public int X, Y;
    public double RefHeight = 1080;
    public bool Custom;
    // full-resolution brightness template, zero-mean, and its norm
    public int W, H;
    public float[] Tz = Array.Empty<float>();
    public double Norm;
    // half-resolution copy for the coarse wide search
    public int Wc, Hc;
    public float[] Tzc = Array.Empty<float>();
    public double NormC;

    public static Template FromImage(string name, GrayImage gray, int x, int y, double refHeight, bool custom)
    {
        var t = new Template { Name = name, X = x, Y = y, RefHeight = refHeight, Custom = custom };
        var b = gray.Bright();
        (t.W, t.H, t.Tz, t.Norm) = ZeroMean(b);
        var c = b.Resize(Math.Max(1, b.W / 2), Math.Max(1, b.H / 2));
        (t.Wc, t.Hc, t.Tzc, t.NormC) = ZeroMean(c);
        return t;
    }

    private static (int, int, float[], double) ZeroMean(GrayImage g)
    {
        double mean = g.Mean();
        var z = new float[g.P.Length];
        double n2 = 0;
        for (int i = 0; i < z.Length; i++)
        {
            z[i] = (float)(g.P[i] - mean);
            n2 += z[i] * (double)z[i];
        }
        return (g.W, g.H, z, Math.Sqrt(n2));
    }
}

public readonly record struct Transform(double Ox, double Oy, double K);

public readonly record struct WideHit(double Score, Transform Tr, string Name);

/// <summary>Finds the "SWITCH PLAYER" prompt VALORANT shows while you spectate after dying.
/// Where to look = a transform (ox, oy, k): screen = window origin + (ox, oy) + template_xy * k.
/// It comes from a saved calibration for this window size, else from the 16:9 picture geometry.
/// Until a death has been seen once per window size, WideSearch scans the bottom-left of the
/// window at a few scales and saves a consistent hit.</summary>
public sealed class DeathDetector
{
    private readonly Settings _s;
    public IScreen Screen { get; set; }
    /// <summary>Swapped as a whole on reload, so the engine thread never sees a half-built list.</summary>
    public List<Template> Templates { get; private set; } = new();
    private int _hits, _misses;
    private Transform? _wideLast;
    private int _wideCount;

    public DeathDetector(Settings s, IScreen screen)
    {
        _s = s;
        Screen = screen;
        LoadTemplates();
    }

    public bool Enabled => Templates.Count > 0;

    public void LoadTemplates()
    {
        var list = new List<Template>();
        var asm = Assembly.GetExecutingAssembly();
        foreach (var (res, x, y) in new[] { ("switch_player.png", 120, 853), ("switch_player_small.png", 118, 849) })
        {
            using var st = asm.GetManifestResourceStream(res);
            if (st == null) continue;
            list.Add(Template.FromImage(Path.GetFileNameWithoutExtension(res), GrayImage.FromPngStream(st), x, y, 1080, false));
        }
        foreach (var c in _s.CustomTemplates)
        {
            string p = Path.IsPathRooted(c.File) ? c.File : Path.Combine(Settings.Dir, c.File);
            if (!File.Exists(p))
            {
                Log.Write($"WARN custom template missing: {c.File}");
                continue;
            }
            using var fs = File.OpenRead(p);
            list.Add(Template.FromImage(Path.GetFileNameWithoutExtension(p), GrayImage.FromPngStream(fs), c.X, c.Y, c.RefHeight, true));
        }
        Templates = list;
    }

    // ------------------------------------------------------------------ geometry
    /// <summary>The 16:9 picture VALORANT draws inside its window: pillarbox on ultrawide /
    /// super-ultrawide (e.g. Samsung G9 5120x1440), letterbox on taller-than-16:9 windows.</summary>
    /// <summary>Test switch: pretend the 16:9 geometry is unknown (only calibration can find it).</summary>
    public static bool NaiveGeometry { get; set; }

    public static Rect Viewport(Rect r)
    {
        if (NaiveGeometry) return r;
        if ((long)r.W * 9 > (long)r.H * 16 + 9)
        {
            int vw = (int)Math.Round(r.H * 16 / 9.0, MidpointRounding.ToEven);
            return new Rect(r.X + (r.W - vw) / 2, r.Y, vw, r.H);
        }
        if ((long)r.W * 9 < (long)r.H * 16 - 16)
        {
            int vh = (int)Math.Round(r.W * 9 / 16.0, MidpointRounding.ToEven);
            return new Rect(r.X, r.Y + (r.H - vh) / 2, r.W, vh);
        }
        return r;
    }

    public bool Calibrated(Rect r) => _s.Calibration.ContainsKey(r.Key);

    public Transform TransformFor(Rect r, double refHeight = 1080)
    {
        if (_s.Calibration.TryGetValue(r.Key, out var c)) return new Transform(c.Ox, c.Oy, c.K);
        var v = Viewport(r);
        return new Transform(v.X - r.X, v.Y - r.Y, v.H / refHeight);
    }

    public void SaveCalibration(Rect r, Transform tr, string why)
    {
        _s.Calibration[r.Key] = new Calibration
        {
            Ox = Math.Round(tr.Ox, 1),
            Oy = Math.Round(tr.Oy, 1),
            K = Math.Round(tr.K, 4),
        };
        _s.Save();
        Log.Write($"CALIBRATED for {r.Key}: offset {tr.Ox:0},{tr.Oy:0} scale {tr.K:0.000} ({why})");
        CalibrationSaved?.Invoke(r.Key);
    }

    public event Action<string>? CalibrationSaved;

    // ------------------------------------------------------------------ fast check
    private static int RoundE(double v) => (int)Math.Round(v, MidpointRounding.ToEven);

    private (Rect box, int pw, int ph) BoxFor(Template t, Rect r, Transform tr)
    {
        int pad = _s.SearchPad;
        int pw = t.W + 2 * pad, ph = t.H + 2 * pad;
        var box = new Rect(
            r.X + RoundE(tr.Ox + (t.X - pad) * tr.K),
            r.Y + RoundE(tr.Oy + (t.Y - pad) * tr.K),
            Math.Max(1, RoundE(pw * tr.K)), Math.Max(1, RoundE(ph * tr.K)));
        return (box, pw, ph);
    }

    /// <summary>Best match score at the expected spot (one screenshot covers every template).</summary>
    public (double score, string name) Score(Rect r)
    {
        if (Templates.Count == 0) return (-1, "");
        var jobs = Templates.Select(t => (t, BoxFor(t, r, TransformFor(r, t.RefHeight)))).ToList();
        int x0 = jobs.Min(j => j.Item2.box.X), y0 = jobs.Min(j => j.Item2.box.Y);
        int x1 = jobs.Max(j => j.Item2.box.X + j.Item2.box.W), y1 = jobs.Max(j => j.Item2.box.Y + j.Item2.box.H);
        var whole = Screen.Grab(new Rect(x0, y0, x1 - x0, y1 - y0));
        double best = -1;
        string name = "";
        foreach (var (t, (box, pw, ph)) in jobs)
        {
            var patch = whole.Crop(box.X - x0, box.Y - y0, box.W, box.H).Resize(pw, ph).Bright();
            double s = BestAround(patch, t, 0, 0, patch.W - t.W, patch.H - t.H);
            if (s > best)
            {
                best = s;
                name = t.Name;
            }
        }
        return (best, name);
    }

    public void Reset()
    {
        _hits = 0;
        _misses = 0;
    }

    /// <summary>New dead/alive state: ConfirmFrames with the marker to go dead,
    /// ReviveFrames without it to come back alive.</summary>
    public bool Check(bool dead, Rect r, out double score)
    {
        var (s, name) = Score(r);
        score = s;
        if (s >= _s.DeathThreshold)
        {
            _misses = 0;
            _hits++;
            if (!dead && _hits >= _s.ConfirmFrames)
            {
                Log.Write($"DEAD  marker '{name}' score {s:0.00}");
                if (!Calibrated(r)) SaveCalibration(r, TransformFor(r), "seen at the expected spot");
                return true;
            }
        }
        else
        {
            _hits = 0;
            if (dead)
            {
                _misses++;
                if (_misses >= _s.ReviveFrames)
                {
                    Log.Write($"ALIVE again (marker gone, best {s:0.00})");
                    _misses = 0;
                    return false;
                }
            }
        }
        return dead;
    }

    // ------------------------------------------------------------------ wide search
    public static Rect WideArea(Rect r) =>
        // the prompt sits ~79 % down and ~6 % across the 16:9 picture; on a 32:9 window that
        // picture starts 25 % in, so the left 40 % x bottom 40 % (from 58 %) covers all cases
        new(r.X, r.Y + (int)(r.H * 0.58), (int)(r.W * 0.40), (int)(r.H * 0.40));

    /// <summary>Scan the bottom-left of the window for any template at a few scales:
    /// coarse pass at half resolution, then refine the best candidates at full resolution.</summary>
    public WideHit WideSearch(Rect r, GrayImage? area = null, double[]? extraScales = null)
    {
        var a = WideArea(r);
        int gx = a.X - r.X, gy = a.Y - r.Y;
        var raw = area ?? Screen.Grab(a);
        double baseK = Viewport(r).H / 1080.0;
        var ks = new SortedSet<double> { Math.Round(baseK * 0.88, 4), Math.Round(baseK, 4), Math.Round(baseK * 1.12, 4), Math.Round(r.H / 1080.0, 4) };
        if (extraScales != null) foreach (double f in extraScales) ks.Add(Math.Round(baseK * f, 4));
        var best = new WideHit(-1, default, "");
        foreach (double k in ks)
        {
            int sw = Math.Max(1, RoundE(raw.W / k)), sh = Math.Max(1, RoundE(raw.H / k));
            var img = raw.Resize(sw, sh).Bright();
            var coarse = img.Resize(Math.Max(1, img.W / 2), Math.Max(1, img.H / 2));
            var ci = new Integral(coarse);
            foreach (var t in Templates)
            {
                if (img.W < t.W || img.H < t.H) continue;
                foreach (var (cx, cy) in CoarseCandidates(coarse, ci, t, 6))
                {
                    int fx = cx * 2, fy = cy * 2;
                    var (s, bx, by) = BestAt(img, t, fx - 3, fy - 3, fx + 3, fy + 3);
                    if (s > best.Score)
                    {
                        double px = gx + bx * k, py = gy + by * k;
                        best = new WideHit(s, new Transform(px - t.X * k, py - t.Y * k, k), t.Name);
                    }
                }
            }
        }
        return best;
    }

    /// <summary>One calibration attempt. True once the same spot was found twice and saved.</summary>
    public bool WideStep(Rect r, WideHit hit)
    {
        if (hit.Score < _s.CalibrateThreshold)
        {
            _wideCount = 0;
            _wideLast = null;
            return false;
        }
        var tr = hit.Tr;
        bool same = _wideLast is { } l
                    && Math.Abs(tr.Ox - l.Ox) <= 4 * tr.K
                    && Math.Abs(tr.Oy - l.Oy) <= 4 * tr.K
                    && Math.Abs(tr.K - l.K) < 0.05;
        _wideCount = same ? _wideCount + 1 : 1;
        _wideLast = tr;
        if (_wideCount >= 2)
        {
            SaveCalibration(r, tr, $"found '{hit.Name}' score {hit.Score:0.00} twice");
            _wideCount = 0;
            return true;
        }
        return false;
    }

    // ------------------------------------------------------------------ matching
    /// <summary>Sum of squared deviations below which a window counts as flat. Any window
    /// holding real HUD text is far above this.</summary>
    public const double MinWindowVar = 1e-2;

    /// <summary>Normalised cross-correlation of template t with img at (x, y).</summary>
    public static double NccAt(GrayImage img, Template t, int x, int y)
    {
        if (x < 0 || y < 0 || x + t.W > img.W || y + t.H > img.H) return -1;
        double num = 0, s = 0, s2 = 0;
        for (int r = 0; r < t.H; r++)
        {
            var row = new ReadOnlySpan<float>(img.P, (y + r) * img.W + x, t.W);
            var trow = new ReadOnlySpan<float>(t.Tz, r * t.W, t.W);
            num += Dot(row, trow);
            foreach (float v in row)
            {
                s += v;
                s2 += v * (double)v;
            }
        }
        int n = t.W * t.H;
        double var = s2 - s * s / n;
        // flat window (wall, sky, black bar): rounding error would blow the score past 1.0
        if (var <= MinWindowVar) return 0;
        return num / (Math.Sqrt(var) * t.Norm);
    }

    private static double BestAround(GrayImage img, Template t, int x0, int y0, int x1, int y1)
        => BestAt(img, t, x0, y0, x1, y1).score;

    private static (double score, int x, int y) BestAt(GrayImage img, Template t, int x0, int y0, int x1, int y1)
    {
        double best = -1;
        int bx = 0, by = 0;
        for (int y = Math.Max(0, y0); y <= Math.Min(y1, img.H - t.H); y++)
            for (int x = Math.Max(0, x0); x <= Math.Min(x1, img.W - t.W); x++)
            {
                double s = NccAt(img, t, x, y);
                if (s > best)
                {
                    best = s;
                    bx = x;
                    by = y;
                }
            }
        return (best, bx, by);
    }

    private sealed class Integral
    {
        public readonly int W, H;
        public readonly double[] S, S2;

        public Integral(GrayImage g)
        {
            W = g.W + 1;
            H = g.H + 1;
            S = new double[W * H];
            S2 = new double[W * H];
            for (int y = 1; y < H; y++)
            {
                double rs = 0, rs2 = 0;
                for (int x = 1; x < W; x++)
                {
                    double v = g.P[(y - 1) * g.W + (x - 1)];
                    rs += v;
                    rs2 += v * v;
                    S[y * W + x] = S[(y - 1) * W + x] + rs;
                    S2[y * W + x] = S2[(y - 1) * W + x] + rs2;
                }
            }
        }

        public (double s, double s2) Sum(int x, int y, int w, int h)
        {
            int a = y * W + x, b = y * W + x + w, c = (y + h) * W + x, d = (y + h) * W + x + w;
            return (S[d] - S[b] - S[c] + S[a], S2[d] - S2[b] - S2[c] + S2[a]);
        }
    }

    /// <summary>Top-n coarse positions (half resolution), kept apart by a few pixels.</summary>
    private static List<(int x, int y)> CoarseCandidates(GrayImage c, Integral ii, Template t, int n)
    {
        var top = new List<(double s, int x, int y)>();
        int tw = t.Wc, th = t.Hc, area = tw * th;
        if (c.W < tw || c.H < th || t.NormC < 1e-6) return new();
        double floor = 0.25;
        for (int y = 0; y <= c.H - th; y++)
        {
            for (int x = 0; x <= c.W - tw; x++)
            {
                var (s, s2) = ii.Sum(x, y, tw, th);
                double var = s2 - s * s / area;
                if (var <= MinWindowVar) continue; // flat area: can't contain text
                double num = 0;
                for (int r = 0; r < th; r++)
                    num += Dot(new ReadOnlySpan<float>(c.P, (y + r) * c.W + x, tw), new ReadOnlySpan<float>(t.Tzc, r * tw, tw));
                double score = num / (Math.Sqrt(var) * t.NormC);
                if (score < floor) continue;
                int near = top.FindIndex(e => Math.Abs(e.x - x) <= 3 && Math.Abs(e.y - y) <= 3);
                if (near >= 0)
                {
                    if (score > top[near].s) top[near] = (score, x, y);
                    continue;
                }
                top.Add((score, x, y));
                if (top.Count > n)
                {
                    top.Sort((p, q) => q.s.CompareTo(p.s));
                    top.RemoveAt(top.Count - 1);
                    floor = Math.Max(floor, top[^1].s);
                }
            }
        }
        return top.Select(e => (e.x, e.y)).ToList();
    }

    private static double Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        int i = 0;
        var acc = Vector<float>.Zero;
        int vn = Vector<float>.Count;
        for (; i <= a.Length - vn; i += vn) acc += new Vector<float>(a.Slice(i, vn)) * new Vector<float>(b.Slice(i, vn));
        double s = Vector.Dot(acc, Vector<float>.One);
        for (; i < a.Length; i++) s += a[i] * b[i];
        return s;
    }
}

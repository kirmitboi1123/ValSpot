using System;
using System.Collections.Generic;
using System.Drawing.Imaging;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ValSpot.Core;
using Bitmap = System.Drawing.Bitmap;

namespace ValSpot.UI;

public partial class LearnWindow : Window
{
    private readonly Engine _engine;
    private Bitmap? _frame;          // whole game window, window coordinates
    private Core.Rect _rect0;        // (0, 0, W, H): same size key as the real window
    private Core.Rect _area;         // the part of the frame shown on screen
    private System.Windows.Point? _dragFrom;

    public LearnWindow(Engine engine)
    {
        _engine = engine;
        InitializeComponent();
        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);
        Closed += (_, _) => _frame?.Dispose();
        var r = _engine.Window.FindRect();
        if (r is { } w && (S.DeathSeen.Contains(w.Key) || DeathDetector.IsPreset(w)))
        {
            Step.Text = (S.DeathSeen.Contains(w.Key)
                            ? $"Your screen ({w.Key}) is already set up: ValSpot has seen your death screen. "
                            : $"Your screen size ({w.Key}) is a built-in preset, so English clients work without this. ") +
                        "You only need this if death music never starts, or your game isn't in English. " +
                        "To use it: die, spectate a teammate, click Capture, then click back into VALORANT.";
        }
    }

    private Settings S => _engine.S;

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void CaptureButton_Click(object sender, RoutedEventArgs e)
    {
        CaptureButton.IsEnabled = false;
        SaveButton.Visibility = Visibility.Collapsed;
        Sel.Visibility = Visibility.Collapsed;
        _ = CaptureFlow();
    }

    /// <summary>Wait until VALORANT is the window in front, then capture. A fixed countdown
    /// captured whatever was in front when it ran out, often this window instead of the game
    /// (seen on a friend's PC 2026-10-22).</summary>
    private async Task CaptureFlow()
    {
        try
        {
            if (_engine.Window.FindRect() is null)
            {
                Result.Text = "Couldn't find the VALORANT window. Is the game open (Windowed Fullscreen)?";
                return;
            }
            var game = new GameWindow();   // own instance: the engine's is used from its own thread
            CaptureButton.Content = "Waiting for VALORANT...";
            Result.Text = "Click back into VALORANT. ValSpot captures as soon as the game is in front.";
            var r = await WaitForGame(game.FocusedRect, TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(600));
            if (r is null)
            {
                Activate();
                Result.Text = "VALORANT never came to the front. Click Capture, then click into the game within 30 seconds.";
                Log.Write("learn: VALORANT wasn't brought to the front within 30 s; nothing captured");
                return;
            }
            CaptureButton.Content = "Capturing...";
            Result.Text = "Capturing...";
            var frames = await Task.Run(() => CaptureBurst(game, r.Value));
            Activate();
            if (frames.Count == 0)
            {
                Result.Text = "You left VALORANT before the capture finished. Capture again and stay in the game for 2 seconds.";
                return;
            }
            Apply(Analyze(S, frames, new Core.Rect(0, 0, r.Value.W, r.Value.H)), r.Value);
        }
        finally
        {
            CaptureButton.Content = "Capture";
            CaptureButton.IsEnabled = true;
        }
    }

    /// <summary>Polls until the game has been the window in front for <paramref name="settle"/>
    /// without a break, then returns its rect; null after <paramref name="timeout"/>.</summary>
    public static async Task<Core.Rect?> WaitForGame(Func<Core.Rect?> focused, TimeSpan timeout, TimeSpan settle, int pollMs = 100)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        TimeSpan? since = null;
        while (clock.Elapsed < timeout)
        {
            var r = focused();
            if (r is null) since = null;
            else
            {
                since ??= clock.Elapsed;
                if (clock.Elapsed - since.Value >= settle) return r;
            }
            await Task.Delay(pollMs);
        }
        return null;
    }

    /// <summary>A burst of 8 frames over ~2 s: one frame can land on a moment where a bright
    /// effect sits behind the text. Stops if the game leaves the front.</summary>
    private static List<Bitmap> CaptureBurst(GameWindow game, Core.Rect r)
    {
        var screen = new DesktopScreen();
        var frames = new List<Bitmap>();
        for (int i = 0; i < 8; i++)
        {
            if (game.FocusedRect() is not { } now || now != r) break;
            frames.Add(screen.GrabColor(r));
            System.Threading.Thread.Sleep(150);
        }
        return frames;
    }

    public sealed record LearnResult(Bitmap Frame, Core.Rect Area, WideHit Hit, bool Black, double PictureMean);

    /// <summary>The part of the frame to show: the bottom-left of the 16:9 game picture, not of
    /// the window (on a 32:9 window the window's bottom-left is mostly black bar).</summary>
    public static Core.Rect PictureArea(Core.Rect r0)
    {
        var vp = DeathDetector.Viewport(r0);
        return new(vp.X, vp.Y + (int)(vp.H * 0.58), (int)(vp.W * 0.40), (int)(vp.H * 0.40));
    }

    /// <summary>Picks the frame where the built-in marker scores best, and checks for a black
    /// capture (the whole 16:9 picture dark: Windows isn't letting ValSpot see the game).
    /// Disposes the frames it doesn't keep.</summary>
    public static LearnResult Analyze(Settings s, IReadOnlyList<Bitmap> frames, Core.Rect r0)
    {
        Bitmap? keep = null;
        var best = new WideHit(-1, default, "");
        foreach (var bmp in frames)
        {
            var h = new DeathDetector(s, ToFrame(bmp)).WideSearch(r0, null, new[] { 0.80, 0.94, 1.06, 1.20 });
            if (keep == null || h.Score > best.Score)
            {
                keep = bmp;
                best = h;
            }
            if (best.Score >= 0.95) break;
        }
        foreach (var bmp in frames) if (!ReferenceEquals(bmp, keep)) bmp.Dispose();

        var vp = DeathDetector.Viewport(r0);
        double mean;
        using (var pic = keep!.Clone(new System.Drawing.Rectangle(vp.X, vp.Y, vp.W, vp.H), PixelFormat.Format32bppArgb))
            mean = GrayImage.FromBitmap(pic).Mean();
        bool black = mean < 3.0;

        var area = PictureArea(r0);
        if (!black && best.Score >= s.CalibrateThreshold && HitBox(s, best) is { } box && !Contains(area, box))
            area = DeathDetector.WideArea(r0);   // the line sits outside the 16:9 crop: show the wider one
        return new LearnResult(keep, area, best, black, mean);
    }

    /// <summary>Where the found marker sits, in window coordinates.</summary>
    public static (double X, double Y, double W, double H)? HitBox(Settings s, WideHit hit)
    {
        var d = new DeathDetector(s, new FrameScreen(new byte[3], 1, 1));
        var t = d.Templates.Find(x => x.Name == hit.Name);
        if (t == null) return null;
        return (hit.Tr.Ox + t.X * hit.Tr.K, hit.Tr.Oy + t.Y * hit.Tr.K, t.W * hit.Tr.K, t.H * hit.Tr.K);
    }

    private static bool Contains(Core.Rect a, (double X, double Y, double W, double H) b) =>
        b.X >= a.X && b.Y >= a.Y && b.X + b.W <= a.X + a.W && b.Y + b.H <= a.Y + a.H;

    /// <summary>Shows a capture's result. <paramref name="r"/> is the real window (for the size key).</summary>
    public void Apply(LearnResult res, Core.Rect r)
    {
        _rect0 = new Core.Rect(0, 0, r.W, r.H);
        _frame?.Dispose();
        _frame = res.Frame;
        var hit = res.Hit;

        if (res.Black)
        {
            View.Visibility = Visibility.Collapsed;
            Placeholder.Visibility = Visibility.Visible;
            Placeholder.Text = "The screenshot came back black.";
            Step.Text = "Windows isn't letting ValSpot see the game picture, so it can't learn your death screen or notice when you die. " +
                        "In VALORANT, set Display Mode to Windowed Fullscreen. If Windows HDR is on, try turning it off. Then capture again.";
            Result.Text = "Nothing saved.";
            Log.Write($"WARN learn: the capture of the VALORANT window is black (picture brightness {res.PictureMean:0.0}); nothing saved");
            return;
        }

        _area = res.Area;
        ShowArea();
        if (hit.Score >= S.CalibrateThreshold)
        {
            _engine.Detector.SaveCalibration(_rect0, hit.Tr, $"learned from your capture, score {hit.Score:0.00}");
            if (HitBox(S, hit) is { } b) MarkBox(b.X, b.Y, b.W, b.H);
            Step.Text = "Found it. The green box is what ValSpot watches for.";
            Result.Text = $"Saved for {r.W}x{r.H} (match {hit.Score:0.00}). Death music is ready.";
            Log.Write($"learn: found the built-in marker in your capture (score {hit.Score:0.00})");
        }
        else
        {
            Step.Text = "ValSpot couldn't find the English \"SWITCH PLAYER\" line. Drag a box around the line under your teammate's name " +
                        "(the one with the mouse icon), then click Save. If you weren't dead and spectating, capture again.";
            Result.Text = $"Best guess only {Math.Max(0, hit.Score):0.00}. Draw the box yourself.";
            Log.Write($"learn: built-in marker not found in your capture (best {hit.Score:0.00}); asking you to mark it");
        }
    }

    public static FrameScreen ToFrame(Bitmap bmp)
    {
        var data = bmp.LockBits(new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var rgb = new byte[bmp.Width * bmp.Height * 3];
        try
        {
            unsafe
            {
                for (int y = 0; y < bmp.Height; y++)
                {
                    byte* row = (byte*)data.Scan0 + y * data.Stride;
                    for (int x = 0; x < bmp.Width; x++)
                    {
                        int o = (y * bmp.Width + x) * 3;
                        rgb[o] = row[x * 4 + 2];
                        rgb[o + 1] = row[x * 4 + 1];
                        rgb[o + 2] = row[x * 4];
                    }
                }
            }
        }
        finally { bmp.UnlockBits(data); }
        return new FrameScreen(rgb, bmp.Width, bmp.Height);
    }

    private void ShowArea()
    {
        using var crop = _frame!.Clone(new System.Drawing.Rectangle(_area.X, _area.Y, _area.W, _area.H), PixelFormat.Format32bppArgb);
        crop.SetResolution(96, 96);
        using var ms = new MemoryStream();
        crop.Save(ms, ImageFormat.Png);
        ms.Position = 0;
        var img = new BitmapImage();
        img.BeginInit();
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.StreamSource = ms;
        img.EndInit();
        Shot.Source = img;
        Overlay.Width = _area.W;
        Overlay.Height = _area.H;
        Placeholder.Visibility = Visibility.Collapsed;
        View.Visibility = Visibility.Visible;
    }

    /// <summary>Box in window coordinates -> overlay (area) coordinates.</summary>
    private void MarkBox(double x, double y, double w, double h)
    {
        Canvas.SetLeft(Sel, x - _area.X);
        Canvas.SetTop(Sel, y - _area.Y);
        Sel.Width = Math.Max(1, w);
        Sel.Height = Math.Max(1, h);
        Sel.Visibility = Visibility.Visible;
    }

    private void Overlay_Down(object sender, MouseButtonEventArgs e)
    {
        if (_frame == null) return;
        _dragFrom = e.GetPosition(Overlay);
        Overlay.CaptureMouse();
        Sel.Visibility = Visibility.Visible;
        Sel.Width = Sel.Height = 0;
        Canvas.SetLeft(Sel, _dragFrom.Value.X);
        Canvas.SetTop(Sel, _dragFrom.Value.Y);
    }

    private void Overlay_Move(object sender, MouseEventArgs e)
    {
        if (_dragFrom is not { } a) return;
        var b = e.GetPosition(Overlay);
        double x = Math.Clamp(Math.Min(a.X, b.X), 0, Overlay.Width), y = Math.Clamp(Math.Min(a.Y, b.Y), 0, Overlay.Height);
        double x2 = Math.Clamp(Math.Max(a.X, b.X), 0, Overlay.Width), y2 = Math.Clamp(Math.Max(a.Y, b.Y), 0, Overlay.Height);
        Canvas.SetLeft(Sel, x);
        Canvas.SetTop(Sel, y);
        Sel.Width = x2 - x;
        Sel.Height = y2 - y;
    }

    private void Overlay_Up(object sender, MouseButtonEventArgs e)
    {
        if (_dragFrom == null) return;
        _dragFrom = null;
        Overlay.ReleaseMouseCapture();
        bool ok = Sel.Width >= 30 && Sel.Height >= 8;
        SaveButton.Visibility = ok ? Visibility.Visible : Visibility.Collapsed;
        Result.Text = ok ? "Looks good? Click Save this marker." : "Drag a bigger box around the whole line.";
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_frame == null) return;
        int sx = _area.X + (int)Math.Round(Canvas.GetLeft(Sel)), sy = _area.Y + (int)Math.Round(Canvas.GetTop(Sel));
        int sw = (int)Math.Round(Sel.Width), sh = (int)Math.Round(Sel.Height);
        var tr = _engine.Detector.TransformFor(_rect0);
        double k = tr.K;
        int tw = Math.Max(8, (int)Math.Round(sw / k)), th = Math.Max(4, (int)Math.Round(sh / k));
        string dir = Path.Combine(Settings.Dir, "templates");
        Directory.CreateDirectory(dir);
        string file = $"templates/custom_{_rect0.W}x{_rect0.H}_{DateTime.Now:yyyyMMdd-HHmmss}.png";
        using (var crop = _frame.Clone(new System.Drawing.Rectangle(sx, sy, sw, sh), PixelFormat.Format32bppArgb))
        using (var scaled = new Bitmap(tw, th, PixelFormat.Format32bppArgb))
        {
            using (var g = System.Drawing.Graphics.FromImage(scaled))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                g.DrawImage(crop, 0, 0, tw, th);
            }
            scaled.Save(Path.Combine(Settings.Dir, file), ImageFormat.Png);
        }
        S.CustomTemplates.Add(new TemplateSpec
        {
            File = file,
            X = (int)Math.Round((sx - tr.Ox) / k),
            Y = (int)Math.Round((sy - tr.Oy) / k),
            RefHeight = 1080,
        });
        S.Save();
        _engine.Detector.LoadTemplates();

        // check it against the same capture before trusting it
        var check = new DeathDetector(S, ToFrame(_frame));
        var (score, name, _) = check.Score(_rect0);
        if (score >= S.DeathThreshold)
        {
            _engine.Detector.SaveCalibration(_rect0, tr, "your own marker");
            Result.Text = $"Saved (match {score:0.00}). Death music is ready in any language.";
            Log.Write($"learn: saved your own marker {file} (self-check {score:0.00})");
        }
        else
        {
            Result.Text = $"Saved, but it only matched {score:0.00} on this capture. Try a tighter box around the line.";
            Log.Write($"learn: saved your own marker {file} but self-check was low ({score:0.00})");
        }
        SaveButton.Visibility = Visibility.Collapsed;
    }
}

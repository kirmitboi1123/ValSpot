using System;
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
                        "To use it: die, spectate a teammate, click Capture, and switch to VALORANT within 5 seconds.";
        }
    }

    private Settings S => _engine.S;

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void CaptureButton_Click(object sender, RoutedEventArgs e)
    {
        CaptureButton.IsEnabled = false;
        SaveButton.Visibility = Visibility.Collapsed;
        Sel.Visibility = Visibility.Collapsed;
        int left = 5;
        Result.Text = "Switch to VALORANT now…";
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += async (_, _) =>
        {
            left--;
            CaptureButton.Content = left > 0 ? $"Capturing in {left} s" : "Capturing…";
            if (left > 0) return;
            timer.Stop();
            await CaptureAndSearch();
            CaptureButton.Content = "Capture again in 5 s";
            CaptureButton.IsEnabled = true;
        };
        CaptureButton.Content = "Capturing in 5 s";
        timer.Start();
    }

    private async Task CaptureAndSearch()
    {
        var rect = _engine.Window.FindRect();
        if (rect is null)
        {
            Activate();
            Result.Text = "Couldn't find the VALORANT window. Is the game open (Windowed Fullscreen)?";
            return;
        }
        var r = rect.Value;
        _rect0 = new Core.Rect(0, 0, r.W, r.H);
        Result.Text = "Capturing…";
        // a burst of 8 frames over ~2 s, keeping the best: one frame can land on a moment where a
        // bright effect sits behind the text
        var (bestBmp, hit) = await Task.Run(() =>
        {
            var screen = new DesktopScreen();
            Bitmap? keep = null;
            var best = new WideHit(-1, default, "");
            for (int i = 0; i < 8; i++)
            {
                var bmp = screen.GrabColor(r);
                var h = new DeathDetector(S, ToFrame(bmp)).WideSearch(_rect0, null, new[] { 0.80, 0.94, 1.06, 1.20 });
                if (h.Score > best.Score)
                {
                    keep?.Dispose();
                    keep = bmp;
                    best = h;
                }
                else bmp.Dispose();
                if (best.Score >= 0.95) break;
                System.Threading.Thread.Sleep(150);
            }
            return (keep!, best);
        });
        Activate();
        _frame?.Dispose();
        _frame = bestBmp;

        _area = DeathDetector.WideArea(_rect0);
        ShowArea();
        if (hit.Score >= S.CalibrateThreshold)
        {
            _engine.Detector.SaveCalibration(_rect0, hit.Tr, $"learned from your capture, score {hit.Score:0.00}");
            var t = _engine.Detector.Templates.Find(x => x.Name == hit.Name);
            if (t != null) MarkBox(hit.Tr.Ox + t.X * hit.Tr.K, hit.Tr.Oy + t.Y * hit.Tr.K, t.W * hit.Tr.K, t.H * hit.Tr.K);
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

    private static FrameScreen ToFrame(Bitmap bmp)
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

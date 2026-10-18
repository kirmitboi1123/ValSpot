using System;
using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ValSpot.Core;

public enum Status { Paused, NoGame, Menu, InMatch, MusicOn, Problem }

/// <summary>Runs everything on one background thread at PollHz: log events -> rules,
/// focused-window death check, one-time calibration, diagnostics.</summary>
public sealed class Engine : IDisposable
{
    public Settings S { get; }
    public Rules Rules { get; }
    public DeathDetector Detector { get; }
    public MediaController Media { get; }
    public GameWindow Window { get; } = new();
    private readonly ConcurrentQueue<GameEvent> _events = new();
    private readonly GameLogTailer _tailer;
    private readonly Diagnostics _diag;
    private Thread? _thread;
    private volatile bool _stop;
    private bool _hadWindow = true;
    private string _lastKey = "";
    private Rect? _lastRect;
    private DateTime _wideAt = DateTime.MinValue;
    private Task<WideHit>? _wide;
    private Rect _wideRect;
    private DateTime _gameCheckAt = DateTime.MinValue;

    public bool GameRunning { get; private set; }
    /// <summary>Tests: catch up on the log even if VALORANT isn't running.</summary>
    public static bool ForceCatchUp { get; set; }
    public string WindowKey => _lastRect?.Key ?? "";
    public bool CalibratedNow => _lastRect is { } r && (Detector.Learned(r) || S.DeathSeen.Contains(r.Key));
    public double LastScore { get; private set; }
    public string Problem { get; private set; } = "";
    public Status Status { get; private set; } = Status.NoGame;
    public event Action? StatusChanged;
    public event Action<string, string>? Notify;   // title, text (tray balloon)

    /// <summary>Tests can point the engine at another log file.</summary>
    public static string? LogPathOverride { get; set; }

    public static string LogPath => LogPathOverride ??
        Environment.ExpandEnvironmentVariables(@"%LOCALAPPDATA%\VALORANT\Saved\Logs\ShooterGame.log");

    public Engine(Settings s, IScreen? screen = null, bool dryRun = false)
    {
        S = s;
        Rules = new Rules(s);
        Detector = new DeathDetector(s, screen ?? new DesktopScreen());
        Media = new MediaController(s) { DryRun = dryRun };
        _tailer = new GameLogTailer(LogPath, _events);
        _diag = new Diagnostics(s, this);
        Rules.Music += (play, why) =>
        {
            if (play) Media.Play(why);
            else Media.Pause(why);
        };
        Rules.Changed += Recompute;
        Detector.CalibrationSaved += key => Notify?.Invoke("ValSpot calibrated", $"Found the death screen for {key}. Death music is on.");
    }

    public void Start()
    {
        Media.RecoverAfterCrash();
        Log.Write($"ValSpot {Program.Version} on Windows {Environment.OSVersion.Version}; settings {Settings.FilePath}");
        if (!File.Exists(LogPath)) Log.Write($"NOTE VALORANT log not found yet at {LogPath} (fine if the game isn't running)");
        Log.Write($"music app: {Media.Describe()}; death templates: {Detector.Templates.Count}" +
                  (Detector.Templates.Any(t => t.Custom) ? " (incl. your own)" : ""));
        // started while VALORANT is running and its log is fresh: read the match so far first
        try
        {
            _tailer.CatchUp = ForceCatchUp || GameWindow.GameRunning() && File.Exists(LogPath)
                              && (DateTime.UtcNow - File.GetLastWriteTimeUtc(LogPath)).TotalMinutes < 5;
        }
        catch { _tailer.CatchUp = false; }
        if (_tailer.CatchUp) Rules.Quiet = true;
        _tailer.Start();
        _thread = new Thread(Loop) { IsBackground = true, Name = "ValSpot engine" };
        _thread.Start();
    }

    public void SetPaused(bool paused)
    {
        S.Paused = paused;
        S.Save();
        Log.Write(paused ? "ValSpot paused by you (music left alone)" : "ValSpot resumed");
        Recompute();
    }

    private void Loop()
    {
        while (!_stop)
        {
            try { Step(DateTime.Now); }
            catch (Exception e) { Log.Write($"WARN engine step failed: {e.GetType().Name}: {e.Message}"); }
            Thread.Sleep(Math.Max(20, 1000 / Math.Max(1, S.PollHz)));
        }
    }

    public void Step(DateTime now)
    {
        while (_events.TryDequeue(out var ev))
        {
            if (ev.Kind == GameEventKind.CaughtUp)
            {
                if (Rules.Quiet)
                {
                    Rules.Tick(now);              // a buy phase that already ran out
                    Rules.Quiet = false;
                    Log.Write($"caught up on the match so far: {Rules.Describe()}");
                    Rules.ApplyCurrent("ValSpot started mid-match");
                    Recompute();
                }
                continue;
            }
            if (ev.History)
            {
                // the log's clock is UTC; run the rules on the time each line happened
                var at = DateTime.SpecifyKind(ev.Stamp, DateTimeKind.Utc).ToLocalTime();
                Rules.Tick(at);
                Rules.Handle(ev, at);
                continue;
            }
            if (ev.Kind is GameEventKind.Buy or GameEventKind.Combat or GameEventKind.MatchStart) Detector.Reset();
            if (ev.Kind == GameEventKind.MatchEnd && _lastRect is { } lr && !S.DeathSeen.Contains(lr.Key) && Rules.InMatch)
                Log.Write($"NOTE death screen not seen yet at {lr.Key} (needs one death where you spectate a teammate)");
            Rules.Handle(ev, now);
        }
        if (Rules.Quiet) return;                  // still reading the match so far
        Rules.Tick(now);

        if ((now - _gameCheckAt).TotalSeconds >= 2)
        {
            _gameCheckAt = now;
            bool running = GameWindow.GameRunning();
            if (running != GameRunning)
            {
                GameRunning = running;
                Log.Write(running ? "VALORANT is running" : "VALORANT closed; waiting in the tray");
                Recompute();
            }
        }

        if (!Rules.WantsDeathCheck || !Detector.Enabled) return;
        Rect? rect = S.OnlyWhenFocused ? Window.FocusedRect() : Window.FindRect();
        if (rect is null)
        {
            if (_hadWindow)
            {
                _hadWindow = false;
                Detector.Reset();
                Log.Write($"tabbed out ({(Window.LastForegroundExe.Length > 0 ? Window.LastForegroundExe : "other window")}), holding state: {(Rules.Dead ? "dead" : "alive")}");
            }
            return;
        }
        var r = rect.Value;
        if (!_hadWindow)
        {
            _hadWindow = true;
            Detector.Reset();
            Log.Write("VALORANT focused again, watching screen");
        }
        if (r.Key != _lastKey)
        {
            _lastKey = r.Key;
            var vp = DeathDetector.Viewport(r);
            var tr = Detector.TransformFor(r);
            Log.Write($"game window {r.W}x{r.H} at {r.X},{r.Y}; 16:9 picture {vp.W}x{vp.H}; " +
                      $"{(Detector.Learned(r) ? "learned on this PC" : DeathDetector.IsPreset(r) ? "preset size" : "not calibrated yet")} " +
                      $"(offset {tr.Ox:0},{tr.Oy:0} scale {tr.K:0.000})");
        }
        _lastRect = r;

        bool was = Rules.Dead;
        bool dead = Detector.Check(was, r, out double score);
        LastScore = score;
        if (dead != was) Rules.SetDead(dead, dead ? "you died" : "revived");

        // one-time calibration: until a death has been seen at this window size, also scan the
        // whole bottom-left every few seconds (in the background, so the 8/s check never waits)
        if (_wide is { IsCompleted: true } done)
        {
            _wide = null;
            if (done.Status == TaskStatus.RanToCompletion && Detector.WideStep(_wideRect, done.Result)
                && Rules.Phase == Phase.Combat && !Rules.Dead)
                Rules.SetDead(true, "you died (found while calibrating)");
        }
        // presets skip this; but if a preset size still hasn't seen a death after 4 rounds,
        // its HUD may sit elsewhere on this PC, so search as a safety net
        bool search = !Detector.Calibrated(r)
                      || (!Detector.Learned(r) && !S.DeathSeen.Contains(r.Key) && Rules.RoundsThisMatch >= 4);
        if (_wide == null && !Rules.Dead && search
            && (now - _wideAt).TotalSeconds >= S.CalibrateIntervalSeconds)
        {
            _wideAt = now;
            _wideRect = r;
            var area = Detector.Screen.Grab(DeathDetector.WideArea(r));
            _wide = Task.Run(() => Detector.WideSearch(r, area));
        }
        if (S.Diagnostics && !S.DeathSeen.Contains(r.Key)) _diag.Tick(now, r);
    }

    public void ReportProblem(string problem)
    {
        Problem = problem;
        Notify?.Invoke("ValSpot needs a look", problem);
        Recompute();
    }

    private void Recompute()
    {
        Status st;
        if (S.Paused) st = Status.Paused;
        else if (Problem.Length > 0 && GameRunning) st = Status.Problem;
        else if (!GameRunning && !Rules.InMatch) st = Status.NoGame;
        else if (Rules.InMatch && Rules.MusicWanted) st = Status.MusicOn;
        else if (Rules.InMatch) st = Status.InMatch;
        else st = Status.Menu;
        Status = st;
        StatusChanged?.Invoke();
    }

    public string StatusText() => Status switch
    {
        Status.Paused => "Paused (music left alone)",
        Status.NoGame => "Waiting for VALORANT",
        Status.Menu => "VALORANT open, in the menus",
        Status.InMatch => Rules.Phase == Phase.Combat ? "Round live, you're alive" : "In a match",
        Status.MusicOn => Rules.Dead ? "Music on: you're dead" : "Music on: buy phase",
        Status.Problem => Problem,
        _ => "",
    };

    private bool _disposed;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stop = true;
        _tailer.Stop();
        Media.Dispose();
    }
}

/// <summary>Until the death screen has been found once for a window size, keep a few small
/// snapshots of the bottom-left of the game window (local only) so a failing setup can be
/// diagnosed from one match. Also warns when the screenshot comes back black.</summary>
public sealed class Diagnostics
{
    private readonly Settings _s;
    private readonly Engine _engine;
    private DateTime _last = DateTime.MinValue;
    private bool _announced, _blackWarned;
    private int _black;
    public static string Folder => Path.Combine(Settings.Dir, "diag");

    public Diagnostics(Settings s, Engine engine)
    {
        _s = s;
        _engine = engine;
    }

    public void Tick(DateTime now, Rect r)
    {
        if ((now - _last).TotalSeconds < _s.DiagnosticsIntervalSeconds) return;
        _last = now;
        var box = new Rect(r.X, r.Y + (int)(r.H * 0.45), (int)(r.W * 0.55), r.H - (int)(r.H * 0.45));
        Bitmap bmp;
        try { bmp = _engine.Detector.Screen.GrabColor(box); }
        catch (Exception e)
        {
            Log.Write($"WARN screen capture failed: {e.Message}");
            return;
        }
        using (bmp)
        {
            var b32 = To32(bmp);
            double mean = GrayImage.FromBitmap(b32).Mean();
            if (!ReferenceEquals(b32, bmp)) b32.Dispose();
            if (mean < 3.0)
            {
                if (++_black >= 3 && !_blackWarned)
                {
                    _blackWarned = true;
                    Log.Write("WARN the screenshot of the VALORANT window is BLACK. Windows isn't letting ValSpot see the " +
                              "game picture on this PC, so death detection can't work. Send valspot.log and the diag folder.");
                    _engine.ReportProblem("Can't see the game picture (screenshot is black)");
                }
            }
            else _black = 0;
            try
            {
                Directory.CreateDirectory(Folder);
                int w = Math.Min(1280, bmp.Width), h = Math.Max(1, bmp.Height * w / bmp.Width);
                using var small = new Bitmap(bmp, new Size(w, h));
                string name = $"{now:yyyyMMdd-HHmmss}_{r.W}x{r.H}_mean{mean:0}.jpg";
                small.Save(Path.Combine(Folder, name), ImageFormat.Jpeg);
                foreach (var old in new DirectoryInfo(Folder).GetFiles("*.jpg").OrderBy(f => f.Name).SkipLast(_s.DiagnosticsKeep))
                    old.Delete();
                if (!_announced)
                {
                    _announced = true;
                    Log.Write($"diagnostics: saving a snapshot of the bottom-left every {_s.DiagnosticsIntervalSeconds:0}s to {Folder} " +
                              $"until the death screen is found once (max {_s.DiagnosticsKeep}, local only)");
                }
            }
            catch (Exception e) { Log.Write($"WARN could not save diagnostic snapshot: {e.Message}"); }
        }
    }

    private static Bitmap To32(Bitmap b)
    {
        if (b.PixelFormat == PixelFormat.Format32bppArgb) return b;
        var o = new Bitmap(b.Width, b.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(o);
        g.DrawImage(b, 0, 0, b.Width, b.Height);
        return o;
    }
}

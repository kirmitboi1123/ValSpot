using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using ValSpot.Core;
using Forms = System.Windows.Forms;
using MediaColor = System.Windows.Media.Color;

namespace ValSpot.UI;

/// <summary>The app: lives in the tray, owns the engine, opens the windows.</summary>
public sealed class TrayApp : Application
{
    private readonly Settings _s;
    private readonly bool _startHidden;
    private Engine? _engine;
    private Forms.NotifyIcon? _tray;
    private Forms.ToolStripMenuItem? _statusItem, _pauseItem;
    private SettingsWindow? _settings;
    private LearnWindow? _learn;
    private readonly Dictionary<Status, Icon> _icons = new();
    private Updater.Result? _update;
    private DispatcherTimer? _updateTimer;

    public TrayApp(Settings s, bool startHidden)
    {
        _s = s;
        _startHidden = startHidden;
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        DispatcherUnhandledException += (_, e) =>
        {
            Log.Write($"WARN UI error: {e.Exception.GetType().Name}: {e.Exception.Message}");
            e.Handled = true;
        };
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _engine = new Engine(_s);
        BuildTray();
        _engine.StatusChanged += () => Dispatcher.BeginInvoke(new Action(UpdateTray));
        _engine.Notify += (title, text) => Dispatcher.BeginInvoke(new Action(() => Balloon(title, text)));
        _engine.Start();
        UpdateTray();

        if (!_s.FirstRunDone)
        {
            _s.FirstRunDone = true;
            _s.Save();
            ShowSettings();
            Balloon("ValSpot is running", "It lives here in the tray. Play VALORANT and it handles your music.");
        }
        else if (!_startHidden) Balloon("ValSpot is running", "Right-click the tray icon for settings.");

        _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(6) };
        _updateTimer.Tick += async (_, _) => await MaybeCheckUpdate();
        _updateTimer.Start();
        _ = Dispatcher.BeginInvoke(new Func<Task>(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(8));
            await MaybeCheckUpdate();
        }));
    }

    private async Task MaybeCheckUpdate()
    {
        if (!_s.CheckForUpdates || (DateTime.UtcNow - _s.LastUpdateCheck).TotalHours < 20) return;
        _s.LastUpdateCheck = DateTime.UtcNow;
        _s.Save();
        var r = await Updater.CheckAsync();
        if (r is { Newer: true })
        {
            _update = r;
            Log.Write($"update available: {r.Latest}");
            Balloon("ValSpot update available", $"Version {r.Latest} is out. Open settings to download it.");
            _settings?.ShowUpdate(r);
        }
    }

    // ------------------------------------------------------------------ tray
    private void BuildTray()
    {
        var menu = new Forms.ContextMenuStrip { ShowImageMargin = false };
        _statusItem = new Forms.ToolStripMenuItem("Waiting for VALORANT") { Enabled = false };
        _pauseItem = new Forms.ToolStripMenuItem("Pause ValSpot", null, (_, _) => _engine!.SetPaused(!_s.Paused));
        menu.Items.Add(_statusItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(_pauseItem);
        menu.Items.Add(new Forms.ToolStripMenuItem("Learn my death screen…", null, (_, _) => ShowLearn(null)));
        menu.Items.Add(new Forms.ToolStripMenuItem("Settings…", null, (_, _) => ShowSettings()));
        menu.Items.Add(new Forms.ToolStripMenuItem("Open log folder", null, (_, _) => SettingsWindow.Open(Settings.Dir)));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(new Forms.ToolStripMenuItem("Quit ValSpot", null, (_, _) => Quit()));
        _tray = new Forms.NotifyIcon
        {
            Text = "ValSpot",
            Icon = IconFor(Status.NoGame),
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.MouseClick += (_, a) =>
        {
            if (a.Button == Forms.MouseButtons.Left) ShowSettings();
        };
    }

    private void UpdateTray()
    {
        if (_tray == null || _engine == null) return;
        var st = _engine.Status;
        string text = _engine.StatusText();
        _tray.Icon = IconFor(st);
        string tip = $"ValSpot · {text}";
        _tray.Text = tip.Length > 63 ? tip[..63] : tip;
        _statusItem!.Text = text;
        _pauseItem!.Text = _s.Paused ? "Resume ValSpot" : "Pause ValSpot";
    }

    private void Balloon(string title, string text)
    {
        if (_tray == null) return;
        _tray.BalloonTipTitle = title;
        _tray.BalloonTipText = text;
        _tray.ShowBalloonTip(4000);
    }

    public static MediaColor StatusColor(Status s) => s switch
    {
        Status.Paused => MediaColor.FromRgb(0xF5, 0xA6, 0x23),
        Status.NoGame => MediaColor.FromRgb(0x8B, 0x98, 0xA5),
        Status.Menu => MediaColor.FromRgb(0x8B, 0x98, 0xA5),
        Status.InMatch => MediaColor.FromRgb(0x3D, 0x9B, 0xE9),
        Status.MusicOn => MediaColor.FromRgb(0x1E, 0xD7, 0x60),
        Status.Problem => MediaColor.FromRgb(0xFF, 0x46, 0x55),
        _ => MediaColor.FromRgb(0x8B, 0x98, 0xA5),
    };

    /// <summary>The ValSpot icon with a status dot in the corner (cached per status).</summary>
    public Icon IconFor(Status s)
    {
        if (_icons.TryGetValue(s, out var cached)) return cached;
        using var bmp = RenderTrayBitmap(s, 32);
        IntPtr h = bmp.GetHicon();
        var icon = (Icon)Icon.FromHandle(h).Clone();
        DestroyIcon(h);
        _icons[s] = icon;
        return icon;
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr h);

    public static Bitmap RenderTrayBitmap(Status s, int size)
    {
        using var st = Assembly.GetExecutingAssembly().GetManifestResourceStream("valspot.ico");
        using var baseIcon = new Icon(st!, size, size);
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.DrawIcon(baseIcon, new Rectangle(0, 0, size, size));
        var c = StatusColor(s);
        float d = size * 0.42f, x = size - d - 0.5f, y = 0.5f;
        using (var ring = new SolidBrush(Color.FromArgb(255, 15, 25, 35))) g.FillEllipse(ring, x - 1.5f, y - 0.5f, d + 2f, d + 2f);
        using (var dot = new SolidBrush(Color.FromArgb(255, c.R, c.G, c.B))) g.FillEllipse(dot, x, y + 0.5f, d, d);
        return bmp;
    }

    // ------------------------------------------------------------------ windows
    public void ShowSettings()
    {
        if (_engine == null) return;
        if (_settings == null || !_settings.IsLoaded)
        {
            _settings = new SettingsWindow(_engine, this);
            _settings.Closed += (_, _) => _settings = null;
            if (_update is { Newer: true }) _settings.ShowUpdate(_update);
            _settings.Show();
        }
        if (_settings.WindowState == WindowState.Minimized) _settings.WindowState = WindowState.Normal;
        _settings.Activate();
    }

    public void ShowLearn(Window? owner)
    {
        if (_engine == null) return;
        if (_learn == null || !_learn.IsLoaded)
        {
            _learn = new LearnWindow(_engine);
            if (owner != null) _learn.Owner = owner;
            else _learn.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            _learn.Closed += (_, _) => _learn = null;
            _learn.Show();
        }
        _learn.Activate();
    }

    private void Quit()
    {
        Log.Write("ValSpot quit");
        if (_tray != null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        _engine?.Dispose();
        foreach (var i in _icons.Values) i.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _engine?.Dispose();
        base.OnExit(e);
    }
}

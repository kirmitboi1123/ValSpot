using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using ValSpot.Core;

namespace ValSpot.UI;

public partial class SettingsWindow : Window
{
    private readonly Engine _engine;
    private readonly TrayApp _app;
    private bool _loading;

    public SettingsWindow(Engine engine, TrayApp app)
    {
        _engine = engine;
        _app = app;
        InitializeComponent();
        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);
        VersionText.Text = "v" + Program.Version;
        LoadFromSettings();
        Wire();
        _engine.StatusChanged += OnStatus;
        _engine.Detector.CalibrationSaved += _ => Dispatcher.BeginInvoke(new Action(RefreshCal));
        Closed += (_, _) => _engine.StatusChanged -= OnStatus;
        OnStatus();
        RefreshCal();
        RefreshApp();
    }

    private Settings S => _engine.S;

    private void LoadFromSettings()
    {
        _loading = true;
        PlayOnDeath.IsChecked = S.PlayOnDeath;
        PlayInBuy.IsChecked = S.PlayInBuy;
        PlayInLobby.IsChecked = S.PlayInLobby;
        LeadSlider.Value = S.PauseLeadSeconds;
        Fade.IsChecked = S.Fade;
        FadeSlider.Value = S.FadeSeconds;
        string app = S.MediaApp ?? "Spotify";
        if (app.Equals("Spotify", StringComparison.OrdinalIgnoreCase)) AppSpotify.IsChecked = true;
        else if (app.Equals("Any", StringComparison.OrdinalIgnoreCase)) AppAny.IsChecked = true;
        else
        {
            AppOther.IsChecked = true;
            AppName.Text = app;
        }
        StartWithWindows.IsChecked = Startup.IsEnabled();
        CheckUpdates.IsChecked = S.CheckForUpdates;
        PauseAll.IsChecked = S.Paused;
        UpdateLabels();
        _loading = false;
    }

    private void Wire()
    {
        PlayOnDeath.Click += (_, _) => Save(() => S.PlayOnDeath = PlayOnDeath.IsChecked == true);
        PlayInBuy.Click += (_, _) => Save(() => S.PlayInBuy = PlayInBuy.IsChecked == true);
        PlayInLobby.Click += (_, _) => Save(() => S.PlayInLobby = PlayInLobby.IsChecked == true);
        Fade.Click += (_, _) => Save(() => S.Fade = Fade.IsChecked == true);
        LeadSlider.ValueChanged += (_, _) => Save(() => S.PauseLeadSeconds = Math.Round(LeadSlider.Value, 1));
        FadeSlider.ValueChanged += (_, _) => Save(() => S.FadeSeconds = Math.Round(FadeSlider.Value, 1));
        AppSpotify.Checked += (_, _) => Save(() => S.MediaApp = "Spotify");
        AppAny.Checked += (_, _) => Save(() => S.MediaApp = "Any");
        AppOther.Checked += (_, _) => Save(() => S.MediaApp = AppName.Text.Trim().Length > 0 ? AppName.Text.Trim() : "Spotify");
        AppName.TextChanged += (_, _) =>
        {
            if (AppName.Text.Trim().Length > 0 && AppOther.IsChecked != true) AppOther.IsChecked = true;
            else if (AppOther.IsChecked == true && AppName.Text.Trim().Length > 0) Save(() => S.MediaApp = AppName.Text.Trim());
        };
        CheckUpdates.Click += (_, _) => Save(() => S.CheckForUpdates = CheckUpdates.IsChecked == true);
        PauseAll.Click += (_, _) => _engine.SetPaused(PauseAll.IsChecked == true);
        StartWithWindows.Click += (_, _) =>
        {
            try { Startup.Set(StartWithWindows.IsChecked == true); }
            catch (Exception e)
            {
                Log.Write($"WARN start with Windows: {e.Message}");
                StartWithWindows.IsChecked = Startup.IsEnabled();
            }
        };
    }

    private void Save(Action change)
    {
        if (_loading) return;
        change();
        S.Save();
        UpdateLabels();
        RefreshApp();
    }

    private void UpdateLabels()
    {
        LeadText.Text = $"{LeadSlider.Value:0.0} s";
        FadeText.Text = $"{FadeSlider.Value:0.0} s";
        bool fade = Fade.IsChecked == true;
        FadeSlider.IsEnabled = fade;
        FadeRow.Opacity = fade ? 1 : 0.45;
        AppName.Visibility = AppOther.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshApp()
    {
        string want = S.MediaApp;
        System.Threading.Tasks.Task.Run(() => _engine.Media.Describe()).ContinueWith(t =>
            Dispatcher.BeginInvoke(new Action(() => AppStatus.Text = "Now: " + t.Result)));
    }

    private void RefreshCal()
    {
        var keys = S.Calibration.Keys.ToList();
        int custom = S.CustomTemplates.Count;
        if (keys.Count == 0 && custom == 0)
            CalText.Text = "Not learned yet. It learns automatically the first time you die and spectate a teammate.";
        else
            CalText.Text = $"Learned for {string.Join(", ", keys.DefaultIfEmpty("-"))}" + (custom > 0 ? $" · {custom} custom marker(s)" : "") + ".";
        ResetCalButton.IsEnabled = keys.Count > 0 || custom > 0;
    }

    private void OnStatus()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            StatusText.Text = _engine.StatusText();
            StatusDot.Fill = new SolidColorBrush(TrayApp.StatusColor(_engine.Status));
            PauseAll.IsChecked = S.Paused;
        }));
    }

    public void ShowUpdate(Updater.Result r)
    {
        UpdateBanner.Visibility = Visibility.Visible;
        UpdateText.Text = $"ValSpot {r.Latest} is out (you have {Program.Version}).";
        UpdateButton.Tag = r.Url;
    }

    private void UpdateButton_Click(object sender, RoutedEventArgs e) => Open(UpdateButton.Tag as string ?? Updater.ReleasesPage);
    private void Repo_Click(object sender, RoutedEventArgs e) => Open($"https://github.com/{Updater.Repo}");
    private void OpenFolder_Click(object sender, RoutedEventArgs e) => Open(Settings.Dir);
    private void LearnButton_Click(object sender, RoutedEventArgs e) => _app.ShowLearn(this);

    private void ResetCalButton_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Forget the learned position and any custom markers? ValSpot will learn again on your next death.",
                "ValSpot", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        S.Calibration.Clear();
        S.CustomTemplates.Clear();
        S.Save();
        _engine.Detector.LoadTemplates();
        Log.Write("calibration and custom markers cleared by you");
        RefreshCal();
    }

    private async void CheckNow_Click(object sender, RoutedEventArgs e)
    {
        CheckNow.IsEnabled = false;
        CheckNow.Content = "Checking…";
        var r = await Updater.CheckAsync();
        CheckNow.IsEnabled = true;
        if (r == null) CheckNow.Content = "Couldn't reach GitHub";
        else if (r.Newer)
        {
            CheckNow.Content = "Update available";
            ShowUpdate(r);
        }
        else CheckNow.Content = "You're up to date";
    }

    public static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Write($"WARN could not open {target}: {ex.Message}"); }
    }
}

/// <summary>Dark title bar on Windows 10 2004+ / 11, to match the window.</summary>
public static class DarkTitleBar
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public static void Apply(Window w)
    {
        try
        {
            var hwnd = new WindowInteropHelper(w).Handle;
            int on = 1;
            if (DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)) != 0) DwmSetWindowAttribute(hwnd, 19, ref on, sizeof(int));
            int caption = 0x0023190F; // COLORREF 0x00BBGGRR for #0F1923
            DwmSetWindowAttribute(hwnd, 35, ref caption, sizeof(int));
        }
        catch { /* older Windows: default title bar */ }
    }
}

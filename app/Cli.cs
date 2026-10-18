using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using ValSpot.Core;

namespace ValSpot;

/// <summary>Command-line tools for testing (not used by the normal app).</summary>
public static class Cli
{
    private static string? Arg(string[] a, string name) { int i = Array.IndexOf(a, name); return i >= 0 && i + 1 < a.Length ? a[i + 1] : null; }
    private static bool Flag(string[] a, string name) => Array.IndexOf(a, name) >= 0;

    public static int Run(string[] a)
    {
        string? outFile = Arg(a, "--out");
        StreamWriter? w = null;
        if (outFile != null)
        {
            w = new StreamWriter(outFile, false) { AutoFlush = true };
            Console.SetOut(w);
        }
        try
        {
            return a[0] switch
            {
                "--replay" => Replay(a),
                "--eval" => Eval(a),
                "--check" => Check(a),
                "--media" => MediaInfoCmd(),
                "--fade-test" => FadeTest(),
                "--render" => Render(a),
                "--bench" => Bench(),
                "--catchup-test" => CatchUpTest(),
                "--version" => Print(Program.Version),
                _ => Print("commands: --replay LOG | --eval --ffmpeg F --height H [--canvas WxH] [--naive] CLIP... | --check | --media | --fade-test | --render DIR | --bench"),
            };
        }
        catch (Exception e)
        {
            Console.WriteLine($"ERROR {e}");
            return 1;
        }
        finally { w?.Dispose(); }
    }

    private static int Print(string s)
    {
        Console.WriteLine(s);
        return 0;
    }

    private static Settings TempSettings()
    {
        string dir = Path.Combine(Path.GetTempPath(), "valspot-cli-" + Environment.ProcessId);
        Directory.CreateDirectory(dir);
        Settings.UseDir(dir);
        return new Settings();
    }

    // -------------------------------------------------------------- replay
    private static int Replay(string[] a)
    {
        var s = TempSettings();
        var rules = new Rules(s);
        rules.Music += (play, why) => Log.Write($"MUSIC {(play ? "PLAY " : "PAUSE")} (dry-run) <- {why}");
        var events = new List<GameEvent>();
        using (var fs = new FileStream(a[1], FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var sr = new StreamReader(fs))
        {
            string? line;
            while ((line = sr.ReadLine()) != null)
                if (GameLogParser.Parse(line) is { } ev) events.Add(ev);
        }
        Log.Write($"replay: {events.Count} events");
        foreach (var ev in events)
        {
            rules.Tick(ev.Stamp);
            rules.Handle(ev, ev.Stamp);
        }
        return 0;
    }

    // -------------------------------------------------------------- catch-up
    /// <summary>Fake log, as if ValSpot started 10 s into round 2's buy phase; then the round goes live.</summary>
    private static int CatchUpTest()
    {
        var s = TempSettings();
        string log = Path.Combine(Settings.Dir, "ShooterGame.log");
        string L(DateTime utc, string text) => $"[{utc:yyyy.MM.dd-HH.mm.ss:fff}][  1]{text}\n";
        var now = DateTime.UtcNow;
        File.WriteAllText(log,
            L(now.AddSeconds(-210), "LogMapLoadModel: Update: [Map Name: Ascent] /Game/GameModes/Bomb/BombGameMode") +
            L(now.AddSeconds(-200), "LogGameFlowStateManager: [PlatformTime: 1.0] Transitioning from TransitionToInGame to InGame") +
            L(now.AddSeconds(-155), "LogShooterGameState: Warning: Gameplay started at local time 45.000000 (server time 45.0)") +
            L(now.AddSeconds(-11), "LogShooterGameState: Warning: AShooterGameState::OnRoundEnded for round '0'") +
            L(now.AddSeconds(-10), "LogShooterGameState: Warning: Gameplay started at local time 0.050000 (server time 0.0)"));
        Engine.LogPathOverride = log;
        Engine.ForceCatchUp = true;
        using var engine = new Engine(s, new FrameScreen(new byte[3], 1, 1), dryRun: true);
        engine.Start();
        Thread.Sleep(1500);
        Console.WriteLine($"state after catch-up: {engine.Rules.Describe()}; music wanted: {engine.Rules.MusicWanted}; pause at in {(engine.Rules.PauseAt - DateTime.Now)?.TotalSeconds:0} s");
        File.AppendAllText(log, L(DateTime.UtcNow, "LogShooterGameState: Warning: Gameplay started at local time 30.000000 (server time 30.0)"));
        Thread.Sleep(1500);
        Console.WriteLine($"after barriers drop: {engine.Rules.Describe()}; music wanted: {engine.Rules.MusicWanted}");
        return 0;
    }

    // -------------------------------------------------------------- eval
    private static int Eval(string[] a)
    {
        string ff = Arg(a, "--ffmpeg") ?? "ffmpeg";
        int ch = int.Parse(Arg(a, "--height") ?? "1080", CultureInfo.InvariantCulture);
        int cw = ch * 16 / 9;
        int W = cw, H = ch;
        if (Arg(a, "--canvas") is { } canvas)
        {
            var p = canvas.Split('x');
            W = int.Parse(p[0], CultureInfo.InvariantCulture);
            H = int.Parse(p[1], CultureInfo.InvariantCulture);
        }
        DeathDetector.NaiveGeometry = Flag(a, "--naive");
        DeathDetector.DisablePresets = Flag(a, "--naive");   // "geometry unknown" = not a known size either
        var clips = a.Skip(1).Where(x => x.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)).ToList();
        string? customPng = Arg(a, "--custom");
        int cx = int.Parse(Arg(a, "--cx") ?? "0", CultureInfo.InvariantCulture), cy = int.Parse(Arg(a, "--cy") ?? "0", CultureInfo.InvariantCulture);
        bool customOnly = Flag(a, "--custom-only");
        var rect = new Rect(0, 0, W, H);
        foreach (var clip in clips)
        {
            var s = TempSettings();
            if (customPng != null)
            {
                Directory.CreateDirectory(Path.Combine(Settings.Dir, "templates"));
                File.Copy(customPng, Path.Combine(Settings.Dir, "templates", "custom.png"), true);
                s.CustomTemplates.Add(new TemplateSpec { File = "templates/custom.png", X = cx, Y = cy, RefHeight = 1080 });
            }
            var det = new DeathDetector(s, new FrameScreen(new byte[3], 1, 1));
            if (customOnly) det.KeepOnlyCustom();
            if (customPng != null) foreach (var t in det.Templates.Where(t => t.Custom)) Console.WriteLine($"  custom marker after trim: {t.W}x{t.H} at {t.X},{t.Y}");
            string vf = $"fps=2,scale={cw}:{ch}:flags=bicubic,pad={W}:{H}:(ow-iw)/2:(oh-ih)/2:black";
            var psi = new ProcessStartInfo(ff, $"-hide_banner -loglevel error -threads 1 -i \"{clip}\" -vf \"{vf}\" -f rawvideo -pix_fmt rgb24 -")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi)!;
            try { proc.PriorityClass = ProcessPriorityClass.Idle; } catch { }
            var stream = proc.StandardOutput.BaseStream;
            int frameBytes = W * H * 3;
            var buf = new byte[frameBytes];
            var rows = new List<(double t, double s)>();
            var passes = new List<bool>();
            double? calAt = null;
            var wideMs = new List<double>();
            int i = 0;
            while (ReadFull(stream, buf))
            {
                det.Screen = new FrameScreen((byte[])buf.Clone(), W, H);
                var sp = det.Score(rect);
                double sc = sp.score;
                bool passed = sp.passed;
                if (Flag(a, "--wide-only"))
                {
                    // the wide search alone, on every frame (what "Learn my death screen" relies on)
                    var hit = det.WideSearch(rect, null, new[] { 0.80, 0.94, 1.06, 1.20 });
                    rows.Add((i / 2.0, sc));
                    passes.Add(passed);
                    wideMs.Add(hit.Score);
                    if (sc >= s.DeathThreshold) Console.WriteLine($"  t={i / 2.0}: spot {sc:0.00} wide {hit.Score:0.00} at {hit.Tr.Ox:0},{hit.Tr.Oy:0} x{hit.Tr.K:0.000}");
                    i++;
                    continue;
                }
                if (!det.Calibrated(rect))
                {
                    var sw = Stopwatch.StartNew();
                    var hit = det.WideSearch(rect);
                    bool ok = det.WideStep(rect, hit);
                    wideMs.Add(sw.Elapsed.TotalMilliseconds);
                    if (ok)
                    {
                        calAt = i / 2.0;
                        var sp2 = det.Score(rect); // after calibration
                        sc = sp2.score;
                        passed = sp2.passed;
                    }
                }
                rows.Add((i / 2.0, sc));
                passes.Add(passed);
                i++;
            }
            proc.WaitForExit();
            double th = s.DeathThreshold;
            var dead = rows.Where((r, k) => k < passes.Count ? passes[k] : r.s >= th).Select(r => r.t).ToList();
            double maxNot = rows.Where((r, k) => !(k < passes.Count ? passes[k] : r.s >= th)).Select(r => r.s).DefaultIfEmpty(0).Max();
            double minDead = rows.Where((r, k) => k < passes.Count && passes[k]).Select(r => r.s).DefaultIfEmpty(0).Min();
            s.Calibration.TryGetValue(rect.Key, out var c);
            Console.WriteLine($"{Path.GetFileName(clip)} window {W}x{H} picture {cw}x{ch}{(DeathDetector.NaiveGeometry ? " naive" : "")}: " +
                              $"frames={rows.Count} dead={dead.Count} dead-span={(dead.Count > 0 ? $"{dead[0]}-{dead[^1]}s" : "-")} " +
                              $"max-not-dead={maxNot:+0.00;-0.00} min-dead={minDead:0.00} calibrated-at={(calAt.HasValue ? calAt + "s" : "-")} " +
                              $"saved={(c == null ? "none" : $"{c.Ox},{c.Oy} x{c.K}")} " +
                              (wideMs.Count > 0 ? $"wide-search={wideMs.Average():0}ms/frame" : ""));
            if (Flag(a, "--verbose")) Console.WriteLine("  " + string.Join(" ", rows.Select(r => $"{r.t}:{r.s:0.00}")));
        }
        return 0;
    }

    private static bool ReadFull(Stream s, byte[] buf)
    {
        int got = 0;
        while (got < buf.Length)
        {
            int n = s.Read(buf, got, buf.Length - got);
            if (n <= 0) return false;
            got += n;
        }
        return true;
    }

    // -------------------------------------------------------------- live
    private static int Check(string[] a)
    {
        var s = TempSettings();
        var det = new DeathDetector(s, new DesktopScreen());
        var gw = new GameWindow();
        int n = int.Parse(Arg(a, "--seconds") ?? "5", CultureInfo.InvariantCulture);
        for (int i = 0; i < n; i++)
        {
            var r = gw.FindRect();
            if (r == null) Console.WriteLine($"{DateTime.Now:HH:mm:ss} VALORANT window not found");
            else
            {
                var (sc, name, _) = det.Score(r.Value);
                var hit = det.WideSearch(r.Value);
                double mean = det.Screen.Grab(new Rect(r.Value.X, r.Value.Y + r.Value.H / 2, r.Value.W / 2, r.Value.H / 2)).Mean();
                Console.WriteLine($"{DateTime.Now:HH:mm:ss} window {r.Value.W}x{r.Value.H} at {r.Value.X},{r.Value.Y} brightness={mean:0} " +
                                  $"spot={sc:+0.00;-0.00} ({name}) wide={hit.Score:+0.00;-0.00} ({hit.Tr.Ox:0},{hit.Tr.Oy:0} x{hit.Tr.K:0.00})");
            }
            Thread.Sleep(1000);
        }
        return 0;
    }

    private static int MediaInfoCmd()
    {
        var s = TempSettings();
        foreach (var m in MediaController.ListSessions())
            Console.WriteLine($"session: {m.AppId} | {MediaController.FriendlyName(m.AppId)} | {(m.Playing ? "playing" : "not playing")} | candidates: {string.Join(",", MediaController.ProcessCandidates(m.AppId))}");
        using var mc = new MediaController(s);
        Console.WriteLine("picked (Spotify): " + mc.Describe());
        s.MediaApp = "Any";
        Console.WriteLine("picked (Any): " + mc.Describe());
        return 0;
    }

    private static int FadeTest()
    {
        // ramps Spotify's own mixer volume to 60 % and back without pausing anything
        using var en = new NAudio.CoreAudioApi.MMDeviceEnumerator();
        var vols = new List<(string proc, NAudio.CoreAudioApi.SimpleAudioVolume v)>();
        foreach (var dev in en.EnumerateAudioEndPoints(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.DeviceState.Active))
        {
            var mgr = dev.AudioSessionManager;
            mgr.RefreshSessions();
            for (int i = 0; i < mgr.Sessions.Count; i++)
            {
                var ses = mgr.Sessions[i];
                try
                {
                    string p = Process.GetProcessById((int)ses.GetProcessID).ProcessName;
                    if (p.Contains("spotify", StringComparison.OrdinalIgnoreCase)) vols.Add((p + "@" + dev.FriendlyName, ses.SimpleAudioVolume));
                }
                catch { }
            }
        }
        if (vols.Count == 0) return Print("no Spotify mixer entry (Spotify has to have played audio since it started)");
        foreach (var (p, v) in vols)
        {
            float v0 = v.Volume;
            Console.WriteLine($"{p}: volume {v0:0.00}");
            for (int i = 1; i <= 20; i++) { v.Volume = v0 * (1 - 0.4f * i / 20); Thread.Sleep(25); }
            Console.WriteLine($"  faded to {v.Volume:0.00}");
            for (int i = 1; i <= 20; i++) { v.Volume = v0 * (0.6f + 0.4f * i / 20); Thread.Sleep(25); }
            v.Volume = v0;
            Console.WriteLine($"  restored to {v.Volume:0.00}");
        }
        return 0;
    }

    private static int Bench()
    {
        var s = TempSettings();
        var det = new DeathDetector(s, new DesktopScreen());
        var r = new GameWindow().FindRect() ?? new Rect(0, 0, 3840, 2160);
        det.Score(r);
        var proc = Process.GetCurrentProcess();
        var cpu0 = proc.TotalProcessorTime;
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 100; i++) det.Score(r);
        proc.Refresh();
        double wall = sw.Elapsed.TotalMilliseconds / 100, cpu = (proc.TotalProcessorTime - cpu0).TotalMilliseconds / 100;
        Console.WriteLine($"window {r.W}x{r.H}");
        Console.WriteLine($"death check: {wall:0.0} ms wall, {cpu:0.0} ms CPU; at 8/s = {cpu * 8 / 10:0.0}% of one core during live rounds");
        var box = new Rect(r.X + 230, r.Y + 1690, 340, 70);
        cpu0 = proc.TotalProcessorTime;
        for (int i = 0; i < 100; i++) det.Screen.Grab(box);
        proc.Refresh();
        Console.WriteLine($"  of which screenshot: {(proc.TotalProcessorTime - cpu0).TotalMilliseconds / 100:0.00} ms CPU");
        var frame = det.Screen.Grab(box);
        using (var gdi = det.Screen.GrabColor(box))
        {
            var g2 = GrayImage.FromBitmap(gdi);
            double diff = 0;
            for (int i = 0; i < frame.P.Length; i++) diff += Math.Abs(frame.P[i] - g2.P[i]);
            Console.WriteLine($"  DIB grab vs GDI+ grab: mean abs difference {diff / frame.P.Length:0.000} (0 = identical pixels), brightness {frame.Mean():0}");
        }
        cpu0 = proc.TotalProcessorTime;
        var t0 = det.Templates[0];
        for (int i = 0; i < 100; i++) frame.Resize(t0.W + 8, t0.H + 8).Bright();
        proc.Refresh();
        Console.WriteLine($"  of which resize+bright: {(proc.TotalProcessorTime - cpu0).TotalMilliseconds / 100:0.00} ms CPU");
        det.WideSearch(r);
        cpu0 = proc.TotalProcessorTime;
        sw.Restart();
        for (int i = 0; i < 5; i++) det.WideSearch(r);
        proc.Refresh();
        double wcpu = (proc.TotalProcessorTime - cpu0).TotalMilliseconds / 5;
        Console.WriteLine($"wide search: {sw.Elapsed.TotalMilliseconds / 5:0} ms wall, {wcpu:0} ms CPU; every 2 s = {wcpu / 20:0.0}% of one core, only until first death");
        return 0;
    }

    // -------------------------------------------------------------- render (screenshots of the UI, off-screen)
    private static int Render(string[] a)
    {
        string dir = a.Length > 1 ? a[1] : ".";
        Directory.CreateDirectory(dir);
        foreach (Status st in Enum.GetValues(typeof(Status)))
            using (var b = UI.TrayApp.RenderTrayBitmap(st, 64)) b.Save(Path.Combine(dir, $"tray_{st}.png"));
        var s = TempSettings();
        var tray = new UI.TrayApp(s, true);   // the one Application instance (not run)
        var engine = new Engine(s, null, dryRun: true);
        Snap(new UI.SettingsWindow(engine, tray), Path.Combine(dir, "settings.png"));
        Snap(new UI.LearnWindow(engine), Path.Combine(dir, "learn.png"));
        engine.Dispose();
        Console.WriteLine($"rendered to {dir}");
        return 0;
    }

    private static void Snap(System.Windows.Window w, string path)
    {
        w.WindowStartupLocation = System.Windows.WindowStartupLocation.Manual;
        w.Left = -20000;
        w.Top = -20000;
        w.ShowActivated = false;
        w.ShowInTaskbar = false;
        w.Show();
        w.UpdateLayout();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        var el = (System.Windows.FrameworkElement)w.Content;
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(el.ActualWidth), (int)Math.Ceiling(el.ActualHeight), 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        var visual = new System.Windows.Media.DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(w.Background, null, new System.Windows.Rect(0, 0, el.ActualWidth, el.ActualHeight));
            dc.DrawRectangle(new System.Windows.Media.VisualBrush(el), null, new System.Windows.Rect(0, 0, el.ActualWidth, el.ActualHeight));
        }
        bmp.Render(visual);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        using (var fs = File.Create(path)) enc.Save(fs);
        w.Close();
    }
}

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using Windows.Media.Control;

namespace ValSpot.Core;

public sealed record MediaInfo(string AppId, string Title, bool Playing);

/// <summary>Plays/pauses through Windows media controls (the volume-flyout buttons), so it
/// works with Spotify, YouTube Music, Apple Music, browsers... Fades by ramping that app's
/// own volume in the Windows mixer, then restores it. One worker thread; a newer command
/// cancels a fade still in progress.</summary>
public sealed class MediaController : IDisposable
{
    private readonly Settings _s;
    private readonly BlockingCollection<(bool play, string why)> _queue = new();
    private readonly Thread _worker;
    private CancellationTokenSource _cts = new();
    private readonly object _ctsGate = new();
    private readonly Dictionary<string, float> _baseline = new(StringComparer.OrdinalIgnoreCase);
    public bool DryRun { get; set; }
    public MediaInfo? LastSeen { get; private set; }

    public MediaController(Settings s)
    {
        _s = s;
        foreach (var kv in s.FadeBaselines) _baseline[kv.Key] = kv.Value;
        _worker = new Thread(Work) { IsBackground = true, Name = "ValSpot media" };
        _worker.SetApartmentState(ApartmentState.MTA);
        _worker.Start();
    }

    public void Play(string why) => Enqueue(true, why);
    public void Pause(string why) => Enqueue(false, why);

    private void Enqueue(bool play, string why)
    {
        lock (_ctsGate) _cts.Cancel();
        _queue.Add((play, why));
    }

    private void Work()
    {
        foreach (var (play, why) in _queue.GetConsumingEnumerable())
        {
            CancellationToken token;
            lock (_ctsGate)
            {
                if (_queue.Count > 0) continue; // a newer command is waiting; skip this one
                _cts = new CancellationTokenSource();
                token = _cts.Token;
            }
            try { Do(play, why, token); }
            catch (Exception e) { Log.Write($"MUSIC {(play ? "PLAY " : "PAUSE")} [error {e.GetType().Name}: {e.Message}] <- {why}"); }
        }
    }

    // ------------------------------------------------------------------ media sessions
    private static GlobalSystemMediaTransportControlsSessionManager Manager() =>
        GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask().GetAwaiter().GetResult();

    public static List<MediaInfo> ListSessions()
    {
        var list = new List<MediaInfo>();
        try
        {
            foreach (var s in Manager().GetSessions())
            {
                string title = "";
                try
                {
                    var p = s.TryGetMediaPropertiesAsync().AsTask().GetAwaiter().GetResult();
                    title = string.IsNullOrEmpty(p.Artist) ? p.Title : $"{p.Artist} - {p.Title}";
                }
                catch { /* some apps don't expose properties */ }
                list.Add(new MediaInfo(s.SourceAppUserModelId ?? "", title,
                    s.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing));
            }
        }
        catch (Exception e) { Log.Write($"WARN media sessions unavailable: {e.Message}"); }
        return list;
    }

    private GlobalSystemMediaTransportControlsSession? Pick()
    {
        var mgr = Manager();
        // the Riot Client registers a media session for lobby audio; never treat it as "your music"
        var sessions = mgr.GetSessions()
            .Where(x => !(x.SourceAppUserModelId ?? "").Contains("riotgames", StringComparison.OrdinalIgnoreCase)).ToList();
        string want = (_s.MediaApp ?? "Spotify").Trim();
        if (want.Equals("Any", StringComparison.OrdinalIgnoreCase) || want.Length == 0)
        {
            return sessions.FirstOrDefault(x => x.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                   ?? (mgr.GetCurrentSession() is { } cur && sessions.Contains(cur) ? cur : sessions.FirstOrDefault());
        }
        return sessions.FirstOrDefault(x => (x.SourceAppUserModelId ?? "").Contains(want, StringComparison.OrdinalIgnoreCase));
    }

    public string Describe()
    {
        try
        {
            var s = Pick();
            if (s == null) return $"{_s.MediaApp} not open";
            bool playing = s.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            return $"{FriendlyName(s.SourceAppUserModelId)} ({(playing ? "playing" : "paused")})";
        }
        catch (Exception e) { return $"media controls unavailable ({e.Message})"; }
    }

    public static string FriendlyName(string? aumid)
    {
        if (string.IsNullOrEmpty(aumid)) return "?";
        string a = aumid;
        if (a.Contains('!')) a = a[(a.IndexOf('!') + 1)..];
        if (a.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) a = a[..^4];
        return a switch { "308046B0AF4A39CB" => "Firefox", "MSEdge" => "Edge", "App" => aumid.Split('_')[0].Split('.').Last(), _ => a };
    }

    /// <summary>Process names whose mixer volume belongs to this media app.</summary>
    public static List<string> ProcessCandidates(string aumid)
    {
        var c = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string a = aumid;
        if (a.Contains('!'))
        {
            c.Add(a[(a.IndexOf('!') + 1)..]);
            string pkg = a[..a.IndexOf('!')].Split('_')[0];
            c.Add(pkg.Split('.').Last());
            a = pkg;
        }
        if (a.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) a = a[..^4];
        c.Add(System.IO.Path.GetFileNameWithoutExtension(a));
        if (aumid == "308046B0AF4A39CB") c.Add("firefox");
        if (aumid.Equals("MSEdge", StringComparison.OrdinalIgnoreCase)) c.Add("msedge");
        c.RemoveWhere(x => x.Length < 3 || x.Equals("App", StringComparison.OrdinalIgnoreCase));
        return c.ToList();
    }

    // ------------------------------------------------------------------ mixer volume
    private sealed record Vol(string Proc, SimpleAudioVolume V);

    private static List<Vol> FindVolumes(string aumid)
    {
        var cands = ProcessCandidates(aumid);
        var found = new List<Vol>();
        using var en = new MMDeviceEnumerator();
        foreach (var dev in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            var mgr = dev.AudioSessionManager;
            mgr.RefreshSessions();
            var sessions = mgr.Sessions;
            for (int i = 0; i < sessions.Count; i++)
            {
                var ses = sessions[i];
                string proc;
                try { proc = Process.GetProcessById((int)ses.GetProcessID).ProcessName; }
                catch { continue; }
                if (cands.Any(c => proc.Contains(c, StringComparison.OrdinalIgnoreCase) || c.Contains(proc, StringComparison.OrdinalIgnoreCase)))
                    found.Add(new Vol(proc, ses.SimpleAudioVolume));
            }
        }
        return found;
    }

    private float BaselineFor(Vol v)
    {
        float now = v.V.Volume;
        if (!_s.FadeInProgress && now > 0.02f) _baseline[v.Proc] = now;      // at rest: remember it
        return _baseline.TryGetValue(v.Proc, out float b) ? b : Math.Max(now, 1f);
    }

    private void MarkFading(bool on)
    {
        _s.FadeInProgress = on;
        _s.FadeBaselines = new Dictionary<string, float>(_baseline);
        _s.Save();
    }

    /// <summary>If ValSpot stopped in the middle of a fade, put the app volume back.</summary>
    public void RecoverAfterCrash()
    {
        if (!_s.FadeInProgress) return;
        try
        {
            var s = Pick();
            if (s != null)
                foreach (var v in FindVolumes(s.SourceAppUserModelId ?? ""))
                    if (_baseline.TryGetValue(v.Proc, out float b)) v.V.Volume = b;
            Log.Write("restored app volume left over from an interrupted fade");
        }
        catch (Exception e) { Log.Write($"WARN could not restore volume: {e.Message}"); }
        MarkFading(false);
    }

    public void RestoreNow()
    {
        lock (_ctsGate) _cts.Cancel();
        try
        {
            var s = Pick();
            if (s == null) return;
            foreach (var v in FindVolumes(s.SourceAppUserModelId ?? ""))
                if (_baseline.TryGetValue(v.Proc, out float b)) v.V.Volume = b;
        }
        catch { /* shutting down */ }
        if (_s.FadeInProgress) MarkFading(false);
    }

    private static void Ramp(List<(Vol v, float from, float to)> plan, int ms, CancellationToken token)
    {
        int steps = Math.Max(1, ms / 25);
        for (int i = 1; i <= steps; i++)
        {
            if (token.IsCancellationRequested) return;
            float f = (float)i / steps;
            foreach (var (v, from, to) in plan)
            {
                try { v.V.Volume = Math.Clamp(from + (to - from) * f, 0f, 1f); } catch { }
            }
            Thread.Sleep(25);
        }
    }

    // ------------------------------------------------------------------ play / pause
    private void Do(bool play, string why, CancellationToken token)
    {
        string verb = play ? "PLAY " : "PAUSE";
        if (DryRun)
        {
            Log.Write($"MUSIC {verb} (dry-run) <- {why}");
            return;
        }
        var s = Pick();
        if (s == null)
        {
            Log.Write($"MUSIC {verb} [{_s.MediaApp} not open] <- {why}");
            return;
        }
        string app = FriendlyName(s.SourceAppUserModelId);
        bool playing = s.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        LastSeen = new MediaInfo(s.SourceAppUserModelId ?? "", app, playing);
        if (play == playing)
        {
            Log.Write($"MUSIC {verb} [already] <- {why} ({app})");
            return;
        }
        int fadeMs = _s.Fade ? (int)(Math.Clamp(_s.FadeSeconds, 0, 5) * 1000) : 0;
        List<Vol> vols = fadeMs > 0 ? FindVolumes(s.SourceAppUserModelId ?? "") : new();
        bool ok;
        if (play)
        {
            var plan = vols.Select(v => (v, 0f, BaselineFor(v))).ToList();
            if (plan.Count > 0)
            {
                MarkFading(true);
                foreach (var p in plan) p.v.V.Volume = 0f;
            }
            ok = s.TryPlayAsync().AsTask().GetAwaiter().GetResult();
            if (plan.Count == 0 && fadeMs > 0)
            {
                // the app may only create its audio stream once playing: fade that one in
                for (int i = 0; i < 12 && vols.Count == 0; i++)
                {
                    Thread.Sleep(40);
                    vols = FindVolumes(s.SourceAppUserModelId ?? "");
                }
                plan = vols.Select(v => (v, 0f, BaselineFor(v))).ToList();
                if (plan.Count > 0)
                {
                    MarkFading(true);
                    foreach (var p in plan) p.v.V.Volume = 0f;
                }
            }
            if (plan.Count > 0)
            {
                Ramp(plan, fadeMs, token);
                if (token.IsCancellationRequested) foreach (var p in plan) p.v.V.Volume = p.Item3;
                MarkFading(false);
            }
        }
        else
        {
            var plan = vols.Select(v => (v, v.V.Volume, 0f)).ToList();
            var targets = vols.Select(BaselineFor).ToList();
            if (plan.Count > 0)
            {
                MarkFading(true);
                Ramp(plan, fadeMs, token);
            }
            ok = s.TryPauseAsync().AsTask().GetAwaiter().GetResult();
            if (plan.Count > 0)
            {
                Thread.Sleep(150);     // let the pause land before the volume comes back
                for (int i = 0; i < plan.Count; i++)
                {
                    try { plan[i].v.V.Volume = targets[i]; } catch { }
                }
                MarkFading(false);
            }
        }
        string how = vols.Count > 0 ? $"fade {fadeMs / 1000.0:0.0}s" : (fadeMs > 0 ? "no mixer entry, no fade" : "no fade");
        Log.Write($"MUSIC {verb} [{(ok ? "ok" : "refused")}, {how}] <- {why} ({app})");
    }

    public void Dispose()
    {
        RestoreNow();
        _queue.CompleteAdding();
    }
}

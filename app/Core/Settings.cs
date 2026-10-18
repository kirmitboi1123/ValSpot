using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ValSpot.Core;

public sealed class Calibration
{
    public double Ox { get; set; }
    public double Oy { get; set; }
    public double K { get; set; }
}

/// <summary>A "SWITCH PLAYER" marker image and where it sits, in a frame RefHeight pixels tall,
/// relative to the transform origin (the 16:9 picture's top-left by default).</summary>
public sealed class TemplateSpec
{
    public string File { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }
    public double RefHeight { get; set; } = 1080;
}

public sealed class Settings
{
    // --- what the user sees in the settings window
    public bool PlayOnDeath { get; set; } = true;
    public bool PlayInBuy { get; set; } = true;
    public bool PlayInLobby { get; set; } = true;
    public double PauseLeadSeconds { get; set; } = 3.0;
    public bool Fade { get; set; } = true;
    public double FadeSeconds { get; set; } = 0.8;
    /// <summary>"Spotify", "Any" (whatever is playing), or part of an app's name.</summary>
    public string MediaApp { get; set; } = "Spotify";
    public bool CheckForUpdates { get; set; } = true;
    public bool Paused { get; set; }
    public bool FirstRunDone { get; set; }

    // --- tuning (measured defaults; see README)
    public bool OnlyWhenFocused { get; set; } = true;
    public int BuySeconds { get; set; } = 30;
    public int FirstBuySeconds { get; set; } = 45;
    /// <summary>Round index after which the long side-swap buy phase comes, per mode. Standard
    /// matches swap after round 12 (index 11), Swiftplay after round 4 (index 3); other modes
    /// are learned the first time a long mid-match buy phase shows up.</summary>
    public Dictionary<string, int> HalfTimeByMode { get; set; } = new() { ["Bomb"] = 11, ["Swiftplay"] = 3 };
    public int PollHz { get; set; } = 8;
    public double DeathThreshold { get; set; } = 0.60;
    public double CalibrateThreshold { get; set; } = 0.80;
    /// <summary>Hand-drawn markers have to match this well (they're less tested than the built-in ones).</summary>
    public double CustomThreshold { get; set; } = 0.80;
    public int ConfirmFrames { get; set; } = 3;
    public int ReviveFrames { get; set; } = 32;
    public int SearchPad { get; set; } = 4;
    public double CalibrateIntervalSeconds { get; set; } = 2.0;
    public bool Diagnostics { get; set; } = true;
    public double DiagnosticsIntervalSeconds { get; set; } = 8.0;
    public int DiagnosticsKeep { get; set; } = 40;

    // --- written by ValSpot
    public Dictionary<string, Calibration> Calibration { get; set; } = new();
    public List<TemplateSpec> CustomTemplates { get; set; } = new();
    /// <summary>Window sizes where a death has been seen at least once (stops diagnostics).</summary>
    public HashSet<string> DeathSeen { get; set; } = new();
    /// <summary>Per-process app volume before a fade; restored if ValSpot stopped mid-fade.</summary>
    public Dictionary<string, float> FadeBaselines { get; set; } = new();
    public bool FadeInProgress { get; set; }
    public DateTime LastUpdateCheck { get; set; }

    // ------------------------------------------------------------------
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>Folder for settings, log, custom templates and diagnostics.
    /// Portable mode: if settings.json sits next to the exe, everything lives there.</summary>
    public static string Dir { get; private set; } = DefaultDir();
    public static string FilePath => Path.Combine(Dir, "settings.json");
    public static Settings Current { get; private set; } = new();

    private static string DefaultDir()
    {
        string exeDir = AppContext.BaseDirectory;
        if (System.IO.File.Exists(Path.Combine(exeDir, "settings.json"))) return exeDir;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ValSpot");
    }

    public static void UseDir(string dir) => Dir = dir;

    public static Settings Load()
    {
        lock (Gate)
        {
            Directory.CreateDirectory(Dir);
            Settings s;
            try
            {
                s = System.IO.File.Exists(FilePath)
                    ? JsonSerializer.Deserialize<Settings>(System.IO.File.ReadAllText(FilePath), Json) ?? new Settings()
                    : new Settings();
            }
            catch (Exception e)
            {
                Log.Write($"WARN settings.json unreadable ({e.Message}); using defaults");
                s = new Settings();
            }
            Current = s;
            return s;
        }
    }

    public void Save()
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                string tmp = FilePath + ".tmp";
                System.IO.File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
                System.IO.File.Move(tmp, FilePath, true);
            }
            catch (Exception e)
            {
                Log.Write($"WARN could not save settings: {e.Message}");
            }
        }
    }
}

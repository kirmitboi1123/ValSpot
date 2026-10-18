using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace ValSpot.Core;

public enum GameEventKind { Mode, MatchStart, Pregame, Buy, Combat, RoundEnd, MatchEnd }

public readonly record struct GameEvent(GameEventKind Kind, string Value, double Number, DateTime Stamp);

/// <summary>Turns ShooterGame.log lines into round events. Same lines as the Python version:
/// "Gameplay started at local time 0.x" = buy start, "30.x"/"45.x" = barriers drop,
/// "OnRoundEnded for round 'N'", GameFlow "-> InGame" / "-> MainMenu", "/Game/GameModes/X/".</summary>
public static class GameLogParser
{
    private static readonly Regex Stamp = new(@"^\[(\d{4})\.(\d{2})\.(\d{2})-(\d{2})\.(\d{2})\.(\d{2}):(\d{3})\]", RegexOptions.Compiled);
    private static readonly Regex Gameplay = new(@"Gameplay started at local time ([\d.]+)", RegexOptions.Compiled);
    private static readonly Regex RoundEnd = new(@"OnRoundEnded for round '(\d+)'", RegexOptions.Compiled);
    private static readonly Regex Flow = new(@"LogGameFlowStateManager: .*Transitioning from (\w+) to (\w+)", RegexOptions.Compiled);
    private static readonly Regex Mode = new(@"/Game/GameModes/(\w+)/", RegexOptions.Compiled);

    public static GameEvent? Parse(string line)
    {
        var m = Gameplay.Match(line);
        if (m.Success)
        {
            double t = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            if (t < 5.0) return new GameEvent(GameEventKind.Buy, "", t, StampOf(line));
            if (t >= 20.0) return new GameEvent(GameEventKind.Combat, "", t, StampOf(line));
            return null;
        }
        m = RoundEnd.Match(line);
        if (m.Success) return new GameEvent(GameEventKind.RoundEnd, "", int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), StampOf(line));
        m = Mode.Match(line);
        if (m.Success) return new GameEvent(GameEventKind.Mode, m.Groups[1].Value, 0, StampOf(line));
        m = Flow.Match(line);
        if (m.Success)
        {
            string dst = m.Groups[2].Value;
            if (dst == "InGame") return new GameEvent(GameEventKind.MatchStart, dst, 0, StampOf(line));
            if (dst is "TransitionToMainMenu" or "MainMenu") return new GameEvent(GameEventKind.MatchEnd, dst, 0, StampOf(line));
            if (dst is "TransitionToPregame" or "Pregame") return new GameEvent(GameEventKind.Pregame, dst, 0, StampOf(line));
        }
        return null;
    }

    public static DateTime StampOf(string line)
    {
        var m = Stamp.Match(line);
        if (!m.Success) return DateTime.MinValue;
        try
        {
            int G(int i) => int.Parse(m.Groups[i].Value, CultureInfo.InvariantCulture);
            return new DateTime(G(1), G(2), G(3), G(4), G(5), G(6), G(7));
        }
        catch (ArgumentOutOfRangeException) { return DateTime.MinValue; }
    }
}

/// <summary>Follows ShooterGame.log. Opened with FILE_SHARE_READ|WRITE|DELETE so VALORANT can
/// still rename/delete it at launch; reopens (from the start) when VALORANT begins a new log.</summary>
public sealed class GameLogTailer
{
    private readonly string _path;
    private readonly ConcurrentQueue<GameEvent> _out;
    private Thread? _thread;
    private volatile bool _stop;

    public GameLogTailer(string path, ConcurrentQueue<GameEvent> output)
    {
        _path = path;
        _out = output;
    }

    public void Start()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "ValSpot log tailer" };
        _thread.Start();
    }

    public void Stop() => _stop = true;

    private void Run()
    {
        FileStream? fs = null;
        (uint vol, ulong idx)? ident = null;
        bool firstOpen = true;
        var pending = new StringBuilder();
        var buf = new byte[64 * 1024];
        var chars = new char[64 * 1024 + 8];
        var decoder = Encoding.UTF8.GetDecoder();   // keeps multi-byte characters intact across reads
        var lastIdentCheck = DateTime.MinValue;
        while (!_stop)
        {
            try
            {
                if (!File.Exists(_path))
                {
                    if (fs != null)
                    {
                        fs.Dispose();
                        fs = null;
                        Log.Write("VALORANT log gone (game closed?); waiting");
                    }
                    Thread.Sleep(2000);
                    continue;
                }
                bool reopen = fs == null;
                if (!reopen && (DateTime.UtcNow - lastIdentCheck).TotalSeconds >= 1)
                {
                    lastIdentCheck = DateTime.UtcNow;
                    var now = FileId(_path);
                    if (now != null && ident != null && now != ident) reopen = true;
                    else if (new FileInfo(_path).Length < fs!.Position) reopen = true;
                }
                if (reopen)
                {
                    if (fs != null)
                    {
                        fs.Dispose();
                        Log.Write("VALORANT started a new log; following it");
                    }
                    else
                    {
                        Log.Write($"following {_path}");
                    }
                    fs = new FileStream(_path, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
                    ident = FileId(_path);
                    // first open: skip history; a fresh log after rotation: read all of it
                    fs.Seek(0, firstOpen ? SeekOrigin.End : SeekOrigin.Begin);
                    firstOpen = false;
                    pending.Clear();
                    decoder.Reset();
                }
                int n = fs!.Read(buf, 0, buf.Length);
                if (n <= 0)
                {
                    Thread.Sleep(100);
                    continue;
                }
                int c = decoder.GetChars(buf, 0, n, chars, 0);
                pending.Append(chars, 0, c);
                string text = pending.ToString();
                int last = text.LastIndexOf('\n');
                if (last < 0) continue;
                foreach (string raw in text.Substring(0, last).Split('\n'))
                {
                    var ev = GameLogParser.Parse(raw);
                    if (ev != null) _out.Enqueue(ev.Value);
                }
                pending.Clear();
                pending.Append(text, last + 1, text.Length - last - 1);
            }
            catch (IOException)
            {
                fs?.Dispose();
                fs = null;
                Thread.Sleep(1000);
            }
            catch (UnauthorizedAccessException)
            {
                fs?.Dispose();
                fs = null;
                Thread.Sleep(1000);
            }
        }
        fs?.Dispose();
    }

    // --- file identity (volume serial + file index), so a renamed/recreated log is noticed
    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Written;
        public uint VolumeSerial, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr sec, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(IntPtr h, out ByHandleFileInformation info);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr h);

    private static (uint, ulong)? FileId(string path)
    {
        // access 0 = attributes only; share everything so VALORANT is never blocked
        IntPtr h = CreateFileW(path, 0, 0x1 | 0x2 | 0x4, IntPtr.Zero, 3, 0x80, IntPtr.Zero);
        if (h == IntPtr.Zero || h == new IntPtr(-1)) return null;
        try
        {
            return GetFileInformationByHandle(h, out var i)
                ? (i.VolumeSerial, ((ulong)i.IndexHigh << 32) | i.IndexLow)
                : null;
        }
        finally { CloseHandle(h); }
    }
}

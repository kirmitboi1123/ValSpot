using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ValSpot.Core;

/// <summary>Timestamped log to valspot.log (rotated at 5 MB), the console, and listeners.</summary>
public static class Log
{
    private static readonly object Gate = new();
    private static readonly LinkedList<string> RecentLines = new();
    public static string? FilePath { get; set; }
    public static event Action<string>? Line;

    public static IReadOnlyList<string> Recent
    {
        get { lock (Gate) return RecentLines.ToList(); }
    }

    public static void Write(string message)
    {
        string line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
        lock (Gate)
        {
            RecentLines.AddLast(line);
            while (RecentLines.Count > 400) RecentLines.RemoveFirst();
            if (FilePath != null)
            {
                try
                {
                    var fi = new FileInfo(FilePath);
                    if (fi.Exists && fi.Length > 5 * 1024 * 1024)
                    {
                        string old = FilePath + ".old";
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(FilePath, old);
                    }
                    File.AppendAllText(FilePath, line + Environment.NewLine);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        try { Console.WriteLine(line); } catch { /* no console */ }
        try { Line?.Invoke(line); } catch { /* listener problems never break logging */ }
    }
}

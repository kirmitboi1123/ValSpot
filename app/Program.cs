using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using ValSpot.Core;
using ValSpot.UI;

namespace ValSpot;

public static class Program
{
    public static string Version { get; } =
        (Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "3.0.0")
        .Split('+')[0];

    private const string MutexName = @"Local\ValSpot.SingleInstance";
    private const string ShowEventName = @"Local\ValSpot.ShowSettings";

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0].StartsWith("--") && args[0] != "--tray")
        {
            AttachConsole(-1);
            return Cli.Run(args);
        }

        using var mutex = new Mutex(true, MutexName, out bool first);
        if (!first)
        {
            // already running: ask that copy to show its settings window
            try { EventWaitHandle.OpenExisting(ShowEventName).Set(); } catch { }
            return 0;
        }

        var settings = Settings.Load();
        Log.FilePath = Path.Combine(Settings.Dir, "valspot.log");
        var app = new TrayApp(settings, startHidden: Array.IndexOf(args, "--tray") >= 0);
        using var show = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        var listener = new Thread(() =>
        {
            while (show.WaitOne()) app.Dispatcher.BeginInvoke(new Action(app.ShowSettings));
        }) { IsBackground = true };
        listener.Start();
        app.Run();
        return 0;
    }

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int pid);
}

/// <summary>"Start with Windows": a per-user Run entry that launches ValSpot quietly in the tray.</summary>
public static class Startup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static string ExePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "ValSpot.exe");

    public static bool IsEnabled()
    {
        using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue("ValSpot") is string v && v.Contains(ExePath, StringComparison.OrdinalIgnoreCase);
    }

    public static void Set(bool on)
    {
        using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) k.SetValue("ValSpot", $"\"{ExePath}\" --tray");
        else k.DeleteValue("ValSpot", false);
        Log.Write(on ? "start with Windows: on" : "start with Windows: off");
    }
}

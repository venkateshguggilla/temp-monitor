#if !WINDOWS
using System.Diagnostics;
using System.Runtime.InteropServices;
using TempMonitor.Fans;
using TempMonitor.History;

namespace TempMonitor.App;

/// <summary>
/// "TempMonitor --monitor": the background monitor as a systemd user service
/// (temp-monitor-background.service). History goes to ~/.local/state/temp-monitor/history.db,
/// settings come from ~/.config/temp-monitor/monitor.json.
/// </summary>
internal static class MonitorHost
{
    private static string Xdg(string variable, string fallback) =>
        Environment.GetEnvironmentVariable(variable) is { Length: > 0 } v ? v
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), fallback);

    public static string StateDir => Path.Combine(Xdg("XDG_STATE_HOME", ".local/state"), "temp-monitor");
    public static string HistoryPath => Path.Combine(StateDir, "history.db");
    public static string SettingsPath => Path.Combine(Xdg("XDG_CONFIG_HOME", ".config"), "temp-monitor", "monitor.json");

    public static int Run()
    {
        Directory.CreateDirectory(StateDir);
        // One monitor per user: a second copy (e.g. started by hand) just exits
        FileStream lockFile;
        try { lockFile = new FileStream(Path.Combine(StateDir, "monitor.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException)
        {
            Console.Error.WriteLine("The background monitor is already running.");
            return 0;
        }

        using (lockFile)
        using (var stop = new CancellationTokenSource())
        {
            void OnSignal(PosixSignalContext ctx)
            {
                ctx.Cancel = true;
                stop.Cancel();
            }
            using var term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, OnSignal);
            using var intr = PosixSignalRegistration.Create(PosixSignal.SIGINT, OnSignal);

            var settings = MonitorSettings.Load(SettingsPath);
            using var sensors = new LinuxSensorProvider();
            using var store = new HistoryStore(HistoryPath);
            var fans = new FanClient();
            var monitor = new BackgroundMonitor(sensors, store, new NotifySend(),
                () => fans.Send(new FanRequest { Cmd = "status" }) is { Ok: true } r ? r.Status : null,
                settings, log: Console.Error.WriteLine);
            Console.Error.WriteLine($"background monitor running: history in {HistoryPath}");
            monitor.Run(stop.Token);
        }
        return 0;
    }

    /// <summary>Desktop notifications through notify-send (libnotify), which every GNOME desktop has.</summary>
    private sealed class NotifySend : INotifier
    {
        public void Notify(string title, string message, bool critical)
        {
            var psi = new ProcessStartInfo("notify-send")
            {
                ArgumentList = { "--app-name=Temp Monitor", "--icon=temp-monitor", $"--urgency={(critical ? "critical" : "normal")}", title, message },
                UseShellExecute = false,
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(5000);
        }
    }
}
#endif

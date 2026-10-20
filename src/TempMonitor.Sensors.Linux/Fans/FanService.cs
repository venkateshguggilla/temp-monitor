using System.Runtime.InteropServices;

namespace TempMonitor.Fans;

/// <summary>Entry points used by the systemd unit (temp-monitor-fans.service).</summary>
public static class FanService
{
    /// <summary>"TempMonitor --fan-daemon": the root fan service. Stops cleanly on SIGTERM / SIGINT.</summary>
    public static int RunDaemon()
    {
        if (!Environment.IsPrivilegedProcess)
        {
            Console.Error.WriteLine("The fan service must run as root; systemd starts it (temp-monitor-fans.service).");
            return 1;
        }
        using var stop = new CancellationTokenSource();
        void OnSignal(PosixSignalContext ctx)
        {
            ctx.Cancel = true; // let Run() hand the fans back before exiting
            stop.Cancel();
        }
        using var term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, OnSignal);
        using var intr = PosixSignalRegistration.Create(PosixSignal.SIGINT, OnSignal);

        using var provider = new LinuxSensorProvider();
        var controller = new FanController(provider, FanDaemon.ConfigPath, FanDaemon.RestorePath, log: Log);
        new FanDaemon(controller, new PolkitAuthorizer()).Run(stop.Token, Log);
        return 0;
    }

    /// <summary>
    /// "TempMonitor --fan-restore" (ExecStopPost): if the service died without handing the fans
    /// back (crash, kill), put firmware back in control from the restore file.
    /// </summary>
    public static int Restore()
    {
        using var nvml = new Nvml();
        FanController.RestoreFromFile(FanDaemon.RestorePath, new FanBackend(new Sysfs("/"), nvml), Log);
        return 0;
    }

    private static void Log(string message) => Console.Error.WriteLine(message);
}

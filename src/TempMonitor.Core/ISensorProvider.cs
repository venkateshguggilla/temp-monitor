namespace TempMonitor;

/// <summary>
/// Reads the hardware sensors of one platform: LibreHardwareMonitor on Windows,
/// hwmon/sysfs on Linux. UIs only talk to this interface.
/// </summary>
public interface ISensorProvider : IDisposable
{
    /// <summary>One reading per device (CPU, each GPU, RAM, each drive), refreshed on every call.</summary>
    List<Reading> Read();

    /// <summary>All fans with speed and control state, as of the latest <see cref="Read"/>.</summary>
    FanReport ReadFans();

    /// <summary>Every raw sensor value, for the "All sensors" view.</summary>
    string DumpAll();

    /// <summary>Everything useful for a bug report, for "Save diagnostics".</summary>
    string DiagnosticReport();

    /// <summary>What currently limits the readings (missing rights or drivers).</summary>
    ProviderStatus Status { get; }
}

/// <param name="Summary">Status-line text, e.g. "admin: yes · PawnIO: installed · sensors via LibreHardwareMonitor".</param>
/// <param name="Warning">Banner text when something is missing, otherwise null.</param>
/// <param name="CanElevate">Whether restarting with more rights would help ("Run as admin").</param>
/// <param name="CanInstallDriver">Whether a one-click driver install is offered.</param>
public sealed record ProviderStatus(string Summary, string? Warning, bool CanElevate, bool CanInstallDriver);

namespace TempMonitor.App;

/// <summary>
/// Everything the UI needs from the operating system, so windows and cards never touch
/// platform APIs directly. Windows uses LibreHardwareMonitor, Task Scheduler and UAC; the
/// Linux implementation (hwmon, XDG autostart) comes with the Linux build.
/// </summary>
public abstract class Platform
{
    public const string RelaunchArg = "--relaunch";

    public static Platform Current { get; } =
#if WINDOWS
        new WindowsAppPlatform();
#else
        new UnsupportedPlatform();
#endif

    public abstract ISensorProvider CreateSensorProvider();

    public abstract bool TryAcquireSingleInstance(bool waitForPrevious);
    public abstract void SignalFirstInstance();
    public abstract void ListenForActivation(Action onActivate);

    /// <summary>Restarts with more rights; false if the user declined.</summary>
    public virtual bool RelaunchElevated() => false;

    /// <summary>One-click install of a missing sensor driver; returns a message for the user.</summary>
    public virtual string InstallDriver() => "No driver install is available on this system.";
    public virtual string DriverButtonText => "Install driver";

    // Start at sign-in
    public virtual string AutoStartLabel => "Start at sign-in";
    public virtual bool AutoStartSupported => false;
    /// <summary>Whether this copy may change the setting (Windows: needs admin).</summary>
    public virtual bool CanChangeAutoStart => false;
    public virtual string? AutoStartHint => null;
    public virtual bool IsAutoStartEnabled() => false;
    public virtual void SetAutoStart(bool enabled) { }
    public virtual void RepairAutoStart() { }

    public virtual string DiagnosticsFolder => AppContext.BaseDirectory;
}

#if WINDOWS
internal sealed class WindowsAppPlatform : Platform
{
#if DEV_INSTANCE
    // Non-elevated dev builds run alongside an installed copy instead of handing off to it.
    private readonly WindowsSingleInstance _instance = new("TempMonitor.Dev");
#else
    private readonly WindowsSingleInstance _instance = new("TempMonitor");
#endif
    private bool? _elevated;

    private bool Elevated => _elevated ??= new System.Security.Principal.WindowsPrincipal(
        System.Security.Principal.WindowsIdentity.GetCurrent()).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);

    public override ISensorProvider CreateSensorProvider() => new WindowsSensorProvider();
    public override bool TryAcquireSingleInstance(bool waitForPrevious) => _instance.TryAcquire(waitForPrevious);
    public override void SignalFirstInstance() => _instance.SignalFirstInstance();
    public override void ListenForActivation(Action onActivate) => _instance.ListenForActivation(onActivate);
    public override bool RelaunchElevated() => WindowsPlatform.RelaunchAsAdmin();
    public override string InstallDriver() => PawnIoInstaller.Install();
    public override string DriverButtonText => "Install PawnIO";

    public override string AutoStartLabel => "Start with Windows";
    public override bool AutoStartSupported => true;
    public override bool CanChangeAutoStart => Elevated;
    public override string? AutoStartHint => Elevated
        ? "Start Temp Monitor minimized when you sign in (no UAC prompt)"
        : "Run as administrator to change this";
    public override bool IsAutoStartEnabled() => AutoStart.IsEnabled();
    public override void SetAutoStart(bool enabled)
    {
        if (enabled) AutoStart.Enable(); else AutoStart.Disable();
    }
    public override void RepairAutoStart()
    {
        if (Elevated) AutoStart.RepairPathIfMoved();
    }
}
#endif

/// <summary>Placeholder until the Linux sensor provider exists.</summary>
internal sealed class UnsupportedPlatform : Platform
{
    public override ISensorProvider CreateSensorProvider() =>
        throw new PlatformNotSupportedException("No sensor provider for this operating system yet.");
    public override bool TryAcquireSingleInstance(bool waitForPrevious) => true;
    public override void SignalFirstInstance() { }
    public override void ListenForActivation(Action onActivate) { }
}

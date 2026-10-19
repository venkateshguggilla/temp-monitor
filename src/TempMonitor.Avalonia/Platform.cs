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
        OperatingSystem.IsLinux() ? new LinuxAppPlatform() : new UnsupportedPlatform();
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

#if !WINDOWS
/// <summary>
/// Linux: hwmon sensors (no root needed), single instance through a Unix socket in
/// $XDG_RUNTIME_DIR, and start at sign-in through an XDG autostart entry.
/// </summary>
internal sealed class LinuxAppPlatform : Platform
{
    private static readonly string RuntimeDir =
        Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } d ? d : Path.GetTempPath();
    private static readonly string SocketPath = Path.Combine(RuntimeDir, "temp-monitor.sock");
    private static readonly string AutoStartFile = Path.Combine(
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } c ? c
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"),
        "autostart", "temp-monitor.desktop");

    private System.Net.Sockets.Socket? _listener;

    public override ISensorProvider CreateSensorProvider() => new LinuxSensorProvider();

    public override bool TryAcquireSingleInstance(bool waitForPrevious)
    {
        // Another copy is running if something answers on the socket
        if (TrySend("ping")) return false;
        try
        {
            File.Delete(SocketPath); // stale socket from a copy that crashed
            _listener = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.Unix,
                System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Unspecified);
            _listener.Bind(new System.Net.Sockets.UnixDomainSocketEndPoint(SocketPath));
            _listener.Listen(4);
        }
        catch (Exception e) when (e is System.Net.Sockets.SocketException or IOException or UnauthorizedAccessException)
        {
            _listener = null; // can't enforce a single copy; run anyway
        }
        return true;
    }

    public override void SignalFirstInstance() => TrySend("show");

    public override void ListenForActivation(Action onActivate)
    {
        if (_listener == null) return;
        new Thread(() =>
        {
            while (true)
            {
                try
                {
                    using var client = _listener.Accept();
                    var buffer = new byte[16];
                    var n = client.Receive(buffer);
                    if (System.Text.Encoding.ASCII.GetString(buffer, 0, n).StartsWith("show", StringComparison.Ordinal))
                        onActivate();
                }
                catch (System.Net.Sockets.SocketException) { return; }
                catch (ObjectDisposedException) { return; }
            }
        }) { IsBackground = true, Name = "Activate listener" }.Start();
    }

    private static bool TrySend(string message)
    {
        try
        {
            using var s = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.Unix,
                System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Unspecified);
            s.Connect(new System.Net.Sockets.UnixDomainSocketEndPoint(SocketPath));
            s.Send(System.Text.Encoding.ASCII.GetBytes(message));
            return true;
        }
        catch (Exception e) when (e is System.Net.Sockets.SocketException or IOException)
        {
            return false;
        }
    }

    public override string AutoStartLabel => "Start at sign-in";
    public override bool AutoStartSupported => true;
    public override bool CanChangeAutoStart => true;
    public override string? AutoStartHint => "Start Temp Monitor in the top bar when you sign in";
    public override bool IsAutoStartEnabled() => File.Exists(AutoStartFile);

    public override void SetAutoStart(bool enabled)
    {
        if (!enabled)
        {
            File.Delete(AutoStartFile);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(AutoStartFile)!);
        File.WriteAllText(AutoStartFile,
            "[Desktop Entry]\n" +
            "Type=Application\n" +
            "Name=Temp Monitor\n" +
            "Comment=CPU, GPU, RAM and drive temperatures\n" +
            $"Exec=\"{Environment.ProcessPath}\" {Program.MinimizedArg}\n" +
            "Icon=temp-monitor\n" +
            "X-GNOME-Autostart-enabled=true\n");
    }

    /// <summary>If the app moved since autostart was turned on, point the entry at the new path.</summary>
    public override void RepairAutoStart()
    {
        if (IsAutoStartEnabled() && !File.ReadAllText(AutoStartFile).Contains(Environment.ProcessPath ?? "", StringComparison.Ordinal))
            SetAutoStart(true);
    }

    public override string DiagnosticsFolder => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
}
#endif

/// <summary>Operating systems without a sensor provider yet.</summary>
internal sealed class UnsupportedPlatform : Platform
{
    public override ISensorProvider CreateSensorProvider() =>
        throw new PlatformNotSupportedException("No sensor provider for this operating system yet.");
    public override bool TryAcquireSingleInstance(bool waitForPrevious) => true;
    public override void SignalFirstInstance() { }
    public override void ListenForActivation(Action onActivate) { }
}

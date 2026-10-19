using System.ComponentModel;
using System.Diagnostics;

namespace TempMonitor;

/// <summary>Windows process helpers shared by the apps: single instance and run as administrator.</summary>
public static class WindowsPlatform
{
    /// <summary>Passed to the elevated copy so it waits for this one to exit instead of handing off.</summary>
    public const string RelaunchArg = "--relaunch";

    /// <summary>
    /// Starts this exe again elevated. Returns false if the user declined the UAC prompt;
    /// on true the caller should exit so the elevated copy takes over.
    /// </summary>
    public static bool RelaunchAsAdmin()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, RelaunchArg)
            {
                UseShellExecute = true,
                Verb = "runas",
            });
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED
        {
            return false;
        }
    }
}

/// <summary>
/// One running copy per session. A second launch asks the first to show its window, then exits.
/// </summary>
public sealed class WindowsSingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly string _activateName;
    private EventWaitHandle? _activate;

    public WindowsSingleInstance(string name)
    {
        _mutex = new Mutex(false, $@"Local\{name}.SingleInstance");
        _activateName = $@"Local\{name}.Activate";
    }

    /// <summary>True if this is the only copy. With <paramref name="waitForPrevious"/> waits up to 10 s
    /// for a copy that is exiting (used after relaunching as admin).</summary>
    public bool TryAcquire(bool waitForPrevious)
    {
        try
        {
            return _mutex.WaitOne(waitForPrevious ? TimeSpan.FromSeconds(10) : TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            return true; // previous copy exited without releasing it; we own it now
        }
    }

    /// <summary>Asks the running copy to show its window.</summary>
    public void SignalFirstInstance()
    {
        try { EventWaitHandle.OpenExisting(_activateName).Set(); }
        catch (WaitHandleCannotBeOpenedException) { /* first instance still starting */ }
    }

    /// <summary>Calls <paramref name="onActivate"/> (on a background thread) whenever another copy starts.</summary>
    public void ListenForActivation(Action onActivate)
    {
        _activate = new EventWaitHandle(false, EventResetMode.AutoReset, _activateName);
        new Thread(() =>
        {
            while (_activate.WaitOne())
                onActivate();
        }) { IsBackground = true, Name = "Activate listener" }.Start();
    }

    public void Dispose()
    {
        _activate?.Dispose();
        _mutex.Dispose();
    }
}

using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using System.Windows;

namespace TempMonitor;

public partial class App : Application
{
#if DEV_INSTANCE
    // Non-elevated dev builds run alongside an installed copy instead of handing off to it.
    private const string InstanceName = @"Local\TempMonitor.Dev.SingleInstance";
    private const string ActivateName = @"Local\TempMonitor.Dev.Activate";
#else
    private const string InstanceName = @"Local\TempMonitor.SingleInstance";
    private const string ActivateName = @"Local\TempMonitor.Activate";
#endif

    /// <summary>Passed to the elevated copy so it waits for this one to exit instead of handing off.</summary>
    private const string RelaunchArg = "--relaunch";

    private Mutex? _instanceMutex;
    private EventWaitHandle? _activateEvent;

    protected override void OnStartup(StartupEventArgs e)
    {
        ShutdownMode = ShutdownMode.OnMainWindowClose;

        // One instance only: a second launch (e.g. opening the app while the sign-in copy
        // is running) asks the first one to show its window, then exits.
        if (!AcquireSingleInstance(waitForPrevious: e.Args.Contains(RelaunchArg)))
        {
            try { EventWaitHandle.OpenExisting(ActivateName).Set(); }
            catch (WaitHandleCannotBeOpenedException) { /* first instance still starting */ }
            Shutdown();
            return;
        }

        base.OnStartup(e);

        var window = new MainWindow();
        MainWindow = window;
        // Started at sign-in: live in the tray until the user opens the window.
        if (!e.Args.Contains(AutoStart.MinimizedArg))
            window.Show();

        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateName);
        new Thread(() =>
        {
            while (_activateEvent.WaitOne())
                Dispatcher.BeginInvoke(window.ShowFromTray);
        }) { IsBackground = true, Name = "Activate listener" }.Start();
    }

    private bool AcquireSingleInstance(bool waitForPrevious)
    {
        _instanceMutex = new Mutex(false, InstanceName);
        try
        {
            return _instanceMutex.WaitOne(waitForPrevious ? TimeSpan.FromSeconds(10) : TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            return true; // previous copy exited without releasing it; we own it now
        }
    }

    /// <summary>
    /// Restarts the app elevated. Returns false if the user declined the UAC prompt,
    /// in which case this copy keeps running.
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
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED
        {
            return false;
        }
        Current.Shutdown();
        return true;
    }
}

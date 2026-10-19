using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace TempMonitor.App;

public partial class MainWindow : Window
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    private readonly Dictionary<string, MetricCard> _cards = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource _stopped = new();
    private readonly TrayController _tray;
    private ISensorProvider? _sensors;
    private List<Reading> _latest = new();
    private bool _shutDown;

    public MainWindow()
    {
        InitializeComponent();
        AutoStartBox.Content = Platform.Current.AutoStartLabel;
        AutoStartBox.IsVisible = Platform.Current.AutoStartSupported;

        _tray = new TrayController(ShowFromTray, ExitApp);
        PropertyChanged += (_, e) =>
        {
            if (e.Property == WindowStateProperty && WindowState == WindowState.Minimized)
                Hide(); // minimize to the tray
        };

        // Started here rather than on Opened: at sign-in the window starts hidden in the tray.
        new Thread(PollLoop) { IsBackground = true, Name = "Sensor poll" }.Start();
    }

    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
    }

    /// <summary>Ends the app even if the window was never shown (started in the tray).</summary>
    private static void ExitApp() =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();

    /// <summary>Stops polling and removes the tray icon; called once when the app exits.</summary>
    public void Shutdown()
    {
        if (_shutDown) return;
        _shutDown = true;
        _cts.Cancel();
        _stopped.Task.Wait(TimeSpan.FromSeconds(3));
        _tray.Dispose();
    }

    private void PollLoop()
    {
        try
        {
            _sensors = Platform.Current.CreateSensorProvider();
            Dispatcher.UIThread.Post(ShowEnvironmentWarning);
            Dispatcher.UIThread.Post(InitAutoStartBox);

            while (!_cts.IsCancellationRequested)
            {
                var started = DateTime.UtcNow;
                List<Reading> readings;
                try
                {
                    readings = _sensors.Read();
                }
                catch (Exception ex)
                {
                    Dispatcher.UIThread.Post(() => StatusText.Text = "Sensor read failed: " + ex.Message);
                    readings = _latest;
                }
                Dispatcher.UIThread.Post(() => Render(readings));

                var wait = Interval - (DateTime.UtcNow - started);
                if (wait > TimeSpan.Zero)
                    _cts.Token.WaitHandle.WaitOne(wait);
            }
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() => StatusText.Text = "Could not start sensors: " + ex.Message);
        }
        finally
        {
            _sensors?.Dispose();
            _stopped.TrySetResult();
        }
    }

    private void ShowEnvironmentWarning()
    {
        if (_sensors == null) return;
        var status = _sensors.Status;
        WarningBanner.IsVisible = status.Warning != null;
        WarningText.Text = status.Warning ?? "";
        InstallDriverButton.Content = Platform.Current.DriverButtonText;
        InstallDriverButton.IsVisible = status.CanInstallDriver;
        RunAsAdminButton.IsVisible = status.CanElevate;
    }

    private void OnRunAsAdmin(object? sender, RoutedEventArgs e)
    {
        // On success this copy exits and the elevated one takes over; if the user
        // declines the prompt, nothing changes.
        if (Platform.Current.RelaunchElevated())
            ExitApp();
    }

    private async void OnInstallDriver(object? sender, RoutedEventArgs e)
    {
        InstallDriverButton.IsEnabled = false;
        try
        {
            var result = await Task.Run(Platform.Current.InstallDriver);
            await MessageDialog.Show(this, Platform.Current.DriverButtonText, result);
        }
        finally
        {
            InstallDriverButton.IsEnabled = true;
        }
    }

    private void Render(List<Reading> readings)
    {
        _latest = readings;
        var fahrenheit = FahrenheitBox.IsChecked == true;

        foreach (var r in readings)
        {
            if (!_cards.TryGetValue(r.Key, out var card))
            {
                card = new MetricCard();
                _cards[r.Key] = card;
                Cards.Children.Add(card);
            }
            card.Update(r, fahrenheit);
        }

        // Drop cards for hardware that has gone away (e.g. an unplugged USB drive).
        var present = readings.Select(r => r.Key).ToHashSet();
        foreach (var gone in _cards.Keys.Where(k => !present.Contains(k)).ToList())
        {
            Cards.Children.Remove(_cards[gone]);
            _cards.Remove(gone);
        }

        _tray.Update(readings.FirstOrDefault(r => r.Key.StartsWith("cpu:")),
                     readings.FirstOrDefault(r => r.Key.StartsWith("gpu:")), fahrenheit);

        StatusText.Text = $"Updated {DateTime.Now:HH:mm:ss} · refresh every {Interval.TotalSeconds:0} s · " +
                          (_sensors?.Status.Summary ?? "");
    }

    private void OnFahrenheitChanged(object? sender, RoutedEventArgs e)
    {
        if (_latest.Count > 0) Render(_latest);
    }

    private void OnTopmostChanged(object? sender, RoutedEventArgs e) => Topmost = TopmostBox.IsChecked == true;

    private bool _autoStartBusy;

    private async void InitAutoStartBox()
    {
        if (!Platform.Current.AutoStartSupported) return;
        _autoStartBusy = true;
        var enabled = await Task.Run(() =>
        {
            Platform.Current.RepairAutoStart();
            return Platform.Current.IsAutoStartEnabled();
        });
        AutoStartBox.IsChecked = enabled;
        AutoStartBox.IsEnabled = Platform.Current.CanChangeAutoStart;
        ToolTip.SetTip(AutoStartBox, Platform.Current.AutoStartHint);
        _autoStartBusy = false;
    }

    private async void OnAutoStartClicked(object? sender, RoutedEventArgs e)
    {
        if (_autoStartBusy) return;
        _autoStartBusy = true;
        var enable = AutoStartBox.IsChecked == true;
        AutoStartBox.IsEnabled = false;
        try
        {
            await Task.Run(() => Platform.Current.SetAutoStart(enable));
        }
        catch (Exception ex)
        {
            await MessageDialog.Show(this, Platform.Current.AutoStartLabel, "Could not change the startup setting:\n" + ex.Message);
        }
        AutoStartBox.IsChecked = await Task.Run(Platform.Current.IsAutoStartEnabled);
        AutoStartBox.IsEnabled = true;
        _autoStartBusy = false;
    }

    private void OnShowAllSensors(object? sender, RoutedEventArgs e)
    {
        var text = _sensors == null ? "Sensors not ready yet." : SafeDump();
        new TextWindow("All sensors (snapshot)", text).Show(this);
    }

    private async void OnSaveDiagnostics(object? sender, RoutedEventArgs e)
    {
        if (_sensors == null) return;
        SaveDiagnosticsButton.IsEnabled = false;
        try
        {
            var sensors = _sensors;
            var report = await Task.Run(sensors.DiagnosticReport);
            var path = Path.Combine(Platform.Current.DiagnosticsFolder, "diagnostics.txt");
            await File.WriteAllTextAsync(path, report);
            await MessageDialog.Show(this, "Diagnostics", "Saved to\n" + path);
        }
        catch (Exception ex)
        {
            await MessageDialog.Show(this, "Diagnostics", "Could not save diagnostics: " + ex.Message);
        }
        finally
        {
            SaveDiagnosticsButton.IsEnabled = true;
        }
    }

    private string SafeDump()
    {
        try { return _sensors!.DumpAll(); }
        catch (Exception ex) { return "Could not read sensors right now: " + ex.Message; }
    }
}

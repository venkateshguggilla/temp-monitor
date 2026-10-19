using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TempMonitor;

public partial class MainWindow : Window
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    private readonly Dictionary<string, MetricCard> _cards = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource _stopped = new();
    private readonly TrayIcon _tray;
    private WindowsSensorProvider? _sensors;
    private List<Reading> _latest = new();

    public MainWindow()
    {
        InitializeComponent();
        StatusText.Text = "Detecting hardware…";

        _tray = new TrayIcon(LoadAppIcon(), ShowFromTray, Close);
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Minimized)
                Hide(); // minimize to the tray
        };

        // Started here rather than on Loaded: at sign-in the window starts hidden in the tray.
        new Thread(PollLoop) { IsBackground = true, Name = "Sensor poll" }.Start();
        Closing += (_, _) =>
        {
            _cts.Cancel();
            _stopped.Task.Wait(TimeSpan.FromSeconds(3));
            _tray.Dispose();
        };
    }

    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
    }

    private static System.Drawing.Icon LoadAppIcon()
    {
        using var stream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico")).Stream;
        return new System.Drawing.Icon(stream, System.Windows.Forms.SystemInformation.SmallIconSize);
    }

    private void PollLoop()
    {
        try
        {
            _sensors = new WindowsSensorProvider();
            Dispatcher.BeginInvoke(ShowEnvironmentWarning);
            Dispatcher.BeginInvoke(InitAutoStartBox);

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
                    Dispatcher.BeginInvoke(() => StatusText.Text = "Sensor read failed: " + ex.Message);
                    readings = _latest;
                }
                Dispatcher.BeginInvoke(() => Render(readings));

                var wait = Interval - (DateTime.UtcNow - started);
                if (wait > TimeSpan.Zero)
                    _cts.Token.WaitHandle.WaitOne(wait);
            }
        }
        catch (Exception ex)
        {
            Dispatcher.BeginInvoke(() => StatusText.Text = "Could not start sensors: " + ex.Message);
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
        string? msg = null;
        if (!_sensors.IsElevated)
            msg = "Not running as administrator. CPU, RAM and drive temperatures need admin rights. Click Run as admin above.";
        else if (!_sensors.PawnIoInstalled)
            msg = "The PawnIO driver isn't installed on this PC, so CPU (and RAM) temperatures can't be read. Install it once, then restart Temp Monitor.";
        WarningBanner.Visibility = msg == null ? Visibility.Collapsed : Visibility.Visible;
        WarningText.Text = msg ?? "";
        InstallPawnIoButton.Visibility = _sensors.IsElevated && !_sensors.PawnIoInstalled
            ? Visibility.Visible : Visibility.Collapsed;
        RunAsAdminButton.Visibility = _sensors.IsElevated ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnRunAsAdmin(object sender, RoutedEventArgs e)
    {
        // On success this copy exits and the elevated one takes over; if the user
        // declines the UAC prompt, nothing changes.
        App.RelaunchAsAdmin();
    }

    private async void OnInstallPawnIo(object sender, RoutedEventArgs e)
    {
        InstallPawnIoButton.IsEnabled = false;
        try
        {
            var result = await Task.Run(PawnIoInstaller.Install);
            MessageBox.Show(this, result, "Install PawnIO");
        }
        finally
        {
            InstallPawnIoButton.IsEnabled = true;
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
                          $"admin: {(_sensors?.IsElevated == true ? "yes" : "no")} · " +
                          $"PawnIO: {(_sensors?.PawnIoInstalled == true ? "installed" : "not installed")} · " +
                          "sensors via LibreHardwareMonitor";
    }

    private void OnOptionChanged(object sender, RoutedEventArgs e)
    {
        Topmost = TopmostBox.IsChecked == true;
    }

    private async void InitAutoStartBox()
    {
        var elevated = _sensors?.IsElevated == true;
        var enabled = await Task.Run(() =>
        {
            if (elevated) AutoStart.RepairPathIfMoved();
            return AutoStart.IsEnabled();
        });
        AutoStartBox.IsChecked = enabled;
        AutoStartBox.IsEnabled = elevated;
        AutoStartBox.ToolTip = elevated
            ? "Start Temp Monitor minimized when you sign in (no UAC prompt)"
            : "Run as administrator to change this";
    }

    private async void OnAutoStartChanged(object sender, RoutedEventArgs e)
    {
        var enable = AutoStartBox.IsChecked == true;
        AutoStartBox.IsEnabled = false;
        try
        {
            await Task.Run(() => { if (enable) AutoStart.Enable(); else AutoStart.Disable(); });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not change the startup setting:\n" + ex.Message, "Start with Windows");
        }
        AutoStartBox.IsChecked = await Task.Run(AutoStart.IsEnabled);
        AutoStartBox.IsEnabled = true;
    }

    private void OnShowAllSensors(object sender, RoutedEventArgs e)
    {
        var text = _sensors == null ? "Sensors not ready yet." : SafeDump();
        var box = new TextBox
        {
            Text = text,
            IsReadOnly = true,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = (Brush)FindResource("WindowBg"),
            Foreground = (Brush)FindResource("TextPrimary"),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(10),
        };
        new Window
        {
            Title = "All sensors (snapshot)",
            Owner = this,
            Icon = Icon,
            Width = 760,
            Height = 700,
            Content = box,
            Background = (Brush)FindResource("WindowBg"),
        }.Show();
    }

    private async void OnSaveDiagnostics(object sender, RoutedEventArgs e)
    {
        if (_sensors == null) return;
        var button = (Button)sender;
        button.IsEnabled = false;
        try
        {
            var sensors = _sensors;
            var report = await Task.Run(sensors.DiagnosticReport);
            var path = System.IO.Path.Combine(AppContext.BaseDirectory, "diagnostics.txt");
            await System.IO.File.WriteAllTextAsync(path, report);
            MessageBox.Show(this, "Saved to\n" + path, "Diagnostics");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not save diagnostics: " + ex.Message, "Diagnostics");
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private string SafeDump()
    {
        try { return _sensors!.DumpAll(); }
        catch (Exception ex) { return "Could not read sensors right now: " + ex.Message; }
    }
}

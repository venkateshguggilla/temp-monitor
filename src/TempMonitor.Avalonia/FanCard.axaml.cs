using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using TempMonitor.Fans;

namespace TempMonitor.App;

/// <summary>
/// The "Fans" card: one row per fan with speed, duty bar and who controls it. When the fan
/// service is reachable (RudramOS / Linux) it also offers the profile, per-fan settings and
/// safety limits; otherwise it shows the read-only readings from the sensor provider.
/// </summary>
public partial class FanCard : UserControl
{
    private static readonly TimeSpan StatusMaxAge = TimeSpan.FromSeconds(5);

    private readonly Dictionary<string, FanRow> _rows = new();
    private readonly IFanService? _service = FanServices.Create();
    private FanReport? _report;
    private DaemonStatus? _status;
    private DateTime _statusAt;
    private bool _fetching;
    private bool _settingProfile;

    public FanCard()
    {
        InitializeComponent();
        ProfileBox.SelectionChanged += OnProfileChanged;
    }

    /// <summary>Called once a second with the provider's fan readings.</summary>
    public void Update(FanReport report)
    {
        _report = report;
        FetchStatus();
        Render();
    }

    private bool ServiceUp => _status != null && DateTime.UtcNow - _statusAt < StatusMaxAge;

    private void FetchStatus()
    {
        if (_service == null || _fetching) return;
        _fetching = true;
        var service = _service;
        Task.Run(() => service.Send(new FanRequest { Cmd = "status" })).ContinueWith(t =>
            Dispatcher.UIThread.Post(() =>
            {
                _fetching = false;
                if (t.IsCompletedSuccessfully && t.Result.Ok) Accept(t.Result.Status);
            }));
    }

    /// <summary>Takes a fresh status from the service (also the answer to every change request).</summary>
    internal void Accept(DaemonStatus? status)
    {
        if (status == null) return;
        _status = status;
        _statusAt = DateTime.UtcNow;
        Render();
    }

    private void Render()
    {
        var up = ServiceUp;
        var models = up
            ? _status!.Fans.Select(FromService).ToList()
            : (_report?.Fans ?? new List<FanReading>()).Select(FromReading).ToList();

        CountText.Text = models.Count switch { 0 => "", 1 => "1 fan", var n => $"{n} fans" };

        var ordered = new List<FanRow>();
        foreach (var m in models)
        {
            if (!_rows.TryGetValue(m.Key, out var row))
                _rows[m.Key] = row = new FanRow(OnEdit);
            row.Update(m);
            ordered.Add(row);
        }
        if (!Rows.Children.SequenceEqual(ordered))
        {
            Rows.Children.Clear();
            Rows.Children.AddRange(ordered);
        }
        foreach (var gone in _rows.Keys.Except(models.Select(m => m.Key)).ToList())
            _rows.Remove(gone);

        // Profile picker
        ProfileBox.IsVisible = up && models.Any(m => m.Editable);
        if (up)
        {
            _settingProfile = true;
            if (!ProfileBox.Items.Cast<object>().Select(o => o.ToString()).SequenceEqual(_status!.Profiles))
            {
                ProfileBox.Items.Clear();
                foreach (var p in _status.Profiles) ProfileBox.Items.Add(p);
            }
            ProfileBox.SelectedItem = _status.Profile;
            _settingProfile = false;
        }
        SafetyButton.IsVisible = up && models.Any(m => m.Editable);

        // Note: critical > service note > provider note > service missing (Linux)
        string? note;
        var hot = false;
        if (up && _status!.Critical)
        {
            note = _status.Note;
            hot = true;
        }
        else if (up)
        {
            note = _status!.Note;
        }
        else
        {
            note = _report?.Note;
            if (_service != null && OperatingSystem.IsLinux() && _report?.Fans.Any(f => f.HasControl) == true)
                note = (note == null ? "" : note + " ") + "Fan control is off: the fan service isn't running (temp-monitor-fans.service).";
        }
        NoteText.Text = note ?? "";
        NoteText.IsVisible = note != null;
        NoteText.Foreground = Brush(hot ? "HotBrush" : "TextMuted");
    }

    private static FanRowModel FromReading(FanReading f)
    {
        var parts = new List<string>();
        if (FanReading.ModeText(f.Mode) is { Length: > 0 } mode) parts.Add(mode);
        if (f.Percent is { } p) parts.Add($"{p:0}%");
        else if (f.MinRpm is { } min && f.MaxRpm is { } max) parts.Add($"{min:#,0}–{max:#,0} RPM");
        if (!f.HasControl) parts.Add("read-only");
        var fill = f.Percent
                   ?? (f.Rpm is { } r && f.MinRpm is { } lo && f.MaxRpm is { } hi && hi > lo ? (r - lo) / (hi - lo) * 100 : null);
        return new FanRowModel(f.Key, f.Name, f.Device, f.Rpm, string.Join(" · ", parts), fill, false, null, false);
    }

    private static FanRowModel FromService(FanStatus f)
    {
        var state = f.Mode switch
        {
            FanControlMode.Fixed => $"Fixed → {f.Target:0}%",
            FanControlMode.Curve => f.Temperature is { } t ? $"Curve · {t:0} °C → {f.Target:0}%" : $"Curve → {f.Target:0}%",
            _ => f.HasControl ? "Auto (firmware)" + (f.Percent is { } p ? $" · {p:0}%" : "") : "read-only",
        };
        return new FanRowModel(f.Key, f.Name, f.Device, f.Rpm, state, f.Percent ?? f.Target, f.Stalled, f.Error, f.HasControl);
    }

    // ---------------------------------------------------------------- Changes

    private async void OnProfileChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_settingProfile || _service == null || ProfileBox.SelectedItem is not string name || name == _status?.Profile) return;
        await Send(new FanRequest { Cmd = "profile", Name = name });
    }

    private async void OnEdit(string key)
    {
        if (_service == null || _status?.Fans.FirstOrDefault(f => f.Key == key) is not { } fan) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        var result = await new FanEditorWindow(fan, _status, _service).ShowDialog<DaemonStatus?>(owner);
        Accept(result);
    }

    private async void OnSafety(object? sender, RoutedEventArgs e)
    {
        if (_service == null || _status == null || TopLevel.GetTopLevel(this) is not Window owner) return;
        var result = await new SafetyWindow(_status.Safety, _service).ShowDialog<DaemonStatus?>(owner);
        Accept(result);
    }

    /// <summary>Sends a change; shows the reason if it was refused.</summary>
    internal async Task<bool> Send(FanRequest request)
    {
        if (_service == null) return false;
        var service = _service;
        var resp = await Task.Run(() => service.Send(request));
        if (resp.Ok)
        {
            Accept(resp.Status);
            return true;
        }
        if (TopLevel.GetTopLevel(this) is Window owner)
            await MessageDialog.Show(owner, "Fans", resp.Error ?? "The change was refused.");
        Render(); // puts the profile picker back
        return false;
    }

    private static IBrush Brush(string key) => (IBrush)Application.Current!.FindResource(key)!;
}

/// <summary>What one fan row shows.</summary>
internal sealed record FanRowModel(string Key, string Name, string Device, float? Rpm, string State,
    float? Fill, bool Stalled, string? Error, bool Editable);

/// <summary>
/// One fan: name, edit button and speed; then what it belongs to and its mode; then a bar for
/// the duty (or, for RPM-controlled fans, the speed within the fan's range); then any problem.
/// </summary>
internal sealed class FanRow : StackPanel
{
    private readonly TextBlock _name = Text(13, "TextPrimary", FontWeight.SemiBold);
    private readonly TextBlock _rpm = Text(13, "TextPrimary", FontWeight.Normal);
    private readonly TextBlock _device = Text(11, "TextMuted", FontWeight.Normal);
    private readonly TextBlock _state = Text(11, "TextMuted", FontWeight.Normal);
    private readonly TextBlock _problem = Text(11, "HotBrush", FontWeight.Normal);
    private readonly Button _edit = new() { Content = "Edit", Classes = { "edit" } };
    private readonly Border _track;
    private readonly Border _bar;
    private float? _fill;
    private string _key = "";

    public FanRow(Action<string> onEdit)
    {
        Margin = new Thickness(0, 10, 0, 0);
        _rpm.HorizontalAlignment = HorizontalAlignment.Right;
        _rpm.VerticalAlignment = VerticalAlignment.Center;
        _state.HorizontalAlignment = HorizontalAlignment.Right;
        _state.Margin = new Thickness(8, 0, 0, 0);
        _device.TextTrimming = TextTrimming.CharacterEllipsis;
        _name.TextTrimming = TextTrimming.CharacterEllipsis;
        _name.VerticalAlignment = VerticalAlignment.Center;
        _problem.TextWrapping = TextWrapping.Wrap;
        _problem.Margin = new Thickness(0, 3, 0, 0);
        _edit.Click += (_, _) => onEdit(_key);
        ToolTip.SetTip(_edit, "Mode, speed curve, name, identify");

        var top = new DockPanel();
        DockPanel.SetDock(_rpm, Dock.Right);
        DockPanel.SetDock(_edit, Dock.Right);
        top.Children.Add(_rpm);
        top.Children.Add(_edit);
        top.Children.Add(_name);

        var bottom = new DockPanel();
        DockPanel.SetDock(_state, Dock.Right);
        bottom.Children.Add(_state);
        bottom.Children.Add(_device);

        _bar = new Border { CornerRadius = new CornerRadius(2), Background = Brush("UsageBrush"), HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
        _track = new Border { Height = 4, CornerRadius = new CornerRadius(2), Background = Brush("TrackBg"), Margin = new Thickness(0, 4, 0, 0), Child = _bar };
        _track.SizeChanged += (_, _) => SetBar();

        Children.Add(top);
        Children.Add(bottom);
        Children.Add(_track);
        Children.Add(_problem);
    }

    public void Update(FanRowModel m)
    {
        _key = m.Key;
        _name.Text = m.Name;
        _rpm.Text = m.Rpm is { } rpm ? FanReading.FormatRpm(rpm) : "— RPM";
        _rpm.Foreground = Brush(m.Stalled ? "HotBrush" : "TextPrimary");
        _device.Text = m.Device;
        ToolTip.SetTip(_device, m.Device);
        _state.Text = m.State;
        _edit.IsVisible = m.Editable;

        var problem = m.Stalled ? "Stalled: 0 RPM while it should be spinning. Check the fan and its cable." : m.Error;
        _problem.Text = problem ?? "";
        _problem.IsVisible = problem != null;

        _fill = m.Fill;
        _track.IsVisible = _fill.HasValue;
        SetBar();
    }

    private void SetBar()
    {
        var width = _track.Bounds.Width > 0 ? _track.Bounds.Width : 286;
        _bar.Width = Math.Clamp(_fill ?? 0, 0, 100) / 100.0 * width;
    }

    private static IBrush Brush(string key) => (IBrush)Application.Current!.FindResource(key)!;

    private static TextBlock Text(double size, string brush, FontWeight weight) =>
        new() { FontSize = size, Foreground = Brush(brush), FontWeight = weight };
}

/// <summary>Which fan service the app talks to.</summary>
internal static class FanServices
{
    public static IFanService? Create()
    {
#if DEV_INSTANCE
        // Development on Windows: TEMPMONITOR_FAKE_FANS=1 shows the fan controls against a stand-in
        if (Environment.GetEnvironmentVariable("TEMPMONITOR_FAKE_FANS") == "1") return new FakeFanService();
#endif
        return OperatingSystem.IsLinux() ? new FanClient() : null;
    }
}

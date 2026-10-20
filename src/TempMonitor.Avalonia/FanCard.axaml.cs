using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace TempMonitor.App;

/// <summary>The "Fans" card: one row per fan with speed, duty bar and who controls it.</summary>
public partial class FanCard : UserControl
{
    private readonly Dictionary<string, FanRow> _rows = new();

    public FanCard() => InitializeComponent();

    public void Update(FanReport report)
    {
        CountText.Text = report.Fans.Count switch { 0 => "", 1 => "1 fan", var n => $"{n} fans" };

        var ordered = new List<FanRow>();
        foreach (var fan in report.Fans)
        {
            if (!_rows.TryGetValue(fan.Key, out var row))
                _rows[fan.Key] = row = new FanRow();
            row.Update(fan);
            ordered.Add(row);
        }
        if (!Rows.Children.SequenceEqual(ordered))
        {
            Rows.Children.Clear();
            Rows.Children.AddRange(ordered);
        }
        foreach (var gone in _rows.Keys.Except(report.Fans.Select(f => f.Key)).ToList())
            _rows.Remove(gone);

        NoteText.Text = report.Note ?? "";
        NoteText.IsVisible = report.Note != null;
    }
}

/// <summary>
/// One fan: name and speed, then what it belongs to and its mode, then a bar for the
/// commanded duty (or, for RPM-controlled fans, the speed within the fan's range).
/// </summary>
internal sealed class FanRow : StackPanel
{
    private readonly TextBlock _name = Text(13, "TextPrimary", FontWeight.SemiBold);
    private readonly TextBlock _rpm = Text(13, "TextPrimary", FontWeight.Normal);
    private readonly TextBlock _device = Text(11, "TextMuted", FontWeight.Normal);
    private readonly TextBlock _state = Text(11, "TextMuted", FontWeight.Normal);
    private readonly Border _track;
    private readonly Border _bar;
    private float? _fill;

    public FanRow()
    {
        Margin = new Thickness(0, 10, 0, 0);
        _rpm.HorizontalAlignment = HorizontalAlignment.Right;
        _state.HorizontalAlignment = HorizontalAlignment.Right;
        _state.Margin = new Thickness(8, 0, 0, 0);
        _device.TextTrimming = TextTrimming.CharacterEllipsis;
        _name.TextTrimming = TextTrimming.CharacterEllipsis;

        var top = new DockPanel();
        DockPanel.SetDock(_rpm, Dock.Right);
        top.Children.Add(_rpm);
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
    }

    public void Update(FanReading f)
    {
        _name.Text = f.Name;
        _rpm.Text = f.Rpm is { } rpm ? FanReading.FormatRpm(rpm) : "— RPM";
        _device.Text = f.Device;
        ToolTip.SetTip(_device, f.Device);

        var parts = new List<string>();
        if (FanReading.ModeText(f.Mode) is { Length: > 0 } mode) parts.Add(mode);
        if (f.Percent is { } p) parts.Add($"{p:0}%");
        else if (f.MinRpm is { } min && f.MaxRpm is { } max) parts.Add($"{min:#,0}–{max:#,0} RPM");
        if (!f.HasControl) parts.Add("read-only");
        _state.Text = string.Join(" · ", parts);

        _fill = f.Percent
                ?? (f.Rpm is { } r && f.MinRpm is { } lo && f.MaxRpm is { } hi && hi > lo ? (r - lo) / (hi - lo) * 100 : null);
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

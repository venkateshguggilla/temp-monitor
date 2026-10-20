using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using TempMonitor.Fans;

namespace TempMonitor.App;

/// <summary>Shared look for the fan dialogs.</summary>
internal abstract class FanDialog : Window
{
    protected FanDialog(string title)
    {
        Title = title;
        Width = 480;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush("WindowBg");
    }

    protected static IBrush Brush(string key) => (IBrush)Application.Current!.FindResource(key)!;

    protected static TextBlock Text(string text, double size = 12, string brush = "TextMuted", bool wrap = true) =>
        new() { Text = text, FontSize = size, Foreground = Brush(brush), TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap };

    protected static Button DialogButton(string text, bool primary = false) => new()
    {
        Content = text,
        Margin = new Thickness(8, 0, 0, 0),
        Padding = new Thickness(14, 5),
        Background = primary ? Brush("UsageBrush") : Brush("CardBg"),
        Foreground = primary ? Brushes.White : Brush("TextPrimary"),
    };

    /// <summary>Runs a request off the UI thread; shows the reason if it was refused.</summary>
    protected async Task<FanResponse> Run(IFanService service, FanRequest request)
    {
        var resp = await Task.Run(() => service.Send(request));
        if (!resp.Ok)
            await MessageDialog.Show(this, Title ?? "Fans", resp.Error ?? "The change was refused.");
        return resp;
    }
}

/// <summary>
/// One fan's settings: name, Auto / Fixed / Curve, the curve and what temperature it follows,
/// and Identify. Closes with the service's new status, or null if cancelled.
/// </summary>
internal sealed class FanEditorWindow : FanDialog
{
    private static readonly (string Key, string Label)[] SourceChoices =
        { ("cpu:*", "CPU"), ("gpu:*", "GPU"), ("disk:*", "Drives"), ("ram", "RAM") };

    private readonly FanStatus _fan;
    private readonly IFanService _service;
    private readonly TextBox _name;
    private readonly RadioButton _auto, _fixed, _curve;
    private readonly Slider _percent;
    private readonly TextBlock _percentText;
    private readonly CurveEditor _editor;
    private readonly List<(string Key, CheckBox Box)> _sources = new();
    private readonly NumericUpDown _hysteresis;
    private readonly StackPanel _fixedPanel, _curvePanel;
    private readonly Button _save;
    private readonly List<string> _otherSources;

    public FanEditorWindow(FanStatus fan, DaemonStatus status, IFanService service) : base("Fan: " + fan.Name)
    {
        _fan = fan;
        _service = service;
        var s = fan.Setting;

        _name = new TextBox { Text = fan.Name, Watermark = "Name", MaxLength = 40 };

        _auto = new RadioButton { Content = "Auto (firmware)", GroupName = "mode", IsChecked = s.Mode == FanControlMode.Auto };
        _fixed = new RadioButton { Content = "Fixed speed", GroupName = "mode", IsChecked = s.Mode == FanControlMode.Fixed, Margin = new Thickness(16, 0, 0, 0) };
        _curve = new RadioButton { Content = "Curve", GroupName = "mode", IsChecked = s.Mode == FanControlMode.Curve, Margin = new Thickness(16, 0, 0, 0) };
        foreach (var rb in new[] { _auto, _fixed, _curve })
        {
            rb.Foreground = Brush("TextPrimary");
            rb.IsCheckedChanged += (_, _) => ShowMode();
        }

        // Fixed
        _percent = new Slider { Minimum = 0, Maximum = 100, Value = s.Percent, TickFrequency = 5, IsSnapToTickEnabled = true };
        _percentText = Text("", 13, "TextPrimary", wrap: false);
        _percent.PropertyChanged += (_, e) => { if (e.Property == RangeBase.ValueProperty) _percentText.Text = $"{_percent.Value:0}%"; };
        _percentText.Text = $"{s.Percent:0}%";
        var fixedRow = new DockPanel();
        DockPanel.SetDock(_percentText, Dock.Right);
        _percentText.Margin = new Thickness(12, 0, 0, 0);
        _percentText.VerticalAlignment = VerticalAlignment.Center;
        fixedRow.Children.Add(_percentText);
        fixedRow.Children.Add(_percent);
        _fixedPanel = new StackPanel { Spacing = 4, Children = { fixedRow, Text($"Never below {status.Safety.FloorPercent:0}% (safety limit).") } };

        // Curve
        _editor = new CurveEditor { CurrentTemp = fan.Temperature, Floor = status.Safety.FloorPercent };
        var points = s.Points.Count >= 2 ? s.Points : FanConfig.Presets["Balanced"].ToList();
        _editor.SetPoints(points);
        var sources = s.Mode == FanControlMode.Curve && s.Sources.Count > 0
            ? s.Sources
            : FanConfig.IsGpuFan(fan.Key) ? new List<string> { "gpu:*" } : new List<string> { "cpu:*", "gpu:*" };
        _otherSources = sources.Where(x => SourceChoices.All(c => c.Key != x)).ToList(); // kept as they are
        var sourceRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
        var follows = Text("Follows the hottest of:", wrap: false);
        follows.VerticalAlignment = VerticalAlignment.Center;
        sourceRow.Children.Add(follows);
        foreach (var (key, label) in SourceChoices)
        {
            var box = new CheckBox { Content = label, IsChecked = sources.Contains(key), Foreground = Brush("TextPrimary"), Margin = new Thickness(0) };
            _sources.Add((key, box));
            sourceRow.Children.Add(box);
        }
        _hysteresis = new NumericUpDown { Minimum = 0, Maximum = 20, Increment = 1, Value = (decimal)s.Hysteresis, FormatString = "0", Width = 110 };
        var hystRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { Text("Slow down only after cooling by", wrap: false), _hysteresis, Text("°C", wrap: false) } };
        foreach (var c in hystRow.Children.OfType<TextBlock>()) c.VerticalAlignment = VerticalAlignment.Center;
        _curvePanel = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                _editor,
                Text("Drag a point to change it · double-click to add one · right-click to remove one. Shaded: below the safety minimum."),
                sourceRow,
                hystRow,
            },
        };

        // Buttons
        var identify = DialogButton("Identify");
        identify.Margin = new Thickness(0);
        ToolTip.SetTip(identify, "Runs this fan at full speed for 5 seconds so you can hear or see which one it is");
        identify.Click += async (_, _) => await Run(_service, new FanRequest { Cmd = "identify", Key = _fan.Key });
        var cancel = DialogButton("Cancel");
        cancel.Click += (_, _) => Close(null);
        _save = DialogButton("Save", primary: true);
        _save.Click += async (_, _) => await Save();
        var buttons = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        DockPanel.SetDock(identify, Dock.Left);
        buttons.Children.Add(identify);
        buttons.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, _save } });

        var body = new StackPanel
        {
            Margin = new Thickness(20, 16),
            Spacing = 12,
            Children =
            {
                Text(fan.Device),
                _name,
                new StackPanel { Orientation = Orientation.Horizontal, Children = { _auto, _fixed, _curve } },
                _fixedPanel,
                _curvePanel,
            },
        };
        if (status.Profile != FanConfig.CustomProfile)
            body.Children.Add(Text($"Saving switches the profile from {status.Profile} to Custom. The other fans keep what they are doing now."));
        body.Children.Add(buttons);
        Content = body;
        ShowMode();
    }

    private void ShowMode()
    {
        _fixedPanel.IsVisible = _fixed.IsChecked == true;
        _curvePanel.IsVisible = _curve.IsChecked == true;
    }

    private FanSetting Setting() => new()
    {
        Mode = _fixed.IsChecked == true ? FanControlMode.Fixed : _curve.IsChecked == true ? FanControlMode.Curve : FanControlMode.Auto,
        Percent = (float)_percent.Value,
        Points = _editor.Points.ToList(),
        Sources = _sources.Where(s => s.Box.IsChecked == true).Select(s => s.Key).Concat(_otherSources).ToList(),
        Hysteresis = (float)(_hysteresis.Value ?? 3),
    };

    private async Task Save()
    {
        var setting = Setting();
        if (setting.Mode == FanControlMode.Curve && setting.Sources.Count == 0)
        {
            await MessageDialog.Show(this, Title!, "Choose at least one temperature for the curve to follow.");
            return;
        }
        _save.IsEnabled = false;
        try
        {
            DaemonStatus? last = null;
            var name = _name.Text?.Trim() ?? "";
            if (name != _fan.Name)
            {
                var renamed = await Run(_service, new FanRequest { Cmd = "rename", Key = _fan.Key, Name = name });
                if (!renamed.Ok) return;
                last = renamed.Status;
            }
            var set = await Run(_service, new FanRequest { Cmd = "set", Key = _fan.Key, Setting = setting });
            if (set.Ok) Close(set.Status);
            else if (last != null) Close(last);
        }
        finally
        {
            _save.IsEnabled = true;
        }
    }
}

/// <summary>
/// Minimum fan speed and the critical temperatures that send every controlled fan to 100 %.
/// Raising protection is allowed straight away; lowering it asks for administrator approval.
/// </summary>
internal sealed class SafetyWindow : FanDialog
{
    public SafetyWindow(SafetySettings current, IFanService service) : base("Fan safety limits")
    {
        static NumericUpDown Number(double min, double max, double value, double step) =>
            new() { Minimum = (decimal)min, Maximum = (decimal)max, Value = (decimal)value, Increment = (decimal)step, FormatString = "0", Width = 130 };

        var floor = Number(0, 100, current.FloorPercent, 5);
        var cpu = Number(50, 110, current.CriticalCpu, 1);
        var gpu = Number(50, 110, current.CriticalGpu, 1);

        Grid Row(string label, string unit, Control input)
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,40") };
            var l = Text(label, 13, "TextPrimary", wrap: false);
            l.VerticalAlignment = VerticalAlignment.Center;
            var u = Text(unit, 13, wrap: false);
            u.VerticalAlignment = VerticalAlignment.Center;
            u.Margin = new Thickness(8, 0, 0, 0);
            Grid.SetColumn(input, 1);
            Grid.SetColumn(u, 2);
            g.Children.Add(l);
            g.Children.Add(input);
            g.Children.Add(u);
            return g;
        }

        var cancel = DialogButton("Cancel");
        cancel.Click += (_, _) => Close(null);
        var save = DialogButton("Save", primary: true);
        save.Click += async (_, _) =>
        {
            var next = new SafetySettings
            {
                FloorPercent = (float)(floor.Value ?? 20),
                CriticalCpu = (float)(cpu.Value ?? 95),
                CriticalGpu = (float)(gpu.Value ?? 90),
            };
            save.IsEnabled = false;
            if (current.IsLoweredBy(next)) save.Content = "Waiting for approval…";
            var resp = await Run(service, new FanRequest { Cmd = "safety", Safety = next });
            save.IsEnabled = true;
            save.Content = "Save";
            if (resp.Ok) Close(resp.Status);
        };

        Content = new StackPanel
        {
            Margin = new Thickness(20, 16),
            Spacing = 12,
            Children =
            {
                Text("These apply whatever profile or fan setting is chosen."),
                Row("Minimum speed of controlled fans", "%", floor),
                Row("All fans to 100% when the CPU reaches", "°C", cpu),
                Row("All fans to 100% when a GPU reaches", "°C", gpu),
                Text("A lower minimum or a higher critical temperature can let the computer overheat, so it asks for administrator approval."),
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, save } },
            },
        };
    }
}

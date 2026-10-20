using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace TempMonitor.App;

public partial class MetricCard : UserControl
{
    private readonly CardHistory _history = new();
    private Reading? _last;
    private float? _usage, _secondary;

    public MetricCard()
    {
        InitializeComponent();
        ChartArea.SizeChanged += (_, _) => DrawHistory();
        // The 2-minute graph opens the full history of this component
        if (HistoryButton.Available)
        {
            ChartArea.Background = Brushes.Transparent; // clickable between the lines too
            ChartArea.Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand);
            ToolTip.SetTip(ChartArea, "Open the history");
            ChartArea.PointerPressed += (_, _) =>
            {
                if (_last is { } r) HistoryButton.Open(this, r.Temperature.HasValue ? $"{r.Key}/temp" : $"{r.Key}/usage");
            };
        }
        UsageTrack.SizeChanged += (_, _) => SetBar(UsageTrack, UsageBar, _usage);
        SecondaryTrack.SizeChanged += (_, _) => SetBar(SecondaryTrack, SecondaryBar, _secondary);
    }

    private IBrush Brush(string key) => (IBrush)Application.Current!.FindResource(key)!;

    public void Update(Reading r, bool fahrenheit)
    {
        _last = r;
        TitleText.Text = r.Title;
        DeviceText.Text = r.DeviceName;
        ToolTip.SetTip(DeviceText, r.DeviceName);

        if (r.Temperature is { } t)
        {
            TempText.Text = Reading.FormatTemp(t, fahrenheit);
            TempText.Foreground = Brush(r.Level(t) switch
            {
                TempLevel.Hot => "HotBrush",
                TempLevel.Warm => "WarmBrush",
                _ => "CoolBrush",
            });
            TempLabel.Text = "Temperature · " + r.TemperatureSource;
            TempLine.Stroke = TempText.Foreground;
        }
        else
        {
            TempText.Text = "N/A";
            TempText.Foreground = Brush("TextMuted");
            TempLabel.Text = r.TemperatureUnavailableReason ?? "Not available";
        }

        UsageLabel.Text = r.UsageLabel;
        UsageText.Text = r.Usage is { } u ? $"{u:0}%" : "—";
        _usage = r.Usage;
        SetBar(UsageTrack, UsageBar, _usage);

        _secondary = r.Secondary;
        SecondaryPanel.IsVisible = r.Secondary.HasValue;
        if (r.Secondary is { } s)
        {
            SecondaryLabel.Text = r.SecondaryLabel;
            SecondaryText.Text = $"{s:0}%";
            SetBar(SecondaryTrack, SecondaryBar, s);
        }

        DetailsText.Text = string.Join("   ·   ", r.Details);
        DetailsText.IsVisible = r.Details.Count > 0;

        _history.Add(r.Temperature, r.Usage);
        LegendText.Text = r.Temperature.HasValue ? "— temperature   — usage   (last 2 min)" : "— usage   (last 2 min)";
        MinMaxText.Text = _history.MinTemp.HasValue
            ? $"min {Reading.FormatTemp(_history.MinTemp.Value, fahrenheit)}  ·  max {Reading.FormatTemp(_history.MaxTemp!.Value, fahrenheit)}"
            : "";
        DrawHistory();
    }

    private static void SetBar(Control track, Control bar, float? percent)
    {
        var width = track.Bounds.Width > 0 ? track.Bounds.Width : 286;
        bar.Width = Math.Clamp(percent ?? 0, 0, 100) / 100.0 * width;
    }

    private void DrawHistory()
    {
        double w = ChartArea.Bounds.Width, h = ChartArea.Bounds.Height;
        if (w <= 0 || h <= 0) return;
        UsageLine.Points = ToPoints(_history.UsagePoints(w, h));
        TempLine.Points = ToPoints(_history.TempPoints(w, h, _last?.HotAt ?? 100));
    }

    private static List<Point> ToPoints(IReadOnlyList<(double X, double Y)> points) =>
        points.Select(p => new Point(p.X, p.Y)).ToList();
}

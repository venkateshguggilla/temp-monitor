using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TempMonitor;

public partial class MetricCard : UserControl
{
    private const int HistoryLength = 120;

    private readonly Queue<float?> _temps = new();
    private readonly Queue<float?> _usages = new();
    private float? _minTemp, _maxTemp;
    private Reading? _last;

    public MetricCard()
    {
        InitializeComponent();
        ChartArea.SizeChanged += (_, _) => DrawHistory();
    }

    public void Update(Reading r, bool fahrenheit)
    {
        _last = r;
        TitleText.Text = r.Title;
        DeviceText.Text = r.DeviceName;
        DeviceText.ToolTip = r.DeviceName;

        if (r.Temperature is { } t)
        {
            _minTemp = Math.Min(_minTemp ?? t, t);
            _maxTemp = Math.Max(_maxTemp ?? t, t);
            TempText.Text = Reading.FormatTemp(t, fahrenheit);
            TempText.FontSize = 34;
            TempText.Foreground = (Brush)FindResource(
                t >= r.HotAt ? "HotBrush" : t >= r.WarmAt ? "WarmBrush" : "CoolBrush");
            TempLabel.Text = "Temperature · " + r.TemperatureSource;
            TempLine.Stroke = TempText.Foreground;
        }
        else
        {
            TempText.Text = "N/A";
            TempText.Foreground = (Brush)FindResource("TextMuted");
            TempLabel.Text = r.TemperatureUnavailableReason ?? "Not available";
        }

        UsageLabel.Text = r.UsageLabel;
        UsageText.Text = r.Usage is { } u ? $"{u:0}%" : "—";
        SetBar(UsageBar, r.Usage);

        if (r.Secondary is { } s)
        {
            SecondaryPanel.Visibility = Visibility.Visible;
            SecondaryLabel.Text = r.SecondaryLabel;
            SecondaryText.Text = $"{s:0}%";
            SetBar(SecondaryBar, s);
        }
        else
        {
            SecondaryPanel.Visibility = Visibility.Collapsed;
        }

        DetailsText.Text = string.Join("   ·   ", r.Details);
        DetailsText.Visibility = r.Details.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        Push(_temps, r.Temperature);
        Push(_usages, r.Usage);
        LegendText.Text = r.Temperature.HasValue ? "— temperature   — usage   (last 2 min)" : "— usage   (last 2 min)";
        MinMaxText.Text = _minTemp.HasValue
            ? $"min {Reading.FormatTemp(_minTemp.Value, fahrenheit)}  ·  max {Reading.FormatTemp(_maxTemp!.Value, fahrenheit)}"
            : "";
        DrawHistory();
    }

    private static void Push(Queue<float?> q, float? v)
    {
        q.Enqueue(v);
        while (q.Count > HistoryLength) q.Dequeue();
    }

    private void SetBar(Border bar, float? percent)
    {
        var track = (FrameworkElement)bar.Parent;
        var width = track.ActualWidth > 0 ? track.ActualWidth : 286;
        bar.Width = Math.Clamp(percent ?? 0, 0, 100) / 100.0 * width;
    }

    private void DrawHistory()
    {
        double w = ChartArea.ActualWidth, h = ChartArea.ActualHeight;
        if (w <= 0 || h <= 0) return;

        // Usage always spans 0–100 %. Temperature uses a fixed band around the card's
        // thresholds so the line's height means the same thing from second to second.
        UsageLine.Points = Plot(_usages, w, h, 0, 100);
        var hot = _last?.HotAt ?? 100;
        var hi = Math.Max(hot + 5, _maxTemp ?? 0);
        var lo = Math.Min(25, _minTemp ?? 25);
        TempLine.Points = Plot(_temps, w, h, lo, hi);
    }

    private static PointCollection Plot(IEnumerable<float?> values, double w, double h, double lo, double hi)
    {
        var points = new PointCollection();
        var step = w / (HistoryLength - 1);
        var arr = values.ToArray();
        var offset = HistoryLength - arr.Length;
        for (var i = 0; i < arr.Length; i++)
        {
            if (arr[i] is not { } v) continue;
            var y = h - (Math.Clamp(v, lo, hi) - lo) / (hi - lo) * (h - 2) - 1;
            points.Add(new Point((offset + i) * step, y));
        }
        return points;
    }
}

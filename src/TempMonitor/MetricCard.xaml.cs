using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TempMonitor;

public partial class MetricCard : UserControl
{
    private readonly CardHistory _history = new();
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
            TempText.Text = Reading.FormatTemp(t, fahrenheit);
            TempText.FontSize = 34;
            TempText.Foreground = (Brush)FindResource(r.Level(t) switch
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

        _history.Add(r.Temperature, r.Usage);
        LegendText.Text = r.Temperature.HasValue ? "— temperature   — usage   (last 2 min)" : "— usage   (last 2 min)";
        MinMaxText.Text = _history.MinTemp.HasValue
            ? $"min {Reading.FormatTemp(_history.MinTemp.Value, fahrenheit)}  ·  max {Reading.FormatTemp(_history.MaxTemp!.Value, fahrenheit)}"
            : "";
        DrawHistory();
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
        UsageLine.Points = ToPoints(_history.UsagePoints(w, h));
        TempLine.Points = ToPoints(_history.TempPoints(w, h, _last?.HotAt ?? 100));
    }

    private static PointCollection ToPoints(IReadOnlyList<(double X, double Y)> points) =>
        new(points.Select(p => new Point(p.X, p.Y)));
}

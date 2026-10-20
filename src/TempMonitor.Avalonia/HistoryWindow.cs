using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using TempMonitor.History;

namespace TempMonitor.App;

/// <summary>
/// History recorded by the background monitor: a chart of any series over 5 minutes to 30 days,
/// optionally compared with a second one, its min / average / max and time above "hot", the
/// alerts in that range, CSV export and Clear history.
/// </summary>
internal sealed class HistoryWindow : Window
{
    private static readonly (string Label, TimeSpan Span)[] Ranges =
    {
        ("5 min", TimeSpan.FromMinutes(5)), ("1 hour", TimeSpan.FromHours(1)), ("24 hours", TimeSpan.FromDays(1)),
        ("7 days", TimeSpan.FromDays(7)), ("30 days", TimeSpan.FromDays(30)),
    };
    private const int MaxPoints = 400;

    private readonly HistoryStore? _store;
    private readonly object _dbLock = new();
    private readonly List<HistoryMetric> _metrics = new();
    private readonly ComboBox _show = new() { MinWidth = 320 };
    private readonly ComboBox _compare = new() { MinWidth = 260 };
    private readonly List<RadioButton> _rangeButtons = new();
    private readonly HistoryChart _chart = new();
    private readonly TextBlock _summary = Text("", 12, "TextPrimary");
    private readonly ListBox _events = new() { Height = 150, Background = Brushes.Transparent };
    private readonly DispatcherTimer _refresh = new();
    private TimeSpan _span = Ranges[1].Span;
    private bool _loading;

    /// <summary>Opens history; <paramref name="metric"/> picks the series to show first (e.g. "cpu:0/temp").</summary>
    public HistoryWindow(string? metric = null)
    {
        Title = "Temp Monitor history";
        Width = 980;
        Height = 700;
        MinWidth = 640;
        MinHeight = 480;
        Background = Brush("WindowBg");
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var path = HistoryPaths.HistoryDb;
        if (path != null && File.Exists(path))
        {
            try { _store = new HistoryStore(path); }
            catch (Exception e) when (e is IOException or Microsoft.Data.Sqlite.SqliteException) { _chart.Message = "Could not open the history: " + e.Message; }
        }
        else
        {
            _chart.Message = "No history yet. The background monitor records it while you're logged in " +
                             "(systemctl --user status temp-monitor-background).";
        }

        Content = Layout();
        LoadMetrics(metric);
        _show.SelectionChanged += (_, _) => Reload();
        _compare.SelectionChanged += (_, _) => Reload();
        _refresh.Tick += (_, _) => Reload();
        Closed += (_, _) =>
        {
            _refresh.Stop();
            lock (_dbLock) _store?.Dispose();
        };
        SelectRange(1);
    }

    private Control Layout()
    {
        var ranges = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        for (var i = 0; i < Ranges.Length; i++)
        {
            var index = i;
            var b = new RadioButton { Content = Ranges[i].Label, GroupName = "range", Foreground = Brush("TextPrimary"), Margin = new Thickness(0, 0, 8, 0) };
            b.IsCheckedChanged += (_, _) => { if (b.IsChecked == true) SelectRange(index); };
            _rangeButtons.Add(b);
            ranges.Children.Add(b);
        }

        var export = Button("Export CSV…");
        export.Click += async (_, _) => await ExportCsv();
        var clear = Button("Clear history…");
        clear.Click += async (_, _) => await ClearHistory();
        export.IsEnabled = clear.IsEnabled = _store != null;

        var top = new DockPanel { Margin = new Thickness(20, 14, 20, 6) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { export, clear } };
        DockPanel.SetDock(actions, Dock.Right);
        top.Children.Add(actions);
        top.Children.Add(ranges);

        var pickers = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(20, 4, 20, 6),
            Children = { Label("Show"), _show, Label("Compare with"), _compare },
        };

        _summary.Margin = new Thickness(20, 2, 20, 8);
        _summary.TextWrapping = TextWrapping.Wrap;

        var eventsHeader = Text("Alerts in this range", 13, "TextPrimary");
        eventsHeader.FontWeight = FontWeight.SemiBold;
        var bottom = new StackPanel { Margin = new Thickness(20, 8, 20, 14), Spacing = 4, Children = { eventsHeader, _events } };

        var chartBorder = new Border
        {
            Margin = new Thickness(20, 0), CornerRadius = new CornerRadius(8), ClipToBounds = true,
            BorderBrush = Brush("CardBorder"), BorderThickness = new Thickness(1), Child = _chart,
        };

        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(pickers, Dock.Top);
        DockPanel.SetDock(_summary, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(pickers);
        root.Children.Add(_summary);
        root.Children.Add(bottom);
        root.Children.Add(chartBorder);
        return root;
    }

    private void LoadMetrics(string? preferred)
    {
        if (_store == null) return;
        lock (_dbLock)
            _metrics.AddRange(_store.Metrics().OrderBy(m => MetricInfo.Group(m.Name)));
        foreach (var m in _metrics) _show.Items.Add(m.Label);
        _compare.Items.Add("Nothing");
        foreach (var m in _metrics) _compare.Items.Add(m.Label);
        var index = preferred == null ? -1 : _metrics.FindIndex(m => m.Name == preferred);
        _show.SelectedIndex = index >= 0 ? index : _metrics.Count > 0 ? 0 : -1;
        _compare.SelectedIndex = 0;
        if (_metrics.Count == 0) _chart.Message = "Nothing recorded yet. The background monitor saves the first samples within 10 seconds.";
    }

    private void SelectRange(int index)
    {
        _span = Ranges[index].Span;
        if (_rangeButtons[index].IsChecked != true) _rangeButtons[index].IsChecked = true;
        // Short ranges move fast; long ones only change every minute
        _refresh.Interval = _span <= TimeSpan.FromHours(1) ? TimeSpan.FromSeconds(2) : TimeSpan.FromMinutes(1);
        _refresh.Start();
        Reload();
    }

    private HistoryMetric? Selected => _show.SelectedIndex >= 0 && _show.SelectedIndex < _metrics.Count ? _metrics[_show.SelectedIndex] : null;
    private HistoryMetric? Compared => _compare.SelectedIndex > 0 ? _metrics[_compare.SelectedIndex - 1] : null;

    private async void Reload()
    {
        if (_store == null || _loading || Selected is not { } metric) { _chart.InvalidateVisual(); return; }
        _loading = true;
        var other = Compared;
        var to = DateTime.UtcNow;
        var from = to - _span;
        try
        {
            var hot = MetricInfo.HotAt(metric.Name);
            var (points, overlay, events, aboveHot) = await Task.Run(() =>
            {
                lock (_dbLock)
                    return (_store.Query(metric.Name, from, to, MaxPoints),
                            other == null ? new List<HistoryPoint>() : _store.Query(other.Name, from, to, MaxPoints),
                            _store.Events(from, to),
                            hot is { } h ? _store.TimeAtOrAbove(metric.Name, from, to, h) : TimeSpan.Zero);
            });
            var unit = MetricInfo.Unit(metric.Name);
            var bucket = HistoryStore.BucketWidth(from, to, MaxPoints);
            _chart.SetData(points, unit, hot, overlay, other == null ? "" : MetricInfo.Unit(other.Name), from, to, bucket);
            ShowSummary(MetricInfo.Summarize(points, aboveHot), unit, hot);
            ShowEvents(events);
        }
        catch (Exception e) when (e is Microsoft.Data.Sqlite.SqliteException or ObjectDisposedException or InvalidOperationException)
        {
            _summary.Text = "Could not read the history: " + e.Message;
        }
        finally
        {
            _loading = false;
        }
    }

    private void ShowSummary(HistorySummary? s, string unit, float? hot)
    {
        if (s == null)
        {
            _summary.Text = "";
            return;
        }
        var text = $"Min {MetricInfo.Format(s.Min, unit)}   ·   Average {MetricInfo.Format(s.Avg, unit)}   ·   Max {MetricInfo.Format(s.Max, unit)}";
        if (hot is { } h)
            text += s.AboveHot > TimeSpan.Zero ? $"   ·   At or above hot ({h:0} °C) for about {MetricInfo.Duration(s.AboveHot)}"
                : s.Max >= h ? $"   ·   Reached hot ({h:0} °C) only briefly"
                : $"   ·   Never hot ({h:0} °C)";
        _summary.Text = text;
    }

    private void ShowEvents(List<HistoryEvent> events)
    {
        _events.Items.Clear();
        foreach (var e in events.AsEnumerable().Reverse().Take(200))
        {
            var row = Text($"{e.Time.ToLocalTime():ddd d MMM HH:mm:ss}    {e.Message}", 12,
                e.Level == "Critical" ? "HotBrush" : e.Level == "Warning" ? "WarmBrush" : "TextMuted");
            row.TextWrapping = TextWrapping.Wrap;
            _events.Items.Add(row);
        }
        if (events.Count == 0) _events.Items.Add(Text("No alerts. Good.", 12));
    }

    // ---------------------------------------------------------------- Export / clear

    private async Task ExportCsv()
    {
        if (_store == null || Selected is not { } metric) return;
        var other = Compared;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export history",
            SuggestedFileName = $"temp-monitor-{metric.Name.Replace(':', '-').Replace('/', '-')}-{DateTime.Now:yyyyMMdd-HHmm}.csv",
            FileTypeChoices = new[] { new FilePickerFileType("CSV") { Patterns = new[] { "*.csv" } } },
        });
        if (file == null) return;

        var to = DateTime.UtcNow;
        var from = to - _span;
        var csv = await Task.Run(() =>
        {
            List<HistoryPoint> points, overlay;
            lock (_dbLock)
            {
                // Full detail for the range: 1 s within the last 2 hours, 1 min beyond
                var max = (int)Math.Min(100_000, _span.TotalSeconds);
                points = _store.Query(metric.Name, from, to, max);
                overlay = other == null ? new() : _store.Query(other.Name, from, to, max);
            }
            var unit = MetricInfo.Unit(metric.Name);
            var sb = new StringBuilder();
            sb.Append($"time,{Csv(metric.Label)} avg ({unit}),min,max");
            if (other != null) sb.Append($",{Csv(other.Label)} avg ({MetricInfo.Unit(other.Name)})");
            sb.AppendLine();
            var byTime = overlay.ToDictionary(p => p.Time, p => p.Avg);
            foreach (var p in points)
            {
                sb.Append(CultureInfo.InvariantCulture, $"{p.Time.ToLocalTime():yyyy-MM-dd HH:mm:ss},{p.Avg:0.##},{p.Min:0.##},{p.Max:0.##}");
                if (other != null)
                    sb.Append(',').Append(byTime.TryGetValue(p.Time, out var o) ? o.ToString("0.##", CultureInfo.InvariantCulture) : "");
                sb.AppendLine();
            }
            return sb.ToString();
        });
        await using var stream = await file.OpenWriteAsync();
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        await writer.WriteAsync(csv);
    }

    private static string Csv(string s) => s.Contains(',') || s.Contains('"') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    private async Task ClearHistory()
    {
        if (_store == null) return;
        if (!await Confirm("Clear history?", "This deletes every recorded temperature, usage figure, fan speed and alert on this computer. Recording continues afterwards.", "Clear history"))
            return;
        await Task.Run(() => { lock (_dbLock) _store.Clear(); });
        Reload();
    }

    private Task<bool> Confirm(string title, string message, string action)
    {
        var dlg = new Window
        {
            Title = title, Width = 420, SizeToContent = SizeToContent.Height, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Brush("WindowBg"),
        };
        var cancel = Button("Cancel");
        var ok = Button(action);
        ok.Background = Brush("HotBrush");
        ok.Foreground = Brushes.White;
        cancel.Click += (_, _) => dlg.Close(false);
        ok.Click += (_, _) => dlg.Close(true);
        var msg = Text(message, 13, "TextPrimary");
        msg.TextWrapping = TextWrapping.Wrap;
        dlg.Content = new StackPanel
        {
            Margin = new Thickness(20, 16), Spacing = 16,
            Children = { msg, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, ok } } },
        };
        return dlg.ShowDialog<bool>(this);
    }

    // ---------------------------------------------------------------- Helpers

    private static IBrush Brush(string key) => (IBrush)Application.Current!.FindResource(key)!;

    private static TextBlock Text(string s, double size, string brush = "TextMuted") =>
        new() { Text = s, FontSize = size, Foreground = Brush(brush) };

    private static TextBlock Label(string s)
    {
        var t = Text(s, 12);
        t.VerticalAlignment = VerticalAlignment.Center;
        return t;
    }

    private static Button Button(string s) => new()
    {
        Content = s, Padding = new Thickness(12, 4), Background = Brush("CardBg"), Foreground = Brush("TextPrimary"),
        BorderBrush = Brush("CardBorder"), BorderThickness = new Thickness(1),
    };
}

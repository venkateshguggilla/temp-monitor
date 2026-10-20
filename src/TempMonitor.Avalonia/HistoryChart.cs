using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using TempMonitor.History;

namespace TempMonitor.App;

/// <summary>
/// A history chart: the min–max band and average line of one series (left axis), an optional
/// second series to compare (right axis), the "hot" line for temperatures, and the value under
/// the pointer. Gaps where nothing was recorded (computer off) are left empty.
/// </summary>
internal sealed class HistoryChart : Control
{
    private const double PadL = 58, PadR = 64, PadT = 12, PadB = 26;

    private IReadOnlyList<HistoryPoint> _points = Array.Empty<HistoryPoint>();
    private IReadOnlyList<HistoryPoint> _overlay = Array.Empty<HistoryPoint>();
    private string _unit = "°C", _overlayUnit = "";
    private float? _hot;
    private DateTime _from, _to;
    private TimeSpan _bucket = TimeSpan.FromSeconds(1);
    private Point? _hover;

    public string Message { get; set; } = "";

    public void SetData(IReadOnlyList<HistoryPoint> points, string unit, float? hotAt,
        IReadOnlyList<HistoryPoint> overlay, string overlayUnit, DateTime from, DateTime to, TimeSpan bucket)
    {
        (_points, _unit, _hot, _overlay, _overlayUnit, _from, _to, _bucket) = (points, unit, hotAt, overlay, overlayUnit, from, to, bucket);
        InvalidateVisual();
    }

    private Rect Plot => new(PadL, PadT, Math.Max(1, Bounds.Width - PadL - PadR), Math.Max(1, Bounds.Height - PadT - PadB));

    /// <summary>Axis range for a series: 0–100 for %, from 0 for RPM, around the data for °C.</summary>
    private (double Lo, double Hi) Range(IReadOnlyList<HistoryPoint> pts, string unit, float? hot)
    {
        if (unit == "%") return (0, 100);
        if (pts.Count == 0) return (0, 100);
        double lo = pts.Min(p => p.Min);
        double hi = pts.Max(p => p.Max);
        if (unit == "RPM") return Nice(0, Math.Max(100, hi * 1.1));
        if (hot is { } h && h - hi < 15) hi = Math.Max(hi, h + 2); // show the hot line when it's near
        return Nice(lo - 5, hi + 5);
    }

    /// <summary>A range starting at a round number and split into 4 round steps (1, 2, 2.5, 5 × 10ⁿ).</summary>
    private static (double Lo, double Hi) Nice(double lo, double hi)
    {
        var raw = Math.Max(1e-6, (hi - lo) / 4);
        var mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        foreach (var m in new[] { 1, 2, 2.5, 5, 10, 20 })
        {
            var step = m * mag;
            var start = Math.Floor(lo / step) * step;
            if (start + 4 * step >= hi) return (start, start + 4 * step);
        }
        return (lo, hi);
    }

    private double X(DateTime t) => Plot.X + (t - _from).TotalSeconds / Math.Max(1, (_to - _from).TotalSeconds) * Plot.Width;
    private double Y(double v, (double Lo, double Hi) r) => Plot.Bottom - (v - r.Lo) / Math.Max(1e-6, r.Hi - r.Lo) * Plot.Height;

    public override void Render(DrawingContext ctx)
    {
        var plot = Plot;
        var muted = Brush("TextMuted");
        ctx.FillRectangle(Brush("CardBg"), new Rect(Bounds.Size));
        var grid = new Pen(Brush("TrackBg"), 1);

        if (_points.Count == 0)
        {
            Label(ctx, Message.Length > 0 ? Message : "Nothing recorded in this range yet.", new Point(plot.X + 12, plot.Y + plot.Height / 2 - 8), muted, 13);
            return;
        }

        var r = Range(_points, _unit, _hot);
        var ro = _overlay.Count > 0 ? Range(_overlay, _overlayUnit, null) : r;

        // Grid and axes: 5 value lines, ~6 time labels
        for (var i = 0; i <= 4; i++)
        {
            var v = r.Lo + (r.Hi - r.Lo) * i / 4;
            var y = Y(v, r);
            ctx.DrawLine(grid, new Point(plot.X, y), new Point(plot.Right, y));
            Label(ctx, MetricInfo.Format((float)v, _unit), new Point(4, y - 7), muted);
            if (_overlay.Count > 0)
                Label(ctx, MetricInfo.Format((float)(ro.Lo + (ro.Hi - ro.Lo) * i / 4), _overlayUnit), new Point(plot.Right + 6, y - 7), Brush("SecondaryBrush"));
        }
        var span = _to - _from;
        var timeFormat = span <= TimeSpan.FromMinutes(10) ? "HH:mm:ss" : span <= TimeSpan.FromDays(1.5) ? "HH:mm" : span <= TimeSpan.FromDays(8) ? "ddd HH:mm" : "MMM d";
        for (var i = 0; i <= 5; i++)
        {
            var t = _from + span * i / 5;
            var x = X(t);
            ctx.DrawLine(grid, new Point(x, plot.Y), new Point(x, plot.Bottom));
            Label(ctx, t.ToLocalTime().ToString(timeFormat, CultureInfo.CurrentCulture), new Point(Math.Min(x - 20, plot.Right - 50), plot.Bottom + 6), muted);
        }

        // Hot line
        if (_hot is { } hot && hot >= r.Lo && hot <= r.Hi)
        {
            var y = Y(hot, r);
            ctx.DrawLine(new Pen(Brush("HotBrush"), 1, new DashStyle(new double[] { 4, 3 }, 0)), new Point(plot.X, y), new Point(plot.Right, y));
            Label(ctx, $"hot {hot:0} °C", new Point(plot.X + 6, y - 15), Brush("HotBrush"));
        }

        using (ctx.PushClip(plot))
        {
            var main = Brush("UsageBrush");
            foreach (var run in Runs(_points))
            {
                // Min–max band, then the average line
                if (run.Count > 1)
                {
                    var band = new StreamGeometry();
                    using (var g = band.Open())
                    {
                        g.BeginFigure(new Point(X(run[0].Time), Y(run[0].Max, r)), true);
                        foreach (var p in run.Skip(1)) g.LineTo(new Point(X(p.Time), Y(p.Max, r)));
                        foreach (var p in run.AsEnumerable().Reverse()) g.LineTo(new Point(X(p.Time), Y(p.Min, r)));
                        g.EndFigure(true);
                    }
                    ctx.DrawGeometry(new SolidColorBrush(Color.FromArgb(50, 0x4C, 0x9A, 0xFF)), null, band);
                }
                Line(ctx, run, r, new Pen(main, 1.6));
            }
            if (_overlay.Count > 0)
                foreach (var run in Runs(_overlay))
                    Line(ctx, run, ro, new Pen(Brush("SecondaryBrush"), 1.4));
        }

        // Value under the pointer
        if (_hover is { } h && plot.Contains(h))
        {
            var p = Nearest(_points, h.X);
            var x = X(p.Time);
            ctx.DrawLine(new Pen(Brush("TextMuted"), 1), new Point(x, plot.Y), new Point(x, plot.Bottom));
            ctx.DrawEllipse(Brush("TextPrimary"), null, new Point(x, Y(p.Avg, r)), 3.5, 3.5);
            var text = $"{p.Time.ToLocalTime().ToString(span <= TimeSpan.FromDays(1.5) ? "HH:mm:ss" : "ddd d MMM HH:mm", CultureInfo.CurrentCulture)}   " +
                       $"{MetricInfo.Format(p.Avg, _unit)}" + (p.Max - p.Min >= 1 ? $" (min {MetricInfo.Format(p.Min, _unit)}, max {MetricInfo.Format(p.Max, _unit)})" : "");
            if (_overlay.Count > 0)
                text += $"   ·   {MetricInfo.Format(Nearest(_overlay, h.X).Avg, _overlayUnit)}";
            var ft = Text(text, Brush("TextPrimary"), 11);
            var bx = Math.Clamp(x + 8, plot.X, plot.Right - ft.Width - 12);
            ctx.FillRectangle(Brush("WindowBg"), new Rect(bx - 4, plot.Y + 2, ft.Width + 8, ft.Height + 4), 4);
            ctx.DrawText(ft, new Point(bx, plot.Y + 4));
        }
    }

    /// <summary>Splits points where the gap is much bigger than a bucket (nothing recorded then).</summary>
    private IEnumerable<List<HistoryPoint>> Runs(IReadOnlyList<HistoryPoint> pts)
    {
        var gap = TimeSpan.FromTicks(Math.Max(_bucket.Ticks * 3, TimeSpan.FromSeconds(5).Ticks));
        var run = new List<HistoryPoint>();
        foreach (var p in pts)
        {
            if (run.Count > 0 && p.Time - run[^1].Time > gap)
            {
                yield return run;
                run = new List<HistoryPoint>();
            }
            run.Add(p);
        }
        if (run.Count > 0) yield return run;
    }

    private void Line(DrawingContext ctx, List<HistoryPoint> run, (double Lo, double Hi) r, Pen pen)
    {
        if (run.Count == 1)
        {
            ctx.DrawEllipse(pen.Brush, null, new Point(X(run[0].Time), Y(run[0].Avg, r)), 1.5, 1.5);
            return;
        }
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(new Point(X(run[0].Time), Y(run[0].Avg, r)), false);
            foreach (var p in run.Skip(1)) g.LineTo(new Point(X(p.Time), Y(p.Avg, r)));
            g.EndFigure(false);
        }
        ctx.DrawGeometry(null, pen, geo);
    }

    private HistoryPoint Nearest(IReadOnlyList<HistoryPoint> pts, double x)
    {
        var t = _from + TimeSpan.FromSeconds((x - Plot.X) / Plot.Width * (_to - _from).TotalSeconds);
        return pts.MinBy(p => Math.Abs((p.Time - t).Ticks));
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        _hover = e.GetPosition(this);
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hover = null;
        InvalidateVisual();
    }

    private static FormattedText Text(string s, IBrush brush, double size = 10) =>
        new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, size, brush);

    private static void Label(DrawingContext ctx, string s, Point at, IBrush brush, double size = 10) => ctx.DrawText(Text(s, brush, size), at);

    private static IBrush Brush(string key) => (IBrush)Application.Current!.FindResource(key)!;
}

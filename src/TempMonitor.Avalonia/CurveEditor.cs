using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using TempMonitor.Fans;

namespace TempMonitor.App;

/// <summary>
/// A fan curve on a temperature (20–100 °C) × speed (0–100 %) grid. Drag a point to move it,
/// double-click empty space to add one, right-click a point to remove it (at least 2 stay).
/// A dashed line marks the temperature the fan follows right now.
/// </summary>
internal sealed class CurveEditor : Control
{
    private const float MinT = 20, MaxT = 100;
    private const double PadL = 34, PadR = 10, PadT = 8, PadB = 22, Grab = 10;

    private readonly List<CurvePoint> _points = new();
    private int _drag = -1;

    public event Action? Changed;

    /// <summary>Temperature the fan follows now (shown as a marker), if known.</summary>
    public float? CurrentTemp { get; set; }

    /// <summary>Lowest speed the service allows (safety floor), shaded.</summary>
    public float Floor { get; set; }

    public IReadOnlyList<CurvePoint> Points => _points;

    public void SetPoints(IEnumerable<CurvePoint> points)
    {
        _points.Clear();
        _points.AddRange(points.OrderBy(p => p.Temp));
        InvalidateVisual();
    }

    public CurveEditor()
    {
        Height = 200;
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    private Rect Plot => new(PadL, PadT, Math.Max(1, Bounds.Width - PadL - PadR), Math.Max(1, Bounds.Height - PadT - PadB));

    private Point ToScreen(float temp, float pct)
    {
        var r = Plot;
        return new Point(r.X + (temp - MinT) / (MaxT - MinT) * r.Width, r.Bottom - pct / 100 * r.Height);
    }

    private (float temp, float pct) FromScreen(Point p)
    {
        var r = Plot;
        var t = MinT + (float)((p.X - r.X) / r.Width) * (MaxT - MinT);
        var pct = (float)((r.Bottom - p.Y) / r.Height) * 100;
        return (MathF.Round(Math.Clamp(t, MinT, MaxT)), MathF.Round(Math.Clamp(pct, 0, 100)));
    }

    public override void Render(DrawingContext ctx)
    {
        var r = Plot;
        var grid = new Pen(Brush("TrackBg"), 1);
        var muted = Brush("TextMuted");
        ctx.FillRectangle(Brush("WindowBg"), new Rect(Bounds.Size));

        // Safety floor
        if (Floor > 0)
            ctx.FillRectangle(new SolidColorBrush(Color.FromArgb(40, 0xFF, 0x5C, 0x5C)),
                new Rect(r.X, ToScreen(MinT, Floor).Y, r.Width, r.Bottom - ToScreen(MinT, Floor).Y));

        for (var t = MinT; t <= MaxT; t += 10)
        {
            var x = ToScreen(t, 0).X;
            ctx.DrawLine(grid, new Point(x, r.Y), new Point(x, r.Bottom));
            if ((int)t % 20 == 0) Label(ctx, $"{t:0}°", new Point(x - 8, r.Bottom + 4), muted);
        }
        for (var p = 0; p <= 100; p += 25)
        {
            var y = ToScreen(MinT, p).Y;
            ctx.DrawLine(grid, new Point(r.X, y), new Point(r.Right, y));
            Label(ctx, $"{p}%", new Point(2, y - 7), muted);
        }

        if (_points.Count > 0)
        {
            // Flat before the first point and after the last, as the service applies it
            var line = new Pen(Brush("UsageBrush"), 2);
            var pts = new List<Point> { ToScreen(MinT, _points[0].Percent) };
            pts.AddRange(_points.Select(p => ToScreen(p.Temp, p.Percent)));
            pts.Add(ToScreen(MaxT, _points[^1].Percent));
            for (var i = 1; i < pts.Count; i++) ctx.DrawLine(line, pts[i - 1], pts[i]);

            var dot = Brush("UsageBrush");
            var ring = new Pen(Brush("TextPrimary"), 1.5);
            for (var i = 0; i < _points.Count; i++)
                ctx.DrawEllipse(i == _drag ? Brush("TextPrimary") : dot, ring, ToScreen(_points[i].Temp, _points[i].Percent), 5, 5);
        }

        if (CurrentTemp is { } now && now >= MinT && now <= MaxT)
        {
            var x = ToScreen(now, 0).X;
            var pen = new Pen(Brush("WarmBrush"), 1, new DashStyle(new double[] { 3, 3 }, 0));
            ctx.DrawLine(pen, new Point(x, r.Y), new Point(x, r.Bottom));
            Label(ctx, $"now {now:0} °C", new Point(Math.Min(x + 4, r.Right - 60), r.Y + 2), Brush("WarmBrush"));
        }

        if (_drag >= 0)
        {
            var p = _points[_drag];
            Label(ctx, $"{p.Temp:0} °C → {p.Percent:0}%", new Point(r.X + 6, r.Y + 2), Brush("TextPrimary"));
        }
    }

    private static void Label(DrawingContext ctx, string text, Point at, IBrush brush) =>
        ctx.DrawText(new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 10, brush), at);

    private int Hit(Point p)
    {
        for (var i = 0; i < _points.Count; i++)
        {
            var s = ToScreen(_points[i].Temp, _points[i].Percent);
            if (Math.Abs(s.X - p.X) <= Grab && Math.Abs(s.Y - p.Y) <= Grab) return i;
        }
        return -1;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var pos = e.GetPosition(this);
        var hit = Hit(pos);
        if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
        {
            if (hit >= 0 && _points.Count > 2)
            {
                _points.RemoveAt(hit);
                Changed?.Invoke();
                InvalidateVisual();
            }
            return;
        }
        if (hit < 0 && e.ClickCount == 2)
        {
            var (t, pct) = FromScreen(pos);
            if (_points.All(p => Math.Abs(p.Temp - t) >= 2))
            {
                _points.Add(new CurvePoint(t, pct));
                _points.Sort((a, b) => a.Temp.CompareTo(b.Temp));
                Changed?.Invoke();
                InvalidateVisual();
            }
            return;
        }
        if (hit >= 0)
        {
            _drag = hit;
            e.Pointer.Capture(this);
            InvalidateVisual();
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_drag < 0) return;
        var (t, pct) = FromScreen(e.GetPosition(this));
        // Keep points in temperature order, at least 1 °C apart
        var lo = _drag > 0 ? _points[_drag - 1].Temp + 1 : MinT;
        var hi = _drag < _points.Count - 1 ? _points[_drag + 1].Temp - 1 : MaxT;
        _points[_drag] = new CurvePoint(Math.Clamp(t, lo, hi), pct);
        Changed?.Invoke();
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_drag < 0) return;
        _drag = -1;
        e.Pointer.Capture(null);
        InvalidateVisual();
    }

    private static IBrush Brush(string key) => (IBrush)Application.Current!.FindResource(key)!;
}

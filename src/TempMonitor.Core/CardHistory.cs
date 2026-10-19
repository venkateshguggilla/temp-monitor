namespace TempMonitor;

/// <summary>
/// The last two minutes of one card (one sample per refresh), its lowest and highest
/// temperature since the card appeared, and the graph geometry. UI-independent so every
/// front end draws the same graph.
/// </summary>
public sealed class CardHistory
{
    public const int Length = 120;

    private readonly Queue<float?> _temps = new();
    private readonly Queue<float?> _usages = new();

    public float? MinTemp { get; private set; }
    public float? MaxTemp { get; private set; }

    public void Add(float? temperature, float? usage)
    {
        if (temperature is { } t)
        {
            MinTemp = Math.Min(MinTemp ?? t, t);
            MaxTemp = Math.Max(MaxTemp ?? t, t);
        }
        Push(_temps, temperature);
        Push(_usages, usage);
    }

    /// <summary>Usage always spans 0–100 %.</summary>
    public IReadOnlyList<(double X, double Y)> UsagePoints(double width, double height) =>
        Plot(_usages, width, height, 0, 100);

    /// <summary>
    /// Temperature uses a fixed band around the card's thresholds (25 °C to hot + 5 °C,
    /// widened by anything seen), so the line's height means the same from second to second.
    /// </summary>
    public IReadOnlyList<(double X, double Y)> TempPoints(double width, double height, float hotAt)
    {
        var hi = Math.Max(hotAt + 5, MaxTemp ?? 0);
        var lo = Math.Min(25, MinTemp ?? 25);
        return Plot(_temps, width, height, lo, hi);
    }

    private static void Push(Queue<float?> q, float? v)
    {
        q.Enqueue(v);
        while (q.Count > Length) q.Dequeue();
    }

    /// <summary>Newest sample at the right edge; gaps (null) are skipped.</summary>
    private static List<(double X, double Y)> Plot(IEnumerable<float?> values, double w, double h, double lo, double hi)
    {
        var points = new List<(double X, double Y)>();
        if (w <= 0 || h <= 0) return points;
        var step = w / (Length - 1);
        var arr = values.ToArray();
        var offset = Length - arr.Length;
        for (var i = 0; i < arr.Length; i++)
        {
            if (arr[i] is not { } v) continue;
            var y = h - (Math.Clamp(v, lo, hi) - lo) / (hi - lo) * (h - 2) - 1;
            points.Add(((offset + i) * step, y));
        }
        return points;
    }
}

namespace TempMonitor;

public enum AlertLevel { None, Warning, Critical }

/// <summary>An overheat (or fan) alert to show the user. Level None means "back to normal" (logged, not shown).</summary>
public sealed record Alert(string Key, AlertLevel Level, string Title, string Message, float? Temperature = null);

/// <summary>When overheat alerts fire. Stored in the monitor settings.</summary>
public sealed class AlertSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>Warning: at or above the component's "hot" temperature for this long.</summary>
    public int WarningAfterSeconds { get; set; } = 30;

    /// <summary>Critical: this many °C above "hot"...</summary>
    public float CriticalMargin { get; set; } = 5;

    /// <summary>...for this long (a short spike is not critical).</summary>
    public int CriticalAfterSeconds { get; set; } = 5;

    /// <summary>The same alert again only after this long; getting worse always alerts at once.</summary>
    public int RepeatMinutes { get; set; } = 10;

    /// <summary>Back to normal once this many °C below "hot".</summary>
    public float ClearMargin { get; set; } = 5;
}

/// <summary>
/// Turns a stream of readings into overheat alerts. Each component is "hot" from its own
/// <see cref="Reading.HotAt"/> (CPU 95, GPU 90, drives 70, RAM 80 °C): staying there gives a
/// warning, going clearly above it a critical alert. Repeats are rate-limited.
/// </summary>
public sealed class AlertTracker
{
    private readonly AlertSettings _s;
    private readonly Dictionary<string, State> _state = new();

    private sealed class State
    {
        public DateTime? HotSince;
        public DateTime? CriticalSince;
        public AlertLevel Active;
        public DateTime LastAlertAt;
    }

    public AlertTracker(AlertSettings settings) => _s = settings;

    public List<Alert> Update(IEnumerable<Reading> readings, DateTime now)
    {
        var alerts = new List<Alert>();
        foreach (var r in readings)
        {
            if (r.Temperature is not { } t) continue;
            if (!_state.TryGetValue(r.Key, out var st)) _state[r.Key] = st = new State();

            var criticalAt = r.HotAt + _s.CriticalMargin;
            st.HotSince = t >= r.HotAt ? st.HotSince ?? now : null;
            st.CriticalSince = t >= criticalAt ? st.CriticalSince ?? now : null;

            var level =
                st.CriticalSince is { } c && now - c >= TimeSpan.FromSeconds(_s.CriticalAfterSeconds) ? AlertLevel.Critical
                : st.HotSince is { } h && now - h >= TimeSpan.FromSeconds(_s.WarningAfterSeconds) ? AlertLevel.Warning
                : AlertLevel.None;

            if (level != AlertLevel.None)
            {
                var worse = level > st.Active;
                var repeat = level >= st.Active && now - st.LastAlertAt >= TimeSpan.FromMinutes(_s.RepeatMinutes);
                if (worse || repeat)
                {
                    alerts.Add(Make(r, level, t));
                    st.LastAlertAt = now;
                }
                if (level > st.Active) st.Active = level;
            }
            else if (st.Active != AlertLevel.None && t < r.HotAt - _s.ClearMargin)
            {
                st.Active = AlertLevel.None;
                alerts.Add(new Alert(r.Key, AlertLevel.None, $"{r.Title} back to normal",
                    $"{r.DeviceName}: {Reading.FormatTemp(t, false)}", t));
            }
        }
        return alerts;
    }

    private Alert Make(Reading r, AlertLevel level, float t) => level == AlertLevel.Critical
        ? new Alert(r.Key, level, $"{r.Title} is overheating",
            $"{r.DeviceName} is at {Reading.FormatTemp(t, false)}. Close heavy apps now: the computer may slow down or switch off to protect itself.", t)
        : new Alert(r.Key, level, $"{r.Title} is running hot",
            $"{r.DeviceName} has been at {Reading.FormatTemp(t, false)} for over {_s.WarningAfterSeconds} s (hot from {Reading.FormatTemp(r.HotAt, false)}). Check its fans and that the vents aren't blocked.", t);
}

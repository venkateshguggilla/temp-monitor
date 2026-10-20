namespace TempMonitor.History;

/// <summary>Where the history lives.</summary>
public static class HistoryPaths
{
    /// <summary>
    /// The history database: ~/.local/state/temp-monitor/history.db on Linux (written by the
    /// background monitor). TEMPMONITOR_HISTORY_DB overrides it (development, other users' files).
    /// Null where there is no background monitor (Windows).
    /// </summary>
    public static string? HistoryDb =>
        Environment.GetEnvironmentVariable("TEMPMONITOR_HISTORY_DB") is { Length: > 0 } custom ? custom
        : OperatingSystem.IsLinux() ? Path.Combine(StateDir, "history.db")
        : null;

    public static string StateDir => Path.Combine(Xdg("XDG_STATE_HOME", ".local/state"), "temp-monitor");
    public static string MonitorSettings => Path.Combine(Xdg("XDG_CONFIG_HOME", ".config"), "temp-monitor", "monitor.json");

    private static string Xdg(string variable, string fallback) =>
        Environment.GetEnvironmentVariable(variable) is { Length: > 0 } v ? v
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), fallback);
}

/// <summary>Min / average / max over a range, and how long it was at or above "hot".</summary>
public sealed record HistorySummary(float Min, float Avg, float Max, TimeSpan AboveHot);

/// <summary>What a metric name ("cpu:0/temp", "ram/usage", "fan:.../rpm") means for display.</summary>
public static class MetricInfo
{
    public static string Unit(string metric) =>
        metric.EndsWith("/temp", StringComparison.Ordinal) ? "°C"
        : metric.EndsWith("/rpm", StringComparison.Ordinal) ? "RPM"
        : "%";

    /// <summary>The same "hot" levels the sensor providers give each device (CPU 95, GPU 90, drives 70, RAM 80 °C).</summary>
    public static float? HotAt(string metric) =>
        !metric.EndsWith("/temp", StringComparison.Ordinal) ? null
        : metric.StartsWith("cpu:", StringComparison.Ordinal) ? 95
        : metric.StartsWith("gpu:", StringComparison.Ordinal) ? 90
        : metric.StartsWith("disk:", StringComparison.Ordinal) ? 70
        : metric.StartsWith("ram", StringComparison.Ordinal) ? 80
        : null;

    /// <summary>Temperatures first, then usage, then fan speeds; within each, the order recorded.</summary>
    public static int Group(string metric) => Unit(metric) switch { "°C" => 0, "%" => 1, _ => 2 };

    public static string Format(float value, string unit) => unit switch
    {
        "RPM" => $"{value:#,0} RPM",
        "°C" => $"{value:0} °C",
        _ => $"{value:0}%",
    };

    /// <summary>
    /// Min / average / max of a chart's points (the average weighted equally per point), with
    /// the time at or above hot measured separately (<see cref="HistoryStore.TimeAtOrAbove"/>).
    /// </summary>
    public static HistorySummary? Summarize(IReadOnlyList<HistoryPoint> points, TimeSpan aboveHot)
    {
        if (points.Count == 0) return null;
        return new HistorySummary(points.Min(p => p.Min), points.Average(p => p.Avg), points.Max(p => p.Max), aboveHot);
    }

    public static string Duration(TimeSpan t) =>
        t < TimeSpan.FromMinutes(1) ? $"{t.TotalSeconds:0} s"
        : t < TimeSpan.FromHours(1) ? $"{t.TotalMinutes:0} min"
        : t < TimeSpan.FromDays(1) ? $"{(int)t.TotalHours} h {t.Minutes} min"
        : $"{(int)t.TotalDays} d {t.Hours} h";
}

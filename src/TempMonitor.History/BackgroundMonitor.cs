using System.Text.Json;
using TempMonitor.Fans;

namespace TempMonitor.History;

/// <summary>Shows a desktop notification (notify-send on Linux).</summary>
public interface INotifier
{
    void Notify(string title, string message, bool critical);
}

/// <summary>Background monitor settings (~/.config/temp-monitor/monitor.json).</summary>
public sealed class MonitorSettings
{
    public AlertSettings Alerts { get; set; } = new();

    /// <summary>Days of per-minute history (and events) to keep.</summary>
    public int RetentionDays { get; set; } = 30;

    public static MonitorSettings Load(string path)
    {
        try { return JsonSerializer.Deserialize<MonitorSettings>(File.ReadAllText(path), FanConfig.Json) ?? new MonitorSettings(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return new MonitorSettings(); }
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, FanConfig.Json));
    }
}

/// <summary>
/// Runs while the user is logged in (a systemd user service on Linux), with or without the app
/// open: once a second it records every temperature, usage and fan speed into the history and
/// turns overheating or a stalled fan into a desktop notification and a logged event.
/// </summary>
public sealed class BackgroundMonitor
{
    private static readonly TimeSpan FlushEvery = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PruneEvery = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan FanCheckEvery = TimeSpan.FromSeconds(5);

    private readonly ISensorProvider _sensors;
    private readonly HistoryStore _store;
    private readonly INotifier _notifier;
    private readonly Func<DaemonStatus?> _fanStatus;
    private readonly MonitorSettings _settings;
    private readonly Func<DateTime> _now;
    private readonly Action<string> _log;
    private readonly AlertTracker _alerts;
    private readonly HashSet<string> _stalled = new();
    private DateTime _lastFlush, _lastPrune, _lastFanCheck = DateTime.MinValue;

    public BackgroundMonitor(ISensorProvider sensors, HistoryStore store, INotifier notifier, Func<DaemonStatus?> fanStatus,
        MonitorSettings settings, Func<DateTime>? now = null, Action<string>? log = null)
    {
        _sensors = sensors;
        _store = store;
        _notifier = notifier;
        _fanStatus = fanStatus;
        _settings = settings;
        _now = now ?? (() => DateTime.UtcNow);
        _log = log ?? (_ => { });
        _alerts = new AlertTracker(settings.Alerts);
        _lastFlush = _lastPrune = _now();
    }

    /// <summary>One second's work: record, alert, and now and then flush / prune.</summary>
    public void Step()
    {
        var now = _now();
        var readings = _sensors.Read();
        var fans = _sensors.ReadFans();

        _store.Append(now, Samples(readings, fans));

        foreach (var alert in _alerts.Update(readings, now))
            Raise(now, "temperature", alert);

        if (now - _lastFanCheck >= FanCheckEvery)
        {
            _lastFanCheck = now;
            CheckFans(now);
        }
        if (now - _lastFlush >= FlushEvery)
        {
            _lastFlush = now;
            _store.Flush();
        }
        if (now - _lastPrune >= PruneEvery)
        {
            _lastPrune = now;
            _store.Prune(now, _settings.RetentionDays);
        }
    }

    /// <summary>What gets recorded: temperature and usage of every device, every fan's speed.</summary>
    internal static IEnumerable<(string, string, float)> Samples(List<Reading> readings, FanReport fans)
    {
        foreach (var r in readings)
        {
            if (r.Temperature is { } t) yield return ($"{r.Key}/temp", $"{r.Title} temperature · {r.DeviceName}", t);
            if (r.Usage is { } u) yield return ($"{r.Key}/usage", $"{r.Title} {r.UsageLabel.ToLowerInvariant()} · {r.DeviceName}", u);
        }
        foreach (var f in fans.Fans)
            if (f.Rpm is { } rpm) yield return ($"fan:{f.Key}/rpm", $"{f.Name} speed · {f.Device}", rpm);
    }

    /// <summary>Stalled fans, as reported by the fan service (only fans it drives can stall).</summary>
    private void CheckFans(DateTime now)
    {
        DaemonStatus? status;
        try { status = _fanStatus(); }
        catch (Exception e) when (e is IOException or InvalidOperationException) { status = null; }
        if (status == null) return;

        foreach (var f in status.Fans)
        {
            if (f.Stalled && _stalled.Add(f.Key))
                Raise(now, "fan", new Alert(f.Key, AlertLevel.Critical, $"Fan stopped: {f.Name}",
                    $"{f.Name} ({f.Device}) reads 0 RPM while it should be spinning. Check the fan and its cable."));
            else if (!f.Stalled && _stalled.Remove(f.Key))
                Raise(now, "fan", new Alert(f.Key, AlertLevel.None, $"Fan spinning again: {f.Name}", $"{f.Name} ({f.Device})"));
        }
    }

    private void Raise(DateTime now, string kind, Alert a)
    {
        _store.AddEvent(now, kind, a.Key, a.Level.ToString(), $"{a.Title}. {a.Message}");
        _log($"{a.Level}: {a.Title}: {a.Message}");
        if (a.Level != AlertLevel.None && _settings.Alerts.Enabled)
        {
            try { _notifier.Notify(a.Title, a.Message, a.Level == AlertLevel.Critical); }
            catch (Exception e) when (e is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                _log("notification failed: " + e.Message);
            }
        }
    }

    /// <summary>Steps once a second until <paramref name="stop"/>, then writes what is buffered.</summary>
    public void Run(CancellationToken stop)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var started = DateTime.UtcNow;
                try { Step(); }
                catch (Exception e) when (e is not OutOfMemoryException) { _log("monitor step failed: " + e.Message); }
                var wait = TimeSpan.FromSeconds(1) - (DateTime.UtcNow - started);
                if (wait > TimeSpan.Zero) stop.WaitHandle.WaitOne(wait);
            }
        }
        finally
        {
            _store.Flush();
        }
    }
}

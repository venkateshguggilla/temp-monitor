using TempMonitor.Fans;
using TempMonitor.History;
using Xunit;

namespace TempMonitor.Tests;

/// <summary>Sensors whose values the test sets.</summary>
internal sealed class FakeSensors : ISensorProvider
{
    public float? CpuTemp { get; set; } = 50;
    public float? CpuUsage { get; set; } = 10;
    public float? FanRpm { get; set; } = 1200;

    public List<Reading> Read() => new()
    {
        new Reading { Key = "cpu:0", Title = "CPU", DeviceName = "Test CPU", Temperature = CpuTemp, Usage = CpuUsage, WarmAt = 80, HotAt = 95 },
        new Reading { Key = "ram", Title = "RAM", DeviceName = "16 GB system memory", Usage = 40 },
    };

    public FanReport ReadFans() => new(FanRpm is { } rpm
        ? new List<FanReading> { new() { Key = "hwmon:nct6775@x/fan1", Name = "Fan 1", Device = "Motherboard (nct6775)", Rpm = rpm } }
        : new List<FanReading>(), null);

    public string DumpAll() => "";
    public string DiagnosticReport() => "";
    public ProviderStatus Status => new("fake", null, false, false);
    public void Dispose() { }
}

internal sealed class FakeNotifier : INotifier
{
    public List<(string Title, string Message, bool Critical)> Shown { get; } = new();
    public void Notify(string title, string message, bool critical) => Shown.Add((title, message, critical));
}

internal sealed class TempDb : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tm-history-" + Guid.NewGuid().ToString("N"), "history.db");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(System.IO.Path.GetDirectoryName(Path)!, recursive: true); } catch (IOException) { }
    }
}

public class AlertTests
{
    private static readonly DateTime T0 = new(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);

    private static List<Reading> Cpu(float t) =>
        new() { new Reading { Key = "cpu:0", Title = "CPU", DeviceName = "Test CPU", Temperature = t, HotAt = 95 } };

    [Fact]
    public void Warning_only_after_staying_hot_for_the_delay()
    {
        var a = new AlertTracker(new AlertSettings());
        Assert.Empty(a.Update(Cpu(96), T0));
        Assert.Empty(a.Update(Cpu(96), T0.AddSeconds(29)));
        var alert = Assert.Single(a.Update(Cpu(96), T0.AddSeconds(30)));
        Assert.Equal(AlertLevel.Warning, alert.Level);
        Assert.Equal("CPU is running hot", alert.Title);
        Assert.Contains("96 °C", alert.Message);
    }

    [Fact]
    public void A_dip_below_hot_restarts_the_delay()
    {
        var a = new AlertTracker(new AlertSettings());
        a.Update(Cpu(96), T0);
        a.Update(Cpu(90), T0.AddSeconds(20));
        Assert.Empty(a.Update(Cpu(96), T0.AddSeconds(35)));
        Assert.Single(a.Update(Cpu(96), T0.AddSeconds(65)));
    }

    [Fact]
    public void Critical_escalates_at_once_and_repeats_are_rate_limited()
    {
        var a = new AlertTracker(new AlertSettings());
        a.Update(Cpu(96), T0);
        Assert.Equal(AlertLevel.Warning, Assert.Single(a.Update(Cpu(96), T0.AddSeconds(30))).Level);
        a.Update(Cpu(101), T0.AddSeconds(31));
        Assert.Equal(AlertLevel.Critical, Assert.Single(a.Update(Cpu(101), T0.AddSeconds(36))).Level);
        Assert.Empty(a.Update(Cpu(101), T0.AddMinutes(5)));   // no repeat within 10 minutes
        Assert.Empty(a.Update(Cpu(96), T0.AddMinutes(6)));    // back to warning level: not "worse"
        Assert.Empty(a.Update(Cpu(101), T0.AddMinutes(11)));  // critical again, but only just: debounce restarts
        Assert.Equal(AlertLevel.Critical, Assert.Single(a.Update(Cpu(101), T0.AddMinutes(11).AddSeconds(5))).Level); // 10+ min on: once more
    }

    [Fact]
    public void Back_to_normal_once_clearly_below_hot()
    {
        var a = new AlertTracker(new AlertSettings());
        a.Update(Cpu(96), T0);
        a.Update(Cpu(96), T0.AddSeconds(30));
        Assert.Empty(a.Update(Cpu(92), T0.AddSeconds(40))); // below hot, but not by the margin
        var ok = Assert.Single(a.Update(Cpu(85), T0.AddSeconds(50)));
        Assert.Equal(AlertLevel.None, ok.Level);
        Assert.Equal("CPU back to normal", ok.Title);
        // A new episode warns again (no rate limit carried over from the old one past 10 min)
        a.Update(Cpu(97), T0.AddMinutes(20));
        Assert.Single(a.Update(Cpu(97), T0.AddMinutes(20).AddSeconds(30)));
    }
}

public class HistoryStoreTests
{
    private static readonly DateTime T0 = new(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Samples_are_kept_per_second_and_summarised_per_minute()
    {
        using var tmp = new TempDb();
        using (var s = new HistoryStore(tmp.Path))
        {
            for (var i = 0; i < 180; i++) // 3 minutes: 40, 41, ... °C
                s.Append(T0.AddSeconds(i), new[] { ("cpu:0/temp", "CPU temperature", 40f + i / 60 * 10 + i % 2) });
            s.Flush();

            var raw = s.Query("cpu:0/temp", T0, T0.AddSeconds(179), maxPoints: 1000, now: T0.AddMinutes(3));
            Assert.Equal(180, raw.Count);

            var perMinute = s.Query("cpu:0/temp", T0, T0.AddMinutes(3), maxPoints: 3, now: T0.AddDays(1)); // too old for raw
            Assert.Equal(3, perMinute.Count);
            Assert.Equal(40.5f, perMinute[0].Avg, 3);
            Assert.Equal(40f, perMinute[0].Min);
            Assert.Equal(61f, perMinute[2].Max);
        }
    }

    [Fact]
    public void Current_minute_is_visible_before_it_is_complete()
    {
        using var tmp = new TempDb();
        using var s = new HistoryStore(tmp.Path);
        for (var i = 0; i < 20; i++) s.Append(T0.AddSeconds(i), new[] { ("ram/usage", "RAM usage", 30f) });
        s.Flush();
        var p = Assert.Single(s.Query("ram/usage", T0, T0.AddMinutes(1), now: T0.AddDays(1)));
        Assert.Equal(30f, p.Avg);
    }

    [Fact]
    public void Prune_drops_old_seconds_and_old_minutes_and_clear_empties_everything()
    {
        using var tmp = new TempDb();
        using var s = new HistoryStore(tmp.Path);
        s.Append(T0, new[] { ("cpu:0/temp", "CPU temperature", 50f) });
        s.Append(T0.AddMinutes(1), new[] { ("cpu:0/temp", "CPU temperature", 50f) });
        s.AddEvent(T0, "temperature", "cpu:0", "Warning", "CPU is running hot.");
        s.Flush();

        s.Prune(T0.AddHours(3), retentionDays: 30);
        Assert.Empty(s.Query("cpu:0/temp", T0, T0.AddMinutes(2), now: T0.AddMinutes(30)));                // 1-second samples gone
        Assert.Equal(2, s.Query("cpu:0/temp", T0, T0.AddMinutes(2), now: T0.AddDays(1)).Count);         // minutes kept

        s.Prune(T0.AddDays(31), retentionDays: 30);
        Assert.Empty(s.Query("cpu:0/temp", T0, T0.AddMinutes(2), now: T0.AddDays(31)));
        Assert.Empty(s.Events(T0, T0.AddDays(1)));

        s.Append(T0.AddDays(31), new[] { ("cpu:0/temp", "CPU temperature", 50f) });
        s.Flush();
        s.Clear();
        Assert.Empty(s.Query("cpu:0/temp", T0.AddDays(30), T0.AddDays(32), now: T0.AddDays(40)));
    }

    [Fact]
    public void Metrics_keep_their_latest_label()
    {
        using var tmp = new TempDb();
        using var s = new HistoryStore(tmp.Path);
        s.Append(T0, new[] { ("gpu:card0/temp", "GPU temperature · old name", 40f) });
        s.Flush();
        using var s2 = new HistoryStore(tmp.Path);
        s2.Append(T0.AddSeconds(1), new[] { ("gpu:card0/temp", "GPU temperature · NVIDIA GeForce GTX 1050 Ti", 41f) });
        s2.Flush();
        Assert.Equal("GPU temperature · NVIDIA GeForce GTX 1050 Ti", Assert.Single(s2.Metrics()).Label);
    }
}

public class BackgroundMonitorTests
{
    private static readonly DateTime T0 = new(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);

    private sealed class Rig : IDisposable
    {
        public TempDb Db { get; } = new();
        public FakeSensors Sensors { get; } = new();
        public FakeNotifier Notifier { get; } = new();
        public DaemonStatus? Fans { get; set; }
        public DateTime Now { get; set; } = T0;
        public HistoryStore Store { get; }
        public BackgroundMonitor Monitor { get; }
        public MonitorSettings Settings { get; } = new();

        public Rig()
        {
            Store = new HistoryStore(Db.Path);
            Monitor = new BackgroundMonitor(Sensors, Store, Notifier, () => Fans, Settings, () => Now);
        }

        public void Run(int seconds)
        {
            for (var i = 0; i < seconds; i++)
            {
                Monitor.Step();
                Now = Now.AddSeconds(1);
            }
        }

        public void Dispose()
        {
            Store.Dispose();
            Db.Dispose();
        }
    }

    [Fact]
    public void Records_temperatures_usage_and_fan_speed()
    {
        using var rig = new Rig();
        rig.Run(15);
        rig.Store.Flush();
        var names = rig.Store.Metrics().Select(m => m.Name).ToList();
        Assert.Equal(new[] { "cpu:0/temp", "cpu:0/usage", "ram/usage", "fan:hwmon:nct6775@x/fan1/rpm" }, names);
        Assert.Equal(15, rig.Store.Query("cpu:0/temp", T0, T0.AddSeconds(14), 1000, now: T0.AddMinutes(1)).Count);
        Assert.Equal("CPU temperature · Test CPU", rig.Store.Metrics()[0].Label);
    }

    [Fact]
    public void Overheating_notifies_once_and_logs_the_event()
    {
        using var rig = new Rig();
        rig.Sensors.CpuTemp = 97;
        rig.Run(40);
        var shown = Assert.Single(rig.Notifier.Shown);
        Assert.Equal("CPU is running hot", shown.Title);
        Assert.False(shown.Critical);
        var ev = Assert.Single(rig.Store.Events(T0, T0.AddMinutes(1)));
        Assert.Equal("Warning", ev.Level);

        rig.Sensors.CpuTemp = 102;
        rig.Run(6);
        Assert.True(rig.Notifier.Shown.Last().Critical);
    }

    [Fact]
    public void Turning_alerts_off_still_logs_but_does_not_notify()
    {
        using var rig = new Rig();
        rig.Settings.Alerts.Enabled = false;
        rig.Sensors.CpuTemp = 97;
        rig.Run(40);
        Assert.Empty(rig.Notifier.Shown);
        Assert.Single(rig.Store.Events(T0, T0.AddMinutes(1)));
    }

    [Fact]
    public void A_stalled_fan_from_the_fan_service_is_a_critical_notification_once()
    {
        using var rig = new Rig();
        var fan = new FanStatus("hwmon:nct6775@x/fan1", "CPU cooler", "Motherboard (nct6775)", 0, 50,
            FanControlMode.Fixed, 50, true, Stalled: true, null, new FanSetting());
        rig.Fans = new DaemonStatus("Custom", FanConfig.Profiles, new SafetySettings(), false, new() { fan }, null);
        rig.Run(20);
        var shown = Assert.Single(rig.Notifier.Shown);
        Assert.Equal("Fan stopped: CPU cooler", shown.Title);
        Assert.True(shown.Critical);

        rig.Fans = rig.Fans with { Fans = new() { fan with { Stalled = false, Rpm = 900 } } };
        rig.Run(6);
        Assert.Single(rig.Notifier.Shown); // "spinning again" is logged, not shown
        Assert.Equal(2, rig.Store.Events(T0, T0.AddMinutes(1)).Count);
    }
}

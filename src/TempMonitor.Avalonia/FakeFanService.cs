#if DEV_INSTANCE
using TempMonitor.Fans;

namespace TempMonitor.App;

/// <summary>
/// Development only (AsInvoker builds, TEMPMONITOR_FAKE_FANS=1): a stand-in fan service with a
/// desktop's fans, so the fan controls can be worked on without Linux or real fans.
/// </summary>
internal sealed class FakeFanService : IFanService
{
    private const float CpuTemp = 58, GpuTemp = 47;
    private readonly object _lock = new();
    private readonly FanConfig _config = new();
    private readonly List<(string Key, string Name, string Device, float Rpm)> _fans = new()
    {
        ("hwmon:nct6775@nct6775.656/fan1", "Fan 1", "Motherboard (nct6798)", 1180),
        ("hwmon:nct6775@nct6775.656/fan2", "Fan 2", "Motherboard (nct6798)", 860),
        ("hwmon:nct6775@nct6775.656/fan3", "Fan 3", "Motherboard (nct6798)", 0),
        ("nvml:00000000:01:00.0/0", "GPU fan 1", "NVIDIA GeForce RTX 3060", 1105),
        ("nvml:00000000:01:00.0/1", "GPU fan 2", "NVIDIA GeForce RTX 3060", 1098),
    };

    public FanResponse Send(FanRequest r)
    {
        Thread.Sleep(150); // like a real round trip
        lock (_lock)
        {
            switch (r.Cmd)
            {
                case "profile":
                    if (!FanConfig.Profiles.Contains(r.Name ?? "")) return new FanResponse(false, "Unknown profile", null);
                    _config.Profile = r.Name!;
                    break;
                case "set":
                    if (_config.Profile != FanConfig.CustomProfile)
                    {
                        foreach (var f in _fans) _config.Custom[f.Key] = _config.Effective(f.Key).Clone();
                        _config.Profile = FanConfig.CustomProfile;
                    }
                    _config.Custom[r.Key!] = r.Setting!;
                    break;
                case "rename":
                    if (string.IsNullOrWhiteSpace(r.Name)) _config.Names.Remove(r.Key!);
                    else _config.Names[r.Key!] = r.Name.Trim();
                    break;
                case "safety":
                    if (_config.Safety.IsLoweredBy(r.Safety!)) Thread.Sleep(1500); // the admin prompt
                    _config.Safety = r.Safety!.Clone();
                    break;
            }
            return new FanResponse(true, null, Status());
        }
    }

    private DaemonStatus Status()
    {
        var fans = new List<FanStatus>();
        foreach (var (key, name, device, rpm) in _fans)
        {
            var s = _config.Effective(key);
            float? temp = s.Mode == FanControlMode.Curve
                ? s.Sources.Any(x => x.StartsWith("cpu")) ? CpuTemp : GpuTemp
                : null;
            float? target = s.Mode switch
            {
                FanControlMode.Fixed => s.Percent,
                FanControlMode.Curve => Interpolate(s.Points, temp!.Value),
                _ => null,
            };
            if (target != null) target = Math.Max(target.Value, _config.Safety.FloorPercent);
            var stalled = rpm == 0 && target >= 25;
            fans.Add(new FanStatus(key, _config.Names.GetValueOrDefault(key, name), device, rpm,
                target ?? 40, s.Mode, target, true, stalled, null, s, temp));
        }
        return new DaemonStatus(_config.Profile, FanConfig.Profiles, _config.Safety.Clone(), false, fans, null);
    }

    private static float Interpolate(List<CurvePoint> points, float t)
    {
        var p = points.OrderBy(x => x.Temp).ToList();
        if (p.Count == 0) return 100;
        if (t <= p[0].Temp) return p[0].Percent;
        for (var i = 1; i < p.Count; i++)
            if (t <= p[i].Temp)
                return p[i - 1].Percent + (p[i].Percent - p[i - 1].Percent) * (t - p[i - 1].Temp) / (p[i].Temp - p[i - 1].Temp);
        return p[^1].Percent;
    }
}
#endif

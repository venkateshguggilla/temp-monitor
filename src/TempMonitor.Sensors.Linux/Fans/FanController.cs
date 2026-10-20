using System.Text.Json;

namespace TempMonitor.Fans;

/// <summary>One fan as the daemon reports it to clients.</summary>
public sealed record FanStatus(string Key, string Name, string Device, float? Rpm, float? Percent,
    FanControlMode Mode, float? Target, bool HasControl, bool Stalled, string? Error, FanSetting Setting);

/// <summary>Everything a client shows: profile, safety limits and every fan.</summary>
public sealed record DaemonStatus(string Profile, IReadOnlyList<string> Profiles, SafetySettings Safety,
    bool Critical, List<FanStatus> Fans, string? Note);

/// <summary>
/// The control loop. Every <see cref="Tick"/> reads temperatures and fans, works out each fan's
/// duty from the active profile and enforces the safety rules:
/// <list type="bullet">
/// <item>never below the floor;</item>
/// <item>a CPU or GPU at its critical temperature sends every controlled fan to 100%;</item>
/// <item>a curve whose temperature can't be read runs at 100%;</item>
/// <item>a fan that reads 0 RPM while told to spin is reported as stalled.</item>
/// </list>
/// Before touching a fan it records what firmware was doing (restore file), so <see cref="ReleaseAll"/>,
/// or <see cref="RecoverFromCrash"/> after a crash, hands every fan back exactly as it was.
/// </summary>
internal sealed class FanController
{
    private static readonly TimeSpan Reassert = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StallAfter = TimeSpan.FromSeconds(8);
    private const float StallMinPercent = 25;

    private readonly LinuxSensorProvider _provider;
    private readonly FanBackend _backend;
    private readonly string _configPath;
    private readonly string _restorePath;
    private readonly Func<DateTime> _now;
    private readonly Action<string> _log;

    private readonly Dictionary<string, RestorePoint> _restore = new();
    private readonly Dictionary<string, FanState> _state = new();
    private readonly Dictionary<string, DateTime> _identifyUntil = new();
    private DaemonStatus? _last;

    private sealed class FanState
    {
        public float? Written;
        public DateTime WrittenAt;
        public float? CurveOut;
        public float CurveTempAtOut;
        public DateTime? ZeroRpmSince;
        public bool Stalled;
        public string? Error;
    }

    public FanConfig Config { get; private set; }

    public FanController(LinuxSensorProvider provider, string configPath, string restorePath,
        Func<DateTime>? now = null, Action<string>? log = null)
    {
        _provider = provider;
        _backend = new FanBackend(provider.Fs, provider.Nvml);
        _configPath = configPath;
        _restorePath = restorePath;
        _now = now ?? (() => DateTime.UtcNow);
        _log = log ?? (_ => { });
        Config = FanConfig.Load(configPath);
    }

    public DaemonStatus? LastStatus => _last;

    public void SaveConfig() => Config.Save(_configPath);

    public void Identify(string key, TimeSpan duration) => _identifyUntil[key] = _now() + duration;

    // ---------------------------------------------------------------- Control loop

    public DaemonStatus Tick()
    {
        var now = _now();
        var readings = _provider.Read();
        var report = _provider.ReadFans();
        var channels = _provider.Channels;
        var safety = Config.Safety;

        var critical = readings.Any(r => r.Temperature is { } t &&
            ((r.Key.StartsWith("cpu:", StringComparison.Ordinal) && t >= safety.CriticalCpu) ||
             (r.Key.StartsWith("gpu:", StringComparison.Ordinal) && t >= safety.CriticalGpu)));

        var fans = new List<FanStatus>();
        foreach (var fan in report.Fans)
        {
            if (!_state.TryGetValue(fan.Key, out var st)) _state[fan.Key] = st = new FanState();
            var setting = Config.Effective(fan.Key);
            var identify = _identifyUntil.TryGetValue(fan.Key, out var until) && now < until;
            float? target = null;
            st.Error = null;

            if (setting.Mode == FanControlMode.Auto && !identify)
            {
                Release(fan.Key);
                st.Written = null;
                st.CurveOut = null;
            }
            else if (!channels.TryGetValue(fan.Key, out var channel))
            {
                st.Error = "This fan's speed can't be controlled";
            }
            else
            {
                string? error = null;
                target = identify ? 100 : Target(setting, readings, st, out error);
                st.Error = error;
                target = Math.Max(target.Value, safety.FloorPercent);
                if (critical) target = 100;
                Drive(fan.Key, channel, target.Value, st, now);
            }

            UpdateStall(fan, st, target, now);
            fans.Add(new FanStatus(fan.Key, Config.Names.GetValueOrDefault(fan.Key, fan.Name), fan.Device,
                fan.Rpm, fan.Percent, setting.Mode, target, fan.HasControl, st.Stalled, st.Error, setting));
        }

        return _last = new DaemonStatus(Config.Profile, FanConfig.Profiles, safety.Clone(), critical, fans,
            critical ? "A CPU or GPU is at its critical temperature: all controlled fans run at 100%." : report.Note);
    }

    /// <summary>Duty a fan's setting asks for, before the floor and the critical override.</summary>
    private static float Target(FanSetting s, List<Reading> readings, FanState st, out string? error)
    {
        error = null;
        if (s.Mode == FanControlMode.Fixed)
            return s.Percent;

        var temp = SourceTemperature(s.Sources, readings);
        if (temp is not { } t)
        {
            error = "Temperature source not available: running at 100%";
            st.CurveOut = null;
            return 100;
        }

        var raw = Interpolate(s.Points, t);
        // Speed up at once; slow down only after cooling by the hysteresis
        if (st.CurveOut is not { } current || raw >= current || t <= st.CurveTempAtOut - s.Hysteresis)
        {
            st.CurveOut = raw;
            st.CurveTempAtOut = t;
        }
        return st.CurveOut.Value;
    }

    /// <summary>Hottest temperature among the sources ("cpu:*" matches every CPU reading).</summary>
    internal static float? SourceTemperature(IEnumerable<string> sources, IEnumerable<Reading> readings)
    {
        float? max = null;
        foreach (var src in sources)
            foreach (var r in readings)
            {
                var match = src.EndsWith('*') ? r.Key.StartsWith(src[..^1], StringComparison.Ordinal) : r.Key == src;
                if (match && r.Temperature is { } t && (max == null || t > max)) max = t;
            }
        return max;
    }

    /// <summary>Linear between points; flat before the first and after the last.</summary>
    internal static float Interpolate(IReadOnlyList<CurvePoint> points, float temp)
    {
        if (points.Count == 0) return 100;
        var p = points.OrderBy(x => x.Temp).ToList();
        if (temp <= p[0].Temp) return p[0].Percent;
        for (var i = 1; i < p.Count; i++)
            if (temp <= p[i].Temp)
            {
                var a = p[i - 1];
                var b = p[i];
                return a.Percent + (b.Percent - a.Percent) * (temp - a.Temp) / (b.Temp - a.Temp);
            }
        return p[^1].Percent;
    }

    private void Drive(string key, FanChannel channel, float target, FanState st, DateTime now)
    {
        if (!_restore.ContainsKey(key))
        {
            _restore[key] = _backend.Capture(key, channel);
            SaveRestoreFile(); // before the first write, so a crash right after can still restore
            _log($"taking control of {key}");
        }
        // Rewrite periodically too: some firmware quietly takes a fan back
        if (st.Written != target || now - st.WrittenAt >= Reassert)
        {
            if (_backend.Apply(channel, target, out var error))
            {
                st.Written = target;
                st.WrittenAt = now;
            }
            else
            {
                st.Error = "Could not set the speed: " + error;
                _log($"{key}: {error}");
            }
        }
    }

    private void UpdateStall(FanReading fan, FanState st, float? target, DateTime now)
    {
        if (fan.Rpm == 0 && target >= StallMinPercent)
        {
            st.ZeroRpmSince ??= now;
            if (!st.Stalled && now - st.ZeroRpmSince >= StallAfter)
            {
                st.Stalled = true;
                _log($"{fan.Key}: STALLED (0 RPM at {target:0}%)");
            }
        }
        else
        {
            st.ZeroRpmSince = null;
            st.Stalled = false;
        }
    }

    // ---------------------------------------------------------------- Hand back to firmware

    private void Release(string key)
    {
        if (!_restore.Remove(key, out var point)) return;
        if (!_backend.Restore(point, out var error)) _log($"{key}: could not hand back to firmware: {error}");
        else _log($"handed {key} back to firmware");
        SaveRestoreFile();
    }

    /// <summary>Every controlled fan back to firmware (clean shutdown).</summary>
    public void ReleaseAll()
    {
        foreach (var key in _restore.Keys.ToList())
            Release(key);
        _state.Clear();
    }

    /// <summary>
    /// After a crash the fans are still in manual mode at the last speed. The restore file holds
    /// what firmware was doing before, so put that back before taking anything over again.
    /// </summary>
    public static int RestoreFromFile(string restorePath, FanBackend backend, Action<string> log)
    {
        List<RestorePoint>? points = null;
        try { points = JsonSerializer.Deserialize<List<RestorePoint>>(File.ReadAllText(restorePath), FanConfig.Json); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
        if (points == null) return 0;
        foreach (var p in points)
            if (!backend.Restore(p, out var error)) log($"{p.Key}: could not hand back to firmware: {error}");
        try { File.Delete(restorePath); } catch (IOException) { }
        log($"restored {points.Count} fan(s) to firmware control");
        return points.Count;
    }

    public int RecoverFromCrash() => RestoreFromFile(_restorePath, _backend, _log);

    private void SaveRestoreFile()
    {
        if (_restore.Count == 0)
        {
            try { File.Delete(_restorePath); } catch (IOException) { }
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(_restorePath)!);
        var tmp = _restorePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_restore.Values.ToList(), FanConfig.Json));
        File.Move(tmp, _restorePath, overwrite: true);
    }
}

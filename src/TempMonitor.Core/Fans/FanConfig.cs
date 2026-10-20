using System.Text.Json;
using System.Text.Json.Serialization;

namespace TempMonitor.Fans;

public enum FanControlMode
{
    /// <summary>Firmware decides (the daemon doesn't touch the fan).</summary>
    Auto,
    /// <summary>A fixed duty in %.</summary>
    Fixed,
    /// <summary>Duty follows a temperature curve.</summary>
    Curve,
}

/// <summary>One point of a fan curve: at <see cref="Temp"/> °C the fan runs at <see cref="Percent"/>.</summary>
public sealed record CurvePoint(float Temp, float Percent);

/// <summary>How one fan should run.</summary>
public sealed class FanSetting
{
    public FanControlMode Mode { get; set; }

    /// <summary>Duty for <see cref="FanControlMode.Fixed"/>.</summary>
    public float Percent { get; set; } = 50;

    /// <summary>
    /// Temperatures the curve follows; the hottest one wins. Reading keys ("cpu:0", "gpu:card1",
    /// "disk:nvme0n1") or a prefix with * ("cpu:*", "gpu:*").
    /// </summary>
    public List<string> Sources { get; set; } = new() { "cpu:*" };

    public List<CurvePoint> Points { get; set; } = new();

    /// <summary>°C the temperature must fall before the fan slows down again (stops "hunting").</summary>
    public float Hysteresis { get; set; } = 3;

    public FanSetting Clone() => new()
    {
        Mode = Mode, Percent = Percent, Sources = new(Sources), Points = new(Points), Hysteresis = Hysteresis,
    };
}

/// <summary>Limits the daemon enforces whatever the fan settings say.</summary>
public sealed class SafetySettings
{
    /// <summary>No controlled fan runs slower than this (%).</summary>
    public float FloorPercent { get; set; } = 20;

    /// <summary>At these temperatures every controlled fan goes to 100%.</summary>
    public float CriticalCpu { get; set; } = 95;
    public float CriticalGpu { get; set; } = 90;

    /// <summary>True if <paramref name="next"/> gives less protection than this (needs admin approval).</summary>
    public bool IsLoweredBy(SafetySettings next) =>
        next.FloorPercent < FloorPercent || next.CriticalCpu > CriticalCpu || next.CriticalGpu > CriticalGpu;

    public SafetySettings Clone() => new() { FloorPercent = FloorPercent, CriticalCpu = CriticalCpu, CriticalGpu = CriticalGpu };
}

/// <summary>
/// Saved fan configuration (/etc/temp-monitor/fans.json). The active profile decides each fan's
/// setting: "Auto" leaves everything to firmware (the default, so a fresh install changes
/// nothing), the presets apply a curve to every controllable fan, "Custom" uses per-fan settings.
/// </summary>
public sealed class FanConfig
{
    public const string AutoProfile = "Auto";
    public const string CustomProfile = "Custom";

    /// <summary>Preset curves: CPU (and GPU) temperature → duty.</summary>
    public static readonly IReadOnlyDictionary<string, CurvePoint[]> Presets = new Dictionary<string, CurvePoint[]>
    {
        ["Silent"] = new CurvePoint[] { new(45, 20), new(60, 35), new(75, 60), new(85, 100) },
        ["Balanced"] = new CurvePoint[] { new(40, 30), new(55, 45), new(70, 70), new(80, 100) },
        ["Performance"] = new CurvePoint[] { new(35, 45), new(50, 60), new(65, 85), new(75, 100) },
    };

    public static IReadOnlyList<string> Profiles { get; } =
        new[] { AutoProfile }.Concat(Presets.Keys).Append(CustomProfile).ToList();

    public string Profile { get; set; } = AutoProfile;
    public Dictionary<string, FanSetting> Custom { get; set; } = new();

    /// <summary>Names the user gave fans, by fan key.</summary>
    public Dictionary<string, string> Names { get; set; } = new();

    public SafetySettings Safety { get; set; } = new();

    /// <summary>What <paramref name="fanKey"/> should do under the active profile.</summary>
    public FanSetting Effective(string fanKey)
    {
        if (Profile == CustomProfile)
            return Custom.TryGetValue(fanKey, out var s) ? s : new FanSetting();
        if (Presets.TryGetValue(Profile, out var points))
            return new FanSetting
            {
                Mode = FanControlMode.Curve,
                Points = points.ToList(),
                // GPU fans follow their GPU; case, CPU and laptop fans the hotter of CPU and GPU
                Sources = IsGpuFan(fanKey) ? new() { "gpu:*" } : new() { "cpu:*", "gpu:*" },
            };
        return new FanSetting();
    }

    public static bool IsGpuFan(string fanKey) =>
        fanKey.StartsWith("nvml:", StringComparison.Ordinal) ||
        fanKey.StartsWith("hwmon:amdgpu@", StringComparison.Ordinal) ||
        fanKey.StartsWith("hwmon:radeon@", StringComparison.Ordinal) ||
        fanKey.StartsWith("hwmon:nouveau@", StringComparison.Ordinal);

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>One line per message on the socket.</summary>
    public static readonly JsonSerializerOptions Compact = new(Json) { WriteIndented = false };

    /// <summary>Loads the config; a missing or damaged file gives the defaults (everything Auto).</summary>
    public static FanConfig Load(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<FanConfig>(File.ReadAllText(path), Json) ?? new FanConfig();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return new FanConfig();
        }
    }

    /// <summary>Saves atomically (temp file + rename), so a power cut can't leave half a file.</summary>
    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, path, overwrite: true);
    }
}

using System.Globalization;

namespace TempMonitor.Fans;

internal enum ChannelKind { Pwm, AppleSmc, Nvml }

/// <summary>
/// How one fan is controlled. Pwm: hwmon pwmN (0–255) + pwmN_enable. AppleSmc: fanN_manual +
/// fanN_output in RPM between the firmware's min and max. Nvml: NVIDIA fan N of the GPU at BusId.
/// </summary>
internal sealed record FanChannel(ChannelKind Kind, string Dir, int Index, string? BusId = null,
    float? MinRpm = null, float? MaxRpm = null);

/// <summary>
/// What a fan was doing before the daemon took it over (e.g. pwm1_enable = 5, "Smart Fan IV"),
/// so it can be handed back to firmware exactly as it was, even after a crash.
/// </summary>
internal sealed record RestorePoint(string Key, ChannelKind Kind, string Dir, int Index, string? BusId, string? Original);

/// <summary>Takes fans from firmware, sets their speed and gives them back. Needs root on a real system.</summary>
internal sealed class FanBackend
{
    private readonly Sysfs _fs;
    private readonly INvml _nvml;

    public FanBackend(Sysfs fs, INvml nvml)
    {
        _fs = fs;
        _nvml = nvml;
    }

    private static string PwmEnable(FanChannel c) => Path.Combine(c.Dir, $"pwm{c.Index}_enable");
    private static string Pwm(FanChannel c) => Path.Combine(c.Dir, $"pwm{c.Index}");
    private static string SmcManual(string dir, int i) => Path.Combine(dir, $"fan{i}_manual");
    private static string SmcOutput(FanChannel c) => Path.Combine(c.Dir, $"fan{c.Index}_output");

    public RestorePoint Capture(string key, FanChannel c) => c.Kind switch
    {
        ChannelKind.Pwm => new(key, c.Kind, c.Dir, c.Index, null, _fs.Text(PwmEnable(c))),
        ChannelKind.AppleSmc => new(key, c.Kind, c.Dir, c.Index, null, _fs.Text(SmcManual(c.Dir, c.Index))),
        _ => new(key, c.Kind, "", c.Index, c.BusId, null),
    };

    /// <summary>Sets the fan to <paramref name="percent"/> (0–100), taking it into manual mode first.</summary>
    public bool Apply(FanChannel c, float percent, out string? error)
    {
        percent = Math.Clamp(percent, 0, 100);
        switch (c.Kind)
        {
            case ChannelKind.Pwm:
                if (_fs.Text(PwmEnable(c)) != "1" && !_fs.Write(PwmEnable(c), "1", out error))
                    return false;
                return _fs.Write(Pwm(c), ((int)Math.Round(percent * 255 / 100)).ToString(CultureInfo.InvariantCulture), out error);

            case ChannelKind.AppleSmc:
                if (c.MinRpm is not { } min || c.MaxRpm is not { } max || max <= min)
                {
                    error = "The SMC doesn't report this fan's speed range";
                    return false;
                }
                if (_fs.Text(SmcManual(c.Dir, c.Index)) != "1" && !_fs.Write(SmcManual(c.Dir, c.Index), "1", out error))
                    return false;
                var rpm = (int)Math.Round(min + percent / 100 * (max - min));
                return _fs.Write(SmcOutput(c), rpm.ToString(CultureInfo.InvariantCulture), out error);

            default:
                return _nvml.SetFanSpeed(c.BusId!, c.Index, (int)Math.Round(percent), out error);
        }
    }

    /// <summary>Hands the fan back to firmware the way it was before <see cref="Capture"/>.</summary>
    public bool Restore(RestorePoint p, out string? error) => p.Kind switch
    {
        // A manual (1), full-speed (0) or unknown original would not hand the fan back to firmware:
        // 2 is "automatic" for every hwmon PWM driver
        ChannelKind.Pwm => _fs.Write(Path.Combine(p.Dir, $"pwm{p.Index}_enable"), p.Original is "0" or "1" or null ? "2" : p.Original, out error),
        ChannelKind.AppleSmc => _fs.Write(SmcManual(p.Dir, p.Index), "0", out error),
        _ => _nvml.SetFanSpeed(p.BusId!, p.Index, null, out error),
    };
}

namespace TempMonitor;

/// <summary>Who currently decides a fan's speed.</summary>
public enum FanMode
{
    /// <summary>The driver doesn't say.</summary>
    Unknown,
    /// <summary>Firmware (BIOS, SMC, GPU driver) controls it.</summary>
    Auto,
    /// <summary>Software has set a fixed speed.</summary>
    Manual,
    /// <summary>Forced to full speed (hwmon pwm_enable = 0).</summary>
    Full,
}

/// <summary>One fan (or fan header / pump), as reported by an <see cref="ISensorProvider"/>.</summary>
public sealed class FanReading
{
    /// <summary>Stable across refreshes and restarts, e.g. "hwmon:nct6798/fan2" or "nvml:0000:01:00.0/0".</summary>
    public required string Key { get; init; }

    /// <summary>The fan's own name: the driver's label, e.g. "Left side", else "Fan 2" or "GPU fan".</summary>
    public required string Name { get; init; }

    /// <summary>What the fan belongs to, e.g. "Motherboard (nct6798)" or "NVIDIA GeForce RTX 3060".</summary>
    public required string Device { get; init; }

    /// <summary>Measured speed, or null when the fan has no tachometer.</summary>
    public float? Rpm { get; set; }

    /// <summary>Commanded duty cycle (PWM) or the driver's target speed, 0–100.</summary>
    public float? Percent { get; set; }

    /// <summary>Lowest / highest speed the firmware allows (Apple SMC), when known.</summary>
    public float? MinRpm { get; set; }
    public float? MaxRpm { get; set; }

    public FanMode Mode { get; set; }

    /// <summary>
    /// The hardware exposes a speed control. Whether it can actually be changed (rights,
    /// firmware locks) is up to the fan controller.
    /// </summary>
    public bool HasControl { get; set; }

    public static string FormatRpm(float rpm) => $"{rpm:#,0} RPM";

    public static string ModeText(FanMode mode) => mode switch
    {
        FanMode.Auto => "Auto",
        FanMode.Manual => "Manual",
        FanMode.Full => "Full speed",
        _ => "",
    };
}

/// <param name="Fans">Every fan found, in a stable order.</param>
/// <param name="Note">
/// Why the list is empty, or what was left out (e.g. motherboard headers reading 0 RPM because
/// nothing is plugged in). Null when there is nothing to say.
/// </param>
public sealed record FanReport(List<FanReading> Fans, string? Note);

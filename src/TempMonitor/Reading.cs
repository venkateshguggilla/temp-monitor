namespace TempMonitor;

/// <summary>One card's worth of data, produced by <see cref="SensorService"/> once per refresh.</summary>
public sealed class Reading
{
    public required string Key { get; init; }
    public required string Title { get; init; }
    public required string DeviceName { get; set; }

    /// <summary>Temperature in °C, or null when the hardware does not expose one.</summary>
    public float? Temperature { get; set; }
    public string TemperatureSource { get; set; } = "";
    public string? TemperatureUnavailableReason { get; set; }

    /// <summary>Temperatures at or above these are shown amber / red.</summary>
    public float WarmAt { get; init; } = 70;
    public float HotAt { get; init; } = 85;

    public float? Usage { get; set; }
    public string UsageLabel { get; set; } = "Usage";

    /// <summary>Optional second bar, e.g. disk space used.</summary>
    public float? Secondary { get; set; }
    public string SecondaryLabel { get; set; } = "";

    public List<string> Details { get; } = new();

    public static string FormatTemp(float celsius, bool fahrenheit) =>
        fahrenheit ? $"{celsius * 9 / 5 + 32:0} °F" : $"{celsius:0} °C";
}

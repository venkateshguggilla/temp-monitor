namespace TempMonitor.Tests;

/// <summary>A throwaway folder that looks like the parts of a Linux root the provider reads.</summary>
internal sealed class FakeLinux : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "tm-fake-" + Guid.NewGuid().ToString("N"));
    public DateTime Now { get; set; } = new(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);
    public Dictionary<string, (double used, double total)> Space { get; } = new();
    public FakeNvml Nvml { get; } = new();

    public FakeLinux() => Directory.CreateDirectory(Root);

    public FakeLinux File(string linuxPath, string content)
    {
        var p = Path.Combine(Root, linuxPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        System.IO.File.WriteAllText(p, content + "\n");
        return this;
    }

    public FakeLinux Dir(string linuxPath)
    {
        Directory.CreateDirectory(Path.Combine(Root, linuxPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar)));
        return this;
    }

    /// <summary>A hwmon chip; temps as (index, label, °C).</summary>
    public FakeLinux Hwmon(string dir, string name, params (int i, string? label, double c)[] temps)
    {
        File($"{dir}/name", name);
        foreach (var (i, label, c) in temps)
        {
            File($"{dir}/temp{i}_input", ((long)(c * 1000)).ToString());
            if (label != null) File($"{dir}/temp{i}_label", label);
        }
        return this;
    }

    public FakeLinux Stat(long busy, long idle) =>
        File("/proc/stat", $"cpu  {busy} 0 0 {idle} 0 0 0 0 0 0\ncpu0 1 0 0 1 0 0 0 0 0 0");

    public LinuxSensorProvider Provider() =>
        new(Root, Nvml, () => Now, mp => Space.TryGetValue(mp, out var s) ? s : null);

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
    }
}

internal sealed class FakeNvml : INvml
{
    public List<NvGpu>? Gpus { get; set; }
    public string? Reason { get; set; } = "NVIDIA driver not installed (no NVML)";

    public IReadOnlyList<NvGpu>? Read(out string? unavailableReason)
    {
        unavailableReason = Gpus == null ? Reason : null;
        return Gpus;
    }

    /// <summary>Every SetFanSpeed call: (bus id, fan, percent or null for automatic).</summary>
    public List<(string BusId, int Fan, int? Percent)> FanCalls { get; } = new();

    public bool SetFanSpeed(string pciBusId, int fan, int? percent, out string? error)
    {
        FanCalls.Add((pciBusId, fan, percent));
        error = null;
        return true;
    }
}

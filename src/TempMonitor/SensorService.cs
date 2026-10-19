using System.Diagnostics;
using System.Management;
using System.Security.Principal;
using LibreHardwareMonitor.Hardware;

namespace TempMonitor;

/// <summary>
/// Reads hardware sensors. Temperatures come from LibreHardwareMonitor, which reads the
/// values the hardware itself reports (Intel DTS via MSR, AMD ADL, NVMe/SMART), with
/// Windows' storage reliability counters as a fallback for drive temperature.
/// </summary>
public sealed class SensorService : IDisposable
{
    private readonly object _lock = new();
    private readonly Computer _computer;
    private List<DiskInfo> _disks;
    private string _diskSignature;
    private DateTime _nextDiskCheck = DateTime.MinValue;
    private readonly Dictionary<string, float> _wmiDiskTemps = new();
    private DateTime _nextWmiTempPoll = DateTime.MinValue;

    // SMART-over-USB state, keyed by DiskInfo.Key.
    private static readonly TimeSpan SatPollInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SatIdleCutoff = TimeSpan.FromMinutes(5);
    private readonly Dictionary<string, (float temp, DateTime at)> _satTemps = new();
    private readonly Dictionary<string, DateTime> _satNextPoll = new();
    private readonly Dictionary<string, DateTime> _lastActive = new();
    private readonly HashSet<string> _satUnsupported = new();

    public bool IsElevated { get; }
    public bool PawnIoInstalled { get; }

    public SensorService()
    {
        IsElevated = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        PawnIoInstalled = PawnIoInstaller.IsInstalled();

        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMemoryEnabled = true,
            IsStorageEnabled = true,
        };
        _computer.Open();
        _diskSignature = DiskInfo.Signature();
        _disks = DiskInfo.Enumerate();
    }

    /// <summary>Re-enumerates disks when one is plugged in or removed.</summary>
    private void RefreshDisksIfChanged()
    {
        if (DateTime.UtcNow < _nextDiskCheck) return;
        _nextDiskCheck = DateTime.UtcNow.AddSeconds(3);

        var signature = DiskInfo.Signature();
        if (signature == _diskSignature) return;
        _diskSignature = signature;

        foreach (var d in _disks) d.Dispose();
        _disks = DiskInfo.Enumerate();
        _wmiDiskTemps.Clear();
        _nextWmiTempPoll = DateTime.MinValue;

        // A replugged drive gets a fresh attempt (it may be in a different enclosure).
        var present = _disks.Select(d => d.Key).ToHashSet();
        _satUnsupported.Clear();
        _satNextPoll.Clear();
        foreach (var gone in _satTemps.Keys.Where(k => !present.Contains(k)).ToList())
            _satTemps.Remove(gone);
    }

    public List<Reading> Read()
    {
        lock (_lock)
            return ReadLocked();
    }

    private List<Reading> ReadLocked()
    {
        foreach (var hw in _computer.Hardware)
            UpdateRecursive(hw);

        RefreshDisksIfChanged();

        if (DateTime.UtcNow >= _nextWmiTempPoll)
        {
            PollWmiDiskTemps();
            _nextWmiTempPoll = DateTime.UtcNow.AddSeconds(5);
        }

        var readings = new List<Reading>();
        readings.AddRange(ReadCpus());
        readings.AddRange(ReadGpus());
        readings.Add(ReadMemory());
        readings.AddRange(ReadStorage());
        return readings;
    }

    private static void UpdateRecursive(IHardware hw)
    {
        hw.Update();
        foreach (var sub in hw.SubHardware)
            UpdateRecursive(sub);
    }

    private static IEnumerable<ISensor> AllSensors(IHardware hw) =>
        hw.Sensors.Concat(hw.SubHardware.SelectMany(AllSensors));

    private static float? Value(IHardware hw, SensorType type, string name) =>
        AllSensors(hw).FirstOrDefault(s => s.SensorType == type && s.Name == name && s.Value.HasValue)?.Value;

    private string DriverHint() =>
        !IsElevated ? "Run as administrator"
        : !PawnIoInstalled ? "Needs the PawnIO driver (see README)"
        : "Sensor not reported";

    // ---------------------------------------------------------------- CPU

    private IEnumerable<Reading> ReadCpus()
    {
        foreach (var cpu in _computer.Hardware.Where(h => h.HardwareType == HardwareType.Cpu))
        {
            var r = new Reading
            {
                Key = "cpu:" + cpu.Identifier,
                Title = "CPU",
                DeviceName = cpu.Name,
                WarmAt = 80,
                HotAt = 95,
            };

            // Intel: "CPU Package" is the package DTS sensor, i.e. what Intel's own tools show.
            // AMD: "Core (Tctl/Tdie)". Otherwise fall back to the hottest core.
            foreach (var name in new[] { "CPU Package", "Core (Tctl/Tdie)", "Core (Tdie)", "Core (Tctl)", "Core Max" })
            {
                var t = Value(cpu, SensorType.Temperature, name);
                if (t is > 0)
                {
                    r.Temperature = t;
                    r.TemperatureSource = name == "CPU Package" ? "Package" : name;
                    break;
                }
            }
            if (r.Temperature == null)
                r.TemperatureUnavailableReason = DriverHint();

            r.Usage = Value(cpu, SensorType.Load, "CPU Total");

            var coreMax = Value(cpu, SensorType.Temperature, "Core Max");
            if (coreMax is > 0 && r.TemperatureSource == "Package")
                r.Details.Add($"Hottest core {coreMax:0} °C");

            var clocks = AllSensors(cpu)
                .Where(s => s.SensorType == SensorType.Clock && s.Name.StartsWith("CPU Core #") && s.Value is > 0)
                .Select(s => s.Value!.Value).ToList();
            if (clocks.Count > 0)
                r.Details.Add($"Clock {clocks.Average() / 1000:0.00} GHz");

            var power = Value(cpu, SensorType.Power, "CPU Package");
            if (power is > 0)
                r.Details.Add($"Power {power:0.0} W");

            yield return r;
        }
    }

    // ---------------------------------------------------------------- GPU

    private static readonly HardwareType[] GpuTypes = { HardwareType.GpuAmd, HardwareType.GpuNvidia, HardwareType.GpuIntel };

    private IEnumerable<Reading> ReadGpus()
    {
        var gpus = _computer.Hardware.Where(h => GpuTypes.Contains(h.HardwareType)).ToList();
        foreach (var gpu in gpus)
        {
            var r = new Reading
            {
                Key = "gpu:" + gpu.Identifier,
                Title = gpus.Count > 1 ? $"GPU {gpus.IndexOf(gpu) + 1}" : "GPU",
                DeviceName = gpu.Name,
                WarmAt = 75,
                HotAt = 90,
            };

            var core = Value(gpu, SensorType.Temperature, "GPU Core");
            if (core is > 0)
            {
                r.Temperature = core;
                r.TemperatureSource = "Core";
            }
            else
            {
                r.TemperatureUnavailableReason = gpu.HardwareType == HardwareType.GpuIntel
                    ? "Integrated GPU: shares the CPU temperature"
                    : "Sensor not reported by driver";
            }

            var hotSpot = Value(gpu, SensorType.Temperature, "GPU Hot Spot");
            if (hotSpot is > 0)
                r.Details.Add($"Hot spot {hotSpot:0} °C");

            // Task Manager's GPU % is the busiest engine (3D, compute, copy, video...).
            // Use the same definition, and also consider the driver's own core-load figure.
            var loads = AllSensors(gpu)
                .Where(s => s.SensorType == SensorType.Load && s.Value.HasValue &&
                            (s.Name.StartsWith("D3D ") || s.Name == "GPU Core"))
                .Select(s => s.Value!.Value).ToList();
            if (loads.Count > 0)
                r.Usage = Math.Clamp(loads.Max(), 0, 100);

            var used = Value(gpu, SensorType.SmallData, "GPU Memory Used") ?? Value(gpu, SensorType.SmallData, "D3D Dedicated Memory Used");
            var total = Value(gpu, SensorType.SmallData, "GPU Memory Total") ?? Value(gpu, SensorType.SmallData, "D3D Dedicated Memory Total");
            if (used.HasValue && total is > 0)
            {
                r.Secondary = used.Value / total.Value * 100;
                r.SecondaryLabel = $"VRAM {used / 1024:0.0} / {total / 1024:0.0} GB";
            }

            var power = Value(gpu, SensorType.Power, "GPU Package") ?? Value(gpu, SensorType.Power, "GPU Power");
            if (power is > 0)
                r.Details.Add($"Power {power:0} W");

            var clock = Value(gpu, SensorType.Clock, "GPU Core");
            if (clock is > 0)
                r.Details.Add($"Clock {clock:0} MHz");

            yield return r;
        }
    }

    // ---------------------------------------------------------------- RAM

    private Reading ReadMemory()
    {
        var mems = _computer.Hardware.Where(h => h.HardwareType == HardwareType.Memory).ToList();
        var total = mems.FirstOrDefault(h => h.Identifier.ToString() == "/ram")
                    ?? mems.FirstOrDefault(h => h.Name.Contains("Total"))
                    ?? mems.FirstOrDefault(h => !h.Name.Contains("Virtual"));

        var r = new Reading
        {
            Key = "ram",
            Title = "RAM",
            DeviceName = "System memory",
            WarmAt = 60,
            HotAt = 80,
        };

        // DIMM temperatures come from the thermal sensor on each module (read over SMBus).
        var dimmTemps = mems.SelectMany(AllSensors)
            .Where(s => s.SensorType == SensorType.Temperature && s.Value is > 0)
            .ToList();
        if (dimmTemps.Count > 0)
        {
            var hottest = dimmTemps.MaxBy(s => s.Value)!;
            r.Temperature = hottest.Value;
            r.TemperatureSource = dimmTemps.Count > 1 ? $"Hottest of {dimmTemps.Count} DIMMs" : "DIMM";
        }
        else
        {
            r.TemperatureUnavailableReason = IsElevated && PawnIoInstalled
                ? "This memory has no thermal sensor (common with soldered RAM)"
                : DriverHint();
        }

        if (total != null)
        {
            r.Usage = Value(total, SensorType.Load, "Memory");
            var used = Value(total, SensorType.Data, "Memory Used");
            var avail = Value(total, SensorType.Data, "Memory Available");
            if (used.HasValue && avail.HasValue)
            {
                r.DeviceName = $"{used + avail:0} GB system memory";
                r.Details.Add($"{used:0.0} GB used · {avail:0.0} GB available");
            }
        }
        return r;
    }

    // ---------------------------------------------------------------- Storage

    private IEnumerable<Reading> ReadStorage()
    {
        var lhmDisks = _computer.Hardware.Where(h => h.HardwareType == HardwareType.Storage).ToList();
        var matched = new HashSet<IHardware>();

        foreach (var disk in _disks)
        {
            var lhm = lhmDisks.FirstOrDefault(h => !matched.Contains(h) && DiskInfo.SameModel(h.Name, disk.Model));
            if (lhm != null) matched.Add(lhm);
            yield return ReadDisk(disk.Key, disk.Model, disk.IsSsd, disk.IsUsb, lhm, disk);
        }

        // Drives LHM sees that Windows' storage API did not list.
        foreach (var lhm in lhmDisks.Where(h => !matched.Contains(h)))
            yield return ReadDisk("disk:" + lhm.Identifier, lhm.Name, null, false, lhm, null);
    }

    private Reading ReadDisk(string key, string model, bool? isSsd, bool isUsb, IHardware? lhm, DiskInfo? disk)
    {
        var kind = isSsd switch { true => "SSD", false => "HDD", _ => "Drive" };
        var r = new Reading
        {
            Key = key,
            Title = isUsb ? "USB " + kind : kind,
            DeviceName = model,
            WarmAt = 55,
            HotAt = 70,
            UsageLabel = "Active time",
        };

        r.Usage = (lhm == null ? null : Value(lhm, SensorType.Load, "Total Activity")) ?? disk?.ActiveTime();
        if (r.Usage is > 0.5f)
            _lastActive[key] = DateTime.UtcNow;

        var lhmTemp = lhm == null ? null : AllSensors(lhm)
            .FirstOrDefault(s => s.SensorType == SensorType.Temperature && s.Value is > 0 and < 150);
        if (lhmTemp != null)
        {
            r.Temperature = lhmTemp.Value;
            r.TemperatureSource = lhmTemp.Name == "Temperature" ? "SMART" : lhmTemp.Name;
        }
        else if (disk != null && _wmiDiskTemps.TryGetValue(disk.DeviceId, out var t))
        {
            r.Temperature = t;
            r.TemperatureSource = "SMART (Windows)";
        }
        else if (disk != null && ReadSatTemperature(disk) is var (satTemp, readAt))
        {
            r.Temperature = satTemp;
            var age = DateTime.UtcNow - readAt;
            r.TemperatureSource = age < SatPollInterval + TimeSpan.FromSeconds(5)
                ? "SMART via USB"
                : $"SMART, read {age.TotalMinutes:0} min ago (drive idle)";
        }
        else
        {
            r.TemperatureUnavailableReason =
                !IsElevated ? "Run as administrator"
                // Boot Camp's AppleSSD driver doesn't pass NVMe health data (incl. temperature) through.
                : model.Contains("APPLE SSD", StringComparison.OrdinalIgnoreCase) ? "Apple's Boot Camp SSD driver doesn't expose temperature"
                : isUsb ? "USB enclosure doesn't pass SMART data through"
                : "Drive does not report temperature";
        }

        var space = disk?.SpaceUsed();
        if (space.HasValue)
        {
            r.Secondary = (float)(space.Value.used / space.Value.total * 100);
            r.SecondaryLabel = $"Space {space.Value.used / 1e9:0} / {space.Value.total / 1e9:0} GB ({disk!.Letters})";
        }
        else if (lhm != null && Value(lhm, SensorType.Load, "Used Space") is { } usedPct)
        {
            r.Secondary = usedPct;
            r.SecondaryLabel = "Space used";
        }

        if (lhm != null)
        {
            var rd = Value(lhm, SensorType.Throughput, "Read Rate");
            var wr = Value(lhm, SensorType.Throughput, "Write Rate");
            if (rd.HasValue && wr.HasValue)
                r.Details.Add($"Read {FormatRate(rd.Value)} · Write {FormatRate(wr.Value)}");
            var wear = Value(lhm, SensorType.Level, "Percentage Used");
            if (wear.HasValue)
                r.Details.Add($"Wear {wear:0}%");
        }
        return r;
    }

    /// <summary>
    /// SMART temperature via ATA pass-through, for drives no other source covers (typically
    /// USB enclosures). Polls every 30 s, but only while the drive has been active recently:
    /// a SMART command would otherwise keep an idle hard disk from spinning down.
    /// </summary>
    private (float temp, DateTime at)? ReadSatTemperature(DiskInfo disk)
    {
        var key = disk.Key;
        if (!IsElevated || _satUnsupported.Contains(key) ||
            disk.Model.Contains("APPLE SSD", StringComparison.OrdinalIgnoreCase))
            return null;

        var now = DateTime.UtcNow;
        var first = !_satTemps.ContainsKey(key);
        var recentlyActive = _lastActive.TryGetValue(key, out var active) && now - active < SatIdleCutoff;
        var due = !_satNextPoll.TryGetValue(key, out var next) || now >= next;

        if (due && (first || recentlyActive))
        {
            _satNextPoll[key] = now + SatPollInterval;
            float? t = null;
            try { t = SatSmart.ReadTemperature(disk.DeviceId); }
            catch (Exception ex) { Debug.WriteLine($"SAT SMART on disk {disk.DeviceId} failed: {ex.Message}"); }

            if (t.HasValue)
                _satTemps[key] = (t.Value, now);
            else if (first)
                _satUnsupported.Add(key); // bridge doesn't do SAT; don't keep retrying
        }

        return _satTemps.TryGetValue(key, out var v) ? v : null;
    }

    private static string FormatRate(float bytesPerSec) =>
        bytesPerSec >= 1e6 ? $"{bytesPerSec / 1e6:0.0} MB/s" : $"{bytesPerSec / 1e3:0} KB/s";

    private void PollWmiDiskTemps()
    {
        if (!IsElevated) return;
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage",
                "SELECT DeviceId, Temperature FROM MSFT_StorageReliabilityCounter");
            foreach (ManagementObject mo in searcher.Get())
            {
                var id = mo["DeviceId"]?.ToString();
                var temp = mo["Temperature"];
                if (id == null || temp == null) continue;
                var t = Convert.ToSingle(temp);
                if (t is > 0 and < 150)
                    _wmiDiskTemps[id] = t;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Reliability counter query failed: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------- Diagnostics

    /// <summary>Every raw sensor value, for the "All sensors" view.</summary>
    public string DumpAll()
    {
        lock (_lock)
            return DumpAllLocked();
    }

    private string DumpAllLocked()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Administrator: {IsElevated}    PawnIO driver: {PawnIoInstalled}");
        sb.AppendLine();
        void Dump(IHardware hw, string indent)
        {
            sb.AppendLine($"{indent}{hw.Name}  [{hw.HardwareType}]");
            foreach (var s in hw.Sensors)
                sb.AppendLine($"{indent}    {s.SensorType,-12} {s.Name,-34} {(s.Value.HasValue ? s.Value.Value.ToString("0.##") : "—")}");
            foreach (var sub in hw.SubHardware)
                Dump(sub, indent + "    ");
        }
        foreach (var hw in _computer.Hardware)
            Dump(hw, "");
        sb.AppendLine("Windows physical disks");
        foreach (var d in _disks)
            sb.AppendLine($"    Disk {d.DeviceId}  {d.Model}  SSD={d.IsSsd?.ToString() ?? "?"}  USB={d.IsUsb}  {d.Letters}" +
                          (_wmiDiskTemps.TryGetValue(d.DeviceId, out var t) ? $"  Windows temp {t:0}" : "") +
                          (_satTemps.TryGetValue(d.Key, out var s) ? $"  SMART-via-USB temp {s.temp:0}" : "") +
                          (_satUnsupported.Contains(d.Key) ? "  SMART-via-USB unsupported" : ""));
        return sb.ToString();
    }

    /// <summary>Sensor dump plus LHM's full hardware report and raw Windows storage data.</summary>
    public string DiagnosticReport()
    {
        lock (_lock)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"TempMonitor diagnostics  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"OS: {Environment.OSVersion}");
            sb.AppendLine();
            sb.AppendLine(DumpAllLocked());

            sb.AppendLine("==== MSFT_StorageReliabilityCounter (raw) ====");
            AppendWmi(sb, @"root\Microsoft\Windows\Storage", "SELECT * FROM MSFT_StorageReliabilityCounter");
            sb.AppendLine("==== MSFT_PhysicalDisk (raw) ====");
            AppendWmi(sb, @"root\Microsoft\Windows\Storage",
                "SELECT DeviceId, FriendlyName, MediaType, BusType, Model, FirmwareVersion FROM MSFT_PhysicalDisk");
            sb.AppendLine("==== Win32_PhysicalMemory ====");
            AppendWmi(sb, @"root\cimv2",
                "SELECT Manufacturer, PartNumber, Capacity, Speed, SMBIOSMemoryType, FormFactor, DeviceLocator FROM Win32_PhysicalMemory");

            sb.AppendLine("==== LibreHardwareMonitor report ====");
            try { sb.AppendLine(_computer.GetReport()); }
            catch (Exception ex) { sb.AppendLine("GetReport failed: " + ex); }
            return sb.ToString();
        }
    }

    private static void AppendWmi(System.Text.StringBuilder sb, string scope, string query)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(scope, query);
            foreach (ManagementObject mo in searcher.Get())
            {
                foreach (var p in mo.Properties)
                    if (p.Value != null)
                        sb.AppendLine($"    {p.Name,-32} {(p.Value is Array a ? string.Join(",", a.Cast<object>()) : p.Value)}");
                sb.AppendLine();
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine("    query failed: " + ex.Message);
        }
    }

    public void Dispose()
    {
        lock (_lock)
            _computer.Close();
        foreach (var d in _disks) d.Dispose();
    }
}

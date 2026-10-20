using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using TempMonitor.Fans;

namespace TempMonitor;

/// <summary>
/// Reads hardware sensors on Linux, without root: hwmon in sysfs (coretemp/k10temp for CPUs,
/// amdgpu, nvme, drivetemp for SATA/USB drives, jc42/spd5118 for memory modules), /proc for
/// usage, and NVML for NVIDIA GPUs. Like the Windows provider, it never estimates: a device
/// without a sensor shows N/A with the reason.
/// </summary>
public sealed class LinuxSensorProvider : ISensorProvider
{
    private readonly object _lock = new();
    private readonly Sysfs _fs;
    private readonly INvml _nvml;
    private readonly PciIds _pci;
    private readonly Func<DateTime> _now;
    private readonly Func<string, (double used, double total)?> _spaceOf;

    // Previous samples for rates: CPU jiffies, and per disk (io ticks ms, sectors read, written)
    private (long total, long idle, DateTime at)? _lastCpu;
    private readonly Dictionary<string, (long ticks, long read, long written, DateTime at)> _lastDisk = new();

    // NVIDIA GPUs from the latest Read(), so fans don't query NVML a second time
    private IReadOnlyList<NvGpu>? _lastNvidia;

    // How to control each fan found by the latest ReadFans(), for the fan daemon
    private Dictionary<string, FanChannel> _channels = new();
    internal IReadOnlyDictionary<string, FanChannel> Channels { get { lock (_lock) return _channels; } }

    internal Sysfs Fs => _fs;
    internal INvml Nvml => _nvml;

    public LinuxSensorProvider() : this("/", null, null, null) { }

    internal LinuxSensorProvider(string root, INvml? nvml, Func<DateTime>? now, Func<string, (double used, double total)?>? spaceOf)
    {
        _fs = new Sysfs(root);
        _nvml = nvml ?? new Nvml();
        _pci = new PciIds(_fs.P("/usr/share/hwdata/pci.ids"));
        _now = now ?? (() => DateTime.UtcNow);
        _spaceOf = spaceOf ?? DefaultSpaceOf;
    }

    public ProviderStatus Status
    {
        get
        {
            var drivetemp = _fs.Exists(_fs.P("/sys/module/drivetemp"));
            var sata = BlockDisks().Any(d => !d.StartsWith("nvme", StringComparison.Ordinal));
            return new ProviderStatus(
                Summary: "sensors via hwmon" + (drivetemp ? " + drivetemp" : "") + " · no admin rights needed",
                Warning: sata && !drivetemp
                    ? "SATA and USB drive temperatures need the drivetemp kernel module. Load it once with: sudo modprobe drivetemp (RudramOS loads it automatically)."
                    : null,
                CanElevate: false,
                CanInstallDriver: false);
        }
    }

    public List<Reading> Read()
    {
        lock (_lock)
        {
            var hwmons = _fs.AllHwmons().ToList();
            var readings = new List<Reading> { ReadCpu(hwmons) };
            readings.AddRange(ReadGpus());
            readings.Add(ReadMemory(hwmons));
            readings.AddRange(ReadDisks());
            return readings;
        }
    }

    // ---------------------------------------------------------------- CPU

    private Reading ReadCpu(List<Hwmon> hwmons)
    {
        var cpuinfo = _fs.Lines(_fs.P("/proc/cpuinfo"));
        var model = cpuinfo.FirstOrDefault(l => l.StartsWith("model name", StringComparison.Ordinal))?.Split(':', 2)[1].Trim();
        var r = new Reading
        {
            Key = "cpu:0",
            Title = "CPU",
            DeviceName = CleanCpuName(model) ?? "Processor",
            WarmAt = 80,
            HotAt = 95,
        };

        // Intel: "Package id 0" is the package DTS sensor (what Intel's tools show).
        // AMD: Tdie (k10temp/zenpower), else Tctl (which may carry an offset on some models).
        var chip = hwmons.FirstOrDefault(h => h.Name is "coretemp" or "k10temp" or "zenpower");
        if (chip != null)
        {
            if (chip.Temp("Package id 0") is { } pkg)
            {
                r.Temperature = pkg;
                r.TemperatureSource = "Package";
            }
            else if (chip.Temp("Tdie") is { } tdie)
            {
                r.Temperature = tdie;
                r.TemperatureSource = "Tdie";
            }
            else if (chip.Temp("Tctl") is { } tctl)
            {
                r.Temperature = tctl;
                r.TemperatureSource = "Tctl";
            }

            var cores = chip.Temps.Where(t => t.Label.StartsWith("Core ", StringComparison.Ordinal) ||
                                              t.Label.StartsWith("Tccd", StringComparison.Ordinal)).ToList();
            if (cores.Count > 0 && r.Temperature.HasValue)
                r.Details.Add($"Hottest core {cores.Max(c => c.Celsius):0} °C");
        }
        if (r.Temperature == null)
            r.TemperatureUnavailableReason = chip == null
                ? "No CPU sensor driver loaded (coretemp / k10temp)"
                : "Sensor not reported";

        r.Usage = CpuUsage();

        var freqs = _fs.Dirs(_fs.P("/sys/devices/system/cpu"), "cpu*")
            .Select(d => _fs.Long(Path.Combine(d, "cpufreq", "scaling_cur_freq")))
            .OfType<long>().Where(k => k > 0).ToList();
        if (freqs.Count > 0)
            r.Details.Add($"Clock {freqs.Average() / 1e6:0.00} GHz");
        return r;
    }

    private static string? CleanCpuName(string? name) =>
        name?.Replace("(R)", "").Replace("(TM)", "").Replace(" CPU", "").Replace("  ", " ").Split(" @ ")[0].Trim();

    /// <summary>Busy share of all CPU time since the last call (the first call has no baseline).</summary>
    private float? CpuUsage()
    {
        var line = _fs.Lines(_fs.P("/proc/stat")).FirstOrDefault(l => l.StartsWith("cpu ", StringComparison.Ordinal));
        if (line == null) return null;
        var v = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Take(8)
            .Select(x => long.TryParse(x, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0).ToArray();
        if (v.Length < 5) return null;
        var total = v.Sum();
        var idle = v[3] + v[4]; // idle + iowait
        var now = _now();
        float? usage = null;
        if (_lastCpu is { } last && total > last.total)
            usage = Math.Clamp(100f * (1 - (float)(idle - last.idle) / (total - last.total)), 0, 100);
        _lastCpu = (total, idle, now);
        return usage;
    }

    // ---------------------------------------------------------------- GPU

    private IEnumerable<Reading> ReadGpus()
    {
        var cards = _fs.Dirs(_fs.P("/sys/class/drm"), "card*")
            .Where(c => Path.GetFileName(c).Skip(4).All(char.IsDigit))
            .Select(c => (dir: c, dev: Path.Combine(c, "device")))
            .Where(c => _fs.Text(Path.Combine(c.dev, "vendor")) != null)
            .ToList();

        string? nvReason = null;
        IReadOnlyList<NvGpu>? nv = null;
        if (cards.Any(c => Hex(_fs.Text(Path.Combine(c.dev, "vendor"))) == "10de"))
            nv = _nvml.Read(out nvReason);
        _lastNvidia = nv;
        var nvIndex = 0;

        var index = 0;
        foreach (var (dir, dev) in cards)
        {
            index++;
            var vendor = Hex(_fs.Text(Path.Combine(dev, "vendor")));
            var device = Hex(_fs.Text(Path.Combine(dev, "device")));
            var r = new Reading
            {
                Key = "gpu:" + Path.GetFileName(dir),
                Title = cards.Count > 1 ? $"GPU {index}" : "GPU",
                DeviceName = _pci.Name(vendor, device) ?? $"Graphics {vendor}:{device}",
                WarmAt = 75,
                HotAt = 90,
            };

            switch (vendor)
            {
                case "1002": ReadAmdGpu(r, dev); break;
                case "10de":
                    var g = nv != null && nvIndex < nv.Count ? nv[nvIndex++] : null;
                    if (g != null) ApplyNvidia(r, g);
                    else r.TemperatureUnavailableReason = nvReason ?? "NVIDIA driver not loaded";
                    break;
                case "8086":
                    r.TemperatureUnavailableReason = "Integrated GPU: shares the CPU temperature";
                    break;
                default:
                    r.TemperatureUnavailableReason = "Sensor not reported by driver";
                    break;
            }
            yield return r;
        }
    }

    private void ReadAmdGpu(Reading r, string dev)
    {
        var hw = _fs.HwmonsOf(dev).FirstOrDefault(h => h.Name == "amdgpu");
        if (hw?.Temp("edge") is { } edge)
        {
            r.Temperature = edge;
            r.TemperatureSource = "Core";
        }
        else
        {
            r.TemperatureUnavailableReason = "Sensor not reported by driver";
        }
        if (hw?.Temp("junction") is { } junction)
            r.Details.Add($"Hot spot {junction:0} °C");

        if (_fs.Long(Path.Combine(dev, "gpu_busy_percent")) is { } busy)
            r.Usage = Math.Clamp(busy, 0, 100);

        var used = _fs.Long(Path.Combine(dev, "mem_info_vram_used"));
        var total = _fs.Long(Path.Combine(dev, "mem_info_vram_total"));
        if (used.HasValue && total is > 0)
            SetVram(r, used.Value, total.Value);

        var power = hw?.Raw("power1_average") ?? hw?.Raw("power1_input");
        if (power is > 0)
            r.Details.Add($"Power {power / 1e6:0} W");
        if (hw?.Raw("freq1_input") is > 0 and var hz)
            r.Details.Add($"Clock {hz / 1e6:0} MHz");
    }

    private static void ApplyNvidia(Reading r, NvGpu g)
    {
        if (!string.IsNullOrWhiteSpace(g.Name)) r.DeviceName = g.Name.StartsWith("NVIDIA", StringComparison.Ordinal) ? g.Name : "NVIDIA " + g.Name;
        if (g.Temperature is { } t)
        {
            r.Temperature = t;
            r.TemperatureSource = "Core";
        }
        else
        {
            r.TemperatureUnavailableReason = "Sensor not reported by driver";
        }
        r.Usage = g.Usage;
        if (g.MemUsedBytes is { } used && g.MemTotalBytes is > 0 and var total)
            SetVram(r, used, total);
        if (g.PowerWatts is > 0 and var w) r.Details.Add($"Power {w:0} W");
        if (g.ClockMHz is > 0 and var c) r.Details.Add($"Clock {c:0} MHz");
    }

    private static void SetVram(Reading r, double usedBytes, double totalBytes)
    {
        r.Secondary = (float)(usedBytes / totalBytes * 100);
        r.SecondaryLabel = $"VRAM {usedBytes / 1073741824:0.0} / {totalBytes / 1073741824:0.0} GB";
    }

    private static string Hex(string? s) => (s ?? "").Replace("0x", "", StringComparison.OrdinalIgnoreCase).ToLowerInvariant();

    // ---------------------------------------------------------------- RAM

    private Reading ReadMemory(List<Hwmon> hwmons)
    {
        var r = new Reading { Key = "ram", Title = "RAM", DeviceName = "System memory", WarmAt = 60, HotAt = 80 };

        // DIMM temperatures from the thermal sensor on each module (jc42 for DDR4, spd5118 for DDR5)
        var dimms = hwmons.Where(h => h.Name is "jc42" or "spd5118").SelectMany(h => h.Temps).Select(t => t.Celsius).ToList();
        if (dimms.Count > 0)
        {
            r.Temperature = dimms.Max();
            r.TemperatureSource = dimms.Count > 1 ? $"Hottest of {dimms.Count} DIMMs" : "DIMM";
        }
        else
        {
            r.TemperatureUnavailableReason = "This memory has no thermal sensor (common with soldered RAM)";
        }

        var info = _fs.Lines(_fs.P("/proc/meminfo"))
            .Select(l => l.Split(':', 2))
            .Where(p => p.Length == 2)
            .ToDictionary(p => p[0].Trim(), p => long.TryParse(p[1].Replace("kB", "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var kb) ? kb : 0);
        if (info.TryGetValue("MemTotal", out var totalKb) && totalKb > 0 && info.TryGetValue("MemAvailable", out var availKb))
        {
            var usedGb = (totalKb - availKb) / 1048576.0;
            var availGb = availKb / 1048576.0;
            r.Usage = (float)((totalKb - availKb) * 100.0 / totalKb);
            r.DeviceName = $"{InstalledGb(totalKb / 1048576.0)} GB system memory";
            r.Details.Add($"{usedGb:0.0} GB used · {availGb:0.0} GB available");
        }
        return r;
    }

    /// <summary>
    /// MemTotal is what's usable after firmware/GPU reservations (e.g. 62.5 GiB on a 64 GB
    /// machine), so show the smallest common module total it fits in.
    /// </summary>
    internal static int InstalledGb(double usableGib)
    {
        int[] sizes = { 1, 2, 3, 4, 6, 8, 12, 16, 20, 24, 32, 36, 40, 48, 64, 96, 128, 192, 256, 384, 512, 768, 1024, 1536, 2048 };
        foreach (var s in sizes)
            if (usableGib <= s) return s;
        return (int)Math.Ceiling(usableGib);
    }

    // ---------------------------------------------------------------- Storage

    /// <summary>Whole disks in /sys/block (no partitions, loop, zram, device-mapper, optical).</summary>
    private IEnumerable<string> BlockDisks() =>
        _fs.Dirs(_fs.P("/sys/block"))
            .Select(Path.GetFileName).OfType<string>()
            .Where(n => !n.StartsWith("loop", StringComparison.Ordinal) && !n.StartsWith("ram", StringComparison.Ordinal) &&
                        !n.StartsWith("zram", StringComparison.Ordinal) && !n.StartsWith("dm-", StringComparison.Ordinal) &&
                        !n.StartsWith("sr", StringComparison.Ordinal) && !n.StartsWith("md", StringComparison.Ordinal) &&
                        !n.StartsWith("nbd", StringComparison.Ordinal))
            .Where(n => (_fs.Long(_fs.P($"/sys/block/{n}/size")) ?? 0) > 0); // empty card-reader slots report 0

    private IEnumerable<Reading> ReadDisks()
    {
        var stats = DiskStats();
        var mounts = MountsByDisk();
        var drivetemp = _fs.Exists(_fs.P("/sys/module/drivetemp"));
        var now = _now();

        foreach (var name in BlockDisks())
        {
            var dir = _fs.P($"/sys/block/{name}");
            var nvme = name.StartsWith("nvme", StringComparison.Ordinal);
            var rotational = _fs.Long(Path.Combine(dir, "queue", "rotational"));
            var kind = nvme || rotational == 0 ? "SSD" : rotational == 1 ? "HDD" : "Drive";
            var usb = IsUsb(name);
            var model = _fs.Text(Path.Combine(dir, "device", "model"));
            var r = new Reading
            {
                Key = "disk:" + name,
                Title = usb ? "USB " + kind : kind,
                DeviceName = string.IsNullOrWhiteSpace(model) ? name : model,
                WarmAt = 55,
                HotAt = 70,
                UsageLabel = "Active time",
            };

            // NVMe: hwmon on the controller ("Composite"); SATA/USB: drivetemp on the SCSI device
            var hw = _fs.HwmonsOf(Path.Combine(dir, "device")).FirstOrDefault(h => h.Temps.Count > 0);
            float? diskTemp = hw == null ? null : hw.Temp("Composite") ?? hw.Temps[0].Celsius;
            if (diskTemp is { } t)
            {
                r.Temperature = t;
                r.TemperatureSource = nvme ? "NVMe" : "SMART";
            }
            else
            {
                r.TemperatureUnavailableReason =
                    nvme ? "Drive does not report temperature"
                    : !drivetemp ? "Needs the drivetemp kernel module"
                    : usb ? "USB enclosure doesn't pass SMART data through"
                    : "Drive does not report temperature";
            }

            if (stats.TryGetValue(name, out var s))
            {
                if (_lastDisk.TryGetValue(name, out var last))
                {
                    var ms = (now - last.at).TotalMilliseconds;
                    if (ms > 0)
                    {
                        r.Usage = (float)Math.Clamp((s.ticks - last.ticks) / ms * 100, 0, 100);
                        var rd = (s.read - last.read) * 512 / (ms / 1000);
                        var wr = (s.written - last.written) * 512 / (ms / 1000);
                        r.Details.Add($"Read {FormatRate(rd)} · Write {FormatRate(wr)}");
                    }
                }
                _lastDisk[name] = (s.ticks, s.read, s.written, now);
            }

            if (mounts.TryGetValue(name, out var mountPoints))
            {
                double used = 0, total = 0;
                var counted = new List<string>();
                foreach (var mp in mountPoints)
                    if (_spaceOf(mp) is { } sp && sp.total >= MinFilesystemBytes)
                    {
                        used += sp.used;
                        total += sp.total;
                        counted.Add(mp);
                    }
                mountPoints = counted;
                if (total > 0)
                {
                    r.Secondary = (float)(used / total * 100);
                    r.SecondaryLabel = $"Space {used / 1e9:0} / {total / 1e9:0} GB ({string.Join(" ", mountPoints.Take(2))})";
                }
            }
            yield return r;
        }
    }

    /// <summary>Tiny filesystems (bind mounts, small helper partitions) would only add noise to "Space".</summary>
    private const double MinFilesystemBytes = 256e6;

    private static string FormatRate(double bytesPerSec) =>
        bytesPerSec >= 1e6 ? $"{bytesPerSec / 1e6:0.0} MB/s" : $"{bytesPerSec / 1e3:0} KB/s";

    /// <summary>USB drives: udev records the bus for each block device (major:minor).</summary>
    private bool IsUsb(string disk)
    {
        var devNum = _fs.Text(_fs.P($"/sys/block/{disk}/dev"));
        if (devNum == null) return false;
        return _fs.Lines(_fs.P($"/run/udev/data/b{devNum}")).Any(l => l == "E:ID_BUS=usb");
    }

    /// <summary>/proc/diskstats: io ticks (ms busy), sectors read, sectors written per device.</summary>
    private Dictionary<string, (long ticks, long read, long written)> DiskStats()
    {
        var result = new Dictionary<string, (long, long, long)>();
        foreach (var line in _fs.Lines(_fs.P("/proc/diskstats")))
        {
            var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (f.Length < 13) continue;
            long N(int i) => long.TryParse(f[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
            result[f[2]] = (N(12), N(5), N(9));
        }
        return result;
    }

    /// <summary>
    /// Mount points per whole disk, following partitions and device-mapper (LUKS, LVM) down to
    /// the physical disk. Each filesystem is counted once even if mounted several times
    /// (e.g. btrfs subvolumes for /, /var and /home).
    /// </summary>
    private Dictionary<string, List<string>> MountsByDisk()
    {
        var disks = BlockDisks().ToList();
        var dmNames = _fs.Dirs(_fs.P("/sys/block"), "dm-*")
            .ToDictionary(d => _fs.Text(Path.Combine(d, "dm", "name")) ?? "", d => Path.GetFileName(d)!);
        var result = new Dictionary<string, List<string>>();
        var seenSources = new HashSet<string>();

        foreach (var line in _fs.Lines(_fs.P("/proc/mounts")))
        {
            var f = line.Split(' ');
            if (f.Length < 3 || !f[0].StartsWith("/dev/", StringComparison.Ordinal)) continue;
            if (!seenSources.Add(f[0])) continue;
            var mountPoint = f[1].Replace("\\040", " ");
            var dev = f[0]["/dev/".Length..];
            if (dev.StartsWith("mapper/", StringComparison.Ordinal))
                dev = dmNames.GetValueOrDefault(dev["mapper/".Length..], dev);
            foreach (var disk in DisksUnder(dev, disks, 0))
            {
                if (!result.TryGetValue(disk, out var list)) result[disk] = list = new List<string>();
                list.Add(mountPoint);
            }
        }
        return result;
    }

    private IEnumerable<string> DisksUnder(string dev, List<string> disks, int depth)
    {
        if (depth > 8) yield break;
        if (disks.Contains(dev))
        {
            yield return dev;
            yield break;
        }
        // A partition is a subfolder of its disk in /sys/block (e.g. /sys/block/nvme0n1/nvme0n1p3)
        var parent = disks.FirstOrDefault(d => _fs.Exists(_fs.P($"/sys/block/{d}/{dev}")));
        if (parent != null)
        {
            yield return parent;
            yield break;
        }
        // Device-mapper (LUKS, LVM): follow its "slaves"
        foreach (var slave in _fs.Names(_fs.P($"/sys/block/{dev}/slaves")))
            foreach (var d in DisksUnder(slave, disks, depth + 1))
                yield return d;
    }

    private static (double used, double total)? DefaultSpaceOf(string mountPoint)
    {
        try
        {
            var di = new DriveInfo(mountPoint);
            if (!di.IsReady || di.TotalSize == 0) return null;
            return (di.TotalSize - di.AvailableFreeSpace, di.TotalSize);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    // ---------------------------------------------------------------- Fans

    /// <summary>Motherboard Super I/O chips. Their unused headers read 0 RPM, so those are hidden.</summary>
    private static readonly HashSet<string> SuperIoChips = new(StringComparer.Ordinal)
    {
        "nct6775", "nct6683", "nct6687", "nct7802", "nct7904", "it87", "f71882fg", "f71805f",
        "w83627ehf", "w83627hf", "w83795", "sch5627", "sch5636",
    };

    /// <summary>GPU drivers. Their fans legitimately stop when the card is cool ("zero RPM" mode).</summary>
    private static readonly HashSet<string> GpuChips = new(StringComparer.Ordinal) { "amdgpu", "radeon", "nouveau" };

    private static readonly Regex FanInput = new(@"^fan(\d+)_input$", RegexOptions.Compiled);

    public FanReport ReadFans()
    {
        lock (_lock)
        {
            var fans = new List<FanReading>();
            var channels = new Dictionary<string, FanChannel>();
            var idleHeaders = 0;
            foreach (var hw in _fs.AllHwmons())
            {
                var indexes = hw.Inputs.Select(i => FanInput.Match(i.File)).Where(m => m.Success)
                    .Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).ToList();
                if (indexes.Count == 0) continue;
                var device = FanDevice(hw);
                var chipId = ChipId(hw);
                foreach (var i in indexes)
                {
                    var f = HwmonFan(hw, i, device, chipId, single: indexes.Count == 1);
                    // 0 RPM on a motherboard header: nothing plugged in. Unless software is driving it,
                    // in which case it may be a stalled fan, which must stay visible.
                    if (f.Rpm == 0 && SuperIoChips.Contains(hw.Name) && f.Mode != FanMode.Manual)
                    {
                        idleHeaders++;
                        continue;
                    }
                    fans.Add(f);
                    if (f.HasControl)
                        channels[f.Key] = hw.Name == "applesmc"
                            ? new FanChannel(ChannelKind.AppleSmc, hw.Dir, i, MinRpm: f.MinRpm, MaxRpm: f.MaxRpm)
                            : new FanChannel(ChannelKind.Pwm, hw.Dir, i);
                }
            }

            foreach (var g in _lastNvidia ?? Array.Empty<NvGpu>())
            {
                var gpuFans = g.Fans ?? Array.Empty<NvFan>();
                foreach (var nf in gpuFans)
                {
                    channels[$"nvml:{g.PciBusId}/{nf.Index}"] = new FanChannel(ChannelKind.Nvml, "", nf.Index, BusId: g.PciBusId);
                    fans.Add(new FanReading
                    {
                        Key = $"nvml:{g.PciBusId}/{nf.Index}",
                        Name = gpuFans.Count > 1 ? $"GPU fan {nf.Index + 1}" : "GPU fan",
                        Device = g.Name.StartsWith("NVIDIA", StringComparison.Ordinal) ? g.Name : "NVIDIA " + g.Name,
                        Rpm = nf.Rpm,
                        Percent = nf.Percent,
                        Mode = nf.Manual switch { true => FanMode.Manual, false => FanMode.Auto, _ => FanMode.Unknown },
                        HasControl = true,
                    });
                }
            }
            _channels = channels;

            string? note = null;
            if (idleHeaders > 0)
                note = idleHeaders == 1
                    ? "1 motherboard fan header reads 0 RPM (nothing connected) and is hidden."
                    : $"{idleHeaders} motherboard fan headers read 0 RPM (nothing connected) and are hidden.";
            else if (fans.Count == 0)
                note = "No fan sensors found. Desktop motherboards need their Super I/O driver (e.g. nct6775 or it87); " +
                       "many laptops control their fans in firmware without reporting the speed.";
            return new FanReport(fans, note);
        }
    }

    private FanReading HwmonFan(Hwmon hw, int i, string device, string chipId, bool single)
    {
        string F(string suffix) => Path.Combine(hw.Dir, $"fan{i}_{suffix}");
        var label = _fs.Text(F("label"))?.Trim();
        var gpu = GpuChips.Contains(hw.Name);
        var f = new FanReading
        {
            Key = $"hwmon:{chipId}/fan{i}",
            Name = !string.IsNullOrEmpty(label) ? label
                : gpu ? (single ? "GPU fan" : $"GPU fan {i}")
                : single ? "Fan" : $"Fan {i}",
            Device = device,
            Rpm = hw.Raw($"fan{i}_input"),
        };

        if (hw.Name == "applesmc")
        {
            // Apple SMC: speeds are set in RPM within the firmware's min/max, not by PWM
            f.MinRpm = _fs.Long(F("min"));
            f.MaxRpm = _fs.Long(F("max"));
            var manual = _fs.Long(F("manual"));
            f.Mode = manual switch { 1 => FanMode.Manual, 0 => FanMode.Auto, _ => FanMode.Unknown };
            f.HasControl = manual != null;
            return f;
        }

        // PWM channel N drives fan N (hwmon convention): 0–255 duty, pwmN_enable 0 = full, 1 = manual, 2+ = automatic
        // Control needs pwmN_enable too: without it there is no way to take the fan from firmware
        var pwm = _fs.Long(Path.Combine(hw.Dir, $"pwm{i}"));
        var enable = _fs.Long(Path.Combine(hw.Dir, $"pwm{i}_enable"));
        if (pwm != null)
            f.Percent = Math.Clamp(pwm.Value * 100f / 255, 0, 100);
        f.HasControl = pwm != null && enable != null;
        f.Mode = enable switch
        {
            0 => FanMode.Full,
            1 => FanMode.Manual,
            >= 2 => FanMode.Auto,
            _ => FanMode.Unknown,
        };
        return f;
    }

    /// <summary>What a hwmon chip's fans belong to, in words.</summary>
    private string FanDevice(Hwmon hw)
    {
        if (SuperIoChips.Contains(hw.Name)) return $"Motherboard ({hw.Name})";
        if (GpuChips.Contains(hw.Name))
        {
            var dev = Path.Combine(hw.Dir, "device");
            var vendor = Hex(_fs.Text(Path.Combine(dev, "vendor")));
            var device = Hex(_fs.Text(Path.Combine(dev, "device")));
            return _pci.Name(vendor, device) ?? "Graphics card";
        }
        return hw.Name switch
        {
            "applesmc" => "Apple SMC",
            "thinkpad" => "ThinkPad",
            "dell_smm" => "Dell",
            _ => hw.Name,
        };
    }

    /// <summary>
    /// Stable id for a hwmon chip: hwmonN numbers can change between boots, so use the device
    /// it belongs to (e.g. "nct6775.656", "0000:03:00.0").
    /// </summary>
    private static string ChipId(Hwmon hw)
    {
        string? device = null;
        try { device = new DirectoryInfo(Path.Combine(hw.Dir, "device")).ResolveLinkTarget(returnFinalTarget: true)?.Name; }
        catch (IOException) { }
        return $"{hw.Name}@{device ?? Path.GetFileName(hw.Dir)}";
    }

    // ---------------------------------------------------------------- Diagnostics

    public string DumpAll()
    {
        lock (_lock)
        {
            var sb = new StringBuilder();
            sb.AppendLine("hwmon sensors (/sys/class/hwmon)");
            foreach (var h in _fs.AllHwmons())
            {
                sb.AppendLine($"{Path.GetFileName(h.Dir)}  [{h.Name}]");
                foreach (var (file, label, raw) in h.Inputs)
                    sb.AppendLine($"    {file,-18} {label,-24} {Format(file, raw)}");
            }
            sb.AppendLine();
            sb.AppendLine("Graphics (/sys/class/drm)");
            foreach (var c in _fs.Dirs(_fs.P("/sys/class/drm"), "card*").Where(c => Path.GetFileName(c).Skip(4).All(char.IsDigit)))
            {
                var dev = Path.Combine(c, "device");
                sb.AppendLine($"    {Path.GetFileName(c)}  {_fs.Text(Path.Combine(dev, "vendor"))}:{_fs.Text(Path.Combine(dev, "device"))}  " +
                              $"busy={_fs.Text(Path.Combine(dev, "gpu_busy_percent")) ?? "-"}");
            }
            var nv = _nvml.Read(out var nvReason);
            if (nv != null)
                foreach (var g in nv)
                    sb.AppendLine($"    NVML {g.PciBusId}  {g.Name}  {g.Temperature} °C  {g.Usage}%  {g.PowerWatts} W");
            else if (nvReason != null)
                sb.AppendLine("    NVML: " + nvReason);
            sb.AppendLine();
            sb.AppendLine("Drives (/sys/block)");
            foreach (var d in BlockDisks())
                sb.AppendLine($"    {d}  {_fs.Text(_fs.P($"/sys/block/{d}/device/model"))}  usb={IsUsb(d)}  " +
                              $"rotational={_fs.Text(_fs.P($"/sys/block/{d}/queue/rotational"))}");
            return sb.ToString();
        }
    }

    private static string Format(string file, long raw) => file switch
    {
        _ when file.StartsWith("temp", StringComparison.Ordinal) => $"{raw / 1000.0:0.0} °C",
        _ when file.StartsWith("fan", StringComparison.Ordinal) => $"{raw} RPM",
        _ when file.StartsWith("in", StringComparison.Ordinal) => $"{raw / 1000.0:0.000} V",
        _ when file.StartsWith("power", StringComparison.Ordinal) => $"{raw / 1e6:0.0} W",
        _ when file.StartsWith("freq", StringComparison.Ordinal) => $"{raw / 1e6:0} MHz",
        _ when file.StartsWith("pwm", StringComparison.Ordinal) => $"{raw * 100 / 255}% ({raw}/255)",
        _ => raw.ToString(CultureInfo.InvariantCulture),
    };

    public string DiagnosticReport()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"TempMonitor diagnostics  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine("OS: " + (_fs.Lines(_fs.P("/etc/os-release")).FirstOrDefault(l => l.StartsWith("PRETTY_NAME=", StringComparison.Ordinal))?[12..].Trim('"') ?? "Linux"));
        sb.AppendLine("Kernel: " + _fs.Text(_fs.P("/proc/version")));
        foreach (var m in new[] { "coretemp", "k10temp", "zenpower", "amdgpu", "nvidia", "nouveau", "nvme", "drivetemp", "jc42", "spd5118" })
            sb.AppendLine($"Module {m,-10} {(_fs.Exists(_fs.P($"/sys/module/{m}")) ? "loaded" : "-")}");
        sb.AppendLine();
        sb.AppendLine(DumpAll());
        sb.AppendLine("==== /proc/mounts ====");
        foreach (var l in _fs.Lines(_fs.P("/proc/mounts")).Where(l => l.StartsWith("/dev/", StringComparison.Ordinal)))
            sb.AppendLine("    " + l);
        return sb.ToString();
    }

    public void Dispose() => (_nvml as IDisposable)?.Dispose();
}

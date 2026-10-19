using Xunit;

namespace TempMonitor.Tests;

public class LinuxProviderTests
{
    private const string PciIds = """
        # pci.ids excerpt
        1002  Advanced Micro Devices, Inc. [AMD/ATI]
        	7340  Navi 14 [Radeon RX 5500/5500M / Pro 5500M]
        	7341  Navi 14 [Radeon Pro W5500]
        10de  NVIDIA Corporation
        	2504  GA106 [GeForce RTX 3060 Lite Hash Rate]
        8086  Intel Corporation
        	3e9b  CoffeeLake-H GT2 [UHD Graphics 630]
        """;

    /// <summary>The MacBook Pro 16 (2019) running RudramOS: Intel i9, AMD 5500M, NVMe behind LUKS.</summary>
    private static FakeLinux MacBook()
    {
        var f = new FakeLinux();
        f.File("/usr/share/hwdata/pci.ids", PciIds.Replace("\r", ""))
         .File("/proc/cpuinfo", "processor\t: 0\nmodel name\t: Intel(R) Core(TM) i9-9980HK CPU @ 2.40GHz\n")
         .Hwmon("/sys/class/hwmon/hwmon2", "coretemp", (1, "Package id 0", 62), (2, "Core 0", 60), (3, "Core 1", 66))
         .Hwmon("/sys/class/hwmon/hwmon0", "acpitz", (1, null, 45))
         .File("/sys/devices/system/cpu/cpu0/cpufreq/scaling_cur_freq", "2400000")
         .File("/sys/devices/system/cpu/cpu1/cpufreq/scaling_cur_freq", "3600000")
         .Stat(busy: 1000, idle: 9000)
         // AMD GPU
         .File("/sys/class/drm/card1/device/vendor", "0x1002")
         .File("/sys/class/drm/card1/device/device", "0x7340")
         .File("/sys/class/drm/card1/device/gpu_busy_percent", "20")
         .File("/sys/class/drm/card1/device/mem_info_vram_used", (1288490188L).ToString())
         .File("/sys/class/drm/card1/device/mem_info_vram_total", (4294967296L).ToString())
         .Hwmon("/sys/class/drm/card1/device/hwmon/hwmon3", "amdgpu", (1, "edge", 60), (2, "junction", 63), (3, "mem", 58))
         .File("/sys/class/drm/card1/device/hwmon/hwmon3/power1_average", "7000000")
         .File("/sys/class/drm/card1/device/hwmon/hwmon3/freq1_input", "93000000")
         .Dir("/sys/class/drm/card1-eDP-1") // a connector, not a card
         // RAM: 64 GB, no DIMM sensors
         .File("/proc/meminfo", "MemTotal:       65536000 kB\nMemFree:  1000 kB\nMemAvailable:   38600000 kB")
         // NVMe with LUKS (dm-0) holding btrfs mounted three times
         .File("/sys/block/nvme0n1/size", "2000409264")
         .File("/sys/block/nvme0n1/dev", "259:0")
         .File("/sys/block/nvme0n1/queue/rotational", "0")
         .File("/sys/block/nvme0n1/device/model", "APPLE SSD AP1024N")
         .Hwmon("/sys/block/nvme0n1/device/hwmon1", "nvme", (1, "Composite", 41), (2, "Sensor 1", 39))
         .Dir("/sys/block/nvme0n1/nvme0n1p3")
         .Dir("/sys/block/nvme0n1/nvme0n1p2")
         .File("/sys/block/dm-0/size", "2000000000")
         .File("/sys/block/dm-0/dm/name", "luks-abc")
         .File("/sys/block/dm-0/slaves/nvme0n1p3", "")
         .File("/sys/block/loop0/size", "100")
         .File("/proc/mounts", "composefs / overlay ro 0 0\n/dev/mapper/luks-abc /sysroot btrfs rw 0 0\n/dev/mapper/luks-abc /var btrfs rw 0 0\n/dev/nvme0n1p2 /boot ext4 rw 0 0\n/dev/loop0 /snap/x squashfs ro 0 0")
         .File("/proc/diskstats", "259 0 nvme0n1 100 0 2000 0 50 0 4000 0 0 1000 0\n7 0 loop0 1 0 1 0 0 0 0 0 0 1 0");
        f.Dir("/sys/module/drivetemp");
        f.Space["/sysroot"] = (199e9, 403e9);
        f.Space["/boot"] = (0.5e9, 1e9);
        return f;
    }

    [Fact]
    public void MacBook_cpu_uses_package_sensor_and_hottest_core()
    {
        using var f = MacBook();
        var cpu = f.Provider().Read().Single(r => r.Key == "cpu:0");
        Assert.Equal("Intel Core i9-9980HK", cpu.DeviceName);
        Assert.Equal(62, cpu.Temperature);
        Assert.Equal("Package", cpu.TemperatureSource);
        Assert.Contains("Hottest core 66 °C", cpu.Details);
        Assert.Contains("Clock 3.00 GHz", cpu.Details);
        Assert.Null(cpu.Usage); // no baseline yet on the first read
    }

    [Fact]
    public void Cpu_usage_comes_from_proc_stat_deltas()
    {
        using var f = MacBook();
        var p = f.Provider();
        p.Read();
        f.Stat(busy: 1000 + 250, idle: 9000 + 750); // 25 % busy since last read
        f.Now = f.Now.AddSeconds(1);
        Assert.Equal(25, p.Read().Single(r => r.Key == "cpu:0").Usage!.Value, 3);
    }

    [Fact]
    public void MacBook_amd_gpu_reads_edge_junction_busy_vram_power_clock()
    {
        using var f = MacBook();
        var gpus = f.Provider().Read().Where(r => r.Key.StartsWith("gpu:")).ToList();
        var gpu = Assert.Single(gpus); // connector "card1-eDP-1" is not a GPU
        Assert.Equal("GPU", gpu.Title);
        Assert.Equal("AMD Navi 14 [Radeon RX 5500/5500M / Pro 5500M]", gpu.DeviceName);
        Assert.Equal(60, gpu.Temperature);
        Assert.Equal("Core", gpu.TemperatureSource);
        Assert.Equal(20, gpu.Usage);
        Assert.Equal("VRAM 1.2 / 4.0 GB", gpu.SecondaryLabel);
        Assert.Contains("Hot spot 63 °C", gpu.Details);
        Assert.Contains("Power 7 W", gpu.Details);
        Assert.Contains("Clock 93 MHz", gpu.Details);
    }

    [Fact]
    public void MacBook_ram_without_dimm_sensors()
    {
        using var f = MacBook();
        var ram = f.Provider().Read().Single(r => r.Key == "ram");
        Assert.Null(ram.Temperature);
        Assert.Contains("no thermal sensor", ram.TemperatureUnavailableReason);
        Assert.Equal("64 GB system memory", ram.DeviceName); // 65536000 kB = 62.5 GiB usable on a 64 GB machine
        Assert.Equal(41.1, ram.Usage!.Value, 1);
        Assert.Contains("25.7 GB used · 36.8 GB available", ram.Details);
    }

    [Fact]
    public void MacBook_nvme_temperature_and_space_through_luks()
    {
        using var f = MacBook();
        var p = f.Provider();
        var disks = p.Read().Where(r => r.Key.StartsWith("disk:")).ToList();
        var nvme = Assert.Single(disks); // loop0 and dm-0 are not listed
        Assert.Equal("SSD", nvme.Title);
        Assert.Equal("APPLE SSD AP1024N", nvme.DeviceName);
        Assert.Equal(41, nvme.Temperature);
        Assert.Equal("NVMe", nvme.TemperatureSource);
        // btrfs on LUKS counted once (/sysroot; /var is the same filesystem), plus /boot on a partition
        Assert.Equal("Space 200 / 404 GB (/sysroot /boot)", nvme.SecondaryLabel);
    }

    [Fact]
    public void Tiny_filesystems_are_left_out_of_space()
    {
        using var f = MacBook();
        f.Space["/boot"] = (0.01e9, 0.1e9); // 100 MB: ignored
        var nvme = f.Provider().Read().Single(r => r.Key == "disk:nvme0n1");
        Assert.Equal("Space 199 / 403 GB (/sysroot)", nvme.SecondaryLabel);
    }

    [Fact]
    public void Disk_active_time_and_rates_from_diskstats()
    {
        using var f = MacBook();
        var p = f.Provider();
        p.Read();
        // +500 ms busy, +2048 sectors read (1 MiB), +0 written, over 1 s
        f.File("/proc/diskstats", "259 0 nvme0n1 100 0 4048 0 50 0 4000 0 0 1500 0");
        f.Now = f.Now.AddSeconds(1);
        var nvme = p.Read().Single(r => r.Key == "disk:nvme0n1");
        Assert.Equal(50, nvme.Usage!.Value, 3);
        Assert.Contains("Read 1.0 MB/s · Write 0 KB/s", nvme.Details);
    }

    /// <summary>The RTX 3060 desktop: NVIDIA through NVML, SATA HDD with drivetemp, a USB disk, an empty reader.</summary>
    private static FakeLinux Desktop()
    {
        var f = new FakeLinux();
        f.File("/usr/share/hwdata/pci.ids", PciIds.Replace("\r", ""))
         .File("/proc/cpuinfo", "model name\t: AMD Ryzen 7 5800X 8-Core Processor\n")
         .Hwmon("/sys/class/hwmon/hwmon1", "k10temp", (1, "Tctl", 55.5), (3, "Tccd1", 50.25))
         .Stat(100, 900)
         .File("/sys/class/drm/card0/device/vendor", "0x10de")
         .File("/sys/class/drm/card0/device/device", "0x2504")
         .File("/proc/meminfo", "MemTotal: 33554432 kB\nMemAvailable: 16777216 kB")
         .Hwmon("/sys/class/hwmon/hwmon4", "jc42", (1, null, 38))
         .Hwmon("/sys/class/hwmon/hwmon5", "jc42", (1, null, 41.5))
         // SATA HDD with drivetemp
         .File("/sys/block/sda/size", "7814037168").File("/sys/block/sda/dev", "8:0")
         .File("/sys/block/sda/queue/rotational", "1").File("/sys/block/sda/device/model", "ST4000DM004-2CV1")
         .Hwmon("/sys/block/sda/device/hwmon/hwmon6", "drivetemp", (1, null, 36))
         // USB SSD without SMART passthrough
         .File("/sys/block/sdb/size", "1000215216").File("/sys/block/sdb/dev", "8:16")
         .File("/sys/block/sdb/queue/rotational", "0").File("/sys/block/sdb/device/model", "Portable SSD")
         .File("/run/udev/data/b8:16", "S:disk/by-id/usb-x\nE:ID_BUS=usb\nE:ID_TYPE=disk")
         .Dir("/sys/block/sdb/sdb1")
         // Empty card reader slot
         .File("/sys/block/sdc/size", "0").File("/sys/block/sdc/dev", "8:32")
         .File("/proc/mounts", "/dev/sdb1 /run/media/venky/Portable\\040SSD exfat rw 0 0");
        f.Dir("/sys/module/drivetemp");
        f.Space["/run/media/venky/Portable SSD"] = (100e9, 500e9);
        return f;
    }

    [Fact]
    public void Desktop_amd_cpu_falls_back_to_tctl_with_ccd_detail()
    {
        using var f = Desktop();
        var cpu = f.Provider().Read().Single(r => r.Key == "cpu:0");
        Assert.Equal("AMD Ryzen 7 5800X 8-Core Processor", cpu.DeviceName);
        Assert.Equal(55.5, cpu.Temperature!.Value, 2);
        Assert.Equal("Tctl", cpu.TemperatureSource);
        Assert.Contains("Hottest core 50 °C", cpu.Details);
    }

    [Fact]
    public void Desktop_nvidia_from_nvml()
    {
        using var f = Desktop();
        f.Nvml.Gpus = new() { new NvGpu("NVIDIA GeForce RTX 3060", "00000000:01:00.0", 52, 35, 2.5 * 1073741824, 12.0 * 1073741824, 98.6f, 1777) };
        var gpu = f.Provider().Read().Single(r => r.Key.StartsWith("gpu:"));
        Assert.Equal("NVIDIA GeForce RTX 3060", gpu.DeviceName);
        Assert.Equal(52, gpu.Temperature);
        Assert.Equal(35, gpu.Usage);
        Assert.Equal("VRAM 2.5 / 12.0 GB", gpu.SecondaryLabel);
        Assert.Contains("Power 99 W", gpu.Details);
        Assert.Contains("Clock 1777 MHz", gpu.Details);
    }

    [Fact]
    public void Desktop_nvidia_without_driver_explains_why()
    {
        using var f = Desktop();
        var gpu = f.Provider().Read().Single(r => r.Key.StartsWith("gpu:"));
        Assert.Equal("NVIDIA GA106 [GeForce RTX 3060 Lite Hash Rate]", gpu.DeviceName); // from pci.ids
        Assert.Null(gpu.Temperature);
        Assert.Contains("no NVML", gpu.TemperatureUnavailableReason);
    }

    [Fact]
    public void Desktop_dimm_sensors_report_the_hottest()
    {
        using var f = Desktop();
        var ram = f.Provider().Read().Single(r => r.Key == "ram");
        Assert.Equal(41.5, ram.Temperature!.Value, 2);
        Assert.Equal("Hottest of 2 DIMMs", ram.TemperatureSource);
        Assert.Equal("32 GB system memory", ram.DeviceName);
        Assert.Equal(50, ram.Usage!.Value, 3);
    }

    [Fact]
    public void Desktop_sata_usb_and_empty_reader()
    {
        using var f = Desktop();
        var disks = f.Provider().Read().Where(r => r.Key.StartsWith("disk:")).ToDictionary(r => r.Key);
        Assert.Equal(new[] { "disk:sda", "disk:sdb" }, disks.Keys.OrderBy(k => k));     // sdc (size 0) skipped
        var hdd = disks["disk:sda"];
        Assert.Equal("HDD", hdd.Title);
        Assert.Equal(36, hdd.Temperature);
        Assert.Equal("SMART", hdd.TemperatureSource);
        var usb = disks["disk:sdb"];
        Assert.Equal("USB SSD", usb.Title);
        Assert.Null(usb.Temperature);
        Assert.Equal("USB enclosure doesn't pass SMART data through", usb.TemperatureUnavailableReason);
        Assert.Equal("Space 100 / 500 GB (/run/media/venky/Portable SSD)", usb.SecondaryLabel);
    }

    [Fact]
    public void Without_drivetemp_status_warns_and_disks_explain()
    {
        using var f = Desktop();
        Directory.Delete(Path.Combine(f.Root, "sys", "module", "drivetemp"));
        Directory.Delete(Path.Combine(f.Root, "sys", "block", "sda", "device", "hwmon"), true);
        var p = f.Provider();
        Assert.Contains("drivetemp", p.Status.Warning);
        Assert.False(p.Status.CanElevate);
        var hdd = p.Read().Single(r => r.Key == "disk:sda");
        Assert.Equal("Needs the drivetemp kernel module", hdd.TemperatureUnavailableReason);
    }

    [Fact]
    public void With_drivetemp_no_warning()
    {
        using var f = Desktop();
        var s = f.Provider().Status;
        Assert.Null(s.Warning);
        Assert.Contains("drivetemp", s.Summary);
    }

    [Fact]
    public void Intel_igpu_shares_cpu_temperature_and_missing_cpu_driver_is_explained()
    {
        using var f = new FakeLinux();
        f.File("/sys/class/drm/card0/device/vendor", "0x8086").File("/sys/class/drm/card0/device/device", "0x3e9b")
         .File("/proc/cpuinfo", "model name\t: Some CPU");
        var readings = f.Provider().Read();
        var gpu = readings.Single(r => r.Key.StartsWith("gpu:"));
        Assert.Equal("Graphics 8086:3e9b", gpu.DeviceName); // no pci.ids in this tree
        Assert.Equal("Integrated GPU: shares the CPU temperature", gpu.TemperatureUnavailableReason);
        Assert.Equal("No CPU sensor driver loaded (coretemp / k10temp)", readings.Single(r => r.Key == "cpu:0").TemperatureUnavailableReason);
    }

    [Fact]
    public void Two_gpus_are_numbered()
    {
        using var f = MacBook();
        f.File("/sys/class/drm/card0/device/vendor", "0x8086").File("/sys/class/drm/card0/device/device", "0x3e9b");
        var titles = f.Provider().Read().Where(r => r.Key.StartsWith("gpu:")).Select(r => (r.Title, r.DeviceName)).ToList();
        Assert.Equal(new[] { ("GPU 1", "Intel CoffeeLake-H GT2 [UHD Graphics 630]"), ("GPU 2", "AMD Navi 14 [Radeon RX 5500/5500M / Pro 5500M]") }, titles);
    }

    [Fact]
    public void Dump_and_diagnostics_list_raw_sensors()
    {
        using var f = MacBook();
        var p = f.Provider();
        var dump = p.DumpAll();
        Assert.Contains("[coretemp]", dump);
        Assert.Contains("Package id 0", dump);
        Assert.Contains("62.0 °C", dump);
        Assert.Contains("nvme0n1", dump);
        Assert.Contains("Module drivetemp  loaded", p.DiagnosticReport());
    }

    [Theory]
    [InlineData(62.5, 64)]
    [InlineData(32.0, 32)]
    [InlineData(31.3, 32)]
    [InlineData(15.5, 16)]
    [InlineData(7.6, 8)]
    [InlineData(23.4, 24)]
    [InlineData(3000.0, 3000)]
    public void Installed_memory_rounds_up_to_a_common_size(double usableGib, int expected) =>
        Assert.Equal(expected, LinuxSensorProvider.InstalledGb(usableGib));

    [Fact]
    public void Empty_system_does_not_throw()
    {
        using var f = new FakeLinux();
        var readings = f.Provider().Read();
        Assert.Contains(readings, r => r.Key == "cpu:0");
        Assert.Contains(readings, r => r.Key == "ram");
    }
}

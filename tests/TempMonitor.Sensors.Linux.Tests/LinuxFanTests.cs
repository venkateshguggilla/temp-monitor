using Xunit;

namespace TempMonitor.Tests;

public class LinuxFanTests
{
    private const string PciIds = "1002  Advanced Micro Devices, Inc. [AMD/ATI]\n\t7340  Navi 14 [Radeon RX 5500/5500M / Pro 5500M]\n" +
                                  "10de  NVIDIA Corporation\n\t2504  GA106 [GeForce RTX 3060 Lite Hash Rate]\n";

    /// <summary>A desktop: Nuvoton Super I/O (2 fans + 1 empty header) and an RTX 3060 with two fans.</summary>
    private static FakeLinux Desktop()
    {
        var f = new FakeLinux();
        f.File("/usr/share/hwdata/pci.ids", PciIds)
         .Hwmon("/sys/class/hwmon/hwmon2", "nct6775", (1, "SYSTIN", 34))
         .File("/sys/class/hwmon/hwmon2/fan1_input", "1180")
         .File("/sys/class/hwmon/hwmon2/pwm1", "128")
         .File("/sys/class/hwmon/hwmon2/pwm1_enable", "5")
         .File("/sys/class/hwmon/hwmon2/fan2_input", "0") // empty header
         .File("/sys/class/hwmon/hwmon2/pwm2", "77")
         .File("/sys/class/hwmon/hwmon2/pwm2_enable", "5")
         .File("/sys/class/hwmon/hwmon2/fan3_input", "850")
         .File("/sys/class/hwmon/hwmon2/pwm3", "255")
         .File("/sys/class/hwmon/hwmon2/pwm3_enable", "1")
         .File("/sys/class/drm/card0/device/vendor", "0x10de")
         .File("/sys/class/drm/card0/device/device", "0x2504");
        f.Nvml.Gpus = new()
        {
            new NvGpu("NVIDIA GeForce RTX 3060", "00000000:01:00.0", 52, 10, 1e9, 12e9, 30, 1200,
                new[] { new NvFan(0, 36, 1105, false), new NvFan(1, 36, 1098, false) }),
        };
        return f;
    }

    /// <summary>MacBook Pro 16 (2019): two SMC fans, and the Radeon's fan stopped while cool.</summary>
    private static FakeLinux MacBook()
    {
        var f = new FakeLinux();
        f.File("/usr/share/hwdata/pci.ids", PciIds)
         .Hwmon("/sys/class/hwmon/hwmon4", "applesmc", (1, "TC0P", 50))
         .File("/sys/class/hwmon/hwmon4/fan1_label", "Left side  ")
         .File("/sys/class/hwmon/hwmon4/fan1_input", "2160")
         .File("/sys/class/hwmon/hwmon4/fan1_min", "2160")
         .File("/sys/class/hwmon/hwmon4/fan1_max", "5927")
         .File("/sys/class/hwmon/hwmon4/fan1_manual", "0")
         .File("/sys/class/hwmon/hwmon4/fan2_label", "Right side ")
         .File("/sys/class/hwmon/hwmon4/fan2_input", "1996")
         .File("/sys/class/hwmon/hwmon4/fan2_min", "1996")
         .File("/sys/class/hwmon/hwmon4/fan2_max", "5489")
         .File("/sys/class/hwmon/hwmon4/fan2_manual", "1")
         .Hwmon("/sys/class/hwmon/hwmon3", "amdgpu", (1, "edge", 48))
         .File("/sys/class/hwmon/hwmon3/fan1_input", "0")
         .File("/sys/class/hwmon/hwmon3/pwm1", "0")
         .File("/sys/class/hwmon/hwmon3/pwm1_enable", "2")
         .File("/sys/class/hwmon/hwmon3/device/vendor", "0x1002")
         .File("/sys/class/hwmon/hwmon3/device/device", "0x7340");
        return f;
    }

    [Fact]
    public void Motherboard_fans_with_pwm_and_mode_and_empty_headers_hidden()
    {
        using var f = Desktop();
        var p = f.Provider();
        p.Read();
        var report = p.ReadFans();
        var board = report.Fans.Where(x => x.Device == "Motherboard (nct6775)").ToList();
        Assert.Equal(new[] { "Fan 1", "Fan 3" }, board.Select(x => x.Name));

        var fan1 = board[0];
        Assert.Equal(1180, fan1.Rpm);
        Assert.Equal(50.2f, fan1.Percent!.Value, 1);
        Assert.Equal(FanMode.Auto, fan1.Mode);
        Assert.True(fan1.HasControl);
        Assert.Equal("hwmon:nct6775@hwmon2/fan1", fan1.Key);

        Assert.Equal(FanMode.Manual, board[1].Mode);
        Assert.Equal(100, board[1].Percent);
        Assert.Equal("1 motherboard fan header reads 0 RPM (nothing connected) and is hidden.", report.Note);
    }

    [Fact]
    public void Nvidia_fans_come_from_nvml()
    {
        using var f = Desktop();
        var p = f.Provider();
        p.Read();
        var gpu = p.ReadFans().Fans.Where(x => x.Key.StartsWith("nvml:")).ToList();
        Assert.Equal(new[] { "GPU fan 1", "GPU fan 2" }, gpu.Select(x => x.Name));
        Assert.All(gpu, x => Assert.Equal("NVIDIA GeForce RTX 3060", x.Device));
        Assert.Equal(1105, gpu[0].Rpm);
        Assert.Equal(36, gpu[0].Percent);
        Assert.Equal(FanMode.Auto, gpu[0].Mode);
        Assert.Equal("nvml:00000000:01:00.0/0", gpu[0].Key);
    }

    [Fact]
    public void Nvidia_driver_without_fan_functions_just_has_no_gpu_fans()
    {
        using var f = Desktop();
        f.Nvml.Gpus = new() { new NvGpu("NVIDIA GeForce GTX 1050 Ti", "00000000:01:00.0", 40, 0, 1e9, 4e9, 10, 300) };
        var p = f.Provider();
        p.Read();
        Assert.DoesNotContain(p.ReadFans().Fans, x => x.Key.StartsWith("nvml:"));
    }

    [Fact]
    public void Apple_smc_fans_use_labels_rpm_range_and_manual_flag()
    {
        using var f = MacBook();
        var p = f.Provider();
        p.Read();
        var smc = p.ReadFans().Fans.Where(x => x.Device == "Apple SMC").ToList();
        Assert.Equal(new[] { "Left side", "Right side" }, smc.Select(x => x.Name));
        Assert.Equal(2160, smc[0].Rpm);
        Assert.Equal(2160, smc[0].MinRpm);
        Assert.Equal(5927, smc[0].MaxRpm);
        Assert.Null(smc[0].Percent);
        Assert.Equal(FanMode.Auto, smc[0].Mode);
        Assert.Equal(FanMode.Manual, smc[1].Mode);
        Assert.True(smc[0].HasControl);
    }

    [Fact]
    public void Gpu_fan_stopped_at_idle_is_still_shown()
    {
        using var f = MacBook();
        var p = f.Provider();
        p.Read();
        var report = p.ReadFans();
        var gpu = Assert.Single(report.Fans, x => x.Name == "GPU fan");
        Assert.Equal("AMD Navi 14 [Radeon RX 5500/5500M / Pro 5500M]", gpu.Device);
        Assert.Equal(0, gpu.Rpm);
        Assert.Equal(0, gpu.Percent);
        Assert.Equal(FanMode.Auto, gpu.Mode);
        Assert.Null(report.Note);
    }

    [Fact]
    public void No_fan_sensors_explains_why()
    {
        using var f = new FakeLinux();
        f.Hwmon("/sys/class/hwmon/hwmon0", "acpitz", (1, null, 40));
        var p = f.Provider();
        p.Read();
        var report = p.ReadFans();
        Assert.Empty(report.Fans);
        Assert.Contains("No fan sensors found", report.Note);
    }
}

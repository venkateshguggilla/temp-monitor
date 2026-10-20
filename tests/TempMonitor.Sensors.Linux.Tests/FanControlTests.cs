using TempMonitor.Fans;
using Xunit;

namespace TempMonitor.Tests;

public class FanControlTests
{
    private const string Hw = "/sys/class/hwmon/hwmon2";
    private const string Fan1 = "hwmon:nct6775@hwmon2/fan1";
    private const string Fan3 = "hwmon:nct6775@hwmon2/fan3";
    private const string GpuFan = "nvml:00000000:01:00.0/0";

    /// <summary>Desktop: CPU sensor, Nuvoton fans 1 (firmware "Smart Fan IV") and 3 (already manual), an NVIDIA GPU.</summary>
    private static FakeLinux Desktop(double cpu = 50)
    {
        var f = new FakeLinux();
        SetCpu(f, cpu);
        f.Hwmon(Hw, "nct6775", (1, "SYSTIN", 34))
         .File($"{Hw}/fan1_input", "1180").File($"{Hw}/pwm1", "128").File($"{Hw}/pwm1_enable", "5")
         .File($"{Hw}/fan3_input", "850").File($"{Hw}/pwm3", "255").File($"{Hw}/pwm3_enable", "1")
         .File("/sys/class/drm/card0/device/vendor", "0x10de")
         .File("/sys/class/drm/card0/device/device", "0x2504");
        f.Nvml.Gpus = new()
        {
            new NvGpu("NVIDIA GeForce RTX 3060", "00000000:01:00.0", 52, 10, 1e9, 12e9, 30, 1200,
                new[] { new NvFan(0, 36, 1105, false) }),
        };
        return f;
    }

    private static void SetCpu(FakeLinux f, double celsius) =>
        f.Hwmon("/sys/class/hwmon/hwmon1", "coretemp", (1, "Package id 0", celsius));

    private static string Read(FakeLinux f, string linuxPath) =>
        File.ReadAllText(Path.Combine(f.Root, linuxPath.TrimStart('/'))).Trim();

    private static string RestoreFile(FakeLinux f) => Path.Combine(f.Root, "run", "fan-restore.json");

    private static FanController Controller(FakeLinux f, FanConfig? config = null)
    {
        var configPath = Path.Combine(f.Root, "etc", "fans.json");
        config?.Save(configPath);
        return new FanController(f.Provider(), configPath, RestoreFile(f), () => f.Now);
    }

    private static FanConfig Custom(params (string key, FanSetting setting)[] fans)
    {
        var c = new FanConfig { Profile = FanConfig.CustomProfile };
        c.Safety.FloorPercent = 0;
        foreach (var (key, setting) in fans) c.Custom[key] = setting;
        return c;
    }

    private static FanSetting Fixed(float percent) => new() { Mode = FanControlMode.Fixed, Percent = percent };

    [Fact]
    public void Default_auto_profile_touches_nothing()
    {
        using var f = Desktop();
        var c = Controller(f);
        var status = c.Tick();
        Assert.Equal("Auto", status.Profile);
        Assert.Equal("5", Read(f, $"{Hw}/pwm1_enable"));
        Assert.Equal("128", Read(f, $"{Hw}/pwm1"));
        Assert.False(File.Exists(RestoreFile(f)));
        Assert.Empty(f.Nvml.FanCalls);
    }

    [Fact]
    public void Fixed_takes_the_fan_from_firmware_and_auto_hands_it_back()
    {
        using var f = Desktop();
        var c = Controller(f, Custom((Fan1, Fixed(60))));
        c.Tick();
        Assert.Equal("1", Read(f, $"{Hw}/pwm1_enable"));
        Assert.Equal("153", Read(f, $"{Hw}/pwm1")); // 60 % of 255
        Assert.Contains("\"Original\": \"5\"", File.ReadAllText(RestoreFile(f)));

        c.Config.Custom[Fan1] = new FanSetting(); // Auto
        c.Tick();
        Assert.Equal("5", Read(f, $"{Hw}/pwm1_enable")); // Smart Fan IV again, exactly as before
        Assert.False(File.Exists(RestoreFile(f)));
    }

    [Fact]
    public void Curve_follows_the_cpu_and_slows_down_only_after_hysteresis()
    {
        using var f = Desktop(cpu: 62);
        var curve = new FanSetting
        {
            Mode = FanControlMode.Curve,
            Sources = new() { "cpu:*" },
            Points = new() { new(40, 20), new(60, 40), new(80, 80) },
            Hysteresis = 3,
        };
        var c = Controller(f, Custom((Fan1, curve)));
        c.Tick();
        Assert.Equal("112", Read(f, $"{Hw}/pwm1")); // 44 %

        SetCpu(f, 70);
        c.Tick();
        Assert.Equal("153", Read(f, $"{Hw}/pwm1")); // 60 %, up at once

        SetCpu(f, 68);
        c.Tick();
        Assert.Equal("153", Read(f, $"{Hw}/pwm1")); // only 2 °C cooler: holds

        SetCpu(f, 66);
        c.Tick();
        Assert.Equal("133", Read(f, $"{Hw}/pwm1")); // 4 °C cooler: 52 %
    }

    [Fact]
    public void Floor_and_critical_temperature_override_the_setting()
    {
        using var f = Desktop(cpu: 50);
        var config = Custom((Fan1, Fixed(5)));
        config.Safety.FloorPercent = 20;
        var c = Controller(f, config);
        c.Tick();
        Assert.Equal("51", Read(f, $"{Hw}/pwm1")); // floor 20 %

        SetCpu(f, 96);
        var status = c.Tick();
        Assert.True(status.Critical);
        Assert.Equal("255", Read(f, $"{Hw}/pwm1"));
        Assert.Contains("critical", status.Note);
    }

    [Fact]
    public void Curve_without_its_temperature_runs_at_full_speed()
    {
        using var f = Desktop();
        var curve = new FanSetting { Mode = FanControlMode.Curve, Sources = new() { "disk:sdz" }, Points = new() { new(30, 20), new(60, 60) } };
        var c = Controller(f, Custom((Fan1, curve)));
        var fan = c.Tick().Fans.Single(x => x.Key == Fan1);
        Assert.Equal("255", Read(f, $"{Hw}/pwm1"));
        Assert.Contains("not available", fan.Error);
    }

    [Fact]
    public void After_a_crash_the_next_start_restores_firmware_control()
    {
        using var f = Desktop();
        var first = Controller(f, Custom((Fan1, Fixed(70)), (Fan3, Fixed(70))));
        first.Tick(); // ... and the process dies without ReleaseAll
        Assert.Equal("1", Read(f, $"{Hw}/pwm1_enable"));

        var second = Controller(f);
        Assert.Equal(2, second.RecoverFromCrash());
        Assert.Equal("5", Read(f, $"{Hw}/pwm1_enable"));
        Assert.Equal("2", Read(f, $"{Hw}/pwm3_enable")); // was manual before: handed to automatic, not left stuck
        Assert.False(File.Exists(RestoreFile(f)));
    }

    [Fact]
    public void A_driven_fan_at_zero_rpm_is_reported_as_stalled()
    {
        using var f = Desktop();
        var c = Controller(f, Custom((Fan1, Fixed(50))));
        c.Tick();
        f.File($"{Hw}/fan1_input", "0");
        Assert.False(c.Tick().Fans.Single(x => x.Key == Fan1).Stalled);
        f.Now = f.Now.AddSeconds(9);
        Assert.True(c.Tick().Fans.Single(x => x.Key == Fan1).Stalled);

        f.File($"{Hw}/fan1_input", "900");
        Assert.False(c.Tick().Fans.Single(x => x.Key == Fan1).Stalled);
    }

    [Fact]
    public void Nvidia_fan_is_set_through_nvml_and_given_back_to_the_driver()
    {
        using var f = Desktop();
        var c = Controller(f, Custom((GpuFan, Fixed(70))));
        c.Tick();
        Assert.Equal(("00000000:01:00.0", 0, (int?)70), f.Nvml.FanCalls.Last());
        c.ReleaseAll();
        Assert.Equal(("00000000:01:00.0", 0, (int?)null), f.Nvml.FanCalls.Last());
    }

    [Fact]
    public void Apple_smc_fans_are_set_in_rpm_within_their_range()
    {
        using var f = new FakeLinux();
        const string smc = "/sys/class/hwmon/hwmon4";
        f.Hwmon(smc, "applesmc", (1, "TC0P", 50))
         .File($"{smc}/fan1_label", "Left side").File($"{smc}/fan1_input", "2160")
         .File($"{smc}/fan1_min", "2160").File($"{smc}/fan1_max", "5927").File($"{smc}/fan1_manual", "0");
        SetCpu(f, 50);
        var c = Controller(f, Custom(("hwmon:applesmc@hwmon4/fan1", Fixed(50))));
        c.Tick();
        Assert.Equal("1", Read(f, $"{smc}/fan1_manual"));
        Assert.Equal("4044", Read(f, $"{smc}/fan1_output")); // halfway between 2160 and 5927
        c.ReleaseAll();
        Assert.Equal("0", Read(f, $"{smc}/fan1_manual"));
    }

    [Fact]
    public void Balanced_profile_drives_case_fans_from_cpu_and_gpu_fans_from_the_gpu()
    {
        using var f = Desktop(cpu: 55);
        var config = new FanConfig { Profile = "Balanced" };
        config.Safety.FloorPercent = 0;
        var c = Controller(f, config);
        c.Tick();
        Assert.Equal("115", Read(f, $"{Hw}/pwm1")); // hotter of CPU 55 / GPU 52 → 45 %
        Assert.Equal(42, f.Nvml.FanCalls.Last().Percent); // GPU 52 °C → 42 %
    }

    [Fact]
    public void Identify_spins_a_fan_up_for_a_few_seconds_then_hands_it_back()
    {
        using var f = Desktop();
        var c = Controller(f);
        c.Tick();
        c.Identify(Fan1, TimeSpan.FromSeconds(5));
        c.Tick();
        Assert.Equal("255", Read(f, $"{Hw}/pwm1"));
        f.Now = f.Now.AddSeconds(6);
        c.Tick();
        Assert.Equal("5", Read(f, $"{Hw}/pwm1_enable"));
    }

    [Fact]
    public void Interpolate_is_flat_outside_the_curve()
    {
        var points = new List<CurvePoint> { new(40, 30), new(80, 90) };
        Assert.Equal(30, FanController.Interpolate(points, 20));
        Assert.Equal(60, FanController.Interpolate(points, 60));
        Assert.Equal(90, FanController.Interpolate(points, 100));
    }

    // ---------------------------------------------------------------- Daemon requests

    private sealed class FakeAuthorizer : IAuthorizer
    {
        public List<string> Asked { get; } = new();
        public List<string> Changes { get; } = new();
        public bool Allow { get; set; } = true;

        public bool Check(string action, Peer peer, string change)
        {
            Asked.Add(action);
            Changes.Add(change);
            return Allow;
        }
    }

    [Fact]
    public void The_approval_prompt_says_what_will_change()
    {
        using var f = Desktop();
        var c = Controller(f);
        c.Tick();
        var auth = new FakeAuthorizer();
        var daemon = new FanDaemon(c, auth);
        daemon.Handle(new FanRequest { Cmd = "rename", Key = Fan1, Name = "CPU cooler" }, User);
        daemon.Handle(new FanRequest { Cmd = "set", Key = Fan1, Setting = Fixed(60) }, User);
        daemon.Handle(new FanRequest { Cmd = "profile", Name = "Silent" }, User);
        daemon.Handle(new FanRequest { Cmd = "identify", Key = GpuFan }, User);
        daemon.Handle(new FanRequest { Cmd = "safety", Safety = new SafetySettings { FloorPercent = 10, CriticalCpu = 95, CriticalGpu = 90 } }, User);
        Assert.Equal(new[]
        {
            "rename Fan 1 to \"CPU cooler\"",
            "set CPU cooler to a fixed 60%",
            "switch the fan profile to Silent",
            "run GPU fan at full speed for 5 seconds",
            "minimum fan speed 10%, all fans to full speed at CPU 95 °C or GPU 90 °C",
        }, auth.Changes);
        Assert.Equal(FanDaemon.SafetyAction, auth.Asked.Last()); // lowering the floor
    }

    private static readonly Peer User = new(1234, 1000);

    [Fact]
    public void Status_needs_no_approval_and_changes_need_the_control_action()
    {
        using var f = Desktop();
        var c = Controller(f);
        c.Tick();
        var auth = new FakeAuthorizer();
        var daemon = new FanDaemon(c, auth);

        Assert.True(daemon.Handle(new FanRequest { Cmd = "status" }, User).Ok);
        Assert.Empty(auth.Asked);

        var resp = daemon.Handle(new FanRequest { Cmd = "profile", Name = "Silent" }, User);
        Assert.True(resp.Ok);
        Assert.Equal("Silent", resp.Status!.Profile);
        Assert.Equal(new[] { FanDaemon.ControlAction }, auth.Asked);
    }

    [Fact]
    public void Lowering_a_safety_limit_needs_the_admin_action_raising_does_not()
    {
        using var f = Desktop();
        var c = Controller(f);
        c.Tick();
        var auth = new FakeAuthorizer();
        var daemon = new FanDaemon(c, auth);

        daemon.Handle(new FanRequest { Cmd = "safety", Safety = new SafetySettings { FloorPercent = 30 } }, User);
        Assert.Equal(FanDaemon.ControlAction, auth.Asked.Last());

        auth.Allow = false;
        var resp = daemon.Handle(new FanRequest { Cmd = "safety", Safety = new SafetySettings { FloorPercent = 10 } }, User);
        Assert.Equal(FanDaemon.SafetyAction, auth.Asked.Last());
        Assert.False(resp.Ok);
        Assert.Contains("administrator", resp.Error);
        Assert.Equal(30, c.Config.Safety.FloorPercent); // unchanged
    }

    [Fact]
    public void Setting_one_fan_switches_to_custom_and_keeps_the_others_as_they_were()
    {
        using var f = Desktop();
        var c = Controller(f, new FanConfig { Profile = "Balanced" });
        c.Tick();
        var daemon = new FanDaemon(c, new FakeAuthorizer());
        var resp = daemon.Handle(new FanRequest { Cmd = "set", Key = Fan1, Setting = Fixed(70) }, User);
        Assert.True(resp.Ok);
        Assert.Equal("Custom", c.Config.Profile);
        Assert.Equal(FanControlMode.Fixed, c.Config.Custom[Fan1].Mode);
        Assert.Equal(FanControlMode.Curve, c.Config.Custom[GpuFan].Mode); // still Balanced's curve

        var saved = FanConfig.Load(Path.Combine(f.Root, "etc", "fans.json"));
        Assert.Equal(70, saved.Custom[Fan1].Percent);
    }

    [Fact]
    public void Bad_requests_are_refused_with_a_reason()
    {
        using var f = Desktop();
        var c = Controller(f);
        c.Tick();
        var daemon = new FanDaemon(c, new FakeAuthorizer());
        Assert.Contains("No fan", daemon.Handle(new FanRequest { Cmd = "set", Key = "nope", Setting = Fixed(50) }, User).Error);
        Assert.Contains("2 points", daemon.Handle(new FanRequest { Cmd = "set", Key = Fan1, Setting = new FanSetting { Mode = FanControlMode.Curve } }, User).Error);
        Assert.Contains("Profiles", daemon.Handle(new FanRequest { Cmd = "profile", Name = "Turbo" }, User).Error);
        Assert.Contains("Unknown command", daemon.Handle(new FanRequest { Cmd = "format-disk" }, User).Error);
    }

    [Fact]
    public void Cli_parses_fan_numbers_curves_and_safety()
    {
        using var f = Desktop();
        var c = Controller(f);
        var status = c.Tick();

        var curve = FanCli.Parse("curve", new[] { "1", "40:30", "80:100", "--source", "cpu:*", "--hysteresis", "4" }, status);
        Assert.Equal(Fan1, curve.Key);
        Assert.Equal(new[] { new CurvePoint(40, 30), new CurvePoint(80, 100) }, curve.Setting!.Points);
        Assert.Equal(4, curve.Setting.Hysteresis);

        var safety = FanCli.Parse("safety", new[] { "floor=10", "gpu=85" }, status);
        Assert.Equal(10, safety.Safety!.FloorPercent);
        Assert.Equal(85, safety.Safety.CriticalGpu);
        Assert.Equal(95, safety.Safety.CriticalCpu);

        Assert.Equal("Performance", FanCli.Parse("profile", new[] { "performance" }, status).Name);
        Assert.Throws<FormatException>(() => FanCli.Parse("fixed", new[] { "9", "50" }, status));
    }
}

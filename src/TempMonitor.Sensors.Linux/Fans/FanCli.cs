using System.Globalization;

namespace TempMonitor.Fans;

/// <summary>"temp-monitor fan ...": fan control from a terminal, through the fan service.</summary>
public static class FanCli
{
    public const string Usage = """
        Usage: temp-monitor fan [command]
          status                          fans, speeds and settings (default)
          profile NAME                    Auto (firmware), Silent, Balanced, Performance or Custom
          auto FAN                        give FAN back to firmware
          fixed FAN PERCENT               run FAN at a fixed speed
          curve FAN T:P T:P ... [--source cpu:*,gpu:*] [--hysteresis 3]
                                          speed follows temperature, e.g. curve 1 40:30 60:50 80:100
          identify FAN                    spin FAN up for 5 seconds
          rename FAN NAME                 name a fan (an empty name resets it)
          safety [floor=N] [cpu=N] [gpu=N]  minimum speed %, critical CPU/GPU °C (all fans 100%)
        FAN is the number from "status" or the fan's key.
        """;

    public static int Run(string[] args, TextWriter output)
    {
        var cmd = args.Length > 0 ? args[0] : "status";
        if (cmd is "-h" or "--help" or "help")
        {
            output.WriteLine(Usage);
            return 0;
        }

        var status = FanClient.Send(new FanRequest { Cmd = "status" }, FanClient.SocketPath);
        if (!status.Ok || status.Status == null)
        {
            output.WriteLine(status.Error);
            return 1;
        }

        FanRequest req;
        try
        {
            req = Parse(cmd, args.Skip(1).ToArray(), status.Status);
        }
        catch (FormatException e)
        {
            output.WriteLine(e.Message);
            output.WriteLine(Usage);
            return 2;
        }

        var resp = req.Cmd == "status" ? status : FanClient.Send(req, FanClient.SocketPath);
        if (!resp.Ok || resp.Status == null)
        {
            output.WriteLine(resp.Error);
            return 1;
        }
        Print(resp.Status, output);
        return 0;
    }

    internal static FanRequest Parse(string cmd, string[] a, DaemonStatus status)
    {
        string Fan(int i) =>
            i >= a.Length ? throw new FormatException("Which fan? Give its number from 'temp-monitor fan status'.")
            : int.TryParse(a[i], out var n) && n >= 1 && n <= status.Fans.Count ? status.Fans[n - 1].Key
            : status.Fans.Any(f => f.Key == a[i]) ? a[i]
            : throw new FormatException($"No fan '{a[i]}'.");
        float Num(string s, string what) =>
            float.TryParse(s.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v
            : throw new FormatException($"{what}: '{s}' is not a number.");

        switch (cmd)
        {
            case "status":
                return new FanRequest { Cmd = "status" };
            case "profile":
                return new FanRequest { Cmd = "profile", Name = a.Length > 0 ? Canonical(a[0], status.Profiles) : throw new FormatException("Which profile?") };
            case "auto":
                return new FanRequest { Cmd = "set", Key = Fan(0), Setting = new FanSetting { Mode = FanControlMode.Auto } };
            case "fixed":
                if (a.Length < 2) throw new FormatException("fixed needs a fan and a percent.");
                return new FanRequest { Cmd = "set", Key = Fan(0), Setting = new FanSetting { Mode = FanControlMode.Fixed, Percent = Num(a[1], "Percent") } };
            case "curve":
                var setting = new FanSetting { Mode = FanControlMode.Curve };
                for (var i = 1; i < a.Length; i++)
                {
                    if (a[i] == "--source" && i + 1 < a.Length) setting.Sources = a[++i].Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
                    else if (a[i] == "--hysteresis" && i + 1 < a.Length) setting.Hysteresis = Num(a[++i], "Hysteresis");
                    else if (a[i].Split(':') is [var t, var p]) setting.Points.Add(new CurvePoint(Num(t, "Temperature"), Num(p, "Percent")));
                    else throw new FormatException($"Curve points look like 60:50 (°C:%), not '{a[i]}'.");
                }
                return new FanRequest { Cmd = "set", Key = Fan(0), Setting = setting };
            case "identify":
                return new FanRequest { Cmd = "identify", Key = Fan(0) };
            case "rename":
                return new FanRequest { Cmd = "rename", Key = Fan(0), Name = string.Join(' ', a.Skip(1)) };
            case "safety":
                var s = status.Safety.Clone();
                foreach (var kv in a)
                {
                    var parts = kv.Split('=', 2);
                    if (parts.Length != 2) throw new FormatException($"Use floor=N, cpu=N or gpu=N, not '{kv}'.");
                    var v = Num(parts[1], parts[0]);
                    switch (parts[0])
                    {
                        case "floor": s.FloorPercent = v; break;
                        case "cpu": s.CriticalCpu = v; break;
                        case "gpu": s.CriticalGpu = v; break;
                        default: throw new FormatException($"Unknown safety setting '{parts[0]}'.");
                    }
                }
                return new FanRequest { Cmd = "safety", Safety = s };
            default:
                throw new FormatException($"Unknown command '{cmd}'.");
        }
    }

    private static string Canonical(string name, IEnumerable<string> profiles) =>
        profiles.FirstOrDefault(p => p.Equals(name, StringComparison.OrdinalIgnoreCase))
        ?? throw new FormatException("Profiles: " + string.Join(", ", profiles));

    internal static void Print(DaemonStatus s, TextWriter o)
    {
        o.WriteLine($"Profile: {s.Profile}   ({string.Join(", ", s.Profiles)})");
        o.WriteLine($"Safety:  floor {s.Safety.FloorPercent:0}% · all fans 100% at CPU {s.Safety.CriticalCpu:0} °C or GPU {s.Safety.CriticalGpu:0} °C");
        o.WriteLine();
        for (var i = 0; i < s.Fans.Count; i++)
        {
            var f = s.Fans[i];
            var rpm = f.Rpm is { } r ? FanReading.FormatRpm(r) : "— RPM";
            var duty = f.Percent is { } p ? $"{p:0}%" : "";
            var mode = f.Mode switch
            {
                FanControlMode.Fixed => $"Fixed {f.Setting.Percent:0}%",
                FanControlMode.Curve => "Curve " + string.Join(" ", f.Setting.Points.Select(pt => $"{pt.Temp:0}:{pt.Percent:0}")) + $" ({string.Join(",", f.Setting.Sources)})",
                _ => f.HasControl ? "Auto (firmware)" : "Auto (firmware, not controllable)",
            };
            if (f.Target is { } t && f.Mode != FanControlMode.Auto) mode += $" → {t:0}%";
            o.WriteLine($"{i + 1,2}  {f.Name,-16} {f.Device,-30} {rpm,11} {duty,5}  {mode}");
            if (f.Stalled) o.WriteLine("    STALLED: reads 0 RPM while it should be spinning. Check the fan and its cable.");
            if (f.Error != null) o.WriteLine("    " + f.Error);
        }
        if (s.Fans.Count == 0) o.WriteLine("No fans found.");
        if (s.Note != null)
        {
            o.WriteLine();
            o.WriteLine(s.Note);
        }
    }
}

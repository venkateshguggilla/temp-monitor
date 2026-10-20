using Avalonia;

namespace TempMonitor.App;

internal static class Program
{
    public const string MinimizedArg = "--minimized";

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--print"))
            return PrintReadings();

        // One copy only: a second launch asks the first one to show its window, then exits.
        if (!Platform.Current.TryAcquireSingleInstance(waitForPrevious: args.Contains(Platform.RelaunchArg)))
        {
            Platform.Current.SignalFirstInstance();
            return 0;
        }

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>
    /// "TempMonitor --print": readings as text, no window. For terminals, SSH sessions and bug
    /// reports. Reads twice, one second apart, so usage figures have a baseline.
    /// </summary>
    private static int PrintReadings()
    {
        try
        {
            using var sensors = Platform.Current.CreateSensorProvider();
            sensors.Read();
            Thread.Sleep(1000);
            foreach (var r in sensors.Read())
            {
                var temp = r.Temperature is { } t ? $"{Reading.FormatTemp(t, false)} ({r.TemperatureSource})" : $"N/A ({r.TemperatureUnavailableReason})";
                var usage = r.Usage is { } u ? $"{r.UsageLabel} {u:0}%" : $"{r.UsageLabel} —";
                Console.WriteLine($"{r.Title,-8} {r.DeviceName}");
                Console.WriteLine($"         {temp} · {usage}" + (r.Secondary.HasValue ? $" · {r.SecondaryLabel}" : ""));
                if (r.Details.Count > 0)
                    Console.WriteLine("         " + string.Join(" · ", r.Details));
            }
            var fans = sensors.ReadFans();
            Console.WriteLine($"{"Fans",-8} " + (fans.Fans.Count == 0 ? "none" : $"{fans.Fans.Count}"));
            foreach (var f in fans.Fans)
            {
                var speed = f.Rpm is { } rpm ? FanReading.FormatRpm(rpm) : "— RPM";
                var extra = new[] { FanReading.ModeText(f.Mode), f.Percent is { } p ? $"{p:0}%" : "", f.HasControl ? "" : "read-only" }
                    .Where(s => s.Length > 0);
                Console.WriteLine($"         {f.Name} ({f.Device}): {speed}" + string.Concat(extra.Select(s => " · " + s)));
            }
            if (fans.Note != null) Console.WriteLine("         " + fans.Note);
            var status = sensors.Status;
            Console.WriteLine();
            Console.WriteLine(status.Summary);
            if (status.Warning != null) Console.WriteLine("Note: " + status.Warning);
            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("Could not read sensors: " + e);
            return 1;
        }
    }

    // Also used by the XAML previewer.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}

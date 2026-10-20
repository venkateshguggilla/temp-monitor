#if DEV_INSTANCE
using TempMonitor.History;

namespace TempMonitor.App;

/// <summary>
/// Development only: "TempMonitor --make-demo-history FILE" writes 30 days of made-up history (a
/// daily rhythm, two hot spells, a fan following the CPU) for working on the history window.
/// Open it with TEMPMONITOR_HISTORY_DB=FILE.
/// </summary>
internal static class DemoHistory
{
    public static int Make(string path)
    {
        if (File.Exists(path)) File.Delete(path);
        var rnd = new Random(42);
        var now = DateTime.UtcNow;
        var start = now.AddDays(-30);
        var hot1 = now.AddDays(-2).AddHours(-3);   // 20 minutes of a game that cooked the CPU
        var hot2 = now.AddMinutes(-35);            // a short hot spell in the last hour

        using var store = new HistoryStore(path);
        IEnumerable<(string, string, float)> Sample(DateTime t)
        {
            var day = Math.Sin((t.ToLocalTime().TimeOfDay.TotalHours - 9) / 24 * 2 * Math.PI); // warm afternoons
            var busy = Math.Clamp(25 + 35 * day + rnd.NextDouble() * 15, 2, 100);
            var cpu = 42 + busy * 0.35 + rnd.NextDouble() * 3;
            if (t >= hot1 && t < hot1.AddMinutes(20)) { busy = 96 + rnd.NextDouble() * 4; cpu = 96 + rnd.NextDouble() * 6; }
            if (t >= hot2 && t < hot2.AddMinutes(4)) { busy = 90 + rnd.NextDouble() * 10; cpu = 93 + rnd.NextDouble() * 5; }
            var gpu = 38 + busy * 0.3 + rnd.NextDouble() * 2;
            var fan = 900 + Math.Max(0, cpu - 45) * 45 + rnd.NextDouble() * 40;
            yield return ("cpu:0/temp", "CPU temperature · Intel Core i7-12700K", (float)cpu);
            yield return ("cpu:0/usage", "CPU usage · Intel Core i7-12700K", (float)busy);
            yield return ("gpu:card0/temp", "GPU temperature · NVIDIA GeForce RTX 3060", (float)gpu);
            yield return ("ram/usage", "RAM usage · 32 GB system memory", (float)(38 + day * 8 + rnd.NextDouble() * 2));
            yield return ("fan:hwmon:nct6775@nct6775.656/fan2/rpm", "CPU fan speed · Motherboard (nct6798)", (float)fan);
        }

        // Older than 2 hours: one sample a minute is enough (only the minute summaries are kept)
        var t = start;
        for (; t < now.AddHours(-2); t = t.AddMinutes(1)) store.Append(t, Sample(t));
        store.Flush();
        for (; t < now; t = t.AddSeconds(1))
        {
            store.Append(t, Sample(t));
            if (t.Second == 0 && t.Minute % 10 == 0) store.Flush();
        }
        store.Flush();
        store.Prune(now, 30);

        store.AddEvent(hot1.AddSeconds(30), "temperature", "cpu:0", "Warning", "CPU is running hot. Intel Core i7-12700K has been at 97 °C for over 30 s (hot from 95 °C).");
        store.AddEvent(hot1.AddMinutes(4), "temperature", "cpu:0", "Critical", "CPU is overheating. Intel Core i7-12700K is at 101 °C. Close heavy apps now.");
        store.AddEvent(hot1.AddMinutes(21), "temperature", "cpu:0", "None", "CPU back to normal. Intel Core i7-12700K: 84 °C");
        store.AddEvent(hot2.AddSeconds(40), "temperature", "cpu:0", "Warning", "CPU is running hot. Intel Core i7-12700K has been at 96 °C for over 30 s (hot from 95 °C).");
        store.AddEvent(hot2.AddMinutes(5), "temperature", "cpu:0", "None", "CPU back to normal. Intel Core i7-12700K: 70 °C");
        Console.WriteLine("demo history written to " + path);
        return 0;
    }
}
#endif

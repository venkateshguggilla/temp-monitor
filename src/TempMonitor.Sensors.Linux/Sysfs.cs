using System.Globalization;
using System.Text.RegularExpressions;

namespace TempMonitor;

/// <summary>
/// Reads sysfs/procfs files under a root folder ("/" on a real system, a fake tree in tests).
/// Every read is forgiving: a missing or unreadable file is just "no value".
/// </summary>
internal sealed class Sysfs
{
    public string Root { get; }

    public Sysfs(string root) => Root = root;

    /// <summary>Absolute path for a Linux path like "/sys/class/hwmon".</summary>
    public string P(string linuxPath) => Path.Combine(Root, linuxPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));

    public string? Text(string path)
    {
        try { return File.ReadAllText(path).Trim(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    public string[] Lines(string path)
    {
        try { return File.ReadAllLines(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return Array.Empty<string>(); }
    }

    public long? Long(string path) =>
        long.TryParse(Text(path), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    public bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    /// <summary>Writes a sysfs attribute (fan control). False, with the kernel's reason, if refused.</summary>
    public bool Write(string path, string value, out string? error)
    {
        try
        {
            File.WriteAllText(path, value);
            error = null;
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            error = e.Message;
            return false;
        }
    }

    public IEnumerable<string> Dirs(string path, string pattern = "*")
    {
        try { return Directory.Exists(path) ? Directory.EnumerateDirectories(path, pattern).OrderBy(d => d, NaturalComparer.Instance).ToList() : Enumerable.Empty<string>(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return Enumerable.Empty<string>(); }
    }

    /// <summary>Names of entries in a folder (files, folders or links), e.g. dm "slaves".</summary>
    public IEnumerable<string> Names(string path)
    {
        try { return Directory.Exists(path) ? Directory.EnumerateFileSystemEntries(path).Select(Path.GetFileName).OfType<string>().ToList() : Enumerable.Empty<string>(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return Enumerable.Empty<string>(); }
    }

    /// <summary>hwmon devices directly under <paramref name="deviceDir"/> (".../device/hwmon/hwmonN" or ".../device/hwmonN").</summary>
    public IEnumerable<Hwmon> HwmonsOf(string deviceDir) =>
        Dirs(Path.Combine(deviceDir, "hwmon"), "hwmon*").Concat(Dirs(deviceDir, "hwmon*")).Select(d => Hwmon.Read(this, d));

    /// <summary>All hwmon devices in /sys/class/hwmon.</summary>
    public IEnumerable<Hwmon> AllHwmons() => Dirs(P("/sys/class/hwmon"), "hwmon*").Select(d => Hwmon.Read(this, d));
}

/// <summary>One hwmon chip: its driver name and its temperature/fan/power/clock inputs.</summary>
internal sealed class Hwmon
{
    public required string Dir { get; init; }
    public required string Name { get; init; }
    /// <summary>Temperatures in °C by label (temp*_label, else "tempN").</summary>
    public List<(string Label, float Celsius)> Temps { get; } = new();
    /// <summary>Raw *_input values by file name, for "All sensors".</summary>
    public List<(string File, string Label, long Raw)> Inputs { get; } = new();

    private static readonly Regex InputFile = new(@"^(temp|fan|in|power|freq|curr|energy|pwm)(\d+)_(input|average)$|^pwm\d+$", RegexOptions.Compiled);

    public static Hwmon Read(Sysfs fs, string dir)
    {
        var h = new Hwmon { Dir = dir, Name = fs.Text(Path.Combine(dir, "name")) ?? Path.GetFileName(dir) };
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(dir).Select(Path.GetFileName).OfType<string>().OrderBy(f => f, NaturalComparer.Instance).ToList(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return h; }

        foreach (var f in files)
        {
            var m = InputFile.Match(f);
            if (!m.Success) continue;
            var raw = fs.Long(Path.Combine(dir, f));
            if (raw is null) continue;
            var underscore = f.IndexOf('_');
            var prefix = underscore > 0 ? f[..underscore] : f;
            var label = fs.Text(Path.Combine(dir, prefix + "_label")) ?? prefix;
            h.Inputs.Add((f, label, raw.Value));
            if (m.Groups[1].Value == "temp" && m.Groups[3].Value == "input")
            {
                var c = raw.Value / 1000f;
                if (c is > -40 and < 150) // ignore unset (-273) and bogus sensors
                    h.Temps.Add((label, c));
            }
        }
        return h;
    }

    public float? Temp(params string[] labels)
    {
        foreach (var l in labels)
            foreach (var t in Temps)
                if (t.Label.Equals(l, StringComparison.OrdinalIgnoreCase))
                    return t.Celsius;
        return null;
    }

    public long? Raw(string file) => Inputs.FirstOrDefault(i => i.File == file) is { File: not null } x ? x.Raw : null;
}

/// <summary>Sorts "hwmon10" after "hwmon9", "nvme1n1" after "nvme0n1".</summary>
internal sealed class NaturalComparer : IComparer<string>
{
    public static readonly NaturalComparer Instance = new();
    private static readonly Regex Chunks = new(@"\d+|\D+", RegexOptions.Compiled);

    public int Compare(string? a, string? b)
    {
        if (a == null || b == null) return string.CompareOrdinal(a, b);
        var ca = Chunks.Matches(a);
        var cb = Chunks.Matches(b);
        for (var i = 0; i < Math.Min(ca.Count, cb.Count); i++)
        {
            var x = ca[i].Value;
            var y = cb[i].Value;
            int r = char.IsDigit(x[0]) && char.IsDigit(y[0]) && x.Length < 18 && y.Length < 18
                ? long.Parse(x, CultureInfo.InvariantCulture).CompareTo(long.Parse(y, CultureInfo.InvariantCulture))
                : string.CompareOrdinal(x, y);
            if (r != 0) return r;
        }
        return ca.Count.CompareTo(cb.Count);
    }
}

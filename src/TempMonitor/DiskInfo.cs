using System.Diagnostics;
using System.IO;
using System.Management;

namespace TempMonitor;

/// <summary>
/// A physical disk as Windows sees it, with its Task Manager-style "active time" counter
/// and the drive letters on it. Used for usage figures and to match LHM's drive list.
/// </summary>
public sealed class DiskInfo : IDisposable
{
    public required string DeviceId { get; init; }
    public required string Model { get; init; }
    public bool? IsSsd { get; init; }
    public bool IsUsb { get; init; }
    public string Key => $"disk:{DeviceId}:{Model}";
    public string Letters { get; private set; } = "";

    private PerformanceCounter? _idle;
    private string[] _roots = Array.Empty<string>();

    public static List<DiskInfo> Enumerate()
    {
        var disks = new List<DiskInfo>();
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage",
                "SELECT DeviceId, FriendlyName, MediaType, BusType, Size FROM MSFT_PhysicalDisk");
            foreach (ManagementObject mo in searcher.Get())
            {
                // Skip empty card-reader slots, which report no capacity.
                if (Convert.ToUInt64(mo["Size"] ?? 0UL) == 0) continue;

                var mediaType = Convert.ToInt32(mo["MediaType"] ?? 0);
                disks.Add(new DiskInfo
                {
                    DeviceId = mo["DeviceId"]?.ToString() ?? "?",
                    Model = mo["FriendlyName"]?.ToString()?.Trim() ?? "Disk",
                    IsSsd = mediaType switch { 4 => true, 3 => false, _ => null },
                    IsUsb = Convert.ToInt32(mo["BusType"] ?? 0) == 7,
                });
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"MSFT_PhysicalDisk query failed: {ex.Message}");
        }

        disks.Sort((a, b) => string.CompareOrdinal(a.DeviceId, b.DeviceId));
        foreach (var d in disks) d.AttachCounter();
        return disks;
    }

    private void AttachCounter()
    {
        try
        {
            // PhysicalDisk instances are named "<disk number> <letters>", e.g. "0 C: D:".
            var instance = new PerformanceCounterCategory("PhysicalDisk").GetInstanceNames()
                .FirstOrDefault(n => n.Split(' ')[0] == DeviceId);
            if (instance == null) return;

            _idle = new PerformanceCounter("PhysicalDisk", "% Idle Time", instance, readOnly: true);
            _idle.NextValue(); // first sample is always 0
            var parts = instance.Split(' ').Skip(1).Where(p => p.EndsWith(':')).ToArray();
            _roots = parts.Select(p => p + "\\").ToArray();
            Letters = string.Join(" ", parts);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"PhysicalDisk counter for {DeviceId} failed: {ex.Message}");
        }
    }

    /// <summary>
    /// PhysicalDisk counter instance names, e.g. "0 C:", "1 E:". Cheap to read, and they
    /// change whenever a disk is attached, removed or gains a drive letter.
    /// </summary>
    public static string Signature()
    {
        try
        {
            var names = new PerformanceCounterCategory("PhysicalDisk").GetInstanceNames();
            Array.Sort(names, StringComparer.Ordinal);
            return string.Join("|", names);
        }
        catch
        {
            return "";
        }
    }

    /// <summary>100 − % idle time: the definition Task Manager uses for "Active time".</summary>
    public float? ActiveTime()
    {
        if (_idle == null) return null;
        try { return Math.Clamp(100f - _idle.NextValue(), 0f, 100f); }
        catch { return null; }
    }

    public (double used, double total)? SpaceUsed()
    {
        double total = 0, free = 0;
        foreach (var root in _roots)
        {
            try
            {
                var di = new DriveInfo(root);
                if (!di.IsReady) continue;
                total += di.TotalSize;
                free += di.TotalFreeSpace;
            }
            catch { /* drive vanished */ }
        }
        return total > 0 ? (total - free, total) : null;
    }

    /// <summary>Loose model-name match, since LHM and Windows format names differently.</summary>
    public static bool SameModel(string a, string b)
    {
        static string Norm(string s) => new(s.ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());
        var na = Norm(a);
        var nb = Norm(b);
        if (na.Length == 0 || nb.Length == 0) return false;
        if (na.Contains(nb) || nb.Contains(na)) return true;

        // Fall back to a shared model-number-like token, e.g. "AP1024N" or "ST4000DM004".
        static IEnumerable<string> Tokens(string s) =>
            s.ToUpperInvariant().Split(' ', '-', '_').Where(t => t.Length >= 5 && t.Any(char.IsDigit));
        var tb = Tokens(b).ToHashSet();
        return Tokens(a).Any(tb.Contains);
    }

    public void Dispose() => _idle?.Dispose();
}

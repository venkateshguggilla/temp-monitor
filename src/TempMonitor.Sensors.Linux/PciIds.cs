namespace TempMonitor;

/// <summary>
/// Looks up device names in the pci.ids database (hwdata package), e.g. 1002:7340 →
/// "Navi 14 [Radeon RX 5500/5500M / Pro 5500M]". Only the vendor blocks needed are read.
/// </summary>
internal sealed class PciIds
{
    private readonly string _path;
    private readonly Dictionary<string, string?> _cache = new();

    public PciIds(string path) => _path = path;

    /// <param name="vendor">Hex vendor id without 0x, e.g. "1002".</param>
    /// <param name="device">Hex device id without 0x, e.g. "7340".</param>
    public string? Name(string vendor, string device)
    {
        vendor = vendor.ToLowerInvariant();
        device = device.ToLowerInvariant();
        var key = vendor + ":" + device;
        if (_cache.TryGetValue(key, out var cached)) return cached;

        string? vendorName = null, deviceName = null;
        try
        {
            var inVendor = false;
            foreach (var line in File.ReadLines(_path))
            {
                if (line.Length == 0 || line[0] == '#') continue;
                if (line[0] != '\t')
                {
                    if (inVendor) break; // past our vendor block
                    if (line.StartsWith(vendor + "  ", StringComparison.Ordinal))
                    {
                        inVendor = true;
                        vendorName = line[6..].Trim();
                    }
                }
                else if (inVendor && line.Length > 1 && line[1] != '\t' && line.StartsWith("\t" + device + "  ", StringComparison.Ordinal))
                {
                    deviceName = line[7..].Trim();
                    break;
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }

        var name = deviceName == null ? null : $"{ShortVendor(vendorName)} {deviceName}".Trim();
        _cache[key] = name;
        return name;
    }

    private static string ShortVendor(string? v) => v switch
    {
        null => "",
        _ when v.Contains("AMD", StringComparison.Ordinal) || v.Contains("ATI", StringComparison.Ordinal) => "AMD",
        _ when v.StartsWith("NVIDIA", StringComparison.Ordinal) => "NVIDIA",
        _ when v.StartsWith("Intel", StringComparison.Ordinal) => "Intel",
        _ => v,
    };
}

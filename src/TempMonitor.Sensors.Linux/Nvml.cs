using System.Runtime.InteropServices;
using System.Text;

namespace TempMonitor;

/// <summary>One NVIDIA GPU as reported by NVML.</summary>
internal sealed record NvGpu(string Name, string PciBusId, float? Temperature, float? Usage,
    double? MemUsedBytes, double? MemTotalBytes, float? PowerWatts, float? ClockMHz,
    IReadOnlyList<NvFan>? Fans = null);

/// <summary>One fan of an NVIDIA GPU: target speed in %, measured RPM (driver 535+), manual or auto.</summary>
internal sealed record NvFan(int Index, float? Percent, float? Rpm, bool? Manual);

internal interface INvml
{
    /// <summary>Null when NVML isn't available (no NVIDIA driver), with a reason.</summary>
    IReadOnlyList<NvGpu>? Read(out string? unavailableReason);
}

/// <summary>
/// NVIDIA Management Library (libnvidia-ml.so.1, installed with the NVIDIA driver). Same
/// source nvidia-smi uses. Loaded on first use; if the driver isn't there, NVIDIA cards
/// show "N/A" with the reason instead.
/// </summary>
internal sealed class Nvml : INvml, IDisposable
{
    private const int Success = 0;
    private bool _initTried;
    private bool _ready;
    private string? _reason;

    public IReadOnlyList<NvGpu>? Read(out string? unavailableReason)
    {
        if (!_initTried)
        {
            _initTried = true;
            try
            {
                var r = Native.nvmlInit_v2();
                _ready = r == Success;
                if (!_ready) _reason = $"NVIDIA driver not responding (NVML error {r})";
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                _reason = "NVIDIA driver not installed (no NVML). RudramOS: install the driver from Graphics in Control Center";
            }
        }
        unavailableReason = _reason;
        if (!_ready) return null;

        var gpus = new List<NvGpu>();
        if (Native.nvmlDeviceGetCount_v2(out var count) != Success) return gpus;
        for (uint i = 0; i < count; i++)
        {
            if (Native.nvmlDeviceGetHandleByIndex_v2(i, out var dev) != Success) continue;
            var name = new StringBuilder(96);
            Native.nvmlDeviceGetName(dev, name, (uint)name.Capacity);
            Native.PciInfo pci = default;
            var busId = Native.nvmlDeviceGetPciInfo_v3(dev, ref pci) == Success ? pci.BusId : "";
            float? temp = Native.nvmlDeviceGetTemperature(dev, 0, out var t) == Success ? t : null;
            float? usage = Native.nvmlDeviceGetUtilizationRates(dev, out var u) == Success ? u.Gpu : null;
            double? used = null, total = null;
            if (Native.nvmlDeviceGetMemoryInfo(dev, out var mem) == Success)
            {
                used = mem.Used;
                total = mem.Total;
            }
            float? power = Native.nvmlDeviceGetPowerUsage(dev, out var mw) == Success ? mw / 1000f : null;
            float? clock = Native.nvmlDeviceGetClockInfo(dev, 0, out var mhz) == Success ? mhz : null;
            gpus.Add(new NvGpu(name.ToString(), busId, temp, usage, used, total, power, clock, ReadFans(dev)));
        }
        return gpus;
    }

    /// <summary>
    /// Fans of one GPU. Each call is optional: older drivers lack some functions and GPUs
    /// without fan control (laptops) answer "not supported", which just leaves the value empty.
    /// </summary>
    private static List<NvFan> ReadFans(IntPtr dev)
    {
        var fans = new List<NvFan>();
        try
        {
            if (Native.nvmlDeviceGetNumFans(dev, out var count) != Success) return fans;
            for (uint f = 0; f < count; f++)
            {
                float? pct = Native.nvmlDeviceGetFanSpeed_v2(dev, f, out var p) == Success ? p : null;
                float? rpm = null;
                bool? manual = null;
                try
                {
                    var info = new Native.FanSpeedInfo { Version = Native.FanSpeedInfoVersion, Fan = f };
                    if (Native.nvmlDeviceGetFanSpeedRPM(dev, ref info) == Success) rpm = info.Speed;
                }
                catch (EntryPointNotFoundException) { /* driver older than 535 */ }
                try
                {
                    if (Native.nvmlDeviceGetFanControlPolicy_v2(dev, f, out var policy) == Success) manual = policy == 1;
                }
                catch (EntryPointNotFoundException) { /* driver older than 510 */ }
                if (pct != null || rpm != null)
                    fans.Add(new NvFan((int)f, pct, rpm, manual));
            }
        }
        catch (EntryPointNotFoundException) { /* driver older than 470 */ }
        return fans;
    }

    public void Dispose()
    {
        if (_ready)
        {
            try { Native.nvmlShutdown(); } catch { /* best effort */ }
            _ready = false;
        }
    }

    private static class Native
    {
        private const string Lib = "libnvidia-ml.so.1";

        [StructLayout(LayoutKind.Sequential)] public struct Utilization { public uint Gpu; public uint Memory; }
        [StructLayout(LayoutKind.Sequential)] public struct Memory { public ulong Total; public ulong Free; public ulong Used; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        public struct PciInfo
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 16)] public string BusIdLegacy;
            public uint Domain, Bus, Device, PciDeviceId, PciSubSystemId;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string BusId;
        }

        [StructLayout(LayoutKind.Sequential)] public struct FanSpeedInfo { public uint Version; public uint Fan; public uint Speed; }
        public const uint FanSpeedInfoVersion = 12 | (1u << 24); // NVML_STRUCT_VERSION(FanSpeedInfo, 1)

        [DllImport(Lib)] public static extern int nvmlInit_v2();
        [DllImport(Lib)] public static extern int nvmlShutdown();
        [DllImport(Lib)] public static extern int nvmlDeviceGetCount_v2(out uint count);
        [DllImport(Lib)] public static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);
        [DllImport(Lib, CharSet = CharSet.Ansi)] public static extern int nvmlDeviceGetName(IntPtr device, StringBuilder name, uint length);
        [DllImport(Lib)] public static extern int nvmlDeviceGetPciInfo_v3(IntPtr device, ref PciInfo pci);
        [DllImport(Lib)] public static extern int nvmlDeviceGetTemperature(IntPtr device, int sensor, out uint temp);
        [DllImport(Lib)] public static extern int nvmlDeviceGetUtilizationRates(IntPtr device, out Utilization u);
        [DllImport(Lib)] public static extern int nvmlDeviceGetMemoryInfo(IntPtr device, out Memory mem);
        [DllImport(Lib)] public static extern int nvmlDeviceGetPowerUsage(IntPtr device, out uint milliwatts);
        [DllImport(Lib)] public static extern int nvmlDeviceGetClockInfo(IntPtr device, int clockType, out uint mhz);
        [DllImport(Lib)] public static extern int nvmlDeviceGetNumFans(IntPtr device, out uint count);
        [DllImport(Lib)] public static extern int nvmlDeviceGetFanSpeed_v2(IntPtr device, uint fan, out uint percent);
        [DllImport(Lib)] public static extern int nvmlDeviceGetFanSpeedRPM(IntPtr device, ref FanSpeedInfo info);
        [DllImport(Lib)] public static extern int nvmlDeviceGetFanControlPolicy_v2(IntPtr device, uint fan, out uint policy);
    }
}

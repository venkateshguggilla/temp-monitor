using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace TempMonitor;

/// <summary>
/// Reads a SATA drive's SMART temperature through a USB bridge using SCSI/ATA Translation
/// (ATA PASS-THROUGH), the same way CrystalDiskInfo reaches drives in USB enclosures.
/// Requires administrator rights.
/// </summary>
public static class SatSmart
{
    private const uint IOCTL_SCSI_PASS_THROUGH = 0x4D004;
    private const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3;

    // SCSI_PASS_THROUGH on x64: 56 bytes, then sense buffer, then data buffer.
    private const int SptLength = 56;
    private const int SenseOffset = SptLength;
    private const int SenseLength = 32;
    private const int DataOffset = 96;
    private const int DataLength = 512;

    // Attribute 194 only: 190 ("airflow") uses vendor-specific encodings that can mislead.
    private const byte SmartTemperature = 194;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sa,
        uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle h, uint code, byte[] inBuf, int inLen,
        byte[] outBuf, int outLen, out int returned, IntPtr overlapped);

    /// <summary>Temperature in °C, or null if the bridge or drive doesn't support it.</summary>
    public static float? ReadTemperature(string diskNumber)
    {
        using var h = CreateFile($@"\\.\PhysicalDrive{diskNumber}", GENERIC_READ | GENERIC_WRITE,
            FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h.IsInvalid) return null;

        // Bridges differ in which CDB they accept: try the 16-byte form, then the 12-byte one.
        var data = SmartReadData(h, Cdb16()) ?? SmartReadData(h, Cdb12());
        return data == null ? null : ParseTemperature(data);
    }

    // SMART READ DATA: command B0h, feature D0h, LBA mid/high 4Fh/C2h, PIO data-in, 1 sector.
    private static byte[] Cdb16() => new byte[]
    {
        0x85,       // ATA PASS-THROUGH (16)
        4 << 1,     // protocol 4 = PIO data-in
        0x0E,       // t_dir = from device, byt_blok = blocks, t_length = sector count field
        0, 0xD0,    // features
        0, 1,       // sector count
        0, 0,       // LBA low
        0, 0x4F,    // LBA mid
        0, 0xC2,    // LBA high
        0xA0,       // device
        0xB0,       // command: SMART
        0,
    };

    private static byte[] Cdb12() => new byte[]
    {
        0xA1,       // ATA PASS-THROUGH (12)
        4 << 1, 0x0E,
        0xD0,       // features
        1,          // sector count
        0,          // LBA low
        0x4F, 0xC2, // LBA mid, high
        0xA0, 0xB0, // device, command
        0, 0,
    };

    private static byte[]? SmartReadData(SafeFileHandle h, byte[] cdb)
    {
        var buf = new byte[DataOffset + DataLength];
        BitConverter.GetBytes((ushort)SptLength).CopyTo(buf, 0); // Length
        buf[6] = (byte)cdb.Length;                               // CdbLength
        buf[7] = SenseLength;                                    // SenseInfoLength
        buf[8] = 1;                                              // DataIn = SCSI_IOCTL_DATA_IN
        BitConverter.GetBytes(DataLength).CopyTo(buf, 12);       // DataTransferLength
        BitConverter.GetBytes(5).CopyTo(buf, 16);                // TimeOutValue (s)
        BitConverter.GetBytes((ulong)DataOffset).CopyTo(buf, 24); // DataBufferOffset
        BitConverter.GetBytes(SenseOffset).CopyTo(buf, 32);      // SenseInfoOffset
        cdb.CopyTo(buf, 36);                                     // Cdb[16]

        if (!DeviceIoControl(h, IOCTL_SCSI_PASS_THROUGH, buf, buf.Length, buf, buf.Length, out _, IntPtr.Zero))
            return null;
        if (buf[2] != 0) return null; // ScsiStatus: CHECK CONDITION etc.

        var data = buf.AsSpan(DataOffset, DataLength).ToArray();
        return data.Any(b => b != 0) ? data : null;
    }

    private static float? ParseTemperature(byte[] data)
    {
        // 30 attribute entries of 12 bytes from offset 2: id, flags(2), value, worst, raw[6], reserved.
        // The low raw byte is the current temperature; higher bytes hold min/max on many drives.
        for (var i = 0; i < 30; i++)
        {
            var o = 2 + i * 12;
            if (data[o] != SmartTemperature) continue;
            var raw0 = data[o + 5];
            return raw0 is > 0 and <= 120 ? raw0 : null;
        }
        return null;
    }
}

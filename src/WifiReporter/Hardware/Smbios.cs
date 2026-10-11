using System.Text;

namespace WifiReporter.Hardware;

/// <summary>One SMBIOS structure: formatted area plus its string set.</summary>
internal sealed class SmbiosStructure
{
    private readonly byte[] _data;
    private readonly int _offset;
    private readonly string[] _strings;

    public SmbiosStructure(byte[] data, int offset, string[] strings)
    {
        _data = data;
        _offset = offset;
        _strings = strings;
    }

    public byte Type => _data[_offset];
    public int Length => _data[_offset + 1];

    public byte? Byte(int field) => field < Length ? _data[_offset + field] : null;

    public ushort? Word(int field) =>
        field + 2 <= Length ? BitConverter.ToUInt16(_data, _offset + field) : null;

    public uint? DWord(int field) =>
        field + 4 <= Length ? BitConverter.ToUInt32(_data, _offset + field) : null;

    /// <summary>String referenced by the 1-based index stored at <paramref name="field"/>; null when absent.</summary>
    public string? String(int field)
    {
        var index = Byte(field) ?? 0;
        if (index == 0 || index > _strings.Length) return null;
        var s = _strings[index - 1].Trim();
        return s.Length == 0 ? null : s;
    }
}

/// <summary>Parser for the raw SMBIOS table returned by GetSystemFirmwareTable('RSMB').</summary>
internal static class Smbios
{
    public const byte TypeBios = 0;
    public const byte TypeSystem = 1;
    public const byte TypeBaseboard = 2;
    public const byte TypeMemoryDevice = 17;
    private const byte TypeEnd = 127;

    /// <summary>Parses RawSMBIOSData (8-byte header followed by the structure table).</summary>
    public static List<SmbiosStructure> Parse(byte[] raw)
    {
        var result = new List<SmbiosStructure>();
        if (raw.Length < 8) return result;

        var tableLength = (int)Math.Min(BitConverter.ToUInt32(raw, 4), (uint)(raw.Length - 8));
        var pos = 8;
        var end = 8 + tableLength;

        while (pos + 4 <= end)
        {
            var type = raw[pos];
            var length = raw[pos + 1];
            if (length < 4 || pos + length > end) break;

            // The string set follows the formatted area and ends with a double NUL.
            var strings = new List<string>();
            var p = pos + length;
            while (p < end)
            {
                var start = p;
                while (p < end && raw[p] != 0) p++;
                if (p == start) break; // empty string = end of set
                strings.Add(Encoding.ASCII.GetString(raw, start, p - start));
                p++;
            }

            // Skip the terminating NUL(s): one when the set had strings, two when it was empty.
            p += strings.Count == 0 ? 2 : 1;

            result.Add(new SmbiosStructure(raw, pos, strings.ToArray()));
            if (type == TypeEnd) break;
            pos = p;
        }

        return result;
    }

    /// <summary>Firmware placeholders that some vendors leave in the tables instead of real names.</summary>
    public static bool IsPlaceholder(string? s) =>
        string.IsNullOrWhiteSpace(s) ||
        s.Trim().ToLowerInvariant() is "to be filled by o.e.m." or "default string" or "system product name"
            or "system manufacturer" or "system version" or "not applicable" or "none" or "n/a" or "o.e.m."
            or "oem" or "base board product name" or "base board manufacturer" or "not specified" or "unknown";

    /// <summary>SMBIOS memory type (type 17, offset 0x12).</summary>
    public static string? MemoryTypeName(byte? type) => type switch
    {
        0x12 => "DDR",
        0x13 => "DDR2",
        0x18 => "DDR3",
        0x1A => "DDR4",
        0x1B => "LPDDR",
        0x1C => "LPDDR2",
        0x1D => "LPDDR3",
        0x1E => "LPDDR4",
        0x22 => "DDR5",
        0x23 => "LPDDR5",
        _ => null,
    };

    /// <summary>Size of a memory device in MB (type 17), or null when the slot is empty / unknown.</summary>
    public static long? MemoryDeviceSizeMb(SmbiosStructure s)
    {
        var size = s.Word(0x0C);
        if (size is null or 0 or 0xFFFF) return null;
        if (size == 0x7FFF) return s.DWord(0x1C) is { } ext ? ext & 0x7FFFFFFF : null;
        // Bit 15 set: the value is in KB.
        return (size & 0x8000) != 0 ? (size & 0x7FFF) / 1024 : size;
    }
}

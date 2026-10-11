using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using static WifiReporter.Hardware.HardwareNative;

namespace WifiReporter.Hardware;

/// <summary>
/// Reads the hardware inventory without WMI: CPU and OS from the registry, board/BIOS/RAM from SMBIOS,
/// GPUs through SetupAPI, disks through storage IOCTLs. Every part is optional; a failure only leaves it empty.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class SystemInfoReader
{
    public static SystemInventory Read()
    {
        var inv = new SystemInventory();
        inv = Try("CPU", inv, ReadCpu);
        inv = Try("SMBIOS", inv, ReadSmbios);
        inv = Try("RAM", inv, i => i.RamMb is null ? i with { RamMb = InstalledMemoryMb() } : i);
        inv = Try("GPU", inv, i => i with { Gpus = ReadGpus() });
        inv = Try("Wi-Fi", inv, i => i with { WifiAdapters = ReadWifiAdapters() });
        inv = Try("disks", inv, i => i with { Disks = ReadDisks() });
        inv = Try("OS", inv, ReadOs);
        return inv;
    }

    private static SystemInventory Try(string part, SystemInventory inv, Func<SystemInventory, SystemInventory> read)
    {
        try
        {
            return read(inv);
        }
        catch (Exception ex)
        {
            Log.Warn($"Reading system info ({part}) failed: {ex.Message}");
            return inv;
        }
    }

    // --- CPU ------------------------------------------------------------------------------------------------

    private static SystemInventory ReadCpu(SystemInventory inv)
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        var name = (key?.GetValue("ProcessorNameString") as string)?.Trim();
        // Some Intel names are padded with runs of spaces.
        if (name is not null) name = string.Join(' ', name.Split(' ', StringSplitOptions.RemoveEmptyEntries));

        var (cores, threads, performance, efficiency) = CountCores();
        return inv with
        {
            Cpu = name,
            CpuCores = cores,
            CpuThreads = threads,
            PerformanceCores = performance,
            EfficiencyCores = efficiency,
        };
    }

    /// <summary>Physical cores and logical processors; P/E core counts only on hybrid CPUs.</summary>
    private static (int? Cores, int? Threads, int? Performance, int? Efficiency) CountCores()
    {
        uint length = 0;
        GetLogicalProcessorInformationEx(RelationProcessorCore, IntPtr.Zero, ref length);
        if (length == 0) return (null, Environment.ProcessorCount, null, null);

        var buffer = Marshal.AllocHGlobal((int)length);
        try
        {
            if (!GetLogicalProcessorInformationEx(RelationProcessorCore, buffer, ref length))
                return (null, Environment.ProcessorCount, null, null);

            // SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX: Relationship(4) Size(4), then PROCESSOR_RELATIONSHIP:
            // Flags(1) EfficiencyClass(1) Reserved(20) GroupCount(2) GroupMask[] at +32 (Mask is pointer-sized).
            var classes = new List<byte>();
            var threads = 0;
            for (var offset = 0; offset < length;)
            {
                var entry = buffer + offset;
                var size = Marshal.ReadInt32(entry, 4);
                if (size <= 0) break;
                classes.Add(Marshal.ReadByte(entry, 9));
                threads += BitOperations.PopCount((ulong)Marshal.ReadInt64(entry, 32));
                offset += size;
            }

            if (classes.Count == 0) return (null, Environment.ProcessorCount, null, null);
            var max = classes.Max();
            var hybrid = classes.Distinct().Count() > 1;
            return (classes.Count, threads,
                hybrid ? classes.Count(c => c == max) : null,
                hybrid ? classes.Count(c => c != max) : null);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    // --- SMBIOS: board, BIOS, system model, memory modules --------------------------------------------------

    private static SystemInventory ReadSmbios(SystemInventory inv)
    {
        var size = GetSystemFirmwareTable(FirmwareTableRsmb, 0, null, 0);
        if (size == 0) return inv;
        var raw = new byte[size];
        if (GetSystemFirmwareTable(FirmwareTableRsmb, 0, raw, size) == 0) return inv;

        var structures = Smbios.Parse(raw);
        var bios = structures.FirstOrDefault(s => s.Type == Smbios.TypeBios);
        var system = structures.FirstOrDefault(s => s.Type == Smbios.TypeSystem);
        var board = structures.FirstOrDefault(s => s.Type == Smbios.TypeBaseboard);

        var boardName = Join(Clean(board?.String(4)), Clean(board?.String(5)));

        // Lenovo stores the machine type in "product name" and the readable model in "version".
        var systemVendor = Clean(system?.String(4));
        var systemProduct = Clean(system?.String(5));
        if (systemVendor?.StartsWith("LENOVO", StringComparison.OrdinalIgnoreCase) == true && Clean(system?.String(6)) is { } lenovo)
            systemProduct = lenovo;
        // Self-built desktops: the "system" is just the board's code ("MS-7E25"), the board name is more telling.
        var model = systemProduct is null || boardName?.Contains(systemProduct, StringComparison.OrdinalIgnoreCase) == true
            ? boardName
            : Join(systemVendor, systemProduct);

        var modules = new List<MemoryModule>();
        var slots = 0;
        string? ramType = null;
        int? speed = null, rated = null;
        foreach (var s in structures.Where(x => x.Type == Smbios.TypeMemoryDevice))
        {
            slots++;
            if (Smbios.MemoryDeviceSizeMb(s) is not { } mb) continue;
            modules.Add(new MemoryModule(Clean(s.String(0x10)), mb, Clean(s.String(0x17)), Clean(s.String(0x1A))));
            ramType ??= Smbios.MemoryTypeName(s.Byte(0x12));
            rated ??= Speed(s.Word(0x15), s.DWord(0x54));
            speed ??= Speed(s.Word(0x20), s.DWord(0x58));
        }

        return inv with
        {
            Board = boardName,
            BiosVendor = Clean(bios?.String(4)),
            BiosVersion = Clean(bios?.String(5)),
            BiosDate = Clean(bios?.String(8)),
            Model = model,
            RamMb = modules.Count > 0 ? modules.Sum(m => m.SizeMb) : null,
            RamType = ramType,
            RamSpeed = speed ?? rated,
            RamRatedSpeed = rated,
            RamSlots = slots > 0 ? slots : null,
            RamModules = modules,
        };
    }

    /// <summary>Speed in MT/s; 0xFFFF means "see the 32-bit extended field" (SMBIOS 3.3+).</summary>
    private static int? Speed(ushort? speed, uint? extended) => speed switch
    {
        null or 0 => null,
        0xFFFF => extended is > 0 ? (int)extended : null,
        _ => (int)speed,
    };

    private static long? InstalledMemoryMb() =>
        GetPhysicallyInstalledSystemMemory(out var kb) ? (long)(kb / 1024) : null;

    // --- GPUs -----------------------------------------------------------------------------------------------

    /// <summary>Present PCI display adapters (virtual displays like Parsec / Meta are skipped), largest VRAM first.</summary>
    private static List<GpuInfo> ReadGpus() =>
        ReadDrivers(DisplayClassGuid, new[] { @"pci\" },
                (name, key) => new GpuInfo(name, VramGb(key), key.GetValue("DriverVersion") as string))
            .OrderByDescending(g => g.VramGb ?? 0)
            .ToList();

    // --- Wi-Fi ----------------------------------------------------------------------------------------------

    /// <summary>
    /// Present Wi-Fi adapters (*IfType 71 = IEEE 802.11), connected or not. Only real hardware on PCI, USB or
    /// SDIO; the Wi-Fi Direct virtual adapters are software devices and drop out.
    /// </summary>
    private static List<WifiAdapterInfo> ReadWifiAdapters() =>
        ReadDrivers(NetClassGuid, new[] { @"pci\", @"usb\", @"sdio\" }, (name, key) =>
            key.GetValue("*IfType")?.ToString() == "71"
                ? new WifiAdapterInfo(name, key.GetValue("DriverVersion") as string,
                    DriverDate(key.GetValue("DriverDate") as string))
                : null);

    /// <summary>"1-27-2025" (M-D-YYYY, as drivers write it) → "2025-01-27".</summary>
    private static string? DriverDate(string? text) =>
        DateTime.TryParseExact(text, "M-d-yyyy", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var date)
            ? date.ToString("yyyy-MM-dd")
            : text;

    /// <summary>
    /// Runs <paramref name="select"/> on the driver registry key (DriverDesc, DriverVersion, …) of each present
    /// device of a setup class whose MatchingDeviceId starts with one of <paramref name="buses"/>; nulls are dropped.
    /// </summary>
    private static List<T> ReadDrivers<T>(Guid classGuid, string[] buses, Func<string, RegistryKey, T?> select)
        where T : class
    {
        var result = new List<T>();
        var set = SetupDiGetClassDevsW(ref classGuid, IntPtr.Zero, IntPtr.Zero, DigcfPresent);
        if (set == new IntPtr(-1)) return result;

        try
        {
            var info = new SP_DEVINFO_DATA { Size = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
            for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref info); i++)
            {
                var handle = SetupDiOpenDevRegKey(set, ref info, DicsFlagGlobal, 0, DiregDrv, KeyRead);
                if (handle == new IntPtr(-1)) continue;

                using var key = RegistryKey.FromHandle(new SafeRegistryHandle(handle, ownsHandle: true));
                var matching = key.GetValue("MatchingDeviceId") as string;
                if (matching is null || !buses.Any(b => matching.StartsWith(b, StringComparison.OrdinalIgnoreCase))) continue;
                if (key.GetValue("DriverDesc") is not string name || string.IsNullOrWhiteSpace(name)) continue;

                if (select(name.Trim(), key) is { } item) result.Add(item);
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }

        return result;
    }

    /// <summary>Dedicated video memory; drivers write it as QWORD, DWORD or 4-byte binary.</summary>
    private static double? VramGb(RegistryKey key)
    {
        long? bytes = key.GetValue("HardwareInformation.qwMemorySize") switch
        {
            long q => q,
            byte[] { Length: >= 8 } b => BitConverter.ToInt64(b, 0),
            _ => null,
        };
        bytes ??= key.GetValue("HardwareInformation.MemorySize") switch
        {
            int d => (uint)d,
            byte[] { Length: >= 4 } b => BitConverter.ToUInt32(b, 0),
            _ => null,
        };
        return bytes is > 0 ? Math.Round(bytes.Value / (1024.0 * 1024 * 1024), 1) : null;
    }

    // --- disks ----------------------------------------------------------------------------------------------

    private static readonly Dictionary<int, string> BusTypes = new()
    {
        [1] = "SCSI", [3] = "ATA", [8] = "RAID", [10] = "SAS", [11] = "SATA", [17] = "NVMe", [19] = "UFS",
    };

    // USB sticks/drives, SD/MMC cards, virtual disks (VHDX of WSL/Docker), Storage Spaces.
    private static readonly HashSet<int> SkippedBusTypes = new() { 7, 12, 13, 14, 15, 16 };

    private static List<DiskInfo> ReadDisks()
    {
        var result = new List<DiskInfo>();
        for (var n = 0; n < 32; n++)
        {
            // Access 0 is enough for these queries and does not need admin rights.
            using var handle = CreateFileW($@"\\.\PhysicalDrive{n}", 0, FileShareRead | FileShareWrite,
                IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (handle.IsInvalid) continue; // numbers can have gaps

            var descriptor = QueryProperty(handle, StorageDeviceProperty, 1024);
            if (descriptor is null || descriptor.Length < 36) continue;

            var busType = BitConverter.ToInt32(descriptor, 28);
            if (SkippedBusTypes.Contains(busType)) continue;

            var vendor = AsciiAt(descriptor, BitConverter.ToInt32(descriptor, 12));
            var product = AsciiAt(descriptor, BitConverter.ToInt32(descriptor, 16));
            var model = vendor is not null && product?.StartsWith(vendor, StringComparison.OrdinalIgnoreCase) != true
                ? Join(vendor, product)
                : product;
            if (model is null) continue;

            string? kind = busType == 17 ? "SSD" : null;
            if (kind is null && QueryProperty(handle, StorageDeviceSeekPenaltyProperty, 12) is { Length: >= 9 } penalty)
                kind = penalty[8] != 0 ? "HDD" : "SSD";

            result.Add(new DiskInfo(model, DiskSize(handle), BusTypes.GetValueOrDefault(busType), kind));
        }

        return result;
    }

    private static byte[]? QueryProperty(SafeFileHandle handle, int propertyId, int size)
    {
        var query = new STORAGE_PROPERTY_QUERY { PropertyId = propertyId };
        var buffer = new byte[size];
        return DeviceIoControl(handle, IoctlStorageQueryProperty, ref query, Marshal.SizeOf<STORAGE_PROPERTY_QUERY>(),
            buffer, buffer.Length, out var returned, IntPtr.Zero) && returned > 0
            ? buffer
            : null;
    }

    private static long? DiskSize(SafeFileHandle handle)
    {
        // DISK_GEOMETRY_EX: DISK_GEOMETRY (24 bytes), then DiskSize (8 bytes).
        var buffer = new byte[256];
        return DeviceIoControl(handle, IoctlDiskGetDriveGeometryEx, IntPtr.Zero, 0, buffer, buffer.Length, out var returned, IntPtr.Zero)
               && returned >= 32
            ? BitConverter.ToInt64(buffer, 24)
            : null;
    }

    private static string? AsciiAt(byte[] buffer, int offset)
    {
        if (offset <= 0 || offset >= buffer.Length) return null;
        var end = Array.IndexOf(buffer, (byte)0, offset);
        if (end < 0) end = buffer.Length;
        var s = Encoding.ASCII.GetString(buffer, offset, end - offset).Trim();
        return s.Length == 0 ? null : s;
    }

    // --- OS -------------------------------------------------------------------------------------------------

    private static SystemInventory ReadOs(SystemInventory inv)
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        if (key is null) return inv;

        var product = key.GetValue("ProductName") as string;
        var displayVersion = key.GetValue("DisplayVersion") as string;
        var buildText = key.GetValue("CurrentBuildNumber") as string;
        var ubr = key.GetValue("UBR") is int u ? u : (int?)null;

        // Windows 11 still says "Windows 10" in ProductName; build 22000+ is Windows 11.
        if (product is not null && int.TryParse(buildText, out var build) && build >= 22000)
            product = product.Replace("Windows 10", "Windows 11", StringComparison.Ordinal);

        var fullBuild = buildText is null ? null : ubr is null ? buildText : $"{buildText}.{ubr}";
        var os = product is null ? null : Join(product, displayVersion);
        if (os is not null && fullBuild is not null) os += $" ({fullBuild})";

        return inv with
        {
            Os = os,
            OsEdition = key.GetValue("EditionID") as string,
            OsVersion = displayVersion,
            OsBuild = fullBuild,
        };
    }

    // --- helpers --------------------------------------------------------------------------------------------

    private static string? Clean(string? s) => Smbios.IsPlaceholder(s) ? null : s!.Trim();

    private static string? Join(string? a, string? b) =>
        a is null ? b : b is null ? a : $"{a} {b}";
}

using System.Globalization;
using System.Text.Json.Nodes;

namespace WifiReporter.Hardware;

/// <summary>Source of hardware inventory and live system metrics (Windows implementation: <see cref="WindowsSystemMonitor"/>).</summary>
public interface ISystemMonitor
{
    /// <summary>Static hardware/OS inventory. Parts that cannot be read are left null/empty.</summary>
    SystemInventory ReadInventory();

    /// <summary>Live metrics; CPU load is averaged since the previous call.</summary>
    SystemStats ReadStats();

    /// <summary>Keys ("c", "d", …) of the fixed volumes that get a disk usage sensor.</summary>
    IReadOnlyList<string> VolumeKeys();
}

public sealed record MemoryModule(string? Slot, long SizeMb, string? Manufacturer, string? PartNumber);

public sealed record GpuInfo(string Name, double? VramGb, string? Driver);

public sealed record WifiAdapterInfo(string Name, string? Driver, string? DriverDate);

public sealed record DiskInfo(string Model, long? SizeBytes, string? Bus, string? Kind);

/// <summary>Hardware inventory; published retained so HA keeps showing it while the PC is off.</summary>
public sealed record SystemInventory
{
    public string? Cpu { get; init; }
    public int? CpuCores { get; init; }
    public int? CpuThreads { get; init; }
    public int? PerformanceCores { get; init; }
    public int? EfficiencyCores { get; init; }

    public string? Board { get; init; }
    public string? BiosVendor { get; init; }
    public string? BiosVersion { get; init; }
    public string? BiosDate { get; init; }

    /// <summary>Computer model ("Dell Latitude 5440"), or the board for self-built desktops.</summary>
    public string? Model { get; init; }

    public long? RamMb { get; init; }
    public string? RamType { get; init; }
    public int? RamSpeed { get; init; }
    public int? RamRatedSpeed { get; init; }
    public int? RamSlots { get; init; }
    public IReadOnlyList<MemoryModule> RamModules { get; init; } = Array.Empty<MemoryModule>();

    public IReadOnlyList<GpuInfo> Gpus { get; init; } = Array.Empty<GpuInfo>();
    public IReadOnlyList<WifiAdapterInfo> WifiAdapters { get; init; } = Array.Empty<WifiAdapterInfo>();
    public IReadOnlyList<DiskInfo> Disks { get; init; } = Array.Empty<DiskInfo>();

    public string? Os { get; init; }
    public string? OsEdition { get; init; }
    public string? OsVersion { get; init; }
    public string? OsBuild { get; init; }

    /// <summary>
    /// Each sensor gets a short state value ("cpu") plus a "<key>_info" object for its HA attributes.
    /// HA rejects states longer than 255 characters, so list values are cut there (full list in the attributes).
    /// </summary>
    public string ToJson() => new JsonObject
    {
        ["cpu"] = Limit(Cpu),
        ["cpu_info"] = new JsonObject
        {
            ["cores"] = CpuCores,
            ["threads"] = CpuThreads,
            ["performance_cores"] = PerformanceCores,
            ["efficiency_cores"] = EfficiencyCores,
        },
        ["board"] = Limit(Board),
        ["board_info"] = new JsonObject
        {
            ["bios_vendor"] = BiosVendor,
            ["bios_version"] = BiosVersion,
            ["bios_date"] = BiosDate,
        },
        ["model"] = Limit(Model),
        ["ram"] = RamMb is { } mb ? Limit($"{FormatNumber(mb / 1024.0)} GB {RamType}".Trim()) : null,
        ["ram_info"] = new JsonObject
        {
            ["total_gb"] = RamMb is { } total ? Math.Round(total / 1024.0, 1) : null,
            ["type"] = RamType,
            ["speed_mts"] = RamSpeed,
            ["rated_speed_mts"] = RamRatedSpeed,
            ["slots"] = RamSlots,
            ["modules"] = new JsonArray(RamModules.Select(m => (JsonNode)new JsonObject
            {
                ["slot"] = m.Slot,
                ["size_gb"] = Math.Round(m.SizeMb / 1024.0, 1),
                ["manufacturer"] = m.Manufacturer,
                ["part_number"] = m.PartNumber,
            }).ToArray()),
        },
        ["gpu"] = Gpus.Count == 0 ? null : Limit(string.Join(", ", Gpus.Select(g => g.Name))),
        ["gpu_info"] = new JsonObject
        {
            ["gpus"] = new JsonArray(Gpus.Select(g => (JsonNode)new JsonObject
            {
                ["name"] = g.Name,
                ["vram_gb"] = g.VramGb,
                ["driver"] = g.Driver,
            }).ToArray()),
        },
        ["wifi"] = WifiAdapters.Count == 0 ? null : Limit(string.Join(", ", WifiAdapters.Select(w => w.Name))),
        ["wifi_info"] = new JsonObject
        {
            ["adapters"] = new JsonArray(WifiAdapters.Select(w => (JsonNode)new JsonObject
            {
                ["name"] = w.Name,
                ["driver"] = w.Driver,
                ["driver_date"] = w.DriverDate,
            }).ToArray()),
        },
        ["disks"] = Disks.Count == 0 ? null : Limit(string.Join(", ", Disks.Select(d =>
            d.SizeBytes is { } size ? $"{d.Model} ({FormatDiskSize(size)})" : d.Model))),
        ["disks_info"] = new JsonObject
        {
            ["disks"] = new JsonArray(Disks.Select(d => (JsonNode)new JsonObject
            {
                ["model"] = d.Model,
                ["size"] = d.SizeBytes is { } size ? FormatDiskSize(size) : null,
                ["size_gb"] = d.SizeBytes is { } bytes ? Math.Round(bytes / 1e9, 1) : null,
                ["bus"] = d.Bus,
                ["type"] = d.Kind,
            }).ToArray()),
        },
        ["os"] = Limit(Os),
        ["os_info"] = new JsonObject
        {
            ["edition"] = OsEdition,
            ["version"] = OsVersion,
            ["build"] = OsBuild,
        },
    }.ToJsonString();

    private static string? Limit(string? s) => s is { Length: > 255 } ? s[..254] + "…" : s;

    private static string FormatNumber(double value) => value.ToString("0.#", CultureInfo.InvariantCulture);

    /// <summary>Decimal units like on the box: 2000398934016 bytes → "2 TB", 512110190592 → "512 GB".</summary>
    internal static string FormatDiskSize(long bytes) =>
        bytes >= 999_500_000_000
            ? $"{FormatNumber(Math.Round(bytes / 1e12, 1))} TB"
            : $"{Math.Round(bytes / 1e9):0} GB";
}

public sealed record VolumeStats(string Name, string? Label, string? Format, double UsedPercent, double FreeGb, double TotalGb);

/// <summary>Live metrics, published with the Wi-Fi state; unavailable in HA while the PC is off.</summary>
public sealed record SystemStats
{
    public double? CpuLoad { get; init; }
    public double? RamUsedPercent { get; init; }
    public double? RamUsedGb { get; init; }
    public double? RamTotalGb { get; init; }
    public DateTimeOffset? BootTime { get; init; }
    public IReadOnlyDictionary<string, VolumeStats> Volumes { get; init; } = new Dictionary<string, VolumeStats>();

    public string ToJson()
    {
        var volumes = new JsonObject();
        foreach (var (key, v) in Volumes)
        {
            volumes[key] = new JsonObject
            {
                ["used"] = v.UsedPercent,
                ["free_gb"] = v.FreeGb,
                ["total_gb"] = v.TotalGb,
                ["label"] = v.Label,
                ["format"] = v.Format,
            };
        }

        return new JsonObject
        {
            ["cpu_load"] = CpuLoad,
            ["ram_used"] = RamUsedPercent,
            ["ram_used_gb"] = RamUsedGb,
            ["ram_total_gb"] = RamTotalGb,
            ["boot_time"] = BootTime?.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture),
            ["volumes"] = volumes,
        }.ToJsonString();
    }
}

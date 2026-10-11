using System.Runtime.Versioning;
using static WifiReporter.Hardware.HardwareNative;

namespace WifiReporter.Hardware;

/// <summary>Inventory via <see cref="SystemInfoReader"/>, live metrics via kernel32 and <see cref="DriveInfo"/>.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsSystemMonitor : ISystemMonitor
{
    private readonly DateTimeOffset _bootTime;
    private (long Idle, long Total)? _lastCpu;

    public WindowsSystemMonitor()
    {
        // Computed once so the HA timestamp does not jitter between reports. With Fast Startup this is the
        // last full start (restart), not the last power-on; Task Manager's uptime counts the same way.
        var boot = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);
        _bootTime = boot.AddTicks(-(boot.Ticks % TimeSpan.TicksPerSecond));
        _lastCpu = CpuTimes();
    }

    public SystemInventory ReadInventory() => SystemInfoReader.Read();

    public SystemStats ReadStats()
    {
        double? cpuLoad = null;
        var cpu = CpuTimes();
        if (cpu is { } current && _lastCpu is { } last && current.Total > last.Total)
        {
            var busy = 1.0 - (double)(current.Idle - last.Idle) / (current.Total - last.Total);
            cpuLoad = Math.Round(Math.Clamp(busy, 0, 1) * 100, 1);
        }
        _lastCpu = cpu;

        double? ramUsed = null, ramUsedGb = null, ramTotalGb = null;
        var mem = new MEMORYSTATUSEX { Length = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (GlobalMemoryStatusEx(ref mem) && mem.TotalPhys > 0)
        {
            var used = mem.TotalPhys - mem.AvailPhys;
            ramUsed = Math.Round(100.0 * used / mem.TotalPhys, 1);
            ramUsedGb = Math.Round(used / GiB, 1);
            ramTotalGb = Math.Round(mem.TotalPhys / GiB, 1);
        }

        var volumes = new Dictionary<string, VolumeStats>();
        foreach (var drive in FixedDrives())
        {
            try
            {
                var total = drive.TotalSize;
                if (total <= 0) continue;
                var free = drive.AvailableFreeSpace;
                volumes[Key(drive)] = new VolumeStats(
                    drive.Name.TrimEnd('\\'),
                    string.IsNullOrWhiteSpace(drive.VolumeLabel) ? null : drive.VolumeLabel,
                    drive.DriveFormat,
                    Math.Round(100.0 * (total - free) / total, 1),
                    Math.Round(free / GiB, 1),
                    Math.Round(total / GiB, 1));
            }
            catch (IOException)
            {
                // drive went away between enumeration and query
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return new SystemStats
        {
            CpuLoad = cpuLoad,
            RamUsedPercent = ramUsed,
            RamUsedGb = ramUsedGb,
            RamTotalGb = ramTotalGb,
            BootTime = _bootTime,
            Volumes = volumes,
        };
    }

    public IReadOnlyList<string> VolumeKeys() => FixedDrives().Select(Key).ToList();

    private const double GiB = 1024.0 * 1024 * 1024;

    private static IEnumerable<DriveInfo> FixedDrives() =>
        DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady);

    private static string Key(DriveInfo drive) => drive.Name[..1].ToLowerInvariant();

    private static (long Idle, long Total)? CpuTimes() =>
        GetSystemTimes(out var idle, out var kernel, out var user) ? (idle, kernel + user) : null;
}

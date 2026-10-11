using System.Runtime.InteropServices;
using WifiReporter;
using WifiReporter.Hardware;
using WifiReporter.Service;
using WifiReporter.Wlan;

const string Usage = """
    Wi-Fi Reporter - reports Wi-Fi quality and system info of this PC to Home Assistant over MQTT.

    Usage: WifiReporter.exe <command> [--config <path>]

      console          Run in this terminal with live log output (Ctrl+C to stop)
      once             Read Wi-Fi state and system info once and print it (no MQTT)
      demo             Publish fake data to MQTT (tests the Home Assistant side)
      install          Install and start the Windows service        (admin)
      uninstall        Stop and remove the Windows service          (admin)
      install-task     Run at logon in your user session instead     (admin)
      uninstall-task   Remove the logon task                         (admin)
      run              Run headless in the foreground (used by the logon task)

    Without a command the app expects to be started by the Service Control Manager.
    Config: %ProgramData%\WifiReporter\config.json (created on first run)
    Logs:   %ProgramData%\WifiReporter\logs
    """;

var command = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal))?.ToLowerInvariant();
var configArg = ArgValue(args, "--config");
var isWindows = OperatingSystem.IsWindows();

if (command is null && isWindows)
{
    // Started by the SCM? Then this call blocks until the service is stopped.
    if (ServiceHost.TryRun(ct => RunReporterAsync(configArg, demo: false, ct)))
        return 0;

    AttachConsole();
    Console.WriteLine(Usage);
    return 1;
}

switch (command)
{
    case "console" or "--console":
        AttachConsole();
        Log.ToConsole = true;
        return await RunInteractiveAsync(demo: false);

    case "demo":
        AttachConsole();
        Log.ToConsole = true;
        return await RunInteractiveAsync(demo: true);

    case "run":
        await RunReporterAsync(configArg, demo: false, CancellationToken.None);
        return 0;

    case "once":
        AttachConsole();
        if (!isWindows) { Console.WriteLine("Wi-Fi reading is only supported on Windows."); return 1; }
        using (var reader = new WlanReader())
        {
            var state = reader.Read();
            Console.WriteLine(state is null ? "Wi-Fi not connected" : Reporter.ToJson(state));
            if (state?.Restricted == true)
                Console.WriteLine("Windows hides SSID/BSSID/rates: allow location access for desktop apps (see README).");
        }
        {
            var system = new WindowsSystemMonitor();
            Console.WriteLine(system.ReadInventory().ToJson());
            await Task.Delay(1000); // CPU load is measured over an interval
            Console.WriteLine(system.ReadStats().ToJson());
        }
        return 0;

    case "install" when isWindows:
        AttachConsole();
        EnsureConfig(configArg);
        return Installer.InstallService(configArg);

    case "uninstall" when isWindows:
        AttachConsole();
        return Installer.UninstallService();

    case "install-task" when isWindows:
        AttachConsole();
        EnsureConfig(configArg);
        return Installer.InstallTask(configArg);

    case "uninstall-task" when isWindows:
        AttachConsole();
        return Installer.UninstallTask();

    default:
        AttachConsole();
        Console.WriteLine(Usage);
        return 1;
}

async Task<int> RunInteractiveAsync(bool demo)
{
    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
    await RunReporterAsync(configArg, demo, cts.Token);
    return 0;
}

static async Task RunReporterAsync(string? configPath, bool demo, CancellationToken ct)
{
    var (cfg, path) = ReporterConfig.Load(configPath);
    Log.Info($"Config: {path}");

    if (demo || !OperatingSystem.IsWindows())
    {
        if (!demo) Log.Warn("Not running on Windows: publishing demo data instead of real Wi-Fi state");
        // The system part is real data on Windows, and left out elsewhere.
        await new Reporter(cfg, DemoState, OperatingSystem.IsWindows() ? new WindowsSystemMonitor() : null).RunAsync(ct);
        return;
    }

    using var reader = new WlanReader();
    await new Reporter(cfg, reader.Read, new WindowsSystemMonitor()).RunAsync(ct);
}

static WifiState DemoState() => new()
{
    Adapter = "Demo adapter",
    Ssid = "HomeWiFi",
    Bssid = "aa:bb:cc:dd:00:84",
    Phy = "Wi-Fi 6 (ax)",
    SignalQuality = Random.Shared.Next(60, 95),
    Rssi = Random.Shared.Next(-70, -50),
    RxRate = 1201,
    TxRate = 865,
    Channel = 100,
    Band = "5 GHz",
};

static void EnsureConfig(string? configPath)
{
    var (_, path) = ReporterConfig.Load(configPath);
    Console.WriteLine($"Config: {path}  (edit MQTT username/password there, then restart)");
}

static string? ArgValue(string[] args, string name)
{
    var i = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

static void AttachConsole()
{
    if (!OperatingSystem.IsWindows()) return;
    // Output redirected to a pipe or file (e.g. `once > out.txt`): keep it, attaching would steal it.
    if (Console.IsOutputRedirected) return;
    // WinExe has no console; reuse the terminal we were started from, or open a new one.
    if (!NativeConsole.AttachConsole(NativeConsole.AttachParentProcess))
        NativeConsole.AllocConsole();
}

internal static class NativeConsole
{
    public const int AttachParentProcess = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool AllocConsole();
}

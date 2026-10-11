using System.Text.Json;
using System.Text.Json.Serialization;

namespace WifiReporter;

public sealed class ReporterConfig
{
    public string MqttHost { get; set; } = "homeassistant.local";
    public int MqttPort { get; set; } = 1883;
    public string MqttUsername { get; set; } = "";
    public string MqttPassword { get; set; } = "";

    /// <summary>Name shown in Home Assistant. Empty = computer name.</summary>
    public string DeviceName { get; set; } = "";

    public string DiscoveryPrefix { get; set; } = "homeassistant";

    /// <summary>How often the Wi-Fi state is read and published.</summary>
    public int IntervalSeconds { get; set; } = 60;

    public bool IperfEnabled { get; set; } = true;

    /// <summary>iperf3.exe on PATH, or a full path.</summary>
    public string IperfPath { get; set; } = "iperf3.exe";

    public string IperfServer { get; set; } = "homeassistant.local";
    public int IperfIntervalSeconds { get; set; } = 1800;
    public int IperfDurationSeconds { get; set; } = 5;

    [JsonIgnore]
    public string EffectiveDeviceName =>
        string.IsNullOrWhiteSpace(DeviceName) ? Environment.MachineName : DeviceName.Trim();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
    };

    public static string DataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WifiReporter");

    public static string DefaultConfigPath => Path.Combine(DataDirectory, "config.json");

    /// <summary>
    /// Loads the config. Lookup order: explicit path, %ProgramData%\WifiReporter\config.json,
    /// config.json next to the exe. If none exists, the example config is copied to %ProgramData%.
    /// </summary>
    public static (ReporterConfig Config, string Path) Load(string? explicitPath)
    {
        string path;
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            path = System.IO.Path.GetFullPath(explicitPath);
        }
        else if (File.Exists(DefaultConfigPath))
        {
            path = DefaultConfigPath;
        }
        else
        {
            var besideExe = System.IO.Path.Combine(AppContext.BaseDirectory, "config.json");
            path = File.Exists(besideExe) ? besideExe : DefaultConfigPath;
        }

        if (!File.Exists(path))
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            var example = System.IO.Path.Combine(AppContext.BaseDirectory, "config.example.json");
            if (File.Exists(example))
                File.Copy(example, path);
            else
                File.WriteAllText(path, JsonSerializer.Serialize(new ReporterConfig(), JsonOptions));
        }

        var cfg = JsonSerializer.Deserialize<ReporterConfig>(File.ReadAllText(path), JsonOptions)
                  ?? new ReporterConfig();
        cfg.Validate();
        return (cfg, path);
    }

    private void Validate()
    {
        if (string.IsNullOrWhiteSpace(MqttHost)) throw new InvalidOperationException("mqttHost is empty");
        if (MqttPort is <= 0 or > 65535) throw new InvalidOperationException("mqttPort is out of range");
        IntervalSeconds = Math.Max(10, IntervalSeconds);
        IperfIntervalSeconds = Math.Max(60, IperfIntervalSeconds);
        IperfDurationSeconds = Math.Clamp(IperfDurationSeconds, 1, 60);
    }
}

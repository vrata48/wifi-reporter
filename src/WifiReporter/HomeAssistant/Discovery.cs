using System.Text;
using System.Text.Json.Nodes;
using WifiReporter.Hardware;

namespace WifiReporter.HomeAssistant;

/// <summary>Home Assistant MQTT discovery: topics and sensor definitions for one computer.</summary>
public sealed class Discovery
{
    private enum Source
    {
        /// <summary>Wi-Fi state, every interval.</summary>
        State,
        /// <summary>iperf results.</summary>
        Iperf,
        /// <summary>Hardware inventory: retained, no availability, so HA keeps it while the PC is off.</summary>
        System,
        /// <summary>Live system metrics, every interval.</summary>
        Stats,
    }

    /// <param name="Value">Path in the payload JSON when it differs from <paramref name="Key"/>.</param>
    /// <param name="Attributes">Path of an object in the payload that becomes the entity's attributes.</param>
    private sealed record Sensor(
        string Key,
        string Name,
        string? Unit = null,
        string? DeviceClass = null,
        string? StateClass = null,
        string? Icon = null,
        Source Source = Source.State,
        string? Value = null,
        string? Attributes = null);

    // HA shows "<device name> <sensor name>", so the names are kept short.
    private static readonly Sensor[] WifiSensors =
    {
        new("rssi", "Signal", "dBm", "signal_strength", "measurement"),
        new("signal_quality", "Quality", "%", null, "measurement", "mdi:wifi"),
        new("rx_rate", "RX rate", "Mbit/s", "data_rate", "measurement"),
        new("tx_rate", "TX rate", "Mbit/s", "data_rate", "measurement"),
        new("band", "Band", Icon: "mdi:wifi-settings"),
        new("phy", "Standard", Icon: "mdi:wifi-star"),
        new("ssid", "SSID", Icon: "mdi:wifi"),
        new("bssid", "Access point", Icon: "mdi:access-point"),
        new("iperf_download", "Download", "Mbit/s", "data_rate", "measurement", Source: Source.Iperf),
        new("iperf_upload", "Upload", "Mbit/s", "data_rate", "measurement", Source: Source.Iperf),
    };

    private static readonly Sensor[] SystemSensors =
    {
        new("cpu", "CPU", Icon: "mdi:cpu-64-bit", Source: Source.System, Attributes: "cpu_info"),
        new("board", "Motherboard", Icon: "mdi:developer-board", Source: Source.System, Attributes: "board_info"),
        new("model", "Model", Icon: "mdi:desktop-tower-monitor", Source: Source.System),
        new("ram", "RAM", Icon: "mdi:memory", Source: Source.System, Attributes: "ram_info"),
        new("gpu", "GPU", Icon: "mdi:expansion-card", Source: Source.System, Attributes: "gpu_info"),
        new("wifi", "Wi-Fi adapter", Icon: "mdi:wifi-cog", Source: Source.System, Attributes: "wifi_info"),
        new("disks", "Disks", Icon: "mdi:harddisk", Source: Source.System, Attributes: "disks_info"),
        new("os", "Windows", Icon: "mdi:microsoft-windows", Source: Source.System, Attributes: "os_info"),
        new("cpu_load", "CPU load", "%", null, "measurement", "mdi:cpu-64-bit", Source.Stats),
        new("ram_used", "RAM used", "%", null, "measurement", "mdi:memory", Source.Stats),
        new("boot_time", "Boot time", DeviceClass: "timestamp", Icon: "mdi:restart", Source: Source.Stats),
    };

    /// <summary>
    /// Sensors that were published by earlier versions. An empty retained config removes them from HA;
    /// the values themselves stay in the state/iperf payloads.
    /// </summary>
    private static readonly string[] RemovedSensorKeys = { "channel", "iperf_upload_retransmits" };

    private readonly ReporterConfig _config;

    public Discovery(ReporterConfig config)
    {
        _config = config;
        DeviceId = Slug(config.EffectiveDeviceName);
        BaseTopic = $"wifi_reporter/{DeviceId}";
    }

    public string DeviceId { get; }
    public string BaseTopic { get; }
    public string StateTopic => $"{BaseTopic}/state";
    public string IperfTopic => $"{BaseTopic}/iperf";
    public string AvailabilityTopic => $"{BaseTopic}/availability";
    public string SystemTopic => $"{BaseTopic}/system";
    public string StatsTopic => $"{BaseTopic}/stats";

    /// <summary>
    /// Discovery messages. With an <paramref name="inventory"/> (Windows) the system sensors are included:
    /// inventory, live metrics and one usage sensor per fixed volume in <paramref name="volumes"/>.
    /// </summary>
    public IEnumerable<(string Topic, string Payload)> ConfigMessages(SystemInventory? inventory = null, IReadOnlyList<string>? volumes = null)
    {
        var device = new JsonObject
        {
            ["identifiers"] = new JsonArray($"wifi_reporter_{DeviceId}"),
            ["name"] = _config.EffectiveDeviceName,
            ["manufacturer"] = "wifi-reporter",
            ["model"] = inventory?.Model ?? "Windows PC",
            ["sw_version"] = typeof(Discovery).Assembly.GetName().Version?.ToString(3),
        };

        var sensors = WifiSensors.AsEnumerable();
        if (inventory is not null)
        {
            sensors = sensors.Concat(SystemSensors).Concat((volumes ?? Array.Empty<string>()).Select(v =>
                new Sensor($"disk_{v}", $"Disk {v.ToUpperInvariant()}:", "%", null, "measurement", "mdi:harddisk",
                    Source.Stats, Value: $"volumes.{v}.used", Attributes: $"volumes.{v}")));
        }

        foreach (var s in sensors)
        {
            var payload = new JsonObject
            {
                ["name"] = s.Name,
                ["unique_id"] = $"wifi_reporter_{DeviceId}_{s.Key}",
                ["state_topic"] = s.Source switch
                {
                    Source.Iperf => IperfTopic,
                    Source.System => SystemTopic,
                    Source.Stats => StatsTopic,
                    _ => StateTopic,
                },
                ["value_template"] = $"{{{{ value_json.{s.Value ?? s.Key} }}}}",
            };
            if (s.Source != Source.System)
            {
                payload["availability_topic"] = AvailabilityTopic;
                payload["expire_after"] = s.Source == Source.Iperf
                    ? (int)(_config.IperfIntervalSeconds * 2.5)
                    : _config.IntervalSeconds * 5;
            }
            if (s.Attributes is not null)
            {
                payload["json_attributes_topic"] = payload["state_topic"]!.GetValue<string>();
                payload["json_attributes_template"] = $"{{{{ value_json.{s.Attributes} | tojson }}}}";
            }
            if (s.Unit is not null) payload["unit_of_measurement"] = s.Unit;
            if (s.DeviceClass is not null) payload["device_class"] = s.DeviceClass;
            if (s.StateClass is not null) payload["state_class"] = s.StateClass;
            if (s.Icon is not null) payload["icon"] = s.Icon;
            payload["device"] = device.DeepClone();

            yield return (ConfigTopic(s.Key), payload.ToJsonString());
        }

        // Online/offline straight from the availability topic, so HA shows since when a PC is off.
        yield return (ConfigTopic("online", "binary_sensor"), new JsonObject
        {
            ["name"] = "Online",
            ["unique_id"] = $"wifi_reporter_{DeviceId}_online",
            ["state_topic"] = AvailabilityTopic,
            ["payload_on"] = "online",
            ["payload_off"] = "offline",
            ["device_class"] = "connectivity",
            ["device"] = device.DeepClone(),
        }.ToJsonString());

        foreach (var key in RemovedSensorKeys)
            yield return (ConfigTopic(key), "");
    }

    private string ConfigTopic(string key, string component = "sensor") =>
        $"{_config.DiscoveryPrefix}/{component}/wifi_reporter_{DeviceId}/{key}/config";

    internal static string Slug(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text.Trim().ToLowerInvariant())
            sb.Append(char.IsAsciiLetterOrDigit(c) ? c : '_');
        var slug = sb.ToString().Trim('_');
        return slug.Length == 0 ? "pc" : slug;
    }
}

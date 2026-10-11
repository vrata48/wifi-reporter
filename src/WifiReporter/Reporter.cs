using System.Text.Json.Nodes;
using WifiReporter.Hardware;
using WifiReporter.HomeAssistant;
using WifiReporter.Mqtt;
using WifiReporter.Wlan;

namespace WifiReporter;

/// <summary>Main loop: read Wi-Fi state and system metrics, run periodic iperf tests, publish everything over MQTT.</summary>
public sealed class Reporter
{
    private static readonly TimeSpan ConnectionCheck = TimeSpan.FromSeconds(5);

    private readonly ReporterConfig _cfg;
    private readonly Func<WifiState?> _readWifi;
    private readonly ISystemMonitor? _system;
    private readonly Discovery _discovery;
    private bool _restrictedLogged;
    private int _failedConnects;

    /// <param name="system">Hardware inventory and live metrics; null where unsupported (Linux).</param>
    public Reporter(ReporterConfig cfg, Func<WifiState?> readWifi, ISystemMonitor? system = null)
    {
        _cfg = cfg;
        _readWifi = readWifi;
        _system = system;
        _discovery = new Discovery(cfg);
    }

    public async Task RunAsync(CancellationToken ct)
    {
        Log.Info($"Starting: device '{_cfg.EffectiveDeviceName}', MQTT {_cfg.MqttHost}:{_cfg.MqttPort}, " +
                 $"interval {_cfg.IntervalSeconds}s, iperf {(_cfg.IperfEnabled ? $"every {_cfg.IperfIntervalSeconds}s to {_cfg.IperfServer}" : "off")}");

        await using var mqtt = new MiniMqttClient(new MqttOptions
        {
            Host = _cfg.MqttHost,
            Port = _cfg.MqttPort,
            ClientId = $"wifi_reporter_{_discovery.DeviceId}",
            Username = _cfg.MqttUsername,
            Password = _cfg.MqttPassword,
            WillTopic = _discovery.AvailabilityTopic,
            WillPayload = "offline",
        });

        // State and iperf run on their own schedules. Both are due right after the first connect, so a
        // (re)started service reports everything at once; a reconnect publishes the state immediately.
        var nextState = DateTime.UtcNow;
        var nextIperf = _cfg.IperfEnabled ? DateTime.UtcNow : DateTime.MaxValue;
        var iperfFailures = 0;
        var reconnectDelay = TimeSpan.FromSeconds(5);
        var wasConnected = false;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (!mqtt.IsConnected)
                {
                    if (wasConnected)
                    {
                        Log.Warn($"MQTT connection lost ({mqtt.LostReason ?? "unknown reason"}), reconnecting");
                        wasConnected = false;
                    }

                    if (!await TryConnectAsync(mqtt, ct).ConfigureAwait(false))
                    {
                        // Broker unreachable is a normal state (e.g. a notebook away from home): back off
                        // up to the report interval, so it's one quiet attempt per interval.
                        await Task.Delay(reconnectDelay, ct).ConfigureAwait(false);
                        reconnectDelay = TimeSpan.FromSeconds(Math.Min(_cfg.IntervalSeconds, reconnectDelay.TotalSeconds * 2));
                        continue;
                    }

                    reconnectDelay = TimeSpan.FromSeconds(5);
                    nextState = DateTime.UtcNow;
                    wasConnected = true;
                }

                if (DateTime.UtcNow >= nextState)
                {
                    await PublishStatsAsync(mqtt, ct).ConfigureAwait(false);
                    await PublishStateAsync(mqtt, ct).ConfigureAwait(false);
                    nextState = DateTime.UtcNow + TimeSpan.FromSeconds(_cfg.IntervalSeconds);
                }

                if (DateTime.UtcNow >= nextIperf)
                {
                    var result = await Iperf.RunAsync(_cfg, ct).ConfigureAwait(false);
                    if (result is not null)
                    {
                        var json = new JsonObject
                        {
                            ["iperf_download"] = result.DownloadMbps,
                            ["iperf_upload"] = result.UploadMbps,
                            ["iperf_upload_retransmits"] = result.UploadRetransmits,
                        }.ToJsonString();
                        await SafePublishAsync(mqtt, _discovery.IperfTopic, json, retain: true, ct).ConfigureAwait(false);
                        Log.Info($"iperf {json}");
                        iperfFailures = 0;
                        nextIperf = DateTime.UtcNow + TimeSpan.FromSeconds(_cfg.IperfIntervalSeconds);
                    }
                    else
                    {
                        // Server busy (another PC is testing): retry in 1-5 minutes, randomized so the PCs
                        // spread out. Repeated failures (iperf missing, server down) back off to the interval.
                        var retry = Random.Shared.Next(60, 300) * Math.Pow(2, Math.Min(iperfFailures++, 6));
                        nextIperf = DateTime.UtcNow + TimeSpan.FromSeconds(Math.Min(retry, _cfg.IperfIntervalSeconds));
                    }
                }

                // Wake at least every few seconds, so a lost connection is re-established right away
                // instead of at the next report.
                var wait = (nextState < nextIperf ? nextState : nextIperf) - DateTime.UtcNow;
                if (wait > ConnectionCheck) wait = ConnectionCheck;
                if (wait > TimeSpan.Zero)
                    await Task.Delay(wait, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // normal shutdown
        }
        finally
        {
            if (mqtt.IsConnected)
            {
                try
                {
                    await mqtt.PublishAsync(_discovery.AvailabilityTopic, "offline", retain: true, CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // shutting down anyway
                }
            }

            Log.Info("Stopped");
        }
    }

    private async Task<bool> TryConnectAsync(MiniMqttClient mqtt, CancellationToken ct)
    {
        // Read on every connect (cheap), so a hardware upgrade shows up after the next restart.
        SystemInventory? inventory = null;
        IReadOnlyList<string>? volumes = null;
        if (_system is not null)
        {
            try
            {
                inventory = _system.ReadInventory();
                volumes = _system.VolumeKeys();
            }
            catch (Exception ex)
            {
                Log.Error("Reading system info failed", ex);
            }
        }

        try
        {
            await mqtt.ConnectAsync(ct).ConfigureAwait(false);
            foreach (var (topic, payload) in _discovery.ConfigMessages(inventory, volumes))
                await mqtt.PublishAsync(topic, payload, retain: true, ct).ConfigureAwait(false);
            // HA subscribes to the state topics only after processing the discovery; the pause lets it do so
            // before the first values of a new device arrive.
            await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
            await mqtt.PublishAsync(_discovery.AvailabilityTopic, "online", retain: true, ct).ConfigureAwait(false);
            if (inventory is not null)
            {
                // Retained: HA shows the hardware even while the PC is off or after an HA restart.
                var json = inventory.ToJson();
                await mqtt.PublishAsync(_discovery.SystemTopic, json, retain: true, ct).ConfigureAwait(false);
                Log.Info($"system {json}");
            }
            var after = _failedConnects > 0 ? $" (after {_failedConnects} failed attempts)" : "";
            Log.Info($"MQTT connected{after}, discovery published under {_discovery.BaseTopic}");
            _failedConnects = 0;
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Only the first failure is logged; the next attempts stay quiet until it works again.
            if (_failedConnects++ == 0)
                Log.Warn($"MQTT connect to {_cfg.MqttHost}:{_cfg.MqttPort} failed: {ex.Message.TrimEnd('.')}. " +
                         $"Retrying quietly, at most every {_cfg.IntervalSeconds}s");
            return false;
        }
    }

    private async Task PublishStatsAsync(MiniMqttClient mqtt, CancellationToken ct)
    {
        if (_system is null) return;

        string json;
        try
        {
            json = _system.ReadStats().ToJson();
        }
        catch (Exception ex)
        {
            Log.Error("Reading system stats failed", ex);
            return;
        }

        await SafePublishAsync(mqtt, _discovery.StatsTopic, json, retain: true, ct).ConfigureAwait(false);
        Log.Info($"stats {json}");
    }

    private async Task PublishStateAsync(MiniMqttClient mqtt, CancellationToken ct)
    {
        WifiState? state;
        try
        {
            state = _readWifi();
        }
        catch (Exception ex)
        {
            Log.Error("Reading Wi-Fi state failed", ex);
            return;
        }

        if (state is null)
            Log.Info("Wi-Fi not connected");

        if (state is { Restricted: true } && !_restrictedLogged)
        {
            Log.Warn("Windows denied access to Wi-Fi connection details (location privacy). " +
                     "Only signal and channel are reported. See README: 'Access denied / location'.");
            _restrictedLogged = true;
        }

        // Retained, so HA has the values right after its own restart. Without Wi-Fi (Ethernet, Wi-Fi off) all
        // fields are null: HA shows the sensors as "unknown" instead of keeping the last connection.
        var json = ToJson(state);
        await SafePublishAsync(mqtt, _discovery.StateTopic, json, retain: true, ct).ConfigureAwait(false);
        if (state is not null)
            Log.Info($"state {json}");
    }

    internal static string ToJson(WifiState? s) => new JsonObject
    {
        ["rssi"] = s?.Rssi,
        ["signal_quality"] = s?.SignalQuality,
        ["rx_rate"] = s?.RxRate,
        ["tx_rate"] = s?.TxRate,
        ["channel"] = s?.Channel,
        ["band"] = s?.Band,
        ["phy"] = s?.Phy,
        ["ssid"] = s?.Ssid,
        ["bssid"] = s?.Bssid,
        ["adapter"] = s?.Adapter,
        ["restricted"] = s?.Restricted ?? false,
    }.ToJsonString();

    private static async Task SafePublishAsync(MiniMqttClient mqtt, string topic, string payload, bool retain, CancellationToken ct)
    {
        try
        {
            await mqtt.PublishAsync(topic, payload, retain, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn($"MQTT publish failed, will reconnect: {ex.Message}");
        }
    }
}

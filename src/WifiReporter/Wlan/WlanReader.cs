using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using static WifiReporter.Wlan.WlanNative;

namespace WifiReporter.Wlan;

public sealed record WifiState
{
    public required string Adapter { get; init; }
    public string? Ssid { get; init; }
    public string? Bssid { get; init; }
    public string? Phy { get; init; }
    public int? SignalQuality { get; init; }
    public int? Rssi { get; init; }
    public double? RxRate { get; init; }
    public double? TxRate { get; init; }
    public int? Channel { get; init; }
    public string? Band { get; init; }

    /// <summary>True when Windows refused the connection details (location privacy).</summary>
    public bool Restricted { get; init; }
}

/// <summary>Reads the current Wi-Fi connection through the Native Wifi API.</summary>
[SupportedOSPlatform("windows")]
public sealed class WlanReader : IDisposable
{
    private readonly IntPtr _handle;

    public WlanReader()
    {
        var rc = WlanOpenHandle(ClientVersion, IntPtr.Zero, out _, out _handle);
        if (rc != ErrorSuccess)
            throw new InvalidOperationException($"WlanOpenHandle failed with error {rc} (is the WLAN AutoConfig service running?)");
    }

    public void Dispose() => WlanCloseHandle(_handle, IntPtr.Zero);

    /// <summary>Returns the state of the first connected Wi-Fi adapter, or null when none is connected.</summary>
    public WifiState? Read()
    {
        var rc = WlanEnumInterfaces(_handle, IntPtr.Zero, out var listPtr);
        if (rc != ErrorSuccess)
            throw new InvalidOperationException($"WlanEnumInterfaces failed with error {rc}");

        try
        {
            var count = Marshal.ReadInt32(listPtr);
            var itemSize = Marshal.SizeOf<WLAN_INTERFACE_INFO>();
            var first = listPtr + 8; // dwNumberOfItems + dwIndex

            for (var i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<WLAN_INTERFACE_INFO>(first + i * itemSize);
                if (info.State == InterfaceStateConnected)
                    return ReadInterface(info);
            }

            return null;
        }
        finally
        {
            WlanFreeMemory(listPtr);
        }
    }

    private WifiState ReadInterface(WLAN_INTERFACE_INFO info)
    {
        var guid = info.InterfaceGuid;
        int? rssi = QueryInt(ref guid, OpcodeRssi);
        int? channel = QueryInt(ref guid, OpcodeChannelNumber);

        var rc = WlanQueryInterface(_handle, ref guid, OpcodeCurrentConnection, IntPtr.Zero, out _, out var data, out _);
        if (rc == ErrorAccessDenied)
        {
            // Windows 11 24H2+: connection details (SSID/BSSID) need location consent.
            // Report what we can still read so the sensors keep working.
            return new WifiState
            {
                Adapter = info.InterfaceDescription,
                Rssi = rssi,
                Channel = channel,
                Band = channel is { } ch ? BandFor(ch) : null,
                Restricted = true,
            };
        }

        if (rc != ErrorSuccess)
            throw new InvalidOperationException($"WlanQueryInterface(current_connection) failed with error {rc}");

        try
        {
            var conn = Marshal.PtrToStructure<WLAN_CONNECTION_ATTRIBUTES>(data);
            var assoc = conn.AssociationAttributes;
            var quality = (int)assoc.WlanSignalQuality;
            var ssidLength = (int)Math.Min(assoc.Dot11Ssid.Length, 32);

            return new WifiState
            {
                Adapter = info.InterfaceDescription,
                Ssid = Encoding.UTF8.GetString(assoc.Dot11Ssid.Ssid, 0, ssidLength),
                Bssid = string.Join(':', assoc.Dot11Bssid.Select(b => b.ToString("x2"))),
                Phy = PhyName(assoc.Dot11PhyType),
                SignalQuality = quality,
                // Windows maps quality 0..100 linearly onto -100..-50 dBm; used when the RSSI opcode is unavailable.
                Rssi = rssi ?? quality / 2 - 100,
                RxRate = Math.Round(assoc.RxRateKbps / 1000.0, 1),
                TxRate = Math.Round(assoc.TxRateKbps / 1000.0, 1),
                Channel = channel,
                Band = channel is { } ch ? BandFor(ch) : null,
            };
        }
        finally
        {
            WlanFreeMemory(data);
        }
    }

    private int? QueryInt(ref Guid guid, int opcode)
    {
        var rc = WlanQueryInterface(_handle, ref guid, opcode, IntPtr.Zero, out var size, out var data, out _);
        if (rc != ErrorSuccess || data == IntPtr.Zero)
            return null;
        try
        {
            return size >= 4 ? Marshal.ReadInt32(data) : null;
        }
        finally
        {
            WlanFreeMemory(data);
        }
    }

    // DOT11_PHY_TYPE enumeration
    internal static string PhyName(int phyType) => phyType switch
    {
        4 => "802.11a",
        5 => "802.11b",
        6 => "802.11g",
        7 => "Wi-Fi 4 (n)",
        8 => "Wi-Fi 5 (ac)",
        10 => "Wi-Fi 6 (ax)",
        11 => "Wi-Fi 7 (be)",
        _ => $"type {phyType}",
    };

    /// <summary>
    /// Band from the channel number. Channels 1-14 are reported as 2.4 GHz; 6 GHz uses overlapping
    /// numbers and cannot be told apart from this API alone (fine for networks without 6 GHz).
    /// </summary>
    internal static string BandFor(int channel) => channel switch
    {
        <= 14 => "2.4 GHz",
        <= 177 => "5 GHz",
        _ => "6 GHz",
    };
}

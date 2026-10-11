using System.Runtime.InteropServices;

namespace WifiReporter.Wlan;

/// <summary>P/Invoke declarations for the Native Wifi API (wlanapi.dll).</summary>
internal static class WlanNative
{
    public const uint ClientVersion = 2;
    public const int ErrorSuccess = 0;
    public const int ErrorAccessDenied = 5;

    // WLAN_INTF_OPCODE values
    public const int OpcodeCurrentConnection = 7;
    public const int OpcodeChannelNumber = 8;
    public const int OpcodeRssi = 0x10000102;

    public const int InterfaceStateConnected = 1;

    [DllImport("wlanapi.dll")]
    public static extern int WlanOpenHandle(uint clientVersion, IntPtr reserved, out uint negotiatedVersion, out IntPtr clientHandle);

    [DllImport("wlanapi.dll")]
    public static extern int WlanCloseHandle(IntPtr clientHandle, IntPtr reserved);

    [DllImport("wlanapi.dll")]
    public static extern int WlanEnumInterfaces(IntPtr clientHandle, IntPtr reserved, out IntPtr interfaceList);

    [DllImport("wlanapi.dll")]
    public static extern int WlanQueryInterface(
        IntPtr clientHandle,
        ref Guid interfaceGuid,
        int opCode,
        IntPtr reserved,
        out int dataSize,
        out IntPtr data,
        out int opcodeValueType);

    [DllImport("wlanapi.dll")]
    public static extern void WlanFreeMemory(IntPtr memory);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WLAN_INTERFACE_INFO
    {
        public Guid InterfaceGuid;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string InterfaceDescription;

        public int State;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DOT11_SSID
    {
        public uint Length;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] Ssid;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WLAN_ASSOCIATION_ATTRIBUTES
    {
        public DOT11_SSID Dot11Ssid;
        public int Dot11BssType;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)]
        public byte[] Dot11Bssid;

        public int Dot11PhyType;
        public uint Dot11PhyIndex;
        public uint WlanSignalQuality;
        public uint RxRateKbps;
        public uint TxRateKbps;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WLAN_SECURITY_ATTRIBUTES
    {
        public int SecurityEnabled;
        public int OneXEnabled;
        public int AuthAlgorithm;
        public int CipherAlgorithm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WLAN_CONNECTION_ATTRIBUTES
    {
        public int State;
        public int ConnectionMode;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string ProfileName;

        public WLAN_ASSOCIATION_ATTRIBUTES AssociationAttributes;
        public WLAN_SECURITY_ATTRIBUTES SecurityAttributes;
    }
}

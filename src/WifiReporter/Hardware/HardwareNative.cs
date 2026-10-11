using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WifiReporter.Hardware;

/// <summary>P/Invoke declarations for kernel32 / setupapi used by the system info readers.</summary>
internal static class HardwareNative
{
    // --- firmware tables (SMBIOS) -------------------------------------------------------------------------
    public const uint FirmwareTableRsmb = 0x52534D42; // 'RSMB'

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint GetSystemFirmwareTable(uint providerSignature, uint tableId, byte[]? buffer, uint bufferSize);

    // --- memory / CPU ---------------------------------------------------------------------------------------
    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORYSTATUSEX
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetPhysicallyInstalledSystemMemory(out ulong totalMemoryInKilobytes);

    /// <summary>FILETIME values as 100 ns ticks; kernel time includes idle time.</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetSystemTimes(out long idleTime, out long kernelTime, out long userTime);

    public const int RelationProcessorCore = 0;

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetLogicalProcessorInformationEx(int relationship, IntPtr buffer, ref uint returnedLength);

    // --- physical disks -------------------------------------------------------------------------------------
    public const uint FileShareRead = 1;
    public const uint FileShareWrite = 2;
    public const uint OpenExisting = 3;

    public const uint IoctlStorageQueryProperty = 0x002D1400;
    public const uint IoctlDiskGetDriveGeometryEx = 0x000700A0; // FILE_ANY_ACCESS: works without admin

    public const int StorageDeviceProperty = 0;
    public const int StorageDeviceSeekPenaltyProperty = 7;

    [StructLayout(LayoutKind.Sequential)]
    public struct STORAGE_PROPERTY_QUERY
    {
        public int PropertyId;
        public int QueryType; // PropertyStandardQuery = 0
        public int AdditionalParameters;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern SafeFileHandle CreateFileW(
        string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool DeviceIoControl(
        SafeFileHandle device, uint ioControlCode, ref STORAGE_PROPERTY_QUERY inBuffer, int inBufferSize,
        byte[] outBuffer, int outBufferSize, out int bytesReturned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool DeviceIoControl(
        SafeFileHandle device, uint ioControlCode, IntPtr inBuffer, int inBufferSize,
        byte[] outBuffer, int outBufferSize, out int bytesReturned, IntPtr overlapped);

    // --- display / network adapters (SetupAPI) --------------------------------------------------------------
    public static readonly Guid DisplayClassGuid = new("4d36e968-e325-11ce-bfc1-08002be10318");
    public static readonly Guid NetClassGuid = new("4d36e972-e325-11ce-bfc1-08002be10318");
    public const uint DigcfPresent = 0x2;
    public const uint DicsFlagGlobal = 0x1;
    public const uint DiregDrv = 0x2;
    public const uint KeyRead = 0x20019;

    [StructLayout(LayoutKind.Sequential)]
    public struct SP_DEVINFO_DATA
    {
        public uint Size;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", SetLastError = true)]
    public static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, IntPtr enumerator, IntPtr parent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    public static extern bool SetupDiEnumDeviceInfo(IntPtr deviceInfoSet, uint memberIndex, ref SP_DEVINFO_DATA deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    public static extern IntPtr SetupDiOpenDevRegKey(
        IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData, uint scope, uint hwProfile, uint keyType, uint samDesired);

    [DllImport("setupapi.dll", SetLastError = true)]
    public static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);
}

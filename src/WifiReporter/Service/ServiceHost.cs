using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace WifiReporter.Service;

/// <summary>
/// Runs the app as a Windows service using the native Service Control Manager API
/// (no Microsoft.Extensions.Hosting / System.ServiceProcess package needed).
/// </summary>
[SupportedOSPlatform("windows")]
public static class ServiceHost
{
    public const string ServiceName = "WifiReporter";
    private const int ErrorFailedServiceControllerConnect = 1063;

    private const uint ServiceWin32OwnProcess = 0x10;
    private const uint StateStopped = 1;
    private const uint StateStartPending = 2;
    private const uint StateStopPending = 3;
    private const uint StateRunning = 4;
    private const uint AcceptStop = 0x1;
    private const uint AcceptShutdown = 0x4;
    private const uint ControlStop = 0x1;
    private const uint ControlInterrogate = 0x4;
    private const uint ControlShutdown = 0x5;

    private delegate void ServiceMainDelegate(uint argc, IntPtr argv);
    private delegate uint HandlerExDelegate(uint control, uint eventType, IntPtr eventData, IntPtr context);

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_STATUS
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SERVICE_TABLE_ENTRY
    {
        public string? ServiceName;
        public IntPtr ServiceProc;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool StartServiceCtrlDispatcherW([In] SERVICE_TABLE_ENTRY[] table);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr RegisterServiceCtrlHandlerExW(string serviceName, HandlerExDelegate handler, IntPtr context);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool SetServiceStatus(IntPtr handle, ref SERVICE_STATUS status);

    // Delegates must stay referenced for the lifetime of the process, or the GC collects them.
    private static ServiceMainDelegate? _serviceMain;
    private static HandlerExDelegate? _handler;
    private static IntPtr _statusHandle;
    private static SERVICE_STATUS _status;
    private static CancellationTokenSource? _cts;
    private static Func<CancellationToken, Task>? _work;

    /// <summary>
    /// Hands the process to the Service Control Manager. Returns false when the process was not
    /// started as a service (e.g. double-clicked), so the caller can fall back to another mode.
    /// </summary>
    public static bool TryRun(Func<CancellationToken, Task> work)
    {
        _work = work;
        _serviceMain = ServiceMain;
        var table = new[]
        {
            new SERVICE_TABLE_ENTRY
            {
                ServiceName = ServiceName,
                ServiceProc = Marshal.GetFunctionPointerForDelegate(_serviceMain),
            },
            new SERVICE_TABLE_ENTRY { ServiceName = null, ServiceProc = IntPtr.Zero },
        };

        if (StartServiceCtrlDispatcherW(table))
            return true; // returns when the service has stopped

        var error = Marshal.GetLastWin32Error();
        if (error == ErrorFailedServiceControllerConnect)
            return false;

        throw new InvalidOperationException($"StartServiceCtrlDispatcher failed with error {error}");
    }

    private static void ServiceMain(uint argc, IntPtr argv)
    {
        _handler = Handler;
        _statusHandle = RegisterServiceCtrlHandlerExW(ServiceName, _handler, IntPtr.Zero);
        if (_statusHandle == IntPtr.Zero)
        {
            Log.Error($"RegisterServiceCtrlHandlerEx failed with error {Marshal.GetLastWin32Error()}");
            return;
        }

        _status = new SERVICE_STATUS { ServiceType = ServiceWin32OwnProcess };
        Report(StateStartPending, waitHint: 10_000);

        _cts = new CancellationTokenSource();
        uint exitCode = 0;
        try
        {
            var task = _work!(_cts.Token);
            Report(StateRunning);
            task.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            // stop requested
        }
        catch (Exception ex)
        {
            Log.Error("Service crashed", ex);
            exitCode = 1;
        }

        Report(StateStopped, exitCode: exitCode);
    }

    private static uint Handler(uint control, uint eventType, IntPtr eventData, IntPtr context)
    {
        switch (control)
        {
            case ControlStop:
            case ControlShutdown:
                Log.Info(control == ControlStop ? "Stop requested" : "System shutdown");
                Report(StateStopPending, waitHint: 15_000);
                _cts?.Cancel();
                return 0;
            case ControlInterrogate:
                return 0;
            default:
                return 120; // ERROR_CALL_NOT_IMPLEMENTED
        }
    }

    private static void Report(uint state, uint exitCode = 0, uint waitHint = 0)
    {
        _status.CurrentState = state;
        _status.Win32ExitCode = exitCode;
        _status.WaitHint = waitHint;
        _status.ControlsAccepted = state is StateRunning ? AcceptStop | AcceptShutdown : 0;
        _status.CheckPoint = state is StateRunning or StateStopped ? 0 : _status.CheckPoint + 1;
        SetServiceStatus(_statusHandle, ref _status);
    }
}

using System.Diagnostics;
using System.Runtime.Versioning;

namespace WifiReporter.Service;

/// <summary>Installs / removes the Windows service (sc.exe) or the per-user logon task (schtasks.exe).</summary>
[SupportedOSPlatform("windows")]
public static class Installer
{
    private const string TaskName = "WifiReporter";

    private static string ExePath => Environment.ProcessPath
                                     ?? throw new InvalidOperationException("Cannot determine the executable path");

    public static int InstallService(string? configPath)
    {
        var binPath = Quote(ExePath) + (configPath is null ? "" : $" --config {Quote(Path.GetFullPath(configPath))}");

        if (Run("sc.exe", "create", ServiceHost.ServiceName, "binPath=", binPath, "start=", "delayed-auto",
                "DisplayName=", "Wi-Fi Reporter") != 0)
            return 1;

        Run("sc.exe", "description", ServiceHost.ServiceName,
            "Reports Wi-Fi signal, link rate, access point and LAN throughput to Home Assistant over MQTT.");
        // Restart after 1 minute on failure; reset the failure counter after a day.
        Run("sc.exe", "failure", ServiceHost.ServiceName, "reset=", "86400", "actions=", "restart/60000/restart/60000/restart/60000");

        return Run("sc.exe", "start", ServiceHost.ServiceName);
    }

    public static int UninstallService()
    {
        Run("sc.exe", "stop", ServiceHost.ServiceName);
        Thread.Sleep(2000);
        return Run("sc.exe", "delete", ServiceHost.ServiceName);
    }

    /// <summary>
    /// Alternative to the service: a task that runs in the signed-in user's session at logon.
    /// Use it when Windows hides Wi-Fi details from the service (location privacy).
    /// </summary>
    public static int InstallTask(string? configPath)
    {
        var command = Quote(ExePath) + " run" + (configPath is null ? "" : $" --config {Quote(Path.GetFullPath(configPath))}");
        var user = $"{Environment.UserDomainName}\\{Environment.UserName}";

        var rc = Run("schtasks.exe", "/Create", "/F", "/TN", TaskName, "/TR", command,
                     "/SC", "ONLOGON", "/RU", user, "/IT", "/RL", "LIMITED");
        if (rc != 0) return rc;

        return Run("schtasks.exe", "/Run", "/TN", TaskName);
    }

    public static int UninstallTask()
    {
        Run("schtasks.exe", "/End", "/TN", TaskName);
        return Run("schtasks.exe", "/Delete", "/F", "/TN", TaskName);
    }

    private static string Quote(string s) => s.Contains(' ') ? $"\"{s}\"" : s;

    private static int Run(string file, params string[] args)
    {
        var psi = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)!;
        var output = (p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd()).Trim();
        p.WaitForExit();
        Console.WriteLine($"> {file} {string.Join(' ', args)}");
        if (output.Length > 0) Console.WriteLine(output);
        if (p.ExitCode == 5)
            Console.WriteLine("Access denied: run this from an elevated (Administrator) terminal.");
        return p.ExitCode;
    }
}

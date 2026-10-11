namespace WifiReporter;

/// <summary>
/// Minimal thread-safe logger: daily files in %ProgramData%\WifiReporter\logs (kept 7 days),
/// plus stdout when running in console mode.
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    private static string _dir = Path.Combine(ReporterConfig.DataDirectory, "logs");
    private static DateOnly _lastCleanup;

    public static bool ToConsole { get; set; }

    public static string Directory => _dir;

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}: {ex.GetType().Name}: {ex.Message}");

    private static void Write(string level, string message)
    {
        var now = DateTime.Now;
        var line = $"{now:yyyy-MM-dd HH:mm:ss} {level,-5} {message}";
        lock (Gate)
        {
            if (ToConsole)
                Console.WriteLine(line);
            try
            {
                System.IO.Directory.CreateDirectory(_dir);
                File.AppendAllText(Path.Combine(_dir, $"wifi-reporter-{now:yyyyMMdd}.log"), line + Environment.NewLine);
                Cleanup(DateOnly.FromDateTime(now));
            }
            catch
            {
                // Logging must never take the service down.
            }
        }
    }

    private static void Cleanup(DateOnly today)
    {
        if (_lastCleanup == today) return;
        _lastCleanup = today;
        foreach (var file in System.IO.Directory.EnumerateFiles(_dir, "wifi-reporter-*.log"))
        {
            if (File.GetLastWriteTime(file) < DateTime.Now.AddDays(-7))
                File.Delete(file);
        }
    }
}

using System.Diagnostics;
using System.Text.Json;

namespace WifiReporter;

public sealed record IperfResult(double DownloadMbps, double UploadMbps, long UploadRetransmits);

/// <summary>Runs short iperf3 tests (download with -R, then upload) and parses the JSON output.</summary>
public static class Iperf
{
    public static async Task<IperfResult?> RunAsync(ReporterConfig cfg, CancellationToken ct)
    {
        var down = await RunOneAsync(cfg, reverse: true, ct).ConfigureAwait(false);
        if (down is null) return null;

        await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);

        var up = await RunOneAsync(cfg, reverse: false, ct).ConfigureAwait(false);
        if (up is null) return null;

        return new IperfResult(down.Value.Mbps, up.Value.Mbps, up.Value.Retransmits);
    }

    private static async Task<(double Mbps, long Retransmits)?> RunOneAsync(ReporterConfig cfg, bool reverse, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ResolvePath(cfg.IperfPath),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(cfg.IperfServer);
        psi.ArgumentList.Add("-t");
        psi.ArgumentList.Add(cfg.IperfDurationSeconds.ToString());
        psi.ArgumentList.Add("-J");
        if (reverse) psi.ArgumentList.Add("-R");

        var direction = reverse ? "download" : "upload";
        string output;
        try
        {
            using var process = Process.Start(psi) ?? throw new InvalidOperationException("process did not start");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(cfg.IperfDurationSeconds + 30));

            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            _ = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
                throw;
            }

            output = await stdout.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Log.Warn($"iperf3 {direction} timed out");
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn($"iperf3 {direction} could not run ({cfg.IperfPath}): {ex.Message}");
            return null;
        }

        return Parse(output, direction);
    }

    /// <summary>
    /// A service starts in System32, so a bare file name is first looked up next to the exe,
    /// then left to the normal PATH search.
    /// </summary>
    internal static string ResolvePath(string configured)
    {
        if (Path.IsPathRooted(configured)) return configured;
        var besideExe = Path.Combine(AppContext.BaseDirectory, configured);
        return File.Exists(besideExe) ? besideExe : configured;
    }

    internal static (double Mbps, long Retransmits)? Parse(string json, string direction = "test")
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var error))
            {
                // Typically "the server is busy running a test" - another PC is testing right now.
                Log.Info($"iperf3 {direction} skipped: {error.GetString()}");
                return null;
            }

            var end = root.GetProperty("end");
            var bps = end.GetProperty("sum_received").GetProperty("bits_per_second").GetDouble();
            long retransmits = 0;
            if (end.TryGetProperty("sum_sent", out var sent) && sent.TryGetProperty("retransmits", out var r))
                retransmits = r.GetInt64();
            return (Math.Round(bps / 1_000_000, 1), retransmits);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            Log.Warn($"iperf3 {direction} output could not be parsed: {ex.Message}");
            return null;
        }
    }
}

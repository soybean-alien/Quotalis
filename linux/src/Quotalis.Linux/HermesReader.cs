using System.Diagnostics;
using Quotalis.Core.Diagnostics;
using Quotalis.Core.Hermes;
using Quotalis.Core.Settings;

namespace Quotalis.Linux;

/// <summary>Reads Hermes's token use: a local <c>state.db</c>, or a server's export copied with OpenSSH.</summary>
internal static class HermesReader
{
    public static async Task<(HermesUsage? Usage, string? Problem)> ReadAsync(AppSettings settings, CancellationToken token) =>
        settings.HermesSource == HermesUsageSource.Remote ? await ReadRemoteAsync(settings, token) : ReadLocal();

    /// <summary>A local install, or a server export set up in settings, turns the Hermes section on.</summary>
    public static bool IsEnabled(AppSettings settings) =>
        settings.HermesEnabled || File.Exists(HermesStateDb.DefaultPath());

    private static (HermesUsage?, string?) ReadLocal()
    {
        var path = HermesStateDb.DefaultPath();
        if (!File.Exists(path))
        {
            return (null, "No Hermes install on this PC");
        }

        var usage = HermesStateDb.Read(path, TimeZoneInfo.Local, DateTimeOffset.UtcNow);
        return usage is null ? (null, "Hermes's database could not be read") : (usage, null);
    }

    private static async Task<(HermesUsage?, string?)> ReadRemoteAsync(AppSettings settings, CancellationToken token)
    {
        if (settings.HermesRemoteTarget.Length == 0 || !File.Exists(settings.HermesRemoteKeyPath))
        {
            return (null, "Set the server and key in settings.json");
        }

        var scp = ProcessRunner.FindExecutable("scp");
        if (scp is null)
        {
            return (null, "OpenSSH is not installed");
        }

        var cache = AppPaths.HermesUsageCache;
        var temp = cache + ".part";
        var start = new ProcessStartInfo(scp)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        foreach (var arg in new[] { "-q", "-i", settings.HermesRemoteKeyPath, "-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=yes", "-o", "ConnectTimeout=10",
                     settings.HermesRemoteTarget + ":" + settings.HermesRemotePath, temp })
        {
            start.ArgumentList.Add(arg);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
        using var process = Process.Start(start)!;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(); } catch (InvalidOperationException) { }
            return (null, "The server did not answer");
        }

        if (process.ExitCode != 0 || !File.Exists(temp))
        {
            // The error text can name the host; only the exit code is logged.
            Log.Info("hermes", "Hermes export not copied", new { exit = process.ExitCode });
            return (null, "The server's export could not be copied");
        }

        File.Move(temp, cache, overwrite: true);
        var usage = HermesUsageParser.Parse(await File.ReadAllTextAsync(cache, token), TimeZoneInfo.Local);
        return usage is null ? (null, "The server's export is not in a known format") : (usage, null);
    }
}

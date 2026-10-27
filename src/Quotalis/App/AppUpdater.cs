using System.Diagnostics;
using System.Reflection;
using Quotalis.Core.Diagnostics;
using Quotalis.Core.Updates;

namespace Quotalis;

/// <summary>
/// The app's view of <see cref="UpdateService"/>: the last check's result for Settings, the daily background check, and
/// installing an update. Installing downloads and verifies the new installer, starts it silently and quits Quotalis; the
/// installer replaces this copy in place (data, settings, hooks and autostart are kept) and starts the new version.
/// Only an installed copy updates itself; a build running from the source tree never does.
/// </summary>
internal sealed class AppUpdater
{
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

    private readonly UpdateService _service = new();

    /// <summary>The one updater of this process (set by <see cref="AppHost"/>); Settings reads it.</summary>
    public static AppUpdater? Shared { get; set; }

    public static Version CurrentVersion { get; } = UpdateService.ParseVersion(
        typeof(AppUpdater).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(AppUpdater).Assembly.GetName().Version?.ToString() ?? "0.0.0");

    /// <summary>Installed by the installer (its uninstaller sits next to the exe), not a development build.</summary>
    public static bool IsInstalledCopy => File.Exists(Path.Combine(AppContext.BaseDirectory, "unins000.exe"));

    /// <summary>A newer release found by the last check, or null.</summary>
    public QuotalisRelease? Available { get; private set; }

    public DateTimeOffset? LastChecked { get; private set; }

    public string? LastError { get; private set; }

    /// <summary>Raised (on a background thread) after every check.</summary>
    public event Action? Changed;

    /// <summary>Asks GitHub for the latest release. Returns it when newer than this build, else null. Throws <see cref="UpdateException"/>.</summary>
    public async Task<QuotalisRelease?> CheckAsync()
    {
        try
        {
            var latest = await _service.GetLatestAsync().ConfigureAwait(false);
            Available = UpdateService.IsNewer(latest, CurrentVersion) ? latest : null;
            LastError = null;
            Log.Info("update", "Update check", new { latest = latest.Tag, current = CurrentVersion.ToString(3), newer = Available is not null });
            return Available;
        }
        catch (UpdateException ex)
        {
            LastError = ex.Message;
            Log.Info("update", "Update check failed", new { reason = ex.Message });
            throw;
        }
        finally
        {
            LastChecked = DateTimeOffset.Now;
            Changed?.Invoke();
        }
    }

    /// <summary>The background check: never throws, never interrupts.</summary>
    public async Task CheckQuietlyAsync()
    {
        try
        {
            await CheckAsync().ConfigureAwait(false);
        }
        catch (UpdateException)
        {
            // Already logged; Settings shows LastError.
        }
    }

    /// <summary>Downloads and verifies <paramref name="release"/>, then starts its installer silently. The caller quits Quotalis next.</summary>
    public async Task StartInstallAsync(QuotalisRelease release)
    {
        if (!IsInstalledCopy)
        {
            throw new UpdateException("This Quotalis runs from a development build. Updates apply to the installed copy only.");
        }

        var installer = await _service.DownloadAsync(release).ConfigureAwait(false);
        var log = Path.Combine(Path.GetTempPath(), $"Quotalis-update-{release.Tag}.log");
        var info = new ProcessStartInfo(installer) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(installer)! };
        // Silent with a progress bar; the previous install's choices (shortcuts, autostart, hooks) are kept.
        foreach (var arg in new[] { "/SILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/CLOSEAPPLICATIONS", $"/LOG={log}" })
        {
            info.ArgumentList.Add(arg);
        }

        Process.Start(info);
        Log.Info("update", "Installer started", new { release = release.Tag });
    }
}

using Quotalis.Core;

namespace Quotalis;

/// <summary>Local data locations. Everything stays under %LOCALAPPDATA%\Quotalis.</summary>
internal static class AppPaths
{
    public static string Root { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Quotalis");

    public static string Logs => Path.Combine(Root, "logs");

    public static string Database => Path.Combine(Root, "usage.db");

    /// <summary>
    /// Files that hooks and other programs write for Quotalis: <c>%USERPROFILE%\.quotalis</c>, outside AppData. Claude desktop
    /// is a packaged (MSIX) app, and Windows redirects new files its hooks create under AppData into Claude's private
    /// package folder, where a normally started Quotalis never sees them. The user profile root is not redirected.
    /// </summary>
    public static string Shared { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".quotalis");

    public static string PricingOverrides => Path.Combine(Shared, "pricing.json");

    public static string CalibrationLog => Path.Combine(Shared, "calibration-log.jsonl");

    public static string CodexHookTrace => Path.Combine(Shared, "codex-stop-hook-last.json");

    /// <summary>Codex's newest 5-hour and weekly readings, published for other programs (Claudex Mini).</summary>
    public static string CodexPlan => Path.Combine(Shared, "codex-plan.json");

    /// <summary>Every Codex rate-limit reading, one JSON line each, published for other programs (Claudex Mini).</summary>
    public static string CodexLimitLog => Path.Combine(Shared, "codex-limit-log.jsonl");

    /// <summary>The names that moved from <see cref="Root"/> to <see cref="Shared"/> in 0.5.10.</summary>
    public static readonly string[] SharedFiles = ["claude-plan.json", "calibration-log.jsonl", "stop-hook-last.json", "codex-stop-hook-last.json", "pricing.json"];

    /// <summary>
    /// Where older copies of the shared files may sit: the old data folder, and the private AppData of any packaged app
    /// that ran our hooks (for example <c>Packages\Claude_…\LocalCache\Local\Quotalis</c>).
    /// </summary>
    public static IEnumerable<string> LegacySharedFolders()
    {
        yield return Root;
        var packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
        string[] dirs;
        try
        {
            dirs = Directory.Exists(packages) ? Directory.GetDirectories(packages) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            dirs = [];
        }

        foreach (var dir in dirs)
        {
            foreach (var name in new[] { "Quotalis", LegacyNames.OldName })
            {
                var candidate = Path.Combine(dir, "LocalCache", "Local", name);
                if (Directory.Exists(candidate))
                {
                    yield return candidate;
                }
            }
        }
    }

    /// <summary>
    /// Moves the folders of Claudex (the name before 0.7.0), <c>%LOCALAPPDATA%\Claudex</c> and <c>%USERPROFILE%\.claudex</c>,
    /// to <see cref="Root"/> and <see cref="Shared"/>, so settings, history and sign-ins carry over (see
    /// <see cref="LegacyNames.AdoptFolder"/>). The installed copy runs it first in every launch, hooks included, because
    /// whichever starts first after an update may write a file before the island does. Development builds and demo runs
    /// never call it, so they can't take the data away from an installed Claudex.
    /// </summary>
    public static void MoveLegacyFolders()
    {
        var now = DateTimeOffset.Now;
        LegacyNames.AdoptFolder(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), LegacyNames.OldName), Root, now);
        LegacyNames.AdoptFolder(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "." + LegacyNames.OldName.ToLowerInvariant()), Shared, now);
    }

    public static string UiState => Path.Combine(Root, "ui-state.json");

    /// <summary>Plan usage read from claude.ai or handed over by Claude Code's status line (numbers only).</summary>
    public static string ClaudePlan => Path.Combine(Shared, "claude-plan.json");

    /// <summary>
    /// The browser profile of the claude.ai sign-in (<see cref="ClaudeWebReader"/>): claude.ai's cookies, on this PC only.
    /// In the user profile like <see cref="Shared"/>, so a Quotalis started from a packaged app finds the same sign-in.
    /// Signing out clears it; it is never part of a build, an installer or the repository.
    /// </summary>
    public static string ClaudeWebProfile => Path.Combine(Shared, "claude-signin");

    /// <summary>Codex-managed ChatGPT credentials for Quotalis only; separate from the user's normal Codex account.</summary>
    public static string ChatGptProfile => Path.Combine(Shared, "chatgpt-signin");

    /// <summary>Quotalis's Nous Portal sign-in, encrypted for this Windows account (see <see cref="NousTokenStore"/>).</summary>
    public static string NousSignIn => Path.Combine(Shared, "nous-signin.bin");

    /// <summary>The Hermes wallet bar's scale ("full" since the last top-up) and the last reading, numbers only.</summary>
    public static string HermesWallet => Path.Combine(Shared, "hermes-wallet.json");

    /// <summary>The last copy of a server's Hermes export (numbers, model and surface names only).</summary>
    public static string HermesUsageCache => Path.Combine(Root, "hermes-usage.json");

    public static string Settings(bool demo) => Path.Combine(Root, demo ? "settings.demo.json" : "settings.json");

    public static string Assets => Path.Combine(AppContext.BaseDirectory, "Assets");

    public static string Icon => Path.Combine(Assets, "Quotalis.ico");
}

/// <summary>
/// Command-line options: <c>--demo</c>, <c>--autostart</c>, <c>--settings[=section]</c>, and for development
/// <c>--snapshot=folder</c> (demo only: renders every state to PNG offscreen, then exits), with an
/// optional <c>--culture=en-US</c> so documentation renders use one number and date format.
/// </summary>
internal sealed record AppOptions(bool IsDemo, bool IsAutostart, string? SettingsSection, string? SnapshotDirectory = null, string? SnapshotCulture = null)
{
    public static AppOptions Parse(IEnumerable<string> args)
    {
        var list = args.Select(a => a.Trim()).ToList();
        var set = new HashSet<string>(list.Select(a => a.ToLowerInvariant()));
        string? settings = null;
        string? snapshot = null;
        string? culture = null;
        foreach (var arg in list)
        {
            if (arg.Equals("--settings", StringComparison.OrdinalIgnoreCase))
            {
                settings = "General";
            }
            else if (arg.StartsWith("--snapshot=", StringComparison.OrdinalIgnoreCase))
            {
                snapshot = arg["--snapshot=".Length..].Trim('"');
            }
            else if (arg.StartsWith("--culture=", StringComparison.OrdinalIgnoreCase))
            {
                culture = arg["--culture=".Length..].Trim('"');
            }
            else if (arg.StartsWith("--settings=", StringComparison.OrdinalIgnoreCase))
            {
                var name = arg["--settings=".Length..];
                settings = name.Length == 0 ? "General" : char.ToUpperInvariant(name[0]) + name[1..].ToLowerInvariant();
            }
        }

        var demo = set.Contains("--demo");
        var snapshotDirectory = demo ? snapshot : null;
        return new AppOptions(demo, set.Contains(Platform.Startup.AutostartService.Argument), settings, snapshotDirectory, snapshotDirectory is null ? null : culture);
    }
}

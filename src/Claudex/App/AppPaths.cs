namespace Claudex;

/// <summary>Local data locations. Everything stays under %LOCALAPPDATA%\Claudex.</summary>
internal static class AppPaths
{
    public static string Root { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Claudex");

    public static string Logs => Path.Combine(Root, "logs");

    public static string Database => Path.Combine(Root, "usage.db");

    /// <summary>
    /// Files that hooks and other programs write for Claudex: <c>%USERPROFILE%\.claudex</c>, outside AppData. Claude desktop
    /// is a packaged (MSIX) app, and Windows redirects new files its hooks create under AppData into Claude's private
    /// package folder, where a normally started Claudex never sees them. The user profile root is not redirected.
    /// </summary>
    public static string Shared { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claudex");

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
    /// that ran our hooks (for example <c>Packages\Claude_…\LocalCache\Local\Claudex</c>).
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
            var candidate = Path.Combine(dir, "LocalCache", "Local", "Claudex");
            if (Directory.Exists(candidate))
            {
                yield return candidate;
            }
        }
    }

    public static string FocusSession => Path.Combine(Root, "focus-session.json");

    /// <summary>The Awake session's recovery journal (the lid setting to put back).</summary>
    public static string AwakeSession => Path.Combine(Root, "awake-session.json");

    public static string Tasks => Path.Combine(Root, "tasks.json");

    public static string UiState => Path.Combine(Root, "ui-state.json");

    /// <summary>Plan usage read from claude.ai or handed over by Claude Code's status line (numbers only).</summary>
    public static string ClaudePlan => Path.Combine(Shared, "claude-plan.json");

    /// <summary>
    /// The browser profile of the claude.ai sign-in (<see cref="ClaudeWebReader"/>): claude.ai's cookies, on this PC only.
    /// In the user profile like <see cref="Shared"/>, so a Claudex started from a packaged app finds the same sign-in.
    /// Signing out clears it; it is never part of a build, an installer or the repository.
    /// </summary>
    public static string ClaudeWebProfile => Path.Combine(Shared, "claude-signin");

    /// <summary>Codex-managed ChatGPT credentials for Claudex only; separate from the user's normal Codex account.</summary>
    public static string ChatGptProfile => Path.Combine(Shared, "chatgpt-signin");

    public static string Settings(bool demo) => Path.Combine(Root, demo ? "settings.demo.json" : "settings.json");

    public static string Assets => Path.Combine(AppContext.BaseDirectory, "Assets");

    public static string Icon => Path.Combine(Assets, "Claudex.ico");
}

/// <summary>
/// Command-line options: <c>--demo</c>, <c>--autostart</c>, <c>--settings[=section]</c>, and for development
/// <c>--snapshot=folder</c> (demo only: renders every state to PNG offscreen, then exits), with an
/// optional <c>--culture=en-US</c> so documentation renders use one number and date format, and
/// <c>--homecloud-upload=path</c> (sends one file or folder through the Home Cloud drop pipeline, then
/// exits with 0 on success; reads the settings, never writes them; with <c>--demo</c> the demo settings).
/// </summary>
internal sealed record AppOptions(bool IsDemo, bool IsAutostart, string? SettingsSection, string? SnapshotDirectory = null, string? SnapshotCulture = null, string? HomeCloudUploadPath = null)
{
    public static AppOptions Parse(IEnumerable<string> args)
    {
        var list = args.Select(a => a.Trim()).ToList();
        var set = new HashSet<string>(list.Select(a => a.ToLowerInvariant()));
        string? settings = null;
        string? snapshot = null;
        string? culture = null;
        string? upload = null;
        foreach (var arg in list)
        {
            if (arg.StartsWith("--homecloud-upload=", StringComparison.OrdinalIgnoreCase))
            {
                var path = arg["--homecloud-upload=".Length..].Trim('"');
                upload = path.Length == 0 ? null : Path.GetFullPath(path);
            }
            else if (arg.Equals("--settings", StringComparison.OrdinalIgnoreCase))
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
        if (upload is not null)
        {
            // The upload debug run is a short pipeline run of its own: no snapshots, no Settings window.
            return new AppOptions(demo, false, null, HomeCloudUploadPath: upload);
        }

        var snapshotDirectory = demo ? snapshot : null;
        return new AppOptions(demo, set.Contains(Platform.Startup.AutostartService.Argument), settings, snapshotDirectory, snapshotDirectory is null ? null : culture);
    }
}

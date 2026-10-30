namespace Quotalis.Linux;

/// <summary>
/// Local data locations: <c>~/.local/share/quotalis</c> (or <c>$XDG_DATA_HOME/quotalis</c>) for the database, settings and
/// state, and <c>~/.quotalis</c> for files that hooks write. <c>QUOTALIS_HOME</c> moves both, for tests.
/// </summary>
internal static class AppPaths
{
    private static string? Override => Environment.GetEnvironmentVariable("QUOTALIS_HOME") is { Length: > 0 } home ? home : null;

    public static string Root => Override is { } home
        ? Path.Combine(home, "data")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "quotalis");

    public static string Shared => Override is { } home
        ? Path.Combine(home, "shared")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".quotalis");

    /// <summary>
    /// On Windows these folders would be the Windows app's own (its folder names differ only by case), so reading real data
    /// there needs <c>QUOTALIS_HOME</c>. A development run can then never touch an installed Quotalis.
    /// </summary>
    public static bool SafeForRealData => !OperatingSystem.IsWindows() || Override is not null;

    public static string Logs => Path.Combine(Root, "logs");

    public static string Database => Path.Combine(Root, "usage.db");

    public static string Settings => Path.Combine(Root, "settings.json");

    public static string State => Path.Combine(Root, "state.json");

    public static string ServiceLock => Path.Combine(Root, "service.lock");

    public static string PricingOverrides => Path.Combine(Shared, "pricing.json");

    public static string ClaudePlan => Path.Combine(Shared, "claude-plan.json");

    public static string CodexPlan => Path.Combine(Shared, "codex-plan.json");

    public static string CodexLimitLog => Path.Combine(Shared, "codex-limit-log.jsonl");

    /// <summary>Codex-managed ChatGPT credentials for Quotalis only; separate from the user's normal Codex account.</summary>
    public static string ChatGptProfile => Path.Combine(Shared, "chatgpt-signin");

    public static string HermesUsageCache => Path.Combine(Root, "hermes-usage.json");

    public static string UserConfig => Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } config
        ? config
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
}

using System.Text.Json.Serialization;
using Quotalis.Core.Modules;
using Quotalis.Core.Placement;

namespace Quotalis.Core.Settings;

public enum AppTheme
{
    System,
    Dark,
}

/// <summary>What Smart Auto-Hide does over a maximized browser.</summary>
public enum SmartHideBehavior
{
    /// <summary>Retract into the top screen edge as a small notch.</summary>
    Retract,

    /// <summary>Hide completely while the browser is maximized in front.</summary>
    Hide,
}

/// <summary>An app that makes the island step aside while it is in front (Settings, Auto-hide in apps).</summary>
public sealed class AutoHideApp
{
    /// <summary>Executable name without extension, lower case ("chrome").</summary>
    public string Process { get; set; } = string.Empty;

    /// <summary>Display name ("Google Chrome").</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Full path of the executable, when known (for its icon).</summary>
    public string? Path { get; set; }

    public bool Enabled { get; set; } = true;

    public AutoHideApp Clone() => (AutoHideApp)MemberwiseClone();
}

/// <summary>Where the Hermes column reads Hermes's token use.</summary>
public enum HermesUsageSource
{
    /// <summary>Only the wallet; no token use.</summary>
    Off,

    /// <summary>This PC's Hermes install (<c>%LOCALAPPDATA%\hermes\state.db</c>, read-only).</summary>
    Local,

    /// <summary>The export file on a server, copied over SSH with the user's own key (see tools/hermes).</summary>
    Remote,
}

/// <summary>User settings, persisted as JSON. Every property has a safe default.</summary>
public sealed class AppSettings
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    // General
    public bool StartWithWindows { get; set; }

    public bool AlwaysOnTop { get; set; } = true;

    public bool LaunchHidden { get; set; }

    /// <summary>The expanded island stays open when you click elsewhere (Esc or the shortcut still close it).</summary>
    public bool KeepIslandOpen { get; set; }

    /// <summary>
    /// The island rests as the usage strip (plan usage, the gold totals and the heatmap) instead of the pill, and opens
    /// in full while you interact. With <see cref="KeepIslandOpen"/> as well, only the strip is shown.
    /// </summary>
    public bool AlwaysDisplayUsage { get; set; }

    // Position
    /// <summary>Device name of the monitor (for example <c>\.\DISPLAY2</c>); null means the primary monitor.</summary>
    public string? MonitorDevice { get; set; }

    /// <summary>
    /// Stable id of the same monitor (its device interface path), which survives Windows renumbering
    /// the device names; null means the primary monitor or a monitor without an id.
    /// </summary>
    public string? MonitorId { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter<IslandAnchor>))]
    public IslandAnchor Anchor { get; set; } = IslandAnchor.TopCenter;

    /// <summary>Offset from the anchor position in device-independent pixels. Non-zero after a drag.</summary>
    public double OffsetX { get; set; }

    public double OffsetY { get; set; }

    [JsonIgnore]
    public bool HasCustomPosition => Math.Abs(OffsetX) > 0.5 || Math.Abs(OffsetY) > 0.5;

    // Modules
    public bool ClaudeEnabled { get; set; } = true;

    /// <summary>Signed in to claude.ai for plan usage (the sign-in itself lives in the browser profile, not here).</summary>
    public bool ClaudeWebSignedIn { get; set; }

    public bool CodexEnabled { get; set; } = true;

    /// <summary>Hermes Agent's column: the Nous wallet, the model and Hermes's token use. Off until turned on.</summary>
    public bool HermesEnabled { get; set; }

    /// <summary>Signed in to Nous Portal (the tokens themselves are encrypted in a file of their own, never here).</summary>
    public bool NousSignedIn { get; set; }

    /// <summary>Where Hermes's token use comes from (the wallet needs only the Nous sign-in).</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<HermesUsageSource>))]
    public HermesUsageSource HermesSource { get; set; } = HermesUsageSource.Local;

    /// <summary>Remote export over SSH (<c>user@host</c>), the private key file and the export's path on the server.</summary>
    public string HermesRemoteTarget { get; set; } = "";

    public string HermesRemoteKeyPath { get; set; } = "";

    public string HermesRemotePath { get; set; } = "outbox/hermes-usage.json";

    /// <summary>The wallet turns amber below this many dollars; 0 turns the warning off.</summary>
    public int HermesLowBalanceUsd { get; set; } = 5;

    public bool GitEnabled { get; set; } = true;

    /// <summary>Modules starred in the expanded island; the compact island features them (see CompactSelector).</summary>
    public List<string> FavoriteModules { get; set; } = [];

    /// <summary>Module names in tab order; see <see cref="ModuleCatalog.NormalizeOrder"/>.</summary>
    public List<string> ModuleOrder { get; set; } = ModuleCatalog.DefaultOrder.Select(m => m.ToString()).ToList();

    /// <summary>Repositories added by the user (working-tree folders).</summary>
    public List<string> GitRepositories { get; set; } = [];

    /// <summary>Repositories of recent Claude Code and Codex sessions, most recent first.</summary>
    public List<string> GitRecentRepositories { get; set; } = [];

    // Appearance
    [JsonConverter(typeof(JsonStringEnumConverter<AppTheme>))]
    public AppTheme Theme { get; set; } = AppTheme.System;

    // Smart Auto-Hide (top-center island over maximized browsers)
    public bool SmartHideEnabled { get; set; } = true;

    [JsonConverter(typeof(JsonStringEnumConverter<SmartHideBehavior>))]
    public SmartHideBehavior SmartHideBehavior { get; set; } = SmartHideBehavior.Retract;

    /// <summary>
    /// Executable names without extension, lower case (chrome, msedge, firefox, ...): the enabled
    /// entries of <see cref="AutoHideApps"/>, kept in sync when settings are saved.
    /// </summary>
    public List<string> SmartHideProcesses { get; set; } = ["chrome", "msedge", "firefox"];

    /// <summary>Every app rule, enabled or not (Settings, Auto-hide in apps).</summary>
    public List<AutoHideApp> AutoHideApps { get; set; } = [];

    /// <summary>Step aside only while the app's window is maximized (the original browser behavior).</summary>
    public bool AutoHideOnlyMaximized { get; set; } = true;

    // Keyboard
    /// <summary>The global shortcut toggles the island from anywhere.</summary>
    public bool GlobalShortcutEnabled { get; set; } = true;

    /// <summary>One of <see cref="ShortcutGesture.Presets"/>, by its text.</summary>
    public string GlobalShortcut { get; set; } = ShortcutGesture.Default.Text;

    // Peak hours (Anthropic's busy hours, weekdays in US Eastern time; announced as 8 AM to 2 PM in March 2026)
    public int PeakStartHour { get; set; } = 8;

    public int PeakEndHour { get; set; } = 14;

    // Updates
    /// <summary>Look for a new Quotalis release once a day (GitHub's public releases, no account).</summary>
    public bool CheckForUpdates { get; set; } = true;

    [JsonIgnore]
    public IReadOnlyList<ModuleId> Order => ModuleCatalog.NormalizeOrder(ModuleOrder);

    public bool IsEnabled(ModuleId module) => module switch
    {
        ModuleId.Claude => ClaudeEnabled,
        ModuleId.Codex => CodexEnabled,
        ModuleId.Git => GitEnabled,
        ModuleId.Hermes => HermesEnabled,
        _ => false,
    };

    public void SetEnabled(ModuleId module, bool enabled)
    {
        switch (module)
        {
            case ModuleId.Claude: ClaudeEnabled = enabled; break;
            case ModuleId.Codex: CodexEnabled = enabled; break;
            case ModuleId.Git: GitEnabled = enabled; break;
            case ModuleId.Hermes: HermesEnabled = enabled; break;
        }
    }

    public bool IsFavorite(ModuleId module) => FavoriteModules.Contains(module.ToString());

    public void SetFavorite(ModuleId module, bool favorite)
    {
        FavoriteModules.Remove(module.ToString());
        if (favorite)
        {
            FavoriteModules.Add(module.ToString());
        }
    }

    public AppSettings Clone()
    {
        var copy = (AppSettings)MemberwiseClone();
        copy.SmartHideProcesses = [.. SmartHideProcesses];
        copy.AutoHideApps = AutoHideApps.Select(a => a.Clone()).ToList();
        copy.ModuleOrder = [.. ModuleOrder];
        copy.FavoriteModules = [.. FavoriteModules];
        copy.GitRepositories = [.. GitRepositories];
        copy.GitRecentRepositories = [.. GitRecentRepositories];
        return copy;
    }
}

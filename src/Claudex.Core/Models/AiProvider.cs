namespace Claudex.Core.Models;

public enum AiProviderKind
{
    Claude,
    Codex,
}

public enum ProviderState
{
    /// <summary>The provider is starting or running its first scan.</summary>
    Starting,

    /// <summary>The tool is not installed or has never written local data.</summary>
    NotDetected,

    /// <summary>Data was read successfully.</summary>
    Ready,

    /// <summary>Reading failed; the rest of the app keeps working.</summary>
    Error,

    /// <summary>Turned off in Settings.</summary>
    Disabled,
}

/// <summary>One assistant response worth of usage metadata. Contains no prompt or response content.</summary>
public sealed record UsageEvent(
    AiProviderKind Provider,
    string Key,
    DateTimeOffset Timestamp,
    string Model,
    TokenCounts Tokens,
    string? SessionId,
    string? Project,
    string? ProjectPath = null);

/// <summary>What the UI shows for one AI tool.</summary>
public sealed record AiUsageSnapshot
{
    public required AiProviderKind Provider { get; init; }

    public ProviderState State { get; init; } = ProviderState.Starting;

    public DateOnly Day { get; init; }

    public TokenCounts Today { get; init; }

    /// <summary>Estimated API equivalent for today in USD; null when no model could be priced.</summary>
    public decimal? ApiValueUsd { get; init; }

    /// <summary>True when some of today's tokens came from models without a known price.</summary>
    public bool ApiValuePartial { get; init; }

    public int SessionsToday { get; init; }

    public string? CurrentModel { get; init; }

    public string? CurrentProject { get; init; }

    /// <summary>Working directory of the latest session; kept in memory only (feeds the Git module).</summary>
    public string? CurrentProjectPath { get; init; }

    /// <summary>True while a session wrote data recently or reports itself busy.</summary>
    public bool IsActive { get; init; }

    public DateTimeOffset? LastActivity { get; init; }

    /// <summary>Usage of the provider's rolling rate-limit window, when the tool reports it locally.</summary>
    public double? LimitPercent { get; init; }

    public int? LimitWindowMinutes { get; init; }

    public DateTimeOffset? LimitResetsAt { get; init; }

    /// <summary>The weekly window (Codex records it locally; Claude's comes through PlanUsage).</summary>
    public double? WeeklyLimitPercent { get; init; }

    public DateTimeOffset? WeeklyLimitResetsAt { get; init; }

    /// <summary>A current (5-hour) window was recorded and has reset since: 0% used until the next request.</summary>
    public bool LimitHasReset { get; init; }

    /// <summary>A weekly window was recorded and has reset since.</summary>
    public bool WeeklyLimitHasReset { get; init; }

    public string? ErrorMessage { get; init; }

    public static AiUsageSnapshot Initial(AiProviderKind provider) => new() { Provider = provider };
}

/// <summary>Aggregated usage of one provider on one local calendar day.</summary>
public sealed record DailyUsage(
    DateOnly Day,
    AiProviderKind Provider,
    TokenCounts Tokens,
    decimal ApiValueUsd,
    bool ApiValuePartial,
    int Sessions);

/// <summary>
/// Estimated API equivalent of Claude and Codex together, in USD, for the current week, the current month and
/// every stored day. A flag is set when some tokens in that range have no list price (the value covers the rest).
/// </summary>
public sealed record SpendTotals(decimal Week, decimal Month, decimal Lifetime, bool WeekPartial, bool MonthPartial, bool LifetimePartial, bool IncludesImported = false)
{
    public static readonly SpendTotals Zero = new(0m, 0m, 0m, false, false, false);
}

/// <summary>One cell of the usage history graph.</summary>
/// <param name="ClaudePartial">Some of Claude's tokens that day have no list price; its value covers the priced ones.</param>
/// <param name="CodexPartial">Some of Codex's tokens that day have no list price; its value covers the priced ones.</param>
/// <param name="CodexImported">Codex's figures that day come from a ChatGPT profile import: estimated, not measured.</param>
public sealed record UsageDay(
    DateOnly Day,
    TokenCounts Claude,
    TokenCounts Codex,
    decimal ClaudeApiValueUsd,
    decimal CodexApiValueUsd,
    int Sessions,
    bool ClaudePartial = false,
    bool CodexPartial = false,
    bool CodexImported = false)
{
    public bool ApiValuePartial => ClaudePartial || CodexPartial;

    /// <summary>Fresh tokens of both tools (drives the history graph intensity).</summary>
    public long FreshTokens => Claude.Fresh + Codex.Fresh;

    public long ProcessedTokens => Claude.Processed + Codex.Processed;

    public bool HasUsage => ProcessedTokens > 0;

    public decimal TotalApiValueUsd => ClaudeApiValueUsd + CodexApiValueUsd;
}

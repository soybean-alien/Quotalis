using System.Text.Json;
using System.Text.Json.Serialization;
using Quotalis.Core.Models;

namespace Quotalis.Linux;

internal sealed record PlanReading(double Percent, DateTimeOffset ResetsAt, bool Estimated = false);

/// <summary>What the bar and the terminal screen show for Claude or Codex.</summary>
internal sealed record ToolState
{
    public required string Name { get; init; }

    public ProviderState Status { get; init; } = ProviderState.Starting;

    public string? Note { get; init; }

    public PlanReading? FiveHour { get; init; }

    public PlanReading? Weekly { get; init; }

    public DateTimeOffset? MeasuredAt { get; init; }

    public decimal? TodayUsd { get; init; }

    public bool TodayPartial { get; init; }

    public long TodayTokens { get; init; }

    public int Sessions { get; init; }

    public string? Model { get; init; }

    public bool Active { get; init; }
}

/// <summary>Hermes's own numbers. Its money never joins the API-equivalent totals of Claude and Codex.</summary>
internal sealed record HermesState
{
    public bool Enabled { get; init; }

    public string? Problem { get; init; }

    public decimal TodayUsd { get; init; }

    public bool TodayEstimated { get; init; }

    public decimal WeekUsd { get; init; }

    public long TodayTokens { get; init; }

    public int Sessions { get; init; }

    public string? Model { get; init; }
}

internal sealed record DayState(DateOnly Day, UsageTool? Leader, long Tokens, decimal Usd);

internal sealed record RepoState(string Name, string? Branch, string? DetachedAt, int Ahead, int Behind, int Staged, int Modified, int Untracked, int Conflicts);

internal sealed record ContributionState(DateOnly Day, int Level);

internal sealed record GitState
{
    public bool Enabled { get; init; }

    public string? Note { get; init; }

    public IReadOnlyList<RepoState> Repositories { get; init; } = [];

    public string? ContributionsNote { get; init; }

    public int ContributionsTotal { get; init; }

    public IReadOnlyList<ContributionState> Contributions { get; init; } = [];
}

/// <summary>Everything the service publishes. Holds numbers and names only: no prompts, no paths, no account details.</summary>
internal sealed record AppState
{
    public required DateTimeOffset GeneratedAt { get; init; }

    public bool Demo { get; init; }

    public required ToolState Claude { get; init; }

    public required ToolState Codex { get; init; }

    public required HermesState Hermes { get; init; }

    public IReadOnlyList<DayState> Days { get; init; } = [];

    public decimal WeekUsd { get; init; }

    public decimal MonthUsd { get; init; }

    public bool TotalsPartial { get; init; }

    public GitState Git { get; init; } = new();

    public decimal TodayUsd => (Claude.TodayUsd ?? 0m) + (Codex.TodayUsd ?? 0m);
}

internal static class AppStateFile
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize(AppState state) => JsonSerializer.Serialize(state, Options);

    public static AppState? Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<AppState>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Null while the service has never written, or while it is replacing the file.</summary>
    public static AppState? Read(string path)
    {
        try
        {
            return File.Exists(path) ? Parse(File.ReadAllText(path)) : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Replaces the file in one step, readable by this user only, so a reader never sees half of it.</summary>
    public static void Write(string path, AppState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".part";
        File.WriteAllText(temp, Serialize(state));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        File.Move(temp, path, overwrite: true);
    }
}

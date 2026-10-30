using Quotalis.Core.Demo;
using Quotalis.Core.Git;
using Quotalis.Core.Hermes;
using Quotalis.Core.Models;
using Quotalis.Core.Plan;
using Quotalis.Core.Storage;
using Quotalis.Core.Usage;

namespace Quotalis.Linux;

/// <summary>Turns Core's models into the published state. No I/O beyond the history queries it is handed.</summary>
internal static class StateBuilder
{
    public const int HistoryWeeks = 26;

    private const DayOfWeek FirstDayOfWeek = DayOfWeek.Sunday;

    public static ToolState Claude(AiUsageSnapshot snapshot, PlanUsage? plan, PlanConnection connection, DateTimeOffset now)
    {
        string? note = null;
        if (plan is null && connection != PlanConnection.Connected)
        {
            note = connection == PlanConnection.OtherStatusLine
                ? "Claude Code's status line is used by another tool"
                : "Run quotalis setup to read Claude's plan";
        }

        return Tool("Claude", snapshot) with
        {
            Note = note ?? snapshot.ErrorMessage,
            FiveHour = plan?.Current is { } c ? new PlanReading(c.UsedPercent, c.ResetsAt, c.IsEstimated) : null,
            Weekly = plan?.Weekly is { } w ? new PlanReading(w.UsedPercent, w.ResetsAt, w.IsEstimated) : null,
            MeasuredAt = plan?.MeasuredAt,
        };
    }

    public static ToolState Codex(AiUsageSnapshot snapshot) => Tool("Codex", snapshot) with
    {
        Note = snapshot.ErrorMessage,
        FiveHour = snapshot.LimitPercent is { } p && snapshot.LimitResetsAt is { } r ? new PlanReading(p, r) : null,
        Weekly = snapshot.WeeklyLimitPercent is { } wp && snapshot.WeeklyLimitResetsAt is { } wr ? new PlanReading(wp, wr) : null,
    };

    private static ToolState Tool(string name, AiUsageSnapshot snapshot) => new()
    {
        Name = name,
        Status = snapshot.State,
        TodayUsd = snapshot.ApiValueUsd,
        TodayPartial = snapshot.ApiValuePartial,
        TodayTokens = snapshot.Today.Fresh,
        Sessions = snapshot.SessionsToday,
        Model = snapshot.CurrentModel,
        Active = snapshot.IsActive,
    };

    public static HermesState Hermes(bool enabled, HermesUsage? usage, string? problem, DateOnly today)
    {
        if (!enabled)
        {
            return new HermesState();
        }

        var day = usage?.DayOf(today);
        var weekStart = HeatmapLayout.WeekStart(today, FirstDayOfWeek);
        return new HermesState
        {
            Enabled = true,
            Problem = problem,
            TodayUsd = day?.CostUsd ?? 0m,
            TodayEstimated = day?.CostEstimated ?? false,
            WeekUsd = usage?.Days.Where(d => d.Day >= weekStart && d.Day <= today).Sum(d => d.CostUsd) ?? 0m,
            TodayTokens = day?.Tokens.Fresh ?? 0,
            Sessions = day?.Sessions ?? 0,
            Model = usage?.CurrentModel,
        };
    }

    public static IReadOnlyList<DayState> Days(UsageHistoryService history, HermesUsage? hermes, DateOnly today)
    {
        var days = HistoryWeeks * 7 + HeatmapLayout.RowOf(today, FirstDayOfWeek) + 1;
        return history.GetHistory(days).Select(d =>
        {
            var withHermes = hermes?.DayOf(d.Day) is { } h ? d with { Hermes = h.Tokens } : d;
            return new DayState(d.Day, withHermes.Leader, withHermes.FreshTokens, withHermes.TotalApiValueUsd);
        }).ToList();
    }

    public static GitState Git(GitService git, ContributionCalendar? calendar, string? calendarNote) => new()
    {
        Enabled = true,
        Note = git.Status.Message,
        Repositories = git.Repositories.Select(Repo).ToList(),
        ContributionsNote = calendarNote,
        ContributionsTotal = calendar?.Total ?? 0,
        Contributions = calendar?.LastWeeks(HistoryWeeks).Select(d => new ContributionState(d.Date, d.Level)).ToList() ?? [],
    };

    private static RepoState Repo(GitRepoStatus r) =>
        new(r.Name, r.Branch, r.DetachedAt, r.Ahead, r.Behind, r.Staged, r.Modified, r.Untracked, r.Conflicts);

    /// <summary>Made-up data, in memory only, so every screen can be seen without any account or log.</summary>
    public static AppState Demo(DateTimeOffset now)
    {
        using var database = UsageDatabase.OpenInMemory();
        var history = new UsageHistoryService(database, Quotalis.Core.Pricing.ModelPricingService.Load(Path.Combine(Path.GetTempPath(), "quotalis-demo-no-pricing.json")));
        DemoData.SeedHistory(history, TimeProvider.System);
        var today = history.Today;
        var hermes = DemoData.HermesUsage(now, TimeZoneInfo.Local);
        var totals = history.GetSpendTotals(FirstDayOfWeek);
        var calendar = DemoModules.Contributions(now);
        return new AppState
        {
            GeneratedAt = now,
            Demo = true,
            Claude = Claude(DemoData.ClaudeSnapshot(history), DemoData.ClaudePlan(now), PlanConnection.Connected, now),
            Codex = Codex(DemoData.CodexSnapshot(history)),
            Hermes = Hermes(true, hermes, null, today),
            Days = Days(history, hermes, today),
            WeekUsd = totals.Week,
            MonthUsd = totals.Month,
            TotalsPartial = totals.WeekPartial || totals.MonthPartial,
            Git = new GitState
            {
                Enabled = true,
                Repositories = DemoModules.Repositories(now).Select(Repo).ToList(),
                ContributionsTotal = calendar.Total,
                Contributions = calendar.LastWeeks(HistoryWeeks).Select(d => new ContributionState(d.Date, d.Level)).ToList(),
            },
        };
    }
}

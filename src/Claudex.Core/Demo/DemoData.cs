using Claudex.Core.Models;
using Claudex.Core.Providers;
using Claudex.Core.Storage;
using Claudex.Core.Usage;

namespace Claudex.Core.Demo;

/// <summary>
/// Mock data for <c>--demo</c>. It only ever lives in an in-memory database and in-memory providers,
/// so it can never leak into the real usage history.
/// </summary>
public static class DemoData
{
    /// <summary>Demo plan usage: 68% of the current window (as the demo snapshot reports), 41% of the week.</summary>
    public static Plan.PlanUsage ClaudePlan(DateTimeOffset now) => new(
        new Plan.PlanWindow(68, now.AddHours(2).AddMinutes(14)),
        new Plan.PlanWindow(41, now.AddDays(4).AddHours(3)),
        now);

    public const string ClaudeModel = "claude-opus-5-5";
    public const string CodexModel = "gpt-5-codex";
    public const string HermesModel = "example/hermes-demo-model";

    /// <summary>A made-up Nous wallet: $18.42 left of $25.00 (the scale is set by the caller).</summary>
    public static Hermes.NousWalletReading HermesWallet(DateTimeOffset now) =>
        new(18.42m, 6.25m, 12.17m, 20m, "Plus", now.AddDays(26), now);

    /// <summary>About 26 weeks of made-up Hermes token use, lighter than Claude's, with today pinned.</summary>
    public static Hermes.HermesUsage HermesUsage(DateTimeOffset now, TimeZoneInfo zone)
    {
        var random = new Random(2085);
        var rows = new List<Hermes.HermesModelRow>();
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        for (var back = 181; back >= 0; back--)
        {
            if (back > 0 && random.NextDouble() > 0.5)
            {
                continue;
            }

            var day = today.AddDays(-back);
            var input = back == 0 ? 141_200 : (long)(random.NextDouble() * 220_000);
            var at = new DateTimeOffset(day.ToDateTime(new TimeOnly(back == 0 ? 0 : 14, back == 0 ? 5 : 0)), zone.GetUtcOffset(day.ToDateTime(TimeOnly.MinValue)));
            rows.Add(new Hermes.HermesModelRow($"demo-{back}", HermesModel, "", back == 0 ? 96 : 10 + random.Next(80),
                new TokenCounts(input, input / 4, back == 0 ? 12_000 : 0, 0, input * 4), input / 8, back == 0 ? 1.27m : (decimal)(input / 150_000.0), 0m, at));
        }

        return new Hermes.HermesUsage
        {
            GeneratedAt = now,
            SchemaVersion = 32,
            LastActivity = now.AddMinutes(-1),
            CurrentModel = HermesModel,
            CurrentModelSeen = now.AddMinutes(-1),
            Days = Hermes.HermesUsageParser.Aggregate(rows, zone),
        };
    }

    /// <summary>Fills the (in-memory) history with about 26 weeks of plausible, uneven usage.</summary>
    public static void SeedHistory(UsageHistoryService history, UsageDatabase focusDb, TimeProvider time)
    {
        var random = new Random(1847);
        var today = history.Today;
        var events = new List<UsageEvent>();
        var zoneOffset = time.GetLocalNow().Offset;

        for (var back = 181; back >= 0; back--)
        {
            var day = today.AddDays(-back);
            var weekend = day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
            var activity = weekend ? 0.25 : 0.85;
            if (back > 0 && random.NextDouble() > activity)
            {
                continue;
            }

            // Today is pinned so screenshots show the documented demo values.
            var claudeTarget = back == 0 ? 2_683_950 : (long)(random.NextDouble() * random.NextDouble() * 3_400_000);
            var codexTarget = back == 0 ? 618_950 : (random.NextDouble() < 0.55 ? (long)(random.NextDouble() * 1_100_000) : 0);
            AddDay(events, AiProviderKind.Claude, ClaudeModel, day, zoneOffset, claudeTarget, random, sessions: back == 0 ? 3 : 1 + random.Next(4));
            AddDay(events, AiProviderKind.Codex, CodexModel, day, zoneOffset, codexTarget, random, sessions: back == 0 ? 2 : 1 + random.Next(2));

            if (back > 0 && !weekend && random.NextDouble() < 0.6)
            {
                var start = new DateTimeOffset(day.ToDateTime(new TimeOnly(9 + random.Next(8), 0)), zoneOffset);
                var minutes = new[] { 25, 50, 90 }[random.Next(3)];
                focusDb.InsertFocusSession(new FocusSessionRecord(start, start.AddMinutes(minutes), day, minutes * 60, minutes * 60, true));
            }
        }

        foreach (var chunk in events.Chunk(4000))
        {
            history.Ingest(chunk);
        }
    }

    public static AiUsageSnapshot ClaudeSnapshot(UsageHistoryService history) => Snapshot(history, AiProviderKind.Claude) with
    {
        LimitPercent = 68,
        LimitWindowMinutes = 300,
        CurrentProject = "claudex",
        IsActive = true,
    };

    public static AiUsageSnapshot CodexSnapshot(UsageHistoryService history) => Snapshot(history, AiProviderKind.Codex) with
    {
        LimitPercent = 31,
        LimitWindowMinutes = 300,
        LimitResetsAt = DateTimeOffset.UtcNow.AddHours(3).AddMinutes(40),
        WeeklyLimitPercent = 12,
        WeeklyLimitResetsAt = DateTimeOffset.UtcNow.AddDays(5),
        CurrentProject = "api-gateway",
    };

    private static AiUsageSnapshot Snapshot(UsageHistoryService history, AiProviderKind kind)
    {
        var totals = history.GetTodayTotals(kind);
        return new AiUsageSnapshot
        {
            Provider = kind,
            State = ProviderState.Ready,
            Day = history.Today,
            Today = totals.Tokens,
            ApiValueUsd = totals.DisplayValueUsd,
            SessionsToday = totals.Sessions,
            CurrentModel = totals.Latest?.Model,
            LastActivity = totals.Latest?.Timestamp,
        };
    }

    private static void AddDay(List<UsageEvent> events, AiProviderKind provider, string model, DateOnly day, TimeSpan offset, long target, Random random, int sessions)
    {
        if (target <= 0)
        {
            return;
        }

        const int perDay = 12;
        var remaining = target;
        for (var i = 0; i < perDay && remaining > 0; i++)
        {
            var share = i == perDay - 1 ? remaining : Math.Min(remaining, (long)(target / perDay * (0.6 + random.NextDouble() * 0.8)));
            remaining -= share;

            // Typical agent traffic: mostly cache reads, a little fresh input, some output.
            var cacheRead = (long)(share * 0.86);
            var cacheWrite = (long)(share * 0.07);
            var output = (long)(share * 0.03);
            var input = share - cacheRead - cacheWrite - output;
            var time = new DateTimeOffset(day.ToDateTime(new TimeOnly(9, 0)), offset).AddMinutes(i * 37 + random.Next(20));
            events.Add(new UsageEvent(
                provider,
                $"demo:{provider}:{day:yyyyMMdd}:{i}",
                time,
                model,
                new TokenCounts(input, output, cacheWrite, 0, cacheRead),
                $"demo-session-{i % sessions}",
                provider == AiProviderKind.Claude ? "claudex" : "api-gateway"));
        }
    }
}

/// <summary>An AI provider that serves a fixed demo snapshot and one sample activity.</summary>
public sealed class DemoAiProvider : IAiUsageProvider
{
    private readonly TimeProvider _time;
    private ITimer? _timer;

    public DemoAiProvider(AiUsageSnapshot snapshot, TimeProvider? time = null)
    {
        Snapshot = snapshot;
        _time = time ?? TimeProvider.System;
    }

    public event Action<AiUsageSnapshot>? SnapshotChanged;

    public event Action<AiActivity>? Activity;

    public AiProviderKind Kind => Snapshot.Provider;

    public AiUsageSnapshot Snapshot { get; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        SnapshotChanged?.Invoke(Snapshot);
        if (Kind == AiProviderKind.Claude)
        {
            _timer = _time.CreateTimer(
                _ => Activity?.Invoke(new AiActivity(Kind, AiActivityKind.SessionStarted, "Claude Code session started", "claudex")),
                null,
                TimeSpan.FromSeconds(14),
                Timeout.InfiniteTimeSpan);
        }

        return Task.CompletedTask;
    }

    public void Dispose() => _timer?.Dispose();
}

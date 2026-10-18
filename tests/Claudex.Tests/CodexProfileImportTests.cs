using Claudex.Core.Models;
using Claudex.Core.Pricing;
using Claudex.Core.Storage;
using Claudex.Core.Usage;
using Microsoft.Extensions.Time.Testing;

namespace Claudex.Tests;

public class CodexProfileImportTests
{
    private const string Profile = """
        {"page":{"activity_graph":{"daily_usage_buckets":[
          {"start_date":"2026-08-23","tokens":18201203,"chat_turns":0},
          {"start_date":"2026-10-09","tokens":12137813,"chat_turns":0},
          {"start_date":"2026-10-10","tokens":54151781,"chat_turns":0},
          {"start_date":"2026-10-11","tokens":0,"chat_turns":0}]}}}
        """;

    [Fact]
    public void Imports_only_days_without_a_local_measurement()
    {
        using var db = UsageDatabase.OpenInMemory();
        var measured = new DailyUsage(new DateOnly(2026, 10, 10), AiProviderKind.Codex, new TokenCounts(1_000_000, 100_000, 0, 0, 20_000_000), 8.77m, false, 3);
        db.UpsertDaily(measured);

        var days = CodexProfileImport.Parse(Profile);
        var result = CodexProfileImport.Import(db, days, TokenMix.Default, DateTimeOffset.UtcNow);

        Assert.Equal(3, days.Count); // the zero day is dropped
        Assert.Equal(2, result.Imported);
        Assert.Equal(1, result.Skipped);
        var rows = db.GetDaily(new DateOnly(2026, 8, 1), new DateOnly(2026, 10, 31)).ToDictionary(d => d.Day);
        Assert.Equal(8.77m, rows[new DateOnly(2026, 10, 10)].ApiValueUsd); // measured day untouched
        var august = rows[new DateOnly(2026, 8, 23)];
        Assert.Equal(18_201_203, august.Tokens.Processed);
        Assert.InRange(august.ApiValueUsd, 5.6m, 5.7m); // 18.2M at $0.31 per million
        Assert.Equal(2, db.GetImportedDays(new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31)).Count);
    }

    [Fact]
    public void A_later_local_measurement_replaces_an_imported_day()
    {
        using var db = UsageDatabase.OpenInMemory();
        CodexProfileImport.Import(db, CodexProfileImport.Parse(Profile), TokenMix.Default, DateTimeOffset.UtcNow);
        var day = new DateOnly(2026, 10, 9);

        db.UpsertDaily(new DailyUsage(day, AiProviderKind.Codex, new TokenCounts(10, 10, 0, 0, 10), 1m, false, 1));

        Assert.DoesNotContain((day, AiProviderKind.Codex), db.GetImportedDays(day, day));
        Assert.True(db.HasMeasuredDaily(day, AiProviderKind.Codex));
    }

    [Fact]
    public void History_and_totals_say_which_days_are_imported()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero));
        using var db = UsageDatabase.OpenInMemory();
        CodexProfileImport.Import(db, CodexProfileImport.Parse(Profile), TokenMix.Default, DateTimeOffset.UtcNow);
        var history = new UsageHistoryService(db, new ModelPricingService(), time, TimeZoneInfo.Utc);

        var days = history.GetHistory(70).ToDictionary(d => d.Day);

        Assert.True(days[new DateOnly(2026, 10, 9)].CodexImported);
        Assert.False(days[new DateOnly(2026, 10, 12)].CodexImported);
        Assert.True(history.GetSpendTotals(DayOfWeek.Sunday).IncludesImported);
        Assert.Equal(0, history.Reprice()); // imported days have no events and are never re-priced away
    }

    [Fact]
    public void The_token_mix_is_measured_from_local_days()
    {
        var local = new[] { new DailyUsage(new DateOnly(2026, 10, 10), AiProviderKind.Codex, new TokenCounts(400_000, 100_000, 0, 0, 1_500_000), 0.6m, false, 1) };

        var mix = TokenMix.FromLocal(local);

        Assert.Equal(0.2, mix.Input, 3);
        Assert.Equal(0.05, mix.Output, 3);
        Assert.Equal(0.75, mix.CacheRead, 3);
        Assert.Equal(0.3m, mix.UsdPerMillion);
    }
}

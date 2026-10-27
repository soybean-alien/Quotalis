using System.Text.Json;
using Quotalis.Core.Pricing;
using Quotalis.Core.Providers.Codex;
using Quotalis.Core.Storage;
using Quotalis.Core.Usage;

namespace Quotalis.Tests;

public class CodexLimitPublisherTests
{
    [Fact]
    public void Account_refresh_updates_live_plan_without_history_and_signout_clears_it()
    {
        var dir = TestData.TempDirectory();
        var plan = Path.Combine(dir, "codex-plan.json");
        var log = Path.Combine(dir, "codex-limit-log.jsonl");
        var publisher = new CodexLimitPublisher(plan, log);
        var account = new CodexRateLimit(38, 300, DateTimeOffset.UtcNow.AddHours(2), DateTimeOffset.UtcNow);

        publisher.Publish(null, null, account, null);
        Assert.Equal(38, JsonDocument.Parse(File.ReadAllText(plan)).RootElement.GetProperty("current").GetProperty("usedPercent").GetDouble());
        Assert.False(File.Exists(log));

        publisher.Publish(null, null, null, null);
        Assert.True(File.Exists(plan)); // a scan with nothing new yet keeps the plan

        publisher.Publish(null, null, null, null, clearWhenEmpty: true);
        Assert.False(File.Exists(plan));
    }

    private static string TokenCountWithWeekly(DateTimeOffset ts, double five, long fiveResets, double weekly, long weeklyResets) => JsonSerializer.Serialize(new
    {
        timestamp = ts.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
        type = "event_msg",
        payload = new
        {
            type = "token_count",
            info = new
            {
                total_token_usage = new { input_tokens = 100, cached_input_tokens = 0, output_tokens = 0, total_tokens = 100 },
                last_token_usage = new { input_tokens = 100, cached_input_tokens = 0, output_tokens = 0, total_tokens = 100 },
            },
            rate_limits = new
            {
                limit_id = "codex",
                primary = new { used_percent = five, window_minutes = 300, resets_at = fiveResets },
                secondary = new { used_percent = weekly, window_minutes = 10080, resets_at = weeklyResets },
            },
        },
    });

    [Fact]
    public async Task Provider_publishes_newest_plan_and_logs_each_reading_once()
    {
        var root = TestData.TempDirectory();
        var sessions = Directory.CreateDirectory(Path.Combine(root, "sessions")).FullName;
        var shared = Path.Combine(root, "shared");
        var now = DateTimeOffset.UtcNow;
        var fiveReset = now.AddHours(2).ToUnixTimeSeconds();
        var weekReset = now.AddDays(3).ToUnixTimeSeconds();

        // An older session and a newer one; the newer one's numbers must win in the plan file.
        File.WriteAllLines(Path.Combine(sessions, "rollout-a.jsonl"),
        [
            TestData.CodexMeta("s1", @"C:\code\a"),
            TokenCountWithWeekly(now.AddHours(-3), 40, fiveReset, 20, weekReset),
        ]);
        File.WriteAllLines(Path.Combine(sessions, "rollout-b.jsonl"),
        [
            TestData.CodexMeta("s2", @"C:\code\b"),
            TokenCountWithWeekly(now.AddMinutes(-5), 12, fiveReset, 25, weekReset),
        ]);

        using var db = UsageDatabase.Open(Path.Combine(root, "usage.db"));
        var history = new UsageHistoryService(db, new ModelPricingService());
        var publisher = new CodexLimitPublisher(Path.Combine(shared, "codex-plan.json"), Path.Combine(shared, "codex-limit-log.jsonl"));
        using (var provider = new CodexUsageProvider(root, history, db, publisher: publisher))
        {
            await provider.StartAsync(TestContext.Current.CancellationToken);
        }

        using var plan = JsonDocument.Parse(File.ReadAllText(publisher.PlanPath));
        Assert.Equal(12, plan.RootElement.GetProperty("current").GetProperty("usedPercent").GetDouble());
        Assert.Equal(300, plan.RootElement.GetProperty("current").GetProperty("windowMinutes").GetInt32());
        Assert.Equal(25, plan.RootElement.GetProperty("weekly").GetProperty("usedPercent").GetDouble());
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(weekReset), plan.RootElement.GetProperty("weekly").GetProperty("resetsAt").GetDateTimeOffset());

        var lines = File.ReadAllLines(publisher.LogPath);
        Assert.Equal(2, lines.Length);
        var percents = lines.Select(l => JsonDocument.Parse(l).RootElement.GetProperty("weekly").GetProperty("percent").GetDouble()).Order().ToArray();
        Assert.Equal([20d, 25d], percents);

        // Publishing the same readings again (a restart) adds nothing.
        var again = new CodexLimitPublisher(publisher.PlanPath, publisher.LogPath);
        var limit = new CodexRateLimit(12, 300, DateTimeOffset.FromUnixTimeSeconds(fiveReset), DateTimeOffset.Parse(JsonDocument.Parse(lines.Max()!).RootElement.GetProperty("at").GetString()!));
        again.Publish(limit, null, limit, null);
        Assert.Equal(2, File.ReadAllLines(publisher.LogPath).Length);
    }

    [Fact]
    public void Old_and_torn_log_lines_are_dropped_on_first_use()
    {
        var dir = TestData.TempDirectory();
        var log = Path.Combine(dir, "codex-limit-log.jsonl");
        var now = DateTimeOffset.UtcNow;
        File.WriteAllLines(log,
        [
            JsonSerializer.Serialize(new { at = now.AddDays(-60), five_hour = new { percent = 1, window_minutes = 300 } }),
            "{\"at\":\"torn",
            JsonSerializer.Serialize(new { at = now.AddHours(-1), five_hour = new { percent = 2, window_minutes = 300 } }),
        ]);
        var publisher = new CodexLimitPublisher(Path.Combine(dir, "codex-plan.json"), log);
        var reading = new CodexRateLimit(3, 300, null, now);
        publisher.Publish(reading, null, reading, null);

        var lines = File.ReadAllLines(log);
        Assert.Equal(2, lines.Length);
        Assert.All(lines, l => Assert.True(JsonDocument.Parse(l).RootElement.GetProperty("five_hour").GetProperty("percent").GetDouble() >= 2));
        Assert.Equal(3, JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "codex-plan.json"))).RootElement.GetProperty("current").GetProperty("usedPercent").GetDouble());
    }
}

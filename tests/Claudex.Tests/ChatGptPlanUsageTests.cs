using Claudex.Core.Usage;

namespace Claudex.Tests;

public class ChatGptPlanUsageTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Reads_all_returned_buckets_and_the_shared_windows()
    {
        var usage = ChatGptPlanUsageParser.Parse("""
        {
          "accountId": "do-not-store-this",
          "rateLimitsByLimitId": {
            "codex": {
              "primary": {"usedPercent": 7, "windowDurationMins": 300, "resetsAt": 1792748123},
              "secondary": {"usedPercent": 1, "windowDurationMins": 10080, "resetsAt": 1793303178}
            },
            "special_model": {
              "limitName": "Special model",
              "primary": {"usedPercent": 25, "windowDurationMins": 10080, "resetsAt": 1793303178}
            }
          }
        }
        """, Now);

        Assert.NotNull(usage);
        Assert.Equal(2, usage.Buckets.Count);
        Assert.Equal(7, usage.Shared!.Current!.UsedPercent);
        Assert.Equal(1, usage.Shared.Weekly!.UsedPercent);
        Assert.Equal("Special model", usage.Buckets[1].Name);
        Assert.Null(usage.Buckets[1].Current);
        Assert.Equal(25, usage.Buckets[1].Weekly!.UsedPercent);
        Assert.DoesNotContain("do-not-store-this", usage.ToString());
    }

    [Fact]
    public void Uses_the_single_bucket_when_an_older_server_omits_the_map()
    {
        var usage = ChatGptPlanUsageParser.Parse("""
        {"rateLimits": {"primary": {"usedPercent": 42, "windowDurationMins": 300, "resetsAt": 1792748123}}}
        """, Now);

        Assert.Equal(42, usage?.Shared?.Current?.UsedPercent);
        Assert.Null(usage?.Shared?.Weekly);
    }

    [Fact]
    public void Rejects_missing_or_invalid_usage_without_making_up_a_percentage()
    {
        Assert.Null(ChatGptPlanUsageParser.Parse("{", Now));
        Assert.Null(ChatGptPlanUsageParser.Parse("{}", Now));
        Assert.Null(ChatGptPlanUsageParser.Parse("""
            {"rateLimitsByLimitId":{"codex":{"primary":{"usedPercent":135,"windowDurationMins":300}}}}
            """, Now));
    }

    [Fact]
    public void Does_not_label_a_nonweekly_secondary_window_as_weekly()
    {
        var usage = ChatGptPlanUsageParser.Parse("""
            {"rateLimits":{"primary":{"usedPercent":10,"windowDurationMins":300},"secondary":{"usedPercent":20,"windowDurationMins":1440}}}
            """, Now);

        Assert.Equal(10, usage?.Shared?.Current?.UsedPercent);
        Assert.Null(usage?.Shared?.Weekly);
    }
}

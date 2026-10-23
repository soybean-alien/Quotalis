using Claudex.Core.Plan;
using Xunit;

namespace Claudex.Tests;

/// <summary>claude.ai's usage answers. Every id and number here is made up.</summary>
public class ClaudeWebUsageTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 16, 0, 0, TimeSpan.Zero);

    private const string OrgA = "00000000-0000-4000-8000-00000000000a";
    private const string OrgB = "00000000-0000-4000-8000-00000000000b";

    [Fact]
    public void Reads_both_windows_and_ignores_the_rest()
    {
        var usage = ClaudeWebUsage.Parse("""
            { "five_hour": { "utilization": 34.0, "resets_at": "2026-10-22T19:00:00.123+00:00" },
              "seven_day": { "utilization": 12, "resets_at": "2026-10-27T06:00:00Z" },
              "seven_day_opus": null, "extra_usage": { "is_enabled": false } }
            """, Now);

        Assert.NotNull(usage);
        Assert.Equal(34, usage.Current!.UsedPercent);
        Assert.Equal(new DateTimeOffset(2026, 10, 22, 19, 0, 0, 123, TimeSpan.Zero), usage.Current.ResetsAt);
        Assert.Equal(12, usage.Weekly!.UsedPercent);
        Assert.False(usage.Current.IsEstimated);
        Assert.Equal(Now, usage.MeasuredAt);
    }

    [Fact]
    public void A_window_without_a_reset_time_is_left_out_not_guessed()
    {
        var usage = ClaudeWebUsage.Parse("""
            { "five_hour": { "utilization": 0, "resets_at": null }, "seven_day": { "utilization": 140, "resets_at": "2026-10-27T06:00:00Z" } }
            """, Now);

        Assert.Null(usage!.Current);
        Assert.Equal(100, usage.Weekly!.UsedPercent);
    }

    [Theory]
    [InlineData("""{ "seven_day": { "utilization": 12, "resets_at": "2026-10-27T06:00:00Z" } }""")]
    [InlineData("""{ "fiveHour": 3, "sevenDay": 4 }""")]
    [InlineData("""[]""")]
    [InlineData("""<html>Just a moment...</html>""")]
    [InlineData("")]
    public void An_unknown_format_shows_nothing(string body)
    {
        Assert.Null(ClaudeWebUsage.Parse(body, Now));
    }

    [Fact]
    public void Picks_the_last_used_organization_then_one_with_chat()
    {
        var orgs = $$"""
            [ { "uuid": "{{OrgA}}", "name": "API", "capabilities": ["api"] },
              { "uuid": "{{OrgB}}", "name": "Personal", "capabilities": ["chat", "claude_max"] } ]
            """;

        Assert.Equal(OrgB, ClaudeWebUsage.PickOrganization(orgs, null));
        Assert.Equal(OrgA, ClaudeWebUsage.PickOrganization(orgs, OrgA));
        Assert.Equal(OrgB, ClaudeWebUsage.PickOrganization(orgs, "00000000-0000-4000-8000-0000000000ff"));
        Assert.Null(ClaudeWebUsage.PickOrganization("""{ "error": "nope" }""", null));
        Assert.Null(ClaudeWebUsage.PickOrganization("""[ { "uuid": "../../etc" } ]""", null));
    }

    [Fact]
    public void Usage_addresses_are_built_from_guids_only()
    {
        Assert.Equal($"https://claude.ai/api/organizations/{OrgA}/usage", ClaudeWebUsage.UsageUrl(OrgA));
        Assert.Null(ClaudeWebUsage.UsageUrl("x/../../account"));
        Assert.Null(ClaudeWebUsage.UsageUrl(null));
    }

    [Fact]
    public void Only_claude_ai_own_refusal_means_signed_out()
    {
        Assert.True(ClaudeWebUsage.IsSignInRefused(401, """{ "type": "error", "error": { "type": "authentication_error" } }"""));
        Assert.True(ClaudeWebUsage.IsSignInRefused(403, """{ "type": "error" }"""));
        Assert.False(ClaudeWebUsage.IsSignInRefused(403, "<html>Just a moment...</html>"));
        Assert.False(ClaudeWebUsage.IsSignInRefused(500, """{ "type": "error" }"""));
        Assert.False(ClaudeWebUsage.IsSignInRefused(200, "{}"));
    }
}

public class ClaudeWebScheduleTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 16, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Reads_at_once_then_every_two_minutes()
    {
        var schedule = new ClaudeWebSchedule();
        Assert.Equal(Now, schedule.Next(Now));

        schedule.OnAttempt(Now, succeeded: true);
        Assert.Equal(Now + ClaudeWebSchedule.Idle, schedule.Next(Now));
    }

    [Fact]
    public void A_reply_brings_the_next_read_forward_without_hammering()
    {
        var schedule = new ClaudeWebSchedule();
        schedule.OnAttempt(Now, succeeded: true);

        schedule.OnActivity(Now + TimeSpan.FromSeconds(60));
        Assert.Equal(Now + TimeSpan.FromSeconds(64), schedule.Next(Now + TimeSpan.FromSeconds(60)));

        schedule.OnActivity(Now + TimeSpan.FromSeconds(62)); // more writes do not push it back
        Assert.Equal(Now + TimeSpan.FromSeconds(64), schedule.Next(Now + TimeSpan.FromSeconds(62)));

        var again = new ClaudeWebSchedule();
        again.OnAttempt(Now, succeeded: true);
        again.OnActivity(Now + TimeSpan.FromSeconds(2));
        Assert.Equal(Now + ClaudeWebSchedule.MinGap, again.Next(Now + TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void Failures_back_off_and_ignore_activity()
    {
        var schedule = new ClaudeWebSchedule();
        schedule.OnAttempt(Now, succeeded: false);
        schedule.OnActivity(Now);
        Assert.Equal(Now + TimeSpan.FromMinutes(2), schedule.Next(Now));

        for (var i = 0; i < 8; i++)
        {
            schedule.OnAttempt(Now, succeeded: false);
        }

        Assert.Equal(Now + ClaudeWebSchedule.MaxBackoff, schedule.Next(Now));

        schedule.Reset();
        Assert.Equal(Now, schedule.Next(Now));
    }
}

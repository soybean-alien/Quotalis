using Quotalis.Core.Plan;
using Xunit;

namespace Quotalis.Tests;

/// <summary>claude.ai's limit-reset answers. Every id, label and date here is made up.</summary>
public class ClaudeResetGrantsTests
{
    [Fact]
    public void Adds_up_resets_left_across_grants()
    {
        var available = ClaudeResetGrants.ParseAvailable("""
            { "five_hour": null, "cedar_ember": { "eligible": true, "grants": [
                { "id": "a", "resets_total": 1, "resets_left": 1, "usable_now": true },
                { "id": "b", "resets_total": 2, "resets_left": 2, "usable_now": false },
                { "id": "c", "resets_total": 1, "resets_left": 0 } ] } }
            """);

        Assert.Equal(3, available);
    }

    [Fact]
    public void Reads_the_grants_object_on_its_own()
    {
        Assert.Equal(1, ClaudeResetGrants.ParseAvailable("""{ "eligible": true, "grants": [ { "resets_left": 1 } ] }"""));
    }

    [Fact]
    public void No_grants_object_means_none_banked_and_an_unknown_shape_means_unknown()
    {
        Assert.Equal(0, ClaudeResetGrants.ParseAvailable("""{ "cedar_ember": null }"""));
        Assert.Null(ClaudeResetGrants.ParseAvailable("""{ "five_hour": {} }"""));
        Assert.Null(ClaudeResetGrants.ParseAvailable("not json"));
    }

    [Fact]
    public void Url_needs_a_guid_organization()
    {
        Assert.Null(ClaudeResetGrants.Url("nope"));
        Assert.EndsWith("/usage?cedar_ember=1&skip_spend=1", ClaudeResetGrants.Url("00000000-0000-4000-8000-00000000000a"));
    }

    [Fact]
    public void Saves_numbers_and_dates_only()
    {
        var path = Path.Combine(Path.GetTempPath(), "quotalis-test-" + Guid.NewGuid().ToString("N"), "claude-resets.json");
        var weekly = new DateTimeOffset(2026, 10, 27, 6, 0, 0, TimeSpan.Zero);
        ClaudeResetGrants.Save(path, new ClaudeResetCount(2, weekly, weekly.AddDays(-3)));
        var text = File.ReadAllText(path);
        Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);

        Assert.Equal("""{"available":2,"weeklyResetsAt":"2026-10-27T06:00:00+00:00","measuredAt":"2026-10-24T06:00:00+00:00"}""", text);
    }
}

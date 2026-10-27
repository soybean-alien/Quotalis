using Quotalis.Core.Git;
using Xunit;

namespace Quotalis.Tests;

/// <summary>GitHub's contribution calendar answer. The account and counts here are made up.</summary>
public class GitHubContributionsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

    private const string Answer = """
        { "data": { "viewer": { "login": "someone", "contributionsCollection": { "contributionCalendar": { "totalContributions": 9,
          "weeks": [ { "contributionDays": [
            { "date": "2026-10-18", "contributionCount": 0, "contributionLevel": "NONE" },
            { "date": "2026-10-19", "contributionCount": 6, "contributionLevel": "FOURTH_QUARTILE" } ] },
            { "contributionDays": [
            { "date": "2026-10-25", "contributionCount": 3, "contributionLevel": "SECOND_QUARTILE" } ] } ] } } } } }
        """;

    [Fact]
    public void Reads_days_counts_and_GitHub_levels()
    {
        var calendar = GitHubContributions.Parse(Answer, Now)!;

        Assert.Equal("someone", calendar.Login);
        Assert.Equal(9, calendar.Total);
        Assert.Equal(3, calendar.Days.Count);
        Assert.Equal(new ContributionDay(new DateOnly(2026, 10, 19), 6, 4), calendar.Days[1]);
        Assert.Equal(2, calendar.Days[2].Level);
    }

    [Fact]
    public void The_last_weeks_are_whole_columns_ending_with_today()
    {
        var calendar = GitHubContributions.Parse(Answer, Now)!;

        Assert.Single(calendar.LastWeeks(1));              // only the week of Sunday 2026-10-25
        Assert.Equal(3, calendar.LastWeeks(2).Count);
    }

    [Theory]
    [InlineData("""{ "errors": [ { "message": "bad" } ] }""")]
    [InlineData("not json")]
    public void Anything_else_is_not_a_calendar(string json) => Assert.Null(GitHubContributions.Parse(json, Now));
}

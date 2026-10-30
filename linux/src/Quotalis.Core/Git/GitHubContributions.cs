using System.Globalization;
using System.Text.Json;
using Quotalis.Core.Diagnostics;

namespace Quotalis.Core.Git;

/// <summary>One day of the GitHub contribution calendar: its count and GitHub's own level (0 none, 1 to 4 quartiles).</summary>
public sealed record ContributionDay(DateOnly Date, int Count, int Level);

/// <summary>The signed-in account's contribution calendar, as its GitHub profile shows it.</summary>
public sealed record ContributionCalendar(string Login, int Total, IReadOnlyList<ContributionDay> Days, DateTimeOffset ReadAt)
{
    /// <summary>The last <paramref name="weeks"/> weeks, whole weeks only (as many days as the grid holds).</summary>
    public IReadOnlyList<ContributionDay> LastWeeks(int weeks)
    {
        if (Days.Count == 0)
        {
            return [];
        }

        // Days come in calendar weeks starting on Sunday; keep whole columns ending with today's.
        var lastSunday = Days[^1].Date.AddDays(-(int)Days[^1].Date.DayOfWeek);
        var from = lastSunday.AddDays(-7 * (weeks - 1));
        return Days.Where(d => d.Date >= from).ToList();
    }
}

/// <summary>
/// Reads the contribution calendar through the signed-in GitHub CLI (<c>gh api graphql</c>, the same query GitHub's
/// profile uses). Read-only; the CLI's token never passes through Quotalis. Only the expected account is shown.
/// </summary>
public static class GitHubContributions
{
    /// <summary>The account whose calendar Quotalis shows (owner's choice, 2026-10-25).</summary>
    public const string Login = "soybean-alien";

    public static readonly TimeSpan RefreshEvery = TimeSpan.FromMinutes(15);

    private const string Query =
        "query{viewer{login contributionsCollection{contributionCalendar{totalContributions weeks{contributionDays{date contributionCount contributionLevel}}}}}}";

    public enum ReadStatus { Ok, NoCli, NotSignedIn, OtherAccount, Failed }

    public static async Task<(ReadStatus Status, ContributionCalendar? Calendar)> ReadAsync(DateTimeOffset now, CancellationToken cancel = default)
    {
        var gh = ProcessRunner.FindExecutable("gh", @"%ProgramFiles%\GitHub CLI\gh.exe", @"%LocalAppData%\Programs\GitHub CLI\gh.exe");
        if (gh is null)
        {
            return (ReadStatus.NoCli, null);
        }

        ProcessResult result;
        try
        {
            result = await ProcessRunner.RunAsync(gh, ["api", "graphql", "-f", "query=" + Query], timeout: TimeSpan.FromSeconds(20), cancellationToken: cancel).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return (ReadStatus.Failed, null);
        }

        if (result.ExitCode != 0)
        {
            return (result.Error.Contains("auth login", StringComparison.OrdinalIgnoreCase) ? ReadStatus.NotSignedIn : ReadStatus.Failed, null);
        }

        var calendar = Parse(result.Output, now);
        if (calendar is null)
        {
            return (ReadStatus.Failed, null);
        }

        return string.Equals(calendar.Login, Login, StringComparison.OrdinalIgnoreCase) ? (ReadStatus.Ok, calendar) : (ReadStatus.OtherAccount, null);
    }

    /// <summary>Null when the answer is not a contribution calendar.</summary>
    public static ContributionCalendar? Parse(string json, DateTimeOffset now)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var viewer = doc.RootElement.GetProperty("data").GetProperty("viewer");
            var login = viewer.GetProperty("login").GetString() ?? string.Empty;
            var calendar = viewer.GetProperty("contributionsCollection").GetProperty("contributionCalendar");
            var days = new List<ContributionDay>();
            foreach (var week in calendar.GetProperty("weeks").EnumerateArray())
            {
                foreach (var day in week.GetProperty("contributionDays").EnumerateArray())
                {
                    if (DateOnly.TryParse(day.GetProperty("date").GetString(), CultureInfo.InvariantCulture, out var date))
                    {
                        days.Add(new ContributionDay(date, Math.Max(0, day.GetProperty("contributionCount").GetInt32()), Level(day.GetProperty("contributionLevel").GetString())));
                    }
                }
            }

            return new ContributionCalendar(login, calendar.GetProperty("totalContributions").GetInt32(), days.OrderBy(d => d.Date).ToList(), now);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    private static int Level(string? level) => level switch
    {
        "FIRST_QUARTILE" => 1,
        "SECOND_QUARTILE" => 2,
        "THIRD_QUARTILE" => 3,
        "FOURTH_QUARTILE" => 4,
        _ => 0,
    };
}

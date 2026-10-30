using Quotalis.Core.Git;

namespace Quotalis.Core.Demo;

/// <summary>
/// Believable module content for demo mode (<c>--demo</c>) and screenshots. Never used otherwise:
/// in normal mode every module shows only real data or an honest empty state.
/// </summary>
public static class DemoModules
{
    public static IReadOnlyList<GitRepoStatus> Repositories(DateTimeOffset now) =>
    [
        new GitRepoStatus
        {
            Root = "/home/demo/code/quotalis",
            Branch = "feature/browser-notch",
            HasUpstream = true,
            Ahead = 2,
            Modified = 3,
            Untracked = 1,
            LastCommitSubject = "Retract into a notch over maximized browsers",
            LastCommitAt = now.AddMinutes(-38),
            GitHubRepository = "soybean-alien/Quotalis",
            UpdatedAt = now,
        },
        new GitRepoStatus { Root = "/home/demo/code/robotics-lab", Branch = "main", HasUpstream = true, UpdatedAt = now },
        new GitRepoStatus { Root = "/home/demo/code/thesis", Branch = "draft", HasUpstream = true, Modified = 5, Staged = 2, UpdatedAt = now },
    ];

    /// <summary>A made-up year of GitHub contributions for demo mode.</summary>
    public static ContributionCalendar Contributions(DateTimeOffset now)
    {
        var random = new Random(4242);
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var days = new List<ContributionDay>();
        for (var back = 370; back >= 0; back--)
        {
            var count = random.NextDouble() < 0.45 ? 0 : (int)(random.NextDouble() * random.NextDouble() * 14);
            var level = count == 0 ? 0 : count < 3 ? 1 : count < 6 ? 2 : count < 9 ? 3 : 4;
            days.Add(new ContributionDay(today.AddDays(-back), count, level));
        }

        return new ContributionCalendar(GitHubContributions.Login, days.Sum(d => d.Count), days, now);
    }
}

using System.Text;
using System.Text.RegularExpressions;
using Quotalis.Core.Formatting;
using Quotalis.Core.Models;

namespace Quotalis.Linux;

internal enum ScreenPage
{
    Overview,
    Git,
    History,
}

/// <summary>
/// Draws the terminal screen as text. It uses the terminal's 16 ANSI colours and block characters, so the screen takes
/// its colours and font from the desktop's theme. Pure: state and time in, lines out.
/// </summary>
internal sealed partial class Screen(bool color)
{
    private const string Red = "31";
    private const string Blue = "34";
    private const string Yellow = "33";
    private const string Dim = "90";
    private const string Bold = "1";

    // Name column, window label, 14-column bar, percent and the longest reset time, with a column spare on each side.
    public const int MaxWidth = 43;

    // Where meters and everything under them start, after the name column.
    private const int Column = 8;

    public IReadOnlyList<string> Render(AppState? state, ScreenPage page, DateTimeOffset now, int terminalWidth)
    {
        var width = Math.Clamp(terminalWidth, 40, MaxWidth);
        var lines = new List<string>();
        if (state is null)
        {
            lines.Add(Paint(Bold, " QUOTALIS"));
            lines.Add(Rule(width));
            AddText(lines, null, 1, "The background service hasn't written anything yet.", width);
            AddText(lines, Dim, 1, "Run quotalis setup, or quotalis service to start it by hand.", width);
            return lines;
        }

        var today = "Today " + DisplayFormat.Usd(state.TodayUsd) + " API equivalent";
        var title = " QUOTALIS" + (state.Demo ? " (sample data)" : string.Empty);
        if (title.Length + today.Length + 2 > width)
        {
            today = "Today " + DisplayFormat.Usd(state.TodayUsd);
        }

        lines.Add(Paint(Bold, title) + new string(' ', Math.Max(1, width - title.Length - today.Length - 1)) + today);
        lines.Add(Rule(width));
        if (!state.Demo && now - state.GeneratedAt > TimeSpan.FromMinutes(2))
        {
            AddText(lines, Yellow, 1, "The background service stopped updating " + DisplayFormat.Ago(now - state.GeneratedAt), width);
            lines.Add(Rule(width));
        }

        switch (page)
        {
            case ScreenPage.Git:
                AddGit(lines, state, width);
                break;
            case ScreenPage.History:
                AddHistory(lines, state, width);
                break;
            default:
                AddOverview(lines, state, now, width);
                break;
        }

        lines.Add(Rule(width));
        lines.Add(Paint(Dim, page == ScreenPage.Overview ? " [r] refresh [g] git [h] history [q] quit" : " [u] usage [g] git [h] history [q] quit"));
        return lines;
    }

    private void AddOverview(List<string> lines, AppState state, DateTimeOffset now, int width)
    {
        AddTool(lines, state.Claude, Red, now, width);
        AddTool(lines, state.Codex, Blue, now, width);
        AddHermes(lines, state.Hermes, width);
        lines.Add(Rule(width));
        lines.Add(" " + Paint(Bold, Pad("USAGE", Column - 1)) + DayStrip(state.Days, width - Column - 1));
        lines.Add(new string(' ', Column) + "week " + DisplayFormat.Usd(state.WeekUsd) + "  month " + DisplayFormat.Usd(state.MonthUsd));
        if (state.TotalsPartial)
        {
            lines.Add(new string(' ', Column) + Paint(Dim, "some models unpriced"));
        }

        var repo = state.Git.Repositories.FirstOrDefault();
        if (repo is not null)
        {
            lines.Add(" " + Paint(Bold, Pad("GIT", Column - 1)) + RepoLine(repo, width - Column - 1));
        }
    }

    private void AddTool(List<string> lines, ToolState tool, string ink, DateTimeOffset now, int width)
    {
        var name = Pad(tool.Name.ToUpperInvariant(), Column - 2);
        var barWidth = width >= MaxWidth ? 14 : 10;
        if (tool.Status is ProviderState.Disabled or ProviderState.NotDetected && tool.FiveHour is null && tool.Weekly is null)
        {
            lines.Add(" " + Paint(ink, name) + " " + Paint(Dim, tool.Status == ProviderState.Disabled ? "turned off" : "not found on this PC"));
            return;
        }

        lines.Add(" " + Paint(ink, name) + " " + Window("5h", tool.FiveHour, ink, now, barWidth));
        lines.Add(new string(' ', Column) + Window("7d", tool.Weekly, ink, now, barWidth));
        var details = new List<string>();
        if (tool.TodayUsd is { } usd)
        {
            details.Add(DisplayFormat.Usd(usd) + (tool.TodayPartial ? " partly priced" : string.Empty));
        }

        if (tool.Active)
        {
            details.Add("working");
        }

        if (tool.MeasuredAt is { } at && now - at > TimeSpan.FromHours(1))
        {
            details.Add("read " + DisplayFormat.Ago(now - at));
        }

        if (tool.TodayTokens > 0)
        {
            details.Add(DisplayFormat.Tokens(tool.TodayTokens) + " tokens");
        }

        if (tool.Sessions > 0)
        {
            details.Add(DisplayFormat.Count(tool.Sessions, "session", "sessions"));
        }

        // The last ones are the first to go when the line runs out of room.
        while (details.Count > 1 && Column + string.Join("  ", details).Length > width - 1)
        {
            details.RemoveAt(details.Count - 1);
        }

        if (details.Count > 0)
        {
            lines.Add(new string(' ', Column) + Paint(Dim, string.Join("  ", details)));
        }

        if (tool.Note is { Length: > 0 } note)
        {
            AddText(lines, Yellow, Column, note, width);
        }
    }

    private string Window(string label, PlanReading? reading, string ink, DateTimeOffset now, int barWidth)
    {
        if (reading is null)
        {
            return label + " " + Paint(Dim, new string('░', barWidth) + "   no reading yet");
        }

        if (reading.ResetsAt <= now)
        {
            return label + " " + Paint(Dim, new string('░', barWidth)) + "   0%" + Paint(Dim, "  reset");
        }

        var used = Math.Clamp(reading.Percent, 0, 100);
        var filled = (int)Math.Round(used / 100 * barWidth);
        return label + " " + Paint(used >= WaybarOutput.CriticalPercent ? Bold + ";" + ink : ink, new string('█', filled)) + Paint(Dim, new string('░', barWidth - filled))
            + " " + used.ToString("0").PadLeft(3) + "%  " + ResetText(reading.ResetsAt, now, TimeZoneInfo.Local)
            + (reading.Estimated ? Paint(Dim, " estimated") : string.Empty);
    }

    /// <summary>A countdown within a day; after that the day and time say more than "99 h".</summary>
    internal static string ResetText(DateTimeOffset resetsAt, DateTimeOffset now, TimeZoneInfo zone) =>
        resetsAt - now < TimeSpan.FromHours(24)
            ? DisplayFormat.Duration(resetsAt - now)
            : TimeZoneInfo.ConvertTime(resetsAt, zone).ToString("ddd HH:mm", System.Globalization.CultureInfo.InvariantCulture);

    private void AddHermes(List<string> lines, HermesState hermes, int width)
    {
        if (!hermes.Enabled)
        {
            return;
        }

        var name = " " + Paint(Yellow, Pad("HERMES", Column - 2)) + " ";
        if (hermes.Problem is { Length: > 0 } problem)
        {
            lines.Add(name.TrimEnd());
            AddText(lines, Dim, Column, problem, width);
            return;
        }

        lines.Add(name + "today " + DisplayFormat.Usd(hermes.TodayUsd) + (hermes.TodayEstimated ? Paint(Dim, " estimated") : string.Empty)
            + "  week " + DisplayFormat.Usd(hermes.WeekUsd));
        if (hermes.TodayTokens > 0)
        {
            lines.Add(new string(' ', Column) + Paint(Dim, DisplayFormat.Tokens(hermes.TodayTokens) + " tokens"));
        }
    }

    private void AddGit(List<string> lines, AppState state, int width)
    {
        lines.Add(" " + Paint(Bold, "GIT"));
        if (!state.Git.Enabled)
        {
            AddText(lines, Dim, 1, "Turned off in settings.json", width);
            return;
        }

        if (state.Git.Repositories.Count == 0)
        {
            AddText(lines, Dim, 1, state.Git.Note ?? "No repository yet. One appears when an AI session works in it.", width);
        }

        foreach (var repo in state.Git.Repositories)
        {
            lines.Add(" " + RepoLine(repo, width - 2));
        }

        lines.Add(Rule(width));
        lines.Add(" " + Paint(Bold, "GITHUB") + (state.Git.ContributionsTotal > 0 ? "  " + state.Git.ContributionsTotal.ToString("N0") + " contributions this year" : string.Empty));
        if (state.Git.ContributionsNote is { Length: > 0 } note)
        {
            AddText(lines, Dim, 1, note, width);
        }

        lines.AddRange(Grid(state.Git.Contributions.Select(d => (d.Day, Level(d.Level), Ink: "32")).ToList(), width));
    }

    private void AddHistory(List<string> lines, AppState state, int width)
    {
        lines.Add(" " + Paint(Bold, "USAGE") + "  week " + DisplayFormat.Usd(state.WeekUsd) + "  month " + DisplayFormat.Usd(state.MonthUsd));
        var max = state.Days.Count == 0 ? 0 : state.Days.Max(d => d.Tokens);
        lines.AddRange(Grid(state.Days.Select(d => (d.Day, Intensity(d.Tokens, max), Ink: LeaderInk(d.Leader))).ToList(), width));
        lines.Add(" " + Paint(Red, "█") + " Claude  " + Paint(Blue, "█") + " Codex  " + Paint(Yellow, "█") + " Hermes" + Paint(Dim, "  top tool"));
        var busiest = state.Days.Where(d => d.Tokens > 0).OrderByDescending(d => d.Tokens).FirstOrDefault();
        if (busiest is not null)
        {
            lines.Add(" Busiest day " + DisplayFormat.ShortDay(busiest.Day) + ": " + DisplayFormat.Tokens(busiest.Tokens) + " tokens, " + DisplayFormat.Usd(busiest.Usd));
        }
    }

    /// <summary>A GitHub-style graph: one column per week, Sunday on top, as many weeks as fit.</summary>
    private List<string> Grid(IReadOnlyList<(DateOnly Day, int Level, string Ink)> days, int width)
    {
        var rows = new StringBuilder[7];
        for (var r = 0; r < 7; r++)
        {
            rows[r] = new StringBuilder(" ");
        }

        if (days.Count == 0)
        {
            return [];
        }

        var weeks = Math.Min((width - 2) / 2, 26);
        var lastWeek = days[^1].Day.AddDays(-(int)days[^1].Day.DayOfWeek);
        var firstWeek = lastWeek.AddDays(-7 * (weeks - 1));
        var byDay = days.ToDictionary(d => d.Day);
        for (var r = 0; r < 7; r++)
        {
            for (var w = 0; w < weeks; w++)
            {
                var day = firstWeek.AddDays(w * 7 + r);
                rows[r].Append(byDay.TryGetValue(day, out var cell) ? Cell(cell.Level, cell.Ink, 2) : "  ");
            }
        }

        return rows.Select(r => r.ToString()).ToList();
    }

    /// <summary>The last days as one row of blocks, each in its top tool's colour.</summary>
    private string DayStrip(IReadOnlyList<DayState> days, int width)
    {
        var shown = days.TakeLast(Math.Max(width, 1)).ToList();
        var max = shown.Count == 0 ? 0 : shown.Max(d => d.Tokens);
        return string.Concat(shown.Select(d => Cell(Intensity(d.Tokens, max), LeaderInk(d.Leader), 1)));
    }

    private string Cell(int level, string ink, int width) => level switch
    {
        <= 0 => Paint(Dim, new string('·', 1) + new string(' ', width - 1)),
        1 => Paint(ink, new string('░', width)),
        2 => Paint(ink, new string('▒', width)),
        3 => Paint(ink, new string('▓', width)),
        _ => Paint(ink, new string('█', width)),
    };

    private static string LeaderInk(UsageTool? leader) => leader switch
    {
        UsageTool.Codex => Blue,
        UsageTool.Hermes => Yellow,
        _ => Red,
    };

    private static int Level(int level) => Math.Clamp(level, 0, 4);

    /// <summary>0 for an empty day, then 1 to 4. The square root keeps ordinary days visible next to a very busy one.</summary>
    internal static int Intensity(long tokens, long max)
    {
        if (tokens <= 0 || max <= 0)
        {
            return 0;
        }

        return Math.Clamp((int)Math.Ceiling(Math.Sqrt((double)tokens / max) * 4), 1, 4);
    }

    /// <summary>In words when they fit, otherwise git's own short marks, then a shortened branch, so the line stays as wide as the meters.</summary>
    private string RepoLine(RepoState repo, int room)
    {
        var branch = repo.Branch ?? "detached at " + repo.DetachedAt;
        string Build(bool words, string shownBranch)
        {
            var parts = new List<string> { Paint(Bold, repo.Name), shownBranch };
            if (repo.Ahead > 0) parts.Add("↑" + repo.Ahead);
            if (repo.Behind > 0) parts.Add("↓" + repo.Behind);
            if (repo.Conflicts > 0) parts.Add(Paint(Red, words ? repo.Conflicts + " conflicts" : "!" + repo.Conflicts));
            if (repo.Staged > 0) parts.Add(words ? repo.Staged + " staged" : "+" + repo.Staged);
            if (repo.Modified > 0) parts.Add(words ? repo.Modified + " modified" : "~" + repo.Modified);
            if (repo.Untracked > 0) parts.Add(words ? repo.Untracked + " untracked" : "?" + repo.Untracked);
            if (repo.Conflicts + repo.Staged + repo.Modified + repo.Untracked == 0) parts.Add(Paint(Dim, "clean"));
            return string.Join(" ", parts);
        }

        var full = Build(words: true, branch);
        if (Plain(full).Length <= room)
        {
            return full;
        }

        var marks = Build(words: false, branch);
        var over = Plain(marks).Length - room;
        return over <= 0 || branch.Length <= over + 1 ? marks : Build(words: false, branch[..(branch.Length - over - 1)] + "…");
    }

    /// <summary>Free text, broken at spaces so a narrow window doesn't wrap it mid-layout.</summary>
    private void AddText(List<string> lines, string? ink, int indent, string text, int width)
    {
        var line = string.Empty;
        foreach (var word in text.Split(' '))
        {
            if (line.Length > 0 && indent + line.Length + 1 + word.Length > width - 1)
            {
                lines.Add(new string(' ', indent) + (ink is null ? line : Paint(ink, line)));
                line = string.Empty;
            }

            line = line.Length == 0 ? word : line + " " + word;
        }

        lines.Add(new string(' ', indent) + (ink is null ? line : Paint(ink, line)));
    }

    private string Rule(int width) => Paint(Dim, " " + new string('─', width - 2));

    private string Paint(string code, string text) => !color || text.Length == 0 ? text : $"\x1b[{code}m{text}\x1b[0m";

    private static string Pad(string text, int width) => text.PadRight(width);

    /// <summary>The text as it appears on screen, without colour codes.</summary>
    internal static string Plain(string line) => AnsiCode().Replace(line, string.Empty);

    [GeneratedRegex(@"\x1b\[[0-9;]*m")]
    private static partial Regex AnsiCode();
}

using System.Text.Json;
using Quotalis.Core.Formatting;

namespace Quotalis.Linux;

/// <summary>
/// The line a Waybar custom module expects: JSON with text, tooltip, class and percentage. Omarchy's bar reads the same
/// line but styles only the class "active", so for that bar it marks a limit that's close rather than a tool at work.
/// </summary>
internal static class WaybarOutput
{
    public const double WarningPercent = 80;
    public const double CriticalPercent = 95;

    private const int Cells = 5;

    public static string Render(AppState? state, DateTimeOffset now, bool omarchy = false)
    {
        if (state is null)
        {
            return Json("Quotalis", "The background service isn't running. Run quotalis setup.", ["stopped"], 0);
        }

        if (!state.Demo && now - state.GeneratedAt > TimeSpan.FromMinutes(2))
        {
            return Json("Quotalis", "The background service stopped updating " + DisplayFormat.Ago(now - state.GeneratedAt), ["stopped"], 0);
        }

        // The shorter window when there is one; a plan may have only the weekly window.
        var claudeWindow = state.Claude.FiveHour ?? state.Claude.Weekly;
        var codexWindow = state.Codex.FiveHour ?? state.Codex.Weekly;
        var claude = Live(claudeWindow, now);
        var codex = Live(codexWindow, now);
        var parts = new List<string>();
        if (claudeWindow is not null) parts.Add("C " + Bar(claude) + " " + claude.ToString("0") + "%");
        if (codexWindow is not null) parts.Add("X " + Bar(codex) + " " + codex.ToString("0") + "%");
        parts.Add(DisplayFormat.Usd(state.TodayUsd));

        var worst = Math.Max(Math.Max(claude, codex), Math.Max(Live(state.Claude.Weekly, now), Live(state.Codex.Weekly, now)));
        var classes = new List<string>();
        if (omarchy)
        {
            if (worst >= WarningPercent) classes.Add("active");
            return Json(string.Join("  ", parts), Tooltip(state, now), classes, (int)Math.Round(worst));
        }

        if (worst >= CriticalPercent) classes.Add("critical");
        else if (worst >= WarningPercent) classes.Add("warning");
        if (state.Claude.Active || state.Codex.Active) classes.Add("active");
        return Json(string.Join("  ", parts), Tooltip(state, now), classes, (int)Math.Round(worst));
    }

    private static double Live(PlanReading? reading, DateTimeOffset now) =>
        reading is { } r && r.ResetsAt > now ? Math.Clamp(r.Percent, 0, 100) : 0;

    private static string Bar(double percent)
    {
        var filled = (int)Math.Round(percent / 100 * Cells);
        return new string('▰', filled) + new string('▱', Cells - filled);
    }

    private static string Tooltip(AppState state, DateTimeOffset now)
    {
        var lines = new List<string>();
        AddTool(lines, state.Claude, now);
        AddTool(lines, state.Codex, now);
        if (state.Hermes.Enabled)
        {
            lines.Add("Hermes: today " + DisplayFormat.Usd(state.Hermes.TodayUsd) + ", week " + DisplayFormat.Usd(state.Hermes.WeekUsd));
        }

        lines.Add("Today " + DisplayFormat.Usd(state.TodayUsd) + " API equivalent");
        return string.Join('\n', lines);
    }

    private static void AddTool(List<string> lines, ToolState tool, DateTimeOffset now)
    {
        lines.Add(tool.Name + ": 5h " + Reading(tool.FiveHour, now) + ", 7d " + Reading(tool.Weekly, now));
    }

    private static string Reading(PlanReading? reading, DateTimeOffset now)
    {
        if (reading is null)
        {
            return "no reading";
        }

        return reading.ResetsAt <= now
            ? "0% (reset)"
            : reading.Percent.ToString("0") + "% (resets " + Screen.ResetText(reading.ResetsAt, now, TimeZoneInfo.Local) + ")";
    }

    private static string Json(string text, string tooltip, IReadOnlyList<string> classes, int percentage) =>
        JsonSerializer.Serialize(new { text, tooltip, @class = classes, percentage }, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
}

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Claudex.Core.Plan;

/// <summary>
/// <c>Claudex.exe --claudex-calibrate</c>: a plan usage reading entered by hand, for example off the Claude desktop
/// app's usage card. Arguments:
/// <c>--five-hour X --five-hour-reset T --weekly Y --weekly-reset T [--plan P] [--by NAME] [--source TEXT]</c>,
/// percentages 0-100 and reset times as ISO 8601 or Unix seconds. A window needs both its percentage
/// and its reset time; at least one window is required.
/// </summary>
public static class PlanCalibration
{
    public const string Argument = "--claudex-calibrate";

    public static PlanUsage Parse(IReadOnlyList<string> args, DateTimeOffset now)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal) && args[i] != Argument && i + 1 < args.Count)
            {
                values[args[i][2..]] = args[++i];
            }
        }

        var current = Window(values, "five-hour");
        var weekly = Window(values, "weekly");
        return current is null && weekly is null
            ? throw new FormatException("Give --five-hour with --five-hour-reset, or --weekly with --weekly-reset.")
            : new PlanUsage(current, weekly, now);
    }

    public static string? Value(IReadOnlyList<string> args, string name)
    {
        var index = args.ToList().FindIndex(a => a.Equals("--" + name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Count ? args[index + 1] : null;
    }

    private static PlanWindow? Window(Dictionary<string, string> values, string name)
    {
        values.TryGetValue(name, out var rawPercent);
        values.TryGetValue(name + "-reset", out var rawReset);
        if (IsMissing(rawPercent) && IsMissing(rawReset))
        {
            return null;
        }

        if (!double.TryParse(rawPercent?.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var percent) || !double.IsFinite(percent))
        {
            throw new FormatException($"--{name} must be a percentage, got \"{rawPercent}\".");
        }

        var reset = ParseTime(rawReset) ?? throw new FormatException($"--{name}-reset is required with --{name} (ISO 8601 or Unix seconds).");
        return new PlanWindow(Math.Clamp(percent, 0, 100), reset);
    }

    private static bool IsMissing(string? value) => string.IsNullOrWhiteSpace(value) || value == "--";

    private static DateTimeOffset? ParseTime(string? raw)
    {
        if (IsMissing(raw))
        {
            return null;
        }

        if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) && seconds > 0)
        {
            return DateTimeOffset.FromUnixTimeSeconds(seconds);
        }

        return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time) ? time : null;
    }
}

/// <summary>
/// How plan usage reaches Claudex: the claude.ai sign-in (every kind of session, see <see cref="ClaudeWebUsage"/>) and,
/// optionally, Claude Code's status line (terminal sessions, see <see cref="ClaudeStatusLineSetup"/>).
/// </summary>
public static class ClaudePlanLink
{
    /// <summary>One state for the island: a sign-in problem wins, then either link counts as connected.</summary>
    public static PlanConnection Combine(ClaudeWebStatus web, PlanConnection statusLine) => web switch
    {
        ClaudeWebStatus.SignedIn => PlanConnection.Connected,
        ClaudeWebStatus.Expired => PlanConnection.SignInExpired,
        ClaudeWebStatus.FormatChanged => PlanConnection.FormatChanged,
        _ => statusLine == PlanConnection.Connected ? PlanConnection.Connected : PlanConnection.NotConnected,
    };
}

/// <summary>
/// The Stop hook earlier versions added to Claude Code (<c>--claudex-stop-hook</c>). It had Claude read its usage card at
/// the end of a turn, which cost model calls every time. Removed in 0.5.14 (the claude.ai sign-in replaced it); this only
/// removes old entries again, whichever copy of Claudex they run, and leaves every other hook alone.
/// </summary>
public static class LegacyClaudeStopHook
{
    public const string Argument = "--claudex-stop-hook";

    public static bool IsOurs(string? command) => command?.Contains(Argument, StringComparison.Ordinal) == true;

    /// <summary>Removes every entry that runs it. True when the file changed.</summary>
    public static bool Remove(string settingsPath)
    {
        JsonObject? root;
        try
        {
            root = ClaudeStatusLineSetup.ReadSettings(settingsPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }

        if (root?["hooks"]?["Stop"] is not JsonArray stop)
        {
            return false;
        }

        var changed = false;
        foreach (var group in stop.OfType<JsonObject>().ToList())
        {
            if (group["hooks"] is not JsonArray inner)
            {
                continue;
            }

            foreach (var hook in inner.OfType<JsonObject>().Where(h => IsOurs(Command(h))).ToList())
            {
                inner.Remove(hook);
                changed = true;
            }

            if (inner.Count == 0)
            {
                stop.Remove(group);
            }
        }

        if (!changed)
        {
            return false;
        }

        if (stop.Count == 0)
        {
            ((JsonObject)root["hooks"]!).Remove("Stop");
        }

        ClaudeStatusLineSetup.Write(settingsPath, root);
        return true;
    }

    private static string? Command(JsonNode? hook) =>
        hook?["command"] is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;
}

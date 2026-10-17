using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Claudex.Core.Plan;

/// <summary>
/// <c>Claudex.exe --claudex-calibrate</c>: exact plan usage read off the Claude desktop app's usage card
/// (Claude's own <c>get_usage</c>), which the desktop app never hands to a status line. Arguments:
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
/// <c>Claudex.exe --claudex-stop-hook</c>: Claude Code's Stop hook. In a Claude desktop session older than
/// two minutes it asks Claude, once the turn is done, to read its usage card and run
/// <see cref="PlanCalibration"/>, then end with one usage line. It stays quiet when the hook itself made the
/// turn continue, outside the desktop app (no usage card there), in young sessions, and right after a reading.
/// </summary>
public static class ClaudeStopHook
{
    public const string Argument = "--claudex-stop-hook";

    public static readonly TimeSpan MinSessionAge = TimeSpan.FromMinutes(2);

    /// <summary>A reading this recent (from the hook or the status line) needs no new one.</summary>
    public static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(30);

    /// <summary>The hook's block reason for Claude, or null to let the turn end; plus why, for the trace.</summary>
    public static (string? Reason, string Why) Decide(string input, PlanUsage? last, DateTimeOffset now, string executable)
    {
        JsonNode? hook;
        try
        {
            hook = JsonNode.Parse(string.IsNullOrWhiteSpace(input) ? "{}" : input);
        }
        catch (JsonException)
        {
            return (null, "unreadable hook input");
        }

        if (hook?["stop_hook_active"]?.GetValueKind() == JsonValueKind.True)
        {
            return (null, "continuing from this hook");
        }

        var transcript = hook?["transcript_path"]?.GetValueKind() == JsonValueKind.String ? hook["transcript_path"]!.GetValue<string>() : null;
        if (transcript is null || !File.Exists(transcript))
        {
            return (null, "no transcript");
        }

        var (started, desktop) = ReadSession(transcript);
        if (!desktop)
        {
            return (null, "not a desktop session (no usage card)");
        }

        if (started is null || now - started < MinSessionAge)
        {
            return (null, "session younger than 2 min");
        }

        if (last is not null && now - last.MeasuredAt < MinInterval)
        {
            return (null, "measured recently");
        }

        return (Instructions(executable), "due");
    }

    public static string Instructions(string executable) =>
        "Claudex auto-calibration is due (session over 2 min). Do this now, briefly: 1) call the " +
        "mcp__ccd_session_mgmt__get_usage tool (load it with ToolSearch 'select:mcp__ccd_session_mgmt__get_usage' if needed); " +
        $"2) run: \"{executable}\" {PlanCalibration.Argument} --five-hour <5-hour percentUsed> --weekly <'Weekly · all models' percentUsed> " +
        "--five-hour-reset <5-hour resetsAt> --weekly-reset <weekly resetsAt> --plan <plan> --by claude-auto " +
        "--source \"desktop usage card (auto hook)\" (Claudex is a windowed app: in PowerShell, pipe it to Out-String to see what it prints; " +
        "write -- for a missing window); 3) once it prints 'applied by Claudex', end your turn with exactly one " +
        "line and nothing else: `🟠 Usage: Claude 5h <x>% · wk <y>%`, using the exact 5-hour and 'Weekly · all models' " +
        "percentUsed values from get_usage (whole numbers, no `~`; write `--` for a missing window). Use the 🟠 emoji itself, " +
        "never HTML color markup, and no Codex numbers. If get_usage reports the plan as unavailable or not_applicable, skip " +
        "steps 2 and 3 and just end your turn without a usage line.";

    /// <summary>The block decision Claude Code reads from the hook's stdout.</summary>
    public static string Output(string reason) => new JsonObject { ["decision"] = "block", ["reason"] = reason }.ToJsonString();

    private static (DateTimeOffset? Started, bool Desktop) ReadSession(string transcript)
    {
        DateTimeOffset? started = null;
        var desktop = false;
        try
        {
            using var reader = new StreamReader(new FileStream(transcript, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
            for (var i = 0; i < 200 && !(started is not null && desktop) && reader.ReadLine() is { } line; i++)
            {
                if (started is null && line.Contains("\"timestamp\"", StringComparison.Ordinal))
                {
                    try
                    {
                        if (JsonNode.Parse(line)?["timestamp"]?.GetValue<string>() is { } raw
                            && DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time))
                        {
                            started = time;
                        }
                    }
                    catch (Exception ex) when (ex is JsonException or InvalidOperationException)
                    {
                    }
                }

                if (line.Contains("\"entrypoint\":\"claude-desktop\"", StringComparison.Ordinal))
                {
                    desktop = true;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return (started, desktop);
    }
}

/// <summary>How plan usage reaches Claudex: its status line (terminal sessions), its Stop hook (desktop sessions), or both.</summary>
public static class ClaudePlanLink
{
    /// <summary>Connected when either link is ours; otherwise what the status line check says.</summary>
    public static PlanConnection Inspect(string settingsPath)
    {
        var statusLine = ClaudeStatusLineSetup.Inspect(settingsPath);
        return statusLine != PlanConnection.Connected && ClaudeStopHookSetup.IsConnected(settingsPath) ? PlanConnection.Connected : statusLine;
    }

    /// <summary>Adds the Stop hook, and the status line unless the user has their own.</summary>
    public static PlanConnection Connect(string settingsPath, string executable)
    {
        var statusLine = ClaudeStatusLineSetup.Connect(settingsPath, executable);
        if (statusLine != PlanConnection.Unreadable)
        {
            ClaudeStopHookSetup.Connect(settingsPath, executable);
        }

        return Inspect(settingsPath);
    }

    /// <summary>Removes our status line and our Stop hook, nothing else.</summary>
    public static PlanConnection Disconnect(string settingsPath)
    {
        ClaudeStatusLineSetup.Disconnect(settingsPath);
        ClaudeStopHookSetup.Disconnect(settingsPath);
        return Inspect(settingsPath);
    }
}

/// <summary>
/// Adds Claudex's Stop hook to Claude Code's <c>settings.json</c> next to any other hooks, and removes only
/// that entry again. Same backup and rules as <see cref="ClaudeStatusLineSetup"/>.
/// </summary>
public static class ClaudeStopHookSetup
{
    public static string CommandFor(string executable) => $"\"{executable}\" {ClaudeStopHook.Argument}";

    public static bool IsOurs(string? command) => command?.Contains(ClaudeStopHook.Argument, StringComparison.Ordinal) == true;

    public static bool IsConnected(string settingsPath)
    {
        try
        {
            return Commands(ClaudeStatusLineSetup.ReadSettings(settingsPath)).Any(IsOurs);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException)
        {
            return false;
        }
    }

    public static void Connect(string settingsPath, string executable)
    {
        if (IsConnected(settingsPath))
        {
            return;
        }

        var root = ClaudeStatusLineSetup.ReadSettings(settingsPath) ?? new JsonObject();
        var hooks = root["hooks"] as JsonObject ?? new JsonObject();
        var stop = hooks["Stop"] as JsonArray ?? new JsonArray();
        stop.Add(new JsonObject
        {
            ["hooks"] = new JsonArray(new JsonObject { ["type"] = "command", ["command"] = CommandFor(executable), ["timeout"] = 15 }),
        });
        hooks["Stop"] = stop;
        root["hooks"] = hooks;
        ClaudeStatusLineSetup.Write(settingsPath, root);
    }

    public static void Disconnect(string settingsPath)
    {
        if (!IsConnected(settingsPath))
        {
            return;
        }

        var root = ClaudeStatusLineSetup.ReadSettings(settingsPath)!;
        var stop = (JsonArray)root["hooks"]!["Stop"]!;
        foreach (var group in stop.OfType<JsonObject>().ToList())
        {
            if (group["hooks"] is JsonArray inner)
            {
                foreach (var hook in inner.OfType<JsonObject>().Where(h => IsOurs(Command(h))).ToList())
                {
                    inner.Remove(hook);
                }

                if (inner.Count == 0)
                {
                    stop.Remove(group);
                }
            }
        }

        if (stop.Count == 0)
        {
            ((JsonObject)root["hooks"]!).Remove("Stop");
        }

        ClaudeStatusLineSetup.Write(settingsPath, root);
    }

    private static IEnumerable<string?> Commands(JsonObject? root) =>
        (root?["hooks"]?["Stop"] as JsonArray ?? [])
            .SelectMany(group => group?["hooks"] as JsonArray ?? [])
            .Select(Command);

    private static string? Command(JsonNode? hook) =>
        hook?["command"] is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;
}

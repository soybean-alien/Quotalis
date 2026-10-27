using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Quotalis.Core.Plan;

/// <summary>
/// <c>Quotalis.exe --quotalis-codex-stop-hook</c>: Codex's Stop hook (in <c>~/.codex/hooks.json</c>). In a Codex
/// session older than two minutes it reads that session's latest exact rate-limit snapshot from its rollout log
/// (Quotalis shows the same numbers from those logs anyway) and asks Codex to end its reply with one line,
/// <c>🔵 Usage: Codex 5h x% · wk y%</c>, with the numbers already filled in, so Codex runs nothing for it.
/// It stays quiet when the hook itself made the turn continue, in young sessions, and right after a report.
/// </summary>
public static class CodexStopHook
{
    public const string Argument = "--quotalis-codex-stop-hook";

    /// <summary>A report this recent needs no new one.</summary>
    public static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(30);

    public static readonly TimeSpan MinSessionAge = TimeSpan.FromMinutes(2);

    /// <summary>The hook's block reason for Codex, or null to let the turn end; plus why, for the trace.</summary>
    public static (string? Reason, string Why) Decide(string input, DateTimeOffset? lastReport, DateTimeOffset now, string executable)
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

        var (started, snapshot) = ReadSession(transcript);
        if (started is null || now - started < MinSessionAge)
        {
            return (null, "session younger than 2 min");
        }

        if (lastReport is { } last && now - last < MinInterval)
        {
            return (null, "reported recently");
        }

        var line = $"🔵 Usage: Codex 5h {Percent(snapshot?.Current, now)} · wk {Percent(snapshot?.Weekly, now)}";
        return (Instructions(line, executable), snapshot is null ? "due, no rate-limit snapshot yet" : "due");
    }

    /// <summary>The block decision the hook prints on stdout.</summary>
    public static string Output(string reason) => new JsonObject { ["decision"] = "block", ["reason"] = reason }.ToJsonString();

    public static string Instructions(string line, string executable) =>
        "Quotalis usage report is due (session over 2 min). Add ONE line at the end of your reply, exactly: " +
        $"`{line}`. These are this session's exact numbers from Codex; run nothing for this and never change them. " +
        "If the user gave you Claude numbers from claude.ai during this session, also run " +
        $"`\"{executable}\" {PlanCalibration.Argument} --five-hour <x> --five-hour-reset <time> --weekly <y> --weekly-reset <time> --by codex --source \"user\"`.";

    private static string Percent(PlanWindow? window, DateTimeOffset now) =>
        window is { } w && w.ResetsAt > now ? $"{Math.Round(w.UsedPercent).ToString("0", CultureInfo.InvariantCulture)}%" : "--";

    /// <summary>The session's start and its latest Codex rate-limit snapshot (other limit ids are skipped).</summary>
    public static (DateTimeOffset? Started, PlanUsage? Snapshot) ReadSession(string transcript)
    {
        DateTimeOffset? started = null;
        PlanUsage? snapshot = null;
        try
        {
            using var reader = new StreamReader(new FileStream(transcript, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
            while (reader.ReadLine() is { } line)
            {
                if (started is null && line.Contains("\"timestamp\"", StringComparison.Ordinal))
                {
                    started = Timestamp(TryParse(line));
                }

                if (line.Contains("\"rate_limits\":{", StringComparison.Ordinal) && TryParse(line) is { } row
                    && row["payload"]?["rate_limits"] is JsonObject limits
                    && (limits["limit_id"]?.GetValueKind() != JsonValueKind.String || limits["limit_id"]!.GetValue<string>() == "codex")
                    && Window(limits["primary"]) is { } current)
                {
                    snapshot = new PlanUsage(current, Window(limits["secondary"]), Timestamp(row) ?? DateTimeOffset.MinValue);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return (started, snapshot);
    }

    private static JsonNode? TryParse(string line)
    {
        try
        {
            return JsonNode.Parse(line);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static DateTimeOffset? Timestamp(JsonNode? row) =>
        row?["timestamp"]?.GetValueKind() == JsonValueKind.String
        && DateTimeOffset.TryParse(row["timestamp"]!.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time)
            ? time
            : null;

    private static PlanWindow? Window(JsonNode? window)
    {
        if (window?["used_percent"]?.GetValueKind() != JsonValueKind.Number || window["resets_at"]?.GetValueKind() != JsonValueKind.Number)
        {
            return null;
        }

        var percent = window["used_percent"]!.GetValue<double>();
        var seconds = window["resets_at"]!.GetValue<long>();
        return double.IsFinite(percent) && seconds > 0 ? new PlanWindow(Math.Clamp(percent, 0, 100), DateTimeOffset.FromUnixTimeSeconds(seconds)) : null;
    }
}

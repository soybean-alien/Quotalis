using System.Globalization;
using System.Text;
using System.Text.Json;
using Claudex.Core.Diagnostics;

namespace Claudex.Core.Usage;

/// <summary>
/// Publishes Codex's rate-limit readings for other programs on this machine (the Claudex Mini Stream Deck plugin), the
/// way <c>claude-plan.json</c> and <c>calibration-log.jsonl</c> do for Claude:
/// <list type="bullet">
/// <item><c>codex-plan.json</c>: the newest 5-hour and weekly readings, written atomically. Raw values: a reader treats a
/// window whose reset time has passed as 0% used, as the island does.</item>
/// <item><c>codex-limit-log.jsonl</c>: one line per reading not logged yet (keyed by when Codex observed it), so readers
/// can work out usage per day. Lines can arrive out of order during the first scan; readers sort them.</item>
/// </list>
/// Numbers only: no prompts, paths or account details.
/// </summary>
public sealed class CodexLimitPublisher(string planPath, string logPath, TimeSpan? keep = null)
{
    private readonly object _gate = new();
    private readonly TimeSpan _keep = keep ?? TimeSpan.FromDays(35);
    private HashSet<long>? _logged;
    private string? _lastPlan;

    public string PlanPath => planPath;

    public string LogPath => logPath;

    /// <summary>
    /// Logs a reading from one file (any age) and rewrites the plan file from the newest readings overall.
    /// <paramref name="clearWhenEmpty"/> removes the plan file when there is nothing newer to show (an account sign-out
    /// with no local fallback); otherwise an empty call leaves it alone, so a scan that has not found a limit yet keeps it.
    /// </summary>
    public void Publish(CodexRateLimit? reading, CodexRateLimit? readingWeekly, CodexRateLimit? newest, CodexRateLimit? newestWeekly, bool clearWhenEmpty = false)
    {
        lock (_gate)
        {
            try
            {
                if (reading is not null)
                {
                    Append(reading, readingWeekly is not null && readingWeekly.ObservedAt == reading.ObservedAt ? readingWeekly : null);
                }

                if (newest is not null || newestWeekly is not null)
                {
                    WritePlan(newest, newestWeekly);
                }
                else if (clearWhenEmpty)
                {
                    if (File.Exists(planPath)) File.Delete(planPath);
                    _lastPlan = null;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn("codex", "Codex limit publish failed", new { error = ex.GetType().Name, ex.Message });
            }
        }
    }

    private void WritePlan(CodexRateLimit? five, CodexRateLimit? weekly)
    {
        var measured = new[] { five?.ObservedAt, weekly?.ObservedAt }.Max();
        var json = Json(w =>
        {
            w.WriteStartObject();
            WriteWindow(w, "current", five, camel: true);
            WriteWindow(w, "weekly", weekly, camel: true);
            if (measured is { } m) w.WriteString("measuredAt", Iso(m));
            w.WriteEndObject();
        });
        if (json == _lastPlan && File.Exists(planPath)) return;

        Directory.CreateDirectory(Path.GetDirectoryName(planPath)!);
        var temp = planPath + "." + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, planPath, overwrite: true);
        _lastPlan = json;
    }

    private void Append(CodexRateLimit five, CodexRateLimit? weekly)
    {
        _logged ??= LoadAndTrim();
        var key = five.ObservedAt.ToUnixTimeMilliseconds();
        if (five.ObservedAt < DateTimeOffset.UtcNow - _keep || !_logged.Add(key)) return;

        var line = Json(w =>
        {
            w.WriteStartObject();
            w.WriteString("at", Iso(five.ObservedAt));
            WriteWindow(w, "five_hour", five, camel: false);
            WriteWindow(w, "weekly", weekly, camel: false);
            w.WriteEndObject();
        });
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        File.AppendAllText(logPath, line + "\n");
    }

    /// <summary>Reads the keys already logged, dropping lines older than the keep window.</summary>
    private HashSet<long> LoadAndTrim()
    {
        var keys = new HashSet<long>();
        if (!File.Exists(logPath)) return keys;

        var cutoff = DateTimeOffset.UtcNow - _keep;
        var kept = new List<string>();
        var dropped = false;
        foreach (var line in File.ReadAllLines(logPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (!doc.RootElement.TryGetProperty("at", out var at) || !at.TryGetDateTimeOffset(out var ts)) continue;
                if (ts < cutoff)
                {
                    dropped = true;
                    continue;
                }

                if (keys.Add(ts.ToUnixTimeMilliseconds())) kept.Add(line);
            }
            catch (JsonException)
            {
                dropped = true; // a torn line from a crash
            }
        }

        if (dropped)
        {
            var temp = logPath + "." + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + ".tmp";
            File.WriteAllLines(temp, kept);
            File.Move(temp, logPath, overwrite: true);
        }

        return keys;
    }

    private static void WriteWindow(Utf8JsonWriter w, string name, CodexRateLimit? limit, bool camel)
    {
        if (limit is null) return;
        w.WriteStartObject(name);
        w.WriteNumber(camel ? "usedPercent" : "percent", limit.UsedPercent);
        if (limit.ResetsAt is { } r) w.WriteString(camel ? "resetsAt" : "resets_at", Iso(r));
        else w.WriteNull(camel ? "resetsAt" : "resets_at");
        w.WriteNumber(camel ? "windowMinutes" : "window_minutes", limit.WindowMinutes);
        w.WriteEndObject();
    }

    private static string Iso(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    private static string Json(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            write(w);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}

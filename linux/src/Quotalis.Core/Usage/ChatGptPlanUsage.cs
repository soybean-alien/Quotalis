using System.Text.Json;

namespace Quotalis.Core.Usage;

/// <summary>Only the usage windows returned by Codex app-server. No account identifiers or credentials are retained.</summary>
public sealed record ChatGptLimitBucket(string Id, string Name, CodexRateLimit? Current, CodexRateLimit? Weekly);

public sealed record ChatGptPlanUsage(IReadOnlyList<ChatGptLimitBucket> Buckets, DateTimeOffset MeasuredAt)
{
    public ChatGptLimitBucket? Shared => Buckets.FirstOrDefault(b => b.Id == "codex");
}

public static class ChatGptPlanUsageParser
{
    public static ChatGptPlanUsage? Parse(string json, DateTimeOffset measuredAt)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var buckets = new List<ChatGptLimitBucket>();
            if (root.TryGetProperty("rateLimitsByLimitId", out var byId) && byId.ValueKind == JsonValueKind.Object)
            {
                foreach (var entry in byId.EnumerateObject())
                {
                    if (entry.Value.ValueKind == JsonValueKind.Object && Bucket(entry.Name, entry.Value, measuredAt) is { } bucket)
                    {
                        buckets.Add(bucket);
                    }
                }
            }

            // Older app-server versions only return the single historical rateLimits field.
            if (buckets.Count == 0 && root.TryGetProperty("rateLimits", out var single) && single.ValueKind == JsonValueKind.Object
                && Bucket("codex", single, measuredAt) is { } fallback)
            {
                buckets.Add(fallback);
            }

            return buckets.Count == 0 ? null : new ChatGptPlanUsage(buckets, measuredAt);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ChatGptLimitBucket? Bucket(string id, JsonElement value, DateTimeOffset measuredAt)
    {
        var first = Window(value, "primary", measuredAt);
        var second = Window(value, "secondary", measuredAt);
        if (first is null && second is null) return null;

        var name = value.TryGetProperty("limitName", out var label) && label.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(label.GetString()) ? label.GetString()! : id == "codex" ? "Work and Codex" : id;
        var weekly = IsWeekly(first) ? first : IsWeekly(second) ? second : null;
        var current = ReferenceEquals(first, weekly) ? second : first;
        if (weekly is null && second is { WindowMinutes: 0 } && first is not null)
        {
            // A backend that omits window length can still use its historical primary/secondary ordering.
            weekly = second;
        }

        return new ChatGptLimitBucket(id, name, current, weekly);
    }

    private static bool IsWeekly(CodexRateLimit? window) => window?.WindowMinutes >= 10080;

    private static CodexRateLimit? Window(JsonElement bucket, string name, DateTimeOffset measuredAt)
    {
        if (!bucket.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object
            || !value.TryGetProperty("usedPercent", out var used) || used.ValueKind != JsonValueKind.Number || !used.TryGetDouble(out var percent)
            || !double.IsFinite(percent) || percent is < 0 or > 100)
        {
            return null;
        }

        var minutes = value.TryGetProperty("windowDurationMins", out var duration) && duration.ValueKind == JsonValueKind.Number
            && duration.TryGetInt32(out var n) && n > 0 ? n : 0;
        DateTimeOffset? reset = null;
        if (value.TryGetProperty("resetsAt", out var at) && at.ValueKind == JsonValueKind.Number && at.TryGetInt64(out var seconds))
        {
            try { reset = DateTimeOffset.FromUnixTimeSeconds(seconds); }
            catch (ArgumentOutOfRangeException) { return null; }
        }

        return new CodexRateLimit(percent, minutes, reset, measuredAt);
    }
}

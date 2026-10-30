using System.Globalization;
using System.Text.Json;
using Quotalis.Core.Models;

namespace Quotalis.Core.Hermes;

/// <summary>Hermes's token use on one local day: per-model rows bucketed by when they were last used.</summary>
public sealed record HermesDay(DateOnly Day, TokenCounts Tokens, long ReasoningTokens, int ApiCalls, int Sessions, decimal CostUsd, bool CostEstimated);

/// <summary>
/// Hermes Agent's usage, read from the metadata export (<c>hermes-usage.json</c>, written once a minute by the export
/// job, see tools/hermes) or straight from a local <c>state.db</c>. Holds numbers, model and surface names only, never
/// message text. Costs are the ones Hermes recorded itself (actual when Nous reported one, else Hermes's estimate);
/// they are shown in the Hermes column only and never join the API-equivalent totals of Claude and Codex.
/// </summary>
public sealed record HermesUsage
{
    public required DateTimeOffset GeneratedAt { get; init; }

    public int? SchemaVersion { get; init; }

    public DateTimeOffset? GatewayHeartbeat { get; init; }

    public DateTimeOffset? LastActivity { get; init; }

    /// <summary>The model of the most recent model-usage row, and when it was last used.</summary>
    public string? CurrentModel { get; init; }

    public DateTimeOffset? CurrentModelSeen { get; init; }

    public IReadOnlyList<HermesDay> Days { get; init; } = [];

    public HermesDay? DayOf(DateOnly day) => Days.FirstOrDefault(d => d.Day == day);
}

/// <summary>One per-model usage row of the export (a session, a model and a task such as "approval").</summary>
public sealed record HermesModelRow(
    string? Session, string? Model, string? Task, int ApiCalls, TokenCounts Tokens, long ReasoningTokens,
    decimal EstimatedUsd, decimal ActualUsd, DateTimeOffset? LastSeen);

public static class HermesUsageParser
{
    /// <summary>The export format this reader understands.</summary>
    public const int Format = 1;

    /// <summary>Parses the export; null when it is not a format-1 export.</summary>
    public static HermesUsage? Parse(string json, TimeZoneInfo zone)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Int(root, "format") != Format || Time(root, "generated_at") is not { } generated)
            {
                return null;
            }

            var rows = new List<HermesModelRow>();
            if (root.TryGetProperty("model_usage", out var usage) && usage.ValueKind == JsonValueKind.Array)
            {
                foreach (var r in usage.EnumerateArray())
                {
                    if (r.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    rows.Add(new HermesModelRow(
                        Str(r, "session"), Str(r, "model"), Str(r, "task"), (int)Long(r, "api_call_count"),
                        new TokenCounts(Long(r, "input_tokens"), Long(r, "output_tokens"), Long(r, "cache_write_tokens"), 0, Long(r, "cache_read_tokens")),
                        Long(r, "reasoning_tokens"), Money(r, "estimated_cost_usd"), Money(r, "actual_cost_usd"), Time(r, "last_seen")));
                }
            }

            string? model = null;
            DateTimeOffset? modelSeen = null;
            if (root.TryGetProperty("current_model", out var current) && current.ValueKind == JsonValueKind.Object)
            {
                model = Str(current, "model");
                modelSeen = Time(current, "last_seen");
            }

            return new HermesUsage
            {
                GeneratedAt = generated,
                SchemaVersion = Int(root, "schema_version"),
                GatewayHeartbeat = Time(root, "gateway_last_heartbeat"),
                LastActivity = Time(root, "last_activity_at"),
                CurrentModel = model,
                CurrentModelSeen = modelSeen,
                Days = Aggregate(rows, zone),
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Per-model rows (they include Hermes's helper calls: approvals, titles, background reviews, vision, compression)
    /// grouped by the local day they were last used. A row spanning midnight counts on its last day.
    /// </summary>
    public static IReadOnlyList<HermesDay> Aggregate(IEnumerable<HermesModelRow> rows, TimeZoneInfo zone) => rows
        .Where(r => r.LastSeen is not null)
        .GroupBy(r => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(r.LastSeen!.Value, zone).DateTime))
        .Select(g =>
        {
            var actual = g.Sum(r => r.ActualUsd);
            return new HermesDay(
                g.Key,
                g.Aggregate(TokenCounts.Zero, (sum, r) => sum + r.Tokens),
                g.Sum(r => r.ReasoningTokens),
                g.Sum(r => r.ApiCalls),
                g.Select(r => r.Session).Where(s => !string.IsNullOrEmpty(s)).Distinct(StringComparer.Ordinal).Count(),
                actual > 0 ? actual : g.Sum(r => r.EstimatedUsd),
                CostEstimated: actual <= 0);
        })
        .OrderBy(d => d.Day)
        .ToList();

    internal static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    internal static long Long(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && double.IsFinite(d) && d > 0 ? (long)d : 0;

    internal static int? Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    /// <summary>A dollar amount sent as a number or a numeric string; 0 when missing.</summary>
    internal static decimal Money(JsonElement e, string name) => MoneyOrNull(e, name) ?? 0m;

    internal static decimal? MoneyOrNull(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v))
        {
            return null;
        }

        return v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetDecimal(out var d) => d,
            JsonValueKind.String when decimal.TryParse(v.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var d) => d,
            _ => null,
        };
    }

    /// <summary>Unix seconds (fractional allowed) as a time; null when missing or not plausible.</summary>
    internal static DateTimeOffset? Time(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var s) && s is > 946684800 and < 32503680000
            ? DateTimeOffset.FromUnixTimeMilliseconds((long)(s * 1000))
            : null;
}

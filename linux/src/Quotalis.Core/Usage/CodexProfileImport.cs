using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Quotalis.Core.Models;
using Quotalis.Core.Storage;

namespace Quotalis.Core.Usage;

/// <summary>One day of a ChatGPT profile's Codex "Token activity" graph: a total token count, nothing more.</summary>
public sealed record ProfileDay(DateOnly Day, long Tokens);

/// <summary>
/// How a day's total splits into input, output and cached input, and what a million tokens cost, measured from the
/// Codex days Quotalis tracked itself. Used to turn a profile's bare totals into estimated daily rows.
/// </summary>
public sealed record TokenMix(double Input, double Output, double CacheRead, decimal UsdPerMillion)
{
    /// <summary>The mix measured on the development PC (Oct 10 to 17, 2026), for when no local Codex day exists.</summary>
    public static TokenMix Default { get; } = new(0.039, 0.006, 0.955, 0.31m);

    public static TokenMix FromLocal(IEnumerable<DailyUsage> codexDays)
    {
        long input = 0, output = 0, cacheRead = 0, total = 0;
        decimal usd = 0m;
        foreach (var d in codexDays.Where(d => !d.ApiValuePartial))
        {
            input += d.Tokens.Input + d.Tokens.CacheWrite;
            output += d.Tokens.Output;
            cacheRead += d.Tokens.CacheRead;
            total += d.Tokens.Processed;
            usd += d.ApiValueUsd;
        }

        return total < 1_000_000
            ? Default
            : new TokenMix((double)input / total, (double)output / total, (double)cacheRead / total, decimal.Round(usd / total * 1_000_000m, 4));
    }
}

public sealed record ProfileImportResult(int Imported, int Skipped, long Tokens, decimal EstimatedUsd);

/// <summary>
/// Imports the Codex "Token activity" of a ChatGPT profile (<c>chatgpt.com/u/&lt;name&gt;</c>, the JSON of
/// <c>/backend-api/profiles/&lt;name&gt;/page</c>) for days Quotalis has no local record of. The profile gives one total per
/// (UTC) day, so each total is split by <see cref="TokenMix"/> and valued at its cost per million: an estimate, recorded
/// in <c>imported_days</c> and labelled as such. Only days before the first locally measured Codex day are imported, so nothing is counted twice; a later local
/// measurement replaces an imported day.
/// </summary>
public static class CodexProfileImport
{
    public const string Source = "ChatGPT profile";

    public static IReadOnlyList<ProfileDay> Parse(string json)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            throw new FormatException("The profile file isn't valid JSON.");
        }

        var buckets = root?["page"]?["activity_graph"]?["daily_usage_buckets"] as JsonArray
            ?? root?["activity_graph"]?["daily_usage_buckets"] as JsonArray
            ?? root?["daily_usage_buckets"] as JsonArray
            ?? throw new FormatException("The profile has no daily_usage_buckets (page.activity_graph).");

        var days = new List<ProfileDay>();
        foreach (var bucket in buckets.OfType<JsonObject>())
        {
            if (bucket["start_date"]?.GetValueKind() == JsonValueKind.String
                && DateOnly.TryParseExact(bucket["start_date"]!.GetValue<string>(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
                && bucket["tokens"]?.GetValueKind() == JsonValueKind.Number
                && bucket["tokens"]!.GetValue<long>() is var tokens and > 0)
            {
                days.Add(new ProfileDay(day, tokens));
            }
        }

        return days;
    }

    /// <summary>
    /// Imports the days before the first Codex day measured locally. Later days are skipped even without a local row: the
    /// profile counts UTC days, so their edges would overlap the evenings Quotalis already measured and count them twice.
    /// </summary>
    public static ProfileImportResult Import(UsageDatabase db, IEnumerable<ProfileDay> days, TokenMix mix, DateTimeOffset now)
    {
        var list = days.ToList();
        var firstMeasured = list.Count == 0 ? (DateOnly?)null : db.GetDaily(DateOnly.MinValue, list.Max(d => d.Day).AddDays(1))
            .Where(d => d.Provider == AiProviderKind.Codex && db.HasMeasuredDaily(d.Day, AiProviderKind.Codex))
            .Select(d => (DateOnly?)d.Day)
            .Min();
        int imported = 0, skipped = 0;
        long tokens = 0;
        decimal usd = 0m;
        foreach (var day in list)
        {
            if (firstMeasured is { } first && day.Day >= first)
            {
                skipped++;
                continue;
            }

            var input = (long)Math.Round(day.Tokens * mix.Input);
            var output = (long)Math.Round(day.Tokens * mix.Output);
            var cacheRead = Math.Max(0, day.Tokens - input - output);
            var value = decimal.Round(day.Tokens / 1_000_000m * mix.UsdPerMillion, 4);
            db.ImportDaily(new DailyUsage(day.Day, AiProviderKind.Codex, new TokenCounts(input, output, 0, 0, cacheRead), value, false, 0), Source, day.Tokens, now);
            imported++;
            tokens += day.Tokens;
            usd += value;
        }

        return new ProfileImportResult(imported, skipped, tokens, usd);
    }
}

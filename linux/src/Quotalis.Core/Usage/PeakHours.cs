using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Quotalis.Core.Models;
using Quotalis.Core.Pricing;

namespace Quotalis.Core.Usage;

/// <summary>
/// Anthropic's peak hours: weekdays, <see cref="StartHour"/> to <see cref="EndHour"/> in US Eastern time (8 AM to 2 PM
/// as announced in March 2026). The window is configurable, because Anthropic may change it.
/// </summary>
public sealed record PeakWindow(int StartHour, int EndHour, TimeZoneInfo Zone)
{
    public static TimeZoneInfo Eastern { get; } = FindEastern();

    public static PeakWindow Default { get; } = new(8, 14, Eastern);

    public bool IsPeak(DateTimeOffset time)
    {
        var local = TimeZoneInfo.ConvertTime(time, Zone);
        return local.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)
            && local.Hour >= StartHour && local.Hour < EndHour;
    }

    /// <summary>"weekdays 8 AM–2 PM ET".</summary>
    public string Describe(CultureInfo? culture = null)
    {
        var c = culture ?? CultureInfo.CurrentCulture;
        static string Hour(int h, CultureInfo c) => new DateTime(2000, 1, 1, h % 24, 0, 0).ToString("h tt", c).Replace(":00", string.Empty);
        return $"weekdays {Hour(StartHour, c)}–{Hour(EndHour, c)} ET";
    }

    private static TimeZoneInfo FindEastern()
    {
        foreach (var id in new[] { "Eastern Standard Time", "America/New_York" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
            }
        }

        return TimeZoneInfo.CreateCustomTimeZone("ET", TimeSpan.FromHours(-5), "ET", "ET");
    }
}

/// <summary>
/// Claude and Codex usage of a period by local hour of day (estimated API equivalent and fresh tokens), and the split
/// between Anthropic's peak hours and the rest.
/// </summary>
public sealed record HourlyProfile(
    IReadOnlyList<decimal> CostByHour,
    IReadOnlyList<long> FreshByHour,
    decimal PeakCost,
    decimal OffPeakCost,
    long PeakFresh,
    long OffPeakFresh)
{
    public static HourlyProfile Empty { get; } = new(new decimal[24], new long[24], 0m, 0m, 0, 0);

    public decimal TotalCost => PeakCost + OffPeakCost;

    /// <summary>Share of the period's cost in peak hours, 0 to 1 (0 without usage).</summary>
    public double PeakShare => TotalCost > 0 ? (double)(PeakCost / TotalCost) : 0;

    public static HourlyProfile From(IEnumerable<UsageEvent> events, ModelPricingService pricing, PeakWindow peak, TimeZoneInfo local)
    {
        var cost = new decimal[24];
        var fresh = new long[24];
        decimal peakCost = 0m, offCost = 0m;
        long peakFresh = 0, offFresh = 0;
        foreach (var e in events)
        {
            var estimate = pricing.Estimate(e.Model, e.Tokens);
            var value = estimate.IsPriced ? estimate.ValueUsd : 0m;
            var hour = TimeZoneInfo.ConvertTime(e.Timestamp, local).Hour;
            cost[hour] += value;
            fresh[hour] += e.Tokens.Fresh;
            if (peak.IsPeak(e.Timestamp))
            {
                peakCost += value;
                peakFresh += e.Tokens.Fresh;
            }
            else
            {
                offCost += value;
                offFresh += e.Tokens.Fresh;
            }
        }

        return new HourlyProfile(cost, fresh, peakCost, offCost, peakFresh, offFresh);
    }
}

/// <summary>One exact reading of Claude's 5-hour window (from calibration or the status line).</summary>
public sealed record PlanReading(DateTimeOffset At, double FiveHourPercent, DateTimeOffset FiveHourResetsAt);

/// <summary>
/// How fast Claude's 5-hour limit fills per dollar of estimated API equivalent, in peak and in off-peak hours. Built from
/// pairs of consecutive readings within the same 5-hour window: the percentage gained between them, against the Claude
/// usage logged between them. Null on a side until it has enough data.
/// </summary>
public sealed record DrainStats(double? PeakPercentPerUsd, double? OffPeakPercentPerUsd, int PeakIntervals, int OffPeakIntervals)
{
    /// <summary>A side needs this many intervals and this much priced usage before it is shown.</summary>
    public const int MinIntervals = 5;

    public const decimal MinUsd = 1m;

    /// <summary>Peak drain divided by off-peak drain, when both sides are known.</summary>
    public double? PeakFactor => PeakPercentPerUsd is { } p && OffPeakPercentPerUsd is { } o && o > 0 ? p / o : null;
}

public static class PlanDrain
{
    /// <summary>Readings further apart than this are not compared (the gap may hide resets or lost readings).</summary>
    public static readonly TimeSpan MaxGap = TimeSpan.FromHours(2);

    public static DrainStats Analyze(IEnumerable<PlanReading> readings, IReadOnlyList<UsageEvent> claudeEvents, ModelPricingService pricing, PeakWindow peak)
    {
        var ordered = readings.OrderBy(r => r.At).ToList();
        var events = claudeEvents.OrderBy(e => e.Timestamp).ToList();
        double peakPercent = 0, offPercent = 0;
        decimal peakUsd = 0m, offUsd = 0m;
        int peakCount = 0, offCount = 0;

        for (var i = 1; i < ordered.Count; i++)
        {
            var (a, b) = (ordered[i - 1], ordered[i]);
            var gained = b.FiveHourPercent - a.FiveHourPercent;
            // Same window (reset times differ only by milliseconds between readings), close together, and no reset.
            if (Math.Abs((a.FiveHourResetsAt - b.FiveHourResetsAt).TotalMinutes) > 1 || b.At - a.At > MaxGap || b.At <= a.At || gained < 0)
            {
                continue;
            }

            decimal usd = 0m;
            foreach (var e in events)
            {
                if (e.Timestamp <= a.At)
                {
                    continue;
                }

                if (e.Timestamp > b.At)
                {
                    break;
                }

                var estimate = pricing.Estimate(e.Model, e.Tokens);
                usd += estimate.IsPriced ? estimate.ValueUsd : 0m;
            }

            if (usd <= 0m)
            {
                continue;
            }

            if (peak.IsPeak(a.At + (b.At - a.At) / 2))
            {
                peakPercent += gained;
                peakUsd += usd;
                peakCount++;
            }
            else
            {
                offPercent += gained;
                offUsd += usd;
                offCount++;
            }
        }

        double? Rate(double percent, decimal usd, int count) => count >= DrainStats.MinIntervals && usd >= DrainStats.MinUsd ? percent / (double)usd : null;
        return new DrainStats(Rate(peakPercent, peakUsd, peakCount), Rate(offPercent, offUsd, offCount), peakCount, offCount);
    }

    /// <summary>
    /// Reads 5-hour readings from Quotalis's <c>calibration-log.jsonl</c> ({"at", "five_hour": {"percent", "resets_at"}})
    /// and from Tokenz's older <c>calibration_log.jsonl</c> ({"at", "action": "claude", "data": {"rate_limits":
    /// {"five_hour": {"used_percentage", "resets_at" (Unix seconds)}}}}). Other lines are skipped.
    /// </summary>
    public static IEnumerable<PlanReading> ParseReadings(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            if (line.Length == 0 || !line.Contains("five_hour", StringComparison.Ordinal))
            {
                continue;
            }

            JsonNode? row;
            try
            {
                row = JsonNode.Parse(line);
            }
            catch (JsonException)
            {
                continue;
            }

            if (Time(row?["at"]) is not { } at)
            {
                continue;
            }

            if (row?["five_hour"] is JsonObject quotalis && Number(quotalis["percent"]) is { } p && Time(quotalis["resets_at"]) is { } r)
            {
                yield return new PlanReading(at, p, r);
            }
            else if (row?["action"]?.GetValueKind() == JsonValueKind.String && row["action"]!.GetValue<string>() == "claude"
                && row["data"]?["rate_limits"]?["five_hour"] is JsonObject tokenz
                && Number(tokenz["used_percentage"]) is { } tp && Number(tokenz["resets_at"]) is { } seconds && seconds > 0)
            {
                yield return new PlanReading(at, tp, DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds * 1000)));
            }
        }
    }

    private static double? Number(JsonNode? node) =>
        node?.GetValueKind() == JsonValueKind.Number ? node.GetValue<double>() : null;

    private static DateTimeOffset? Time(JsonNode? node) =>
        node?.GetValueKind() == JsonValueKind.String
        && DateTimeOffset.TryParse(node.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t)
            ? t
            : null;
}

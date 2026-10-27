using Quotalis.Core.Models;
using Quotalis.Core.Pricing;
using Quotalis.Core.Usage;

namespace Quotalis.Tests;

public class PeakHoursTests
{
    private static readonly ModelPricingService Pricing = new();

    // Wednesday 2026-10-14. Eastern is UTC-4 (daylight time) that week.
    private static DateTimeOffset Et(int hour, int minute = 0, int day = 14) => new(2026, 10, day, hour, minute, 0, TimeSpan.FromHours(-4));

    [Fact]
    public void Peak_is_weekdays_8_to_2_eastern_by_default()
    {
        var peak = PeakWindow.Default;

        Assert.False(peak.IsPeak(Et(7, 59)));
        Assert.True(peak.IsPeak(Et(8)));
        Assert.True(peak.IsPeak(Et(13, 59)));
        Assert.False(peak.IsPeak(Et(14)));
        Assert.False(peak.IsPeak(Et(10, day: 17)));                     // Saturday
        Assert.True(peak.IsPeak(new DateTimeOffset(2026, 10, 14, 13, 0, 0, TimeSpan.Zero))); // 9 AM ET, given in UTC
    }

    [Fact]
    public void Profile_splits_cost_by_local_hour_and_by_peak()
    {
        // Opus 5.5 input: $4 per million tokens.
        var events = new[]
        {
            TestData.Event(AiProviderKind.Claude, "a", Et(9), 1_000_000),   // peak
            TestData.Event(AiProviderKind.Claude, "b", Et(20), 500_000),    // off-peak
        };

        var profile = HourlyProfile.From(events, Pricing, PeakWindow.Default, PeakWindow.Eastern);

        Assert.Equal(4m, profile.CostByHour[9]);
        Assert.Equal(2m, profile.CostByHour[20]);
        Assert.Equal(4m, profile.PeakCost);
        Assert.Equal(2m, profile.OffPeakCost);
        Assert.Equal(2.0 / 3, profile.PeakShare, 3);
    }

    [Fact]
    public void Drain_compares_percent_per_dollar_between_readings_of_one_window()
    {
        var reset = Et(16, day: 14);
        var readings = new List<PlanReading>();
        var events = new List<UsageEvent>();
        // Peak (9-10 AM ET): five intervals of +2 % for $1 each. Off-peak (7-8 PM ET, next window): five of +1 % for $1.
        for (var i = 0; i <= 5; i++)
        {
            readings.Add(new PlanReading(Et(9, i * 10), i * 2, reset.AddMilliseconds(i)));
            readings.Add(new PlanReading(Et(19, i * 10), i, Et(23, 30)));
            if (i > 0)
            {
                events.Add(TestData.Event(AiProviderKind.Claude, $"p{i}", Et(9, i * 10 - 5), 250_000));
                events.Add(TestData.Event(AiProviderKind.Claude, $"o{i}", Et(19, i * 10 - 5), 250_000));
            }
        }

        var drain = PlanDrain.Analyze(readings, events, Pricing, PeakWindow.Default);

        Assert.Equal(5, drain.PeakIntervals);
        Assert.Equal(5, drain.OffPeakIntervals);
        Assert.Equal(2.0, drain.PeakPercentPerUsd!.Value, 6);
        Assert.Equal(1.0, drain.OffPeakPercentPerUsd!.Value, 6);
        Assert.Equal(2.0, drain.PeakFactor!.Value, 6);
    }

    [Fact]
    public void Drain_skips_resets_gaps_and_sides_without_enough_data()
    {
        var readings = new[]
        {
            new PlanReading(Et(9), 40, Et(12)),
            new PlanReading(Et(9, 10), 2, Et(14, 10)),        // a new window: not compared
            new PlanReading(Et(12, 30), 10, Et(14, 10)),      // over two hours later: not compared
        };
        var events = new[] { TestData.Event(AiProviderKind.Claude, "x", Et(9, 5), 1_000_000) };

        var drain = PlanDrain.Analyze(readings, events, Pricing, PeakWindow.Default);

        Assert.Equal(0, drain.PeakIntervals + drain.OffPeakIntervals);
        Assert.Null(drain.PeakFactor);
    }

    [Fact]
    public void Reads_quotalis_and_tokenz_calibration_logs()
    {
        string[] lines =
        [
            """{"at":"2026-10-17T23:35:19+00:00","by":"claude-auto","five_hour":{"percent":15,"resets_at":"2026-10-18T04:30:00+00:00"},"weekly":{"percent":2}}""",
            """{"at":"2026-10-16T07:06:23+00:00","action":"claude","by":"claude-auto","data":{"rate_limits":{"five_hour":{"used_percentage":66,"resets_at":1792144800.328}}}}""",
            """{"at":"2026-10-16T05:45:19+00:00","action":"codex","data":{"rate_limits":{"primary":{"used_percent":7}}}}""",
            "not json",
        ];

        var readings = PlanDrain.ParseReadings(lines).ToList();

        Assert.Equal(2, readings.Count);
        Assert.Equal(15, readings[0].FiveHourPercent);
        Assert.Equal(new DateTimeOffset(2026, 10, 18, 4, 30, 0, TimeSpan.Zero), readings[0].FiveHourResetsAt);
        Assert.Equal(66, readings[1].FiveHourPercent);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1792144800328), readings[1].FiveHourResetsAt);
    }
}

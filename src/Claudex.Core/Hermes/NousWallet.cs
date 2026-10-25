using System.Globalization;
using System.Text.Json;

namespace Claudex.Core.Hermes;

/// <summary>
/// The Nous Portal wallet as <c>GET /api/oauth/account</c> reports it. Amounts are USD (Nous keeps the older
/// <c>*_credits</c> names). Only these numbers, the plan name and the period end are kept; the answer's email,
/// organisation and ids are never read into memory beyond parsing, and never logged.
/// </summary>
public sealed record NousWalletReading(
    decimal TotalUsd,
    decimal? SubscriptionRemainingUsd,
    decimal? PurchasedRemainingUsd,
    decimal? MonthlyCreditsUsd,
    string? PlanName,
    DateTimeOffset? PeriodEnd,
    DateTimeOffset MeasuredAt);

public enum WalletLevel { Healthy, Low, Empty }

public static class NousWalletParser
{
    /// <summary>Null when the answer has no wallet numbers Claudex knows.</summary>
    public static NousWalletReading? Parse(string json, DateTimeOffset now)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var access = root.TryGetProperty("paid_service_access", out var a) && a.ValueKind == JsonValueKind.Object ? a : default;
            var sub = root.TryGetProperty("subscription", out var s) && s.ValueKind == JsonValueKind.Object ? s : default;

            var subscription = Pick(access, "subscription_credits_remaining") ?? Pick(sub, "credits_remaining");
            var purchased = Pick(access, "purchased_credits_remaining") ?? Pick(root, "purchased_credits_remaining");
            var total = Pick(access, "total_usable_credits")
                ?? (subscription is null && purchased is null ? null : (subscription ?? 0) + (purchased ?? 0));
            if (total is null)
            {
                return null;
            }

            var plan = sub.ValueKind == JsonValueKind.Object
                ? HermesUsageParser.Str(sub, "plan") ?? HermesUsageParser.Str(sub, "tier")
                : null;
            return new NousWalletReading(
                Math.Max(0, total.Value), subscription, purchased, Pick(sub, "monthly_credits"),
                plan, sub.ValueKind == JsonValueKind.Object ? Date(sub, "current_period_end") : null, now);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static decimal? Pick(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object ? HermesUsageParser.MoneyOrNull(e, name) : null;

    /// <summary>An ISO date or Unix seconds or milliseconds.</summary>
    private static DateTimeOffset? Date(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v))
        {
            return null;
        }

        if (v.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(v.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            return parsed;
        }

        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n) && n > 0)
        {
            return n > 1e11 ? DateTimeOffset.FromUnixTimeMilliseconds((long)n) : DateTimeOffset.FromUnixTimeMilliseconds((long)(n * 1000));
        }

        return null;
    }
}

/// <summary>
/// The yellow bar's scale. "Full" is the wallet right after it last grew (a top-up or a plan renewal); until then the
/// first reading is full. The bar drains toward 0 as Hermes spends. Persisted next to the other plan files so a
/// restart keeps the same scale.
/// </summary>
public sealed record WalletGauge(decimal FullUsd, decimal LastUsd)
{
    /// <summary>A rise smaller than this is rounding, not a top-up.</summary>
    public const decimal RefillThreshold = 0.01m;

    public static WalletGauge Next(WalletGauge? previous, decimal currentUsd)
    {
        var current = Math.Max(0, currentUsd);
        if (previous is null || current > previous.LastUsd + RefillThreshold || current > previous.FullUsd)
        {
            return new WalletGauge(current, current);
        }

        return previous with { LastUsd = current };
    }

    public double Fraction => FullUsd <= 0 ? 0 : (double)Math.Clamp(LastUsd / FullUsd, 0, 1);

    public static WalletLevel Level(decimal currentUsd, decimal lowUsd) =>
        currentUsd <= 0 ? WalletLevel.Empty : lowUsd > 0 && currentUsd < lowUsd ? WalletLevel.Low : WalletLevel.Healthy;
}

/// <summary>
/// When to read the wallet next (owner's rule, 2026-10-24): every second while Hermes works; once nothing has
/// happened for five minutes, every five minutes; never while the PC is locked or asleep. "Something happened" is a
/// balance change, new Hermes activity or a sign-in. Failures and throttling back off, honouring Retry-After.
/// Reading the wallet is a free account read: it never runs a model.
/// </summary>
public sealed class NousPollSchedule
{
    public static readonly TimeSpan Fast = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan Idle = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan QuietAfter = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan FirstBackoff = TimeSpan.FromSeconds(5);

    private DateTimeOffset? _lastSignal;
    private DateTimeOffset? _lastAttempt;
    private DateTimeOffset? _retryAt;
    private TimeSpan _backoff = TimeSpan.Zero;

    public bool Paused { get; set; }

    /// <summary>True while reads run every second.</summary>
    public bool IsFast(DateTimeOffset now) => _lastSignal is { } s && now - s < QuietAfter;

    /// <summary>Hermes did something, a sign-in finished, or the user asked for a refresh: read fast again.</summary>
    public void Wake(DateTimeOffset now) => _lastSignal = now;

    public void OnSuccess(DateTimeOffset now, bool balanceChanged)
    {
        _lastAttempt = now;
        _retryAt = null;
        _backoff = TimeSpan.Zero;
        if (balanceChanged)
        {
            _lastSignal = now;
        }
    }

    /// <summary>A failed read: back off (doubling from 5 s to 5 min), at least as long as the server's Retry-After.</summary>
    public void OnFailure(DateTimeOffset now, TimeSpan? retryAfter = null)
    {
        _lastAttempt = now;
        _backoff = _backoff == TimeSpan.Zero ? FirstBackoff : TimeSpan.FromTicks(Math.Min(_backoff.Ticks * 2, MaxBackoff.Ticks));
        var wait = retryAfter is { } r && r > _backoff ? (r > MaxBackoff * 2 ? MaxBackoff * 2 : r) : _backoff;
        _retryAt = now + wait;
    }

    /// <summary>The next read, or null while paused.</summary>
    public DateTimeOffset? Next(DateTimeOffset now)
    {
        if (Paused)
        {
            return null;
        }

        if (_retryAt is { } retry)
        {
            return retry > now ? retry : now;
        }

        if (_lastAttempt is not { } last)
        {
            return now;
        }

        var due = last + (IsFast(now) ? Fast : Idle);
        return due > now ? due : now;
    }
}

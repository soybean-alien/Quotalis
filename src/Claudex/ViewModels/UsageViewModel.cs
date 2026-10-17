using Claudex.Core.Formatting;
using Claudex.Core.Models;
using Claudex.Core.Usage;

namespace Claudex.ViewModels;

/// <summary>AI usage for today plus the contribution-style history graph.</summary>
public sealed class UsageViewModel : ObservableObject
{
    private IReadOnlyDictionary<DateOnly, IReadOnlyDictionary<string, double>> _planPeaks = new Dictionary<DateOnly, IReadOnlyDictionary<string, double>>();
    public const int HistoryWeeks = 26;

    private IReadOnlyList<UsageDay> _days = [];
    private int[] _levels = [];
    private int _inspectedIndex = -1;
    private long _maxProviderFresh;

    public UsageViewModel()
    {
        Claude.PropertyChanged += (_, _) => OnProvidersChanged();
        Codex.PropertyChanged += (_, _) => OnProvidersChanged();
    }

    /// <summary>Raised after new history was loaded (the graph rebuilds its cells).</summary>
    public event Action? HistoryChanged;

    public AiProviderViewModel Claude { get; } = new(AiProviderKind.Claude);

    public AiProviderViewModel Codex { get; } = new(AiProviderKind.Codex);

    public IReadOnlyList<UsageDay> Days => _days;

    public IReadOnlyList<int> Levels => _levels;

    public bool ShowClaude => Claude.IsEnabled;

    public bool ShowCodex => Codex.IsEnabled;

    public bool ShowBoth => ShowClaude && ShowCodex;

    public bool HasAnyProvider => ShowClaude || ShowCodex;

    public bool HasHistory => _days.Any(d => d.HasUsage);

    public int InspectedIndex => _inspectedIndex;

    public bool IsInspecting => _inspectedIndex >= 0 && _inspectedIndex < _days.Count;

    public bool IsNotInspecting => !IsInspecting;

    public string InspectedDate => IsInspecting ? DisplayFormat.ShortDay(_days[_inspectedIndex].Day) : string.Empty;

    public string InspectedClaude => IsInspecting ? DisplayFormat.Tokens(_days[_inspectedIndex].Claude.Fresh) : string.Empty;

    public string InspectedCodex => IsInspecting ? DisplayFormat.Tokens(_days[_inspectedIndex].Codex.Fresh) : string.Empty;

    /// <summary>Complete breakdown of the inspected day (second readout line).</summary>
    public string InspectedDetail
    {
        get
        {
            if (!IsInspecting)
            {
                return string.Empty;
            }

            var d = _days[_inspectedIndex];
            var all = d.Claude + d.Codex;
            return all.IsZero
                ? "No usage"
                : $"Input {DisplayFormat.Tokens(all.Input)}, cache write {DisplayFormat.Tokens(all.CacheWrite)}, output {DisplayFormat.Tokens(all.Output)}, cache read {DisplayFormat.Tokens(all.CacheRead)}";
        }
    }

    /// <summary>"Claude plan peak 72% (5 h), 41% (week)" for days on which Claude Code reported plan usage.</summary>
    public string InspectedPlan
    {
        get
        {
            if (!IsInspecting || !_planPeaks.TryGetValue(_days[_inspectedIndex].Day, out var windows))
            {
                return string.Empty;
            }

            var parts = new List<string>();
            if (windows.TryGetValue("five_hour", out var current)) parts.Add($"{Math.Round(current):0}% (5 h)");
            if (windows.TryGetValue("seven_day", out var weekly)) parts.Add($"{Math.Round(weekly):0}% (week)");
            return parts.Count == 0 ? string.Empty : "Claude plan peak " + string.Join(", ", parts);
        }
    }

    public bool HasInspectedPlan => InspectedPlan.Length > 0;

    /// <summary>Readout: the day's fresh tokens (Claude and Codex together).</summary>
    public string InspectedTooltipTokens => !IsInspecting ? string.Empty
        : _days[_inspectedIndex].FreshTokens == 0 ? "No usage"
        : $"{DisplayFormat.Tokens(_days[_inspectedIndex].FreshTokens)} fresh tokens";

    /// <summary>The readout grows by one line once plan usage has ever been measured (no jump per day).</summary>
    public double ReadoutHeight => _planPeaks.Count > 0 ? 100 : 82;

    // Per-tool bars of the inspected day: Claude above Codex, on one scale (the busiest single-tool day).

    public double InspectedClaudeFraction => Fraction(IsInspecting ? _days[_inspectedIndex].Claude.Fresh : 0);

    public double InspectedCodexFraction => Fraction(IsInspecting ? _days[_inspectedIndex].Codex.Fresh : 0);

    /// <summary>"2.96M · $42.03"; "2.96M · $42.03, partly priced" when some of the tool's models have no list price.</summary>
    public string InspectedClaudeText => IsInspecting ? ToolText(_days[_inspectedIndex].Claude.Fresh, _days[_inspectedIndex].ClaudeApiValueUsd, _days[_inspectedIndex].ClaudePartial) : string.Empty;

    public string InspectedCodexText => IsInspecting ? ToolText(_days[_inspectedIndex].Codex.Fresh, _days[_inspectedIndex].CodexApiValueUsd, _days[_inspectedIndex].CodexPartial) : string.Empty;

    public string InspectedSessions => IsInspecting && _days[_inspectedIndex].HasUsage
        ? DisplayFormat.Count(_days[_inspectedIndex].Sessions, "session", "sessions")
        : "No usage";

    private double Fraction(long fresh) => _maxProviderFresh <= 0 ? 0 : Math.Clamp((double)fresh / _maxProviderFresh, 0, 1);

    // Unpriced tokens are never guessed: the priced part is shown and labelled, and a tool with no priced
    // tokens at all says so instead of showing $0.00.
    private static string ToolText(long fresh, decimal usd, bool partial) => fresh <= 0 ? "None"
        : !partial ? $"{DisplayFormat.Tokens(fresh)} · {DisplayFormat.Usd(usd)}"
        : usd > 0 ? $"{DisplayFormat.Tokens(fresh)} · {DisplayFormat.Usd(usd)}, partly priced"
        : $"{DisplayFormat.Tokens(fresh)} · no list price";

    public string InspectedValue => !IsInspecting ? string.Empty
        : _days[_inspectedIndex].ApiValuePartial
            ? (_days[_inspectedIndex].TotalApiValueUsd > 0 ? $"{DisplayFormat.Usd(_days[_inspectedIndex].TotalApiValueUsd)}, partly priced" : "Unavailable")
            : DisplayFormat.Usd(_days[_inspectedIndex].TotalApiValueUsd);

    /// <summary>Spoken description of the inspected day.</summary>
    public string InspectedAccessibleText => IsInspecting
        ? $"{InspectedDate}: Claude {InspectedClaude} fresh tokens, Codex {InspectedCodex} fresh tokens, estimated API equivalent {InspectedValue}. {InspectedDetail}{(HasInspectedPlan ? ". " + InspectedPlan : string.Empty)}"
        : "Usage history, last 26 weeks. Use the arrow keys to inspect a day.";

    public string HistorySummary
    {
        get
        {
            var active = _days.Count(d => d.HasUsage);
            return active == 0 ? "No usage in the last 26 weeks" : $"{DisplayFormat.Count(active, "active day", "active days")}, combined fresh tokens";
        }
    }

    /// <summary>Peek text for the hover activity.</summary>
    public string PeekTitle
    {
        get
        {
            var parts = new List<string>();
            if (Claude.IsReady && ShowClaude)
            {
                parts.Add($"Claude {Claude.CompactValue}");
            }

            if (Codex.IsReady && ShowCodex)
            {
                parts.Add($"Codex {Codex.CompactValue}");
            }

            return parts.Count == 0 ? "No AI usage detected" : string.Join("   ", parts);
        }
    }

    /// <summary>Mark for the peek: the tool with more fresh tokens today.</summary>
    public string PeekMark => ShowCodex && Codex.IsReady && (!ShowClaude || !Claude.IsReady || Codex.Snapshot.Today.Fresh > Claude.Snapshot.Today.Fresh) ? "Codex" : "Claude";

    public string PeekSubtitle
    {
        get
        {
            decimal total = 0;
            var any = false;
            foreach (var p in new[] { Claude, Codex })
            {
                if (p.IsEnabled && p.IsReady && p.Snapshot.ApiValueUsd is { } v)
                {
                    total += v;
                    any = true;
                }
            }

            return any ? $"{DisplayFormat.Usd(total)} estimated API equivalent today" : "Claude Code and Codex usage appears here";
        }
    }

    // Running cost totals of Claude and Codex together (estimated API equivalent, USD).

    private SpendTotals _spend = SpendTotals.Zero;

    public string WeekTotalText => DisplayFormat.Usd(_spend.Week);

    public string MonthTotalText => DisplayFormat.Usd(_spend.Month);

    public string LifetimeTotalText => DisplayFormat.Usd(_spend.Lifetime);

    /// <summary>Some tokens in these totals have no list price; the totals cover the priced part.</summary>
    public bool SpendPartial => _spend.LifetimePartial;

    public string SpendAccessibleText =>
        $"Estimated API equivalent: this week {WeekTotalText}, this month {MonthTotalText}, lifetime {LifetimeTotalText}{(SpendPartial ? ", partly priced" : string.Empty)}";

    public void SetSpendTotals(SpendTotals totals)
    {
        if (totals == _spend)
        {
            return;
        }

        _spend = totals;
        OnPropertyChanged(nameof(WeekTotalText));
        OnPropertyChanged(nameof(MonthTotalText));
        OnPropertyChanged(nameof(LifetimeTotalText));
        OnPropertyChanged(nameof(SpendPartial));
        OnPropertyChanged(nameof(SpendAccessibleText));
    }

    /// <summary>Plan usage peaks that were actually measured, per day (never backfilled).</summary>
    public void SetPlanPeaks(IReadOnlyDictionary<DateOnly, IReadOnlyDictionary<string, double>> peaks)
    {
        _planPeaks = peaks;
        OnPropertyChanged(nameof(ReadoutHeight));
        OnPropertyChanged(nameof(InspectedPlan));
        OnPropertyChanged(nameof(HasInspectedPlan));
    }

    public void SetHistory(IReadOnlyList<UsageDay> days)
    {
        _days = days;
        // Intensity follows fresh tokens: cache reads would let one long session flatten every other day.
        _levels = UsageAggregator.IntensityLevels(days.Select(d => d.FreshTokens).ToList());
        _maxProviderFresh = days.Count == 0 ? 0 : days.Max(d => Math.Max(d.Claude.Fresh, d.Codex.Fresh));
        if (_inspectedIndex >= days.Count)
        {
            _inspectedIndex = -1;
        }

        OnAllPropertiesChanged();
        HistoryChanged?.Invoke();
    }

    public void Inspect(int index)
    {
        var next = index >= 0 && index < _days.Count ? index : -1;
        if (next == _inspectedIndex)
        {
            return;
        }

        _inspectedIndex = next;
        OnPropertyChanged(nameof(InspectedIndex));
        OnPropertyChanged(nameof(IsInspecting));
        OnPropertyChanged(nameof(IsNotInspecting));
        OnPropertyChanged(nameof(InspectedDate));
        OnPropertyChanged(nameof(InspectedClaude));
        OnPropertyChanged(nameof(InspectedCodex));
        OnPropertyChanged(nameof(InspectedValue));
        OnPropertyChanged(nameof(InspectedDetail));
        OnPropertyChanged(nameof(InspectedTooltipTokens));
        OnPropertyChanged(nameof(InspectedClaudeFraction));
        OnPropertyChanged(nameof(InspectedCodexFraction));
        OnPropertyChanged(nameof(InspectedClaudeText));
        OnPropertyChanged(nameof(InspectedCodexText));
        OnPropertyChanged(nameof(InspectedSessions));
        OnPropertyChanged(nameof(InspectedAccessibleText));
    }

    private void OnProvidersChanged()
    {
        OnPropertyChanged(nameof(ShowClaude));
        OnPropertyChanged(nameof(ShowCodex));
        OnPropertyChanged(nameof(ShowBoth));
        OnPropertyChanged(nameof(HasAnyProvider));
        OnPropertyChanged(nameof(PeekTitle));
        OnPropertyChanged(nameof(PeekSubtitle));
    }
}

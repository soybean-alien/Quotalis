using Quotalis.Core.Formatting;
using Quotalis.Core.Models;
using Quotalis.Core.Usage;

namespace Quotalis.ViewModels;

/// <summary>AI usage for today plus the contribution-style history graph.</summary>
public sealed class UsageViewModel : ObservableObject
{
    private IReadOnlyDictionary<DateOnly, IReadOnlyDictionary<string, double>> _planPeaks = new Dictionary<DateOnly, IReadOnlyDictionary<string, double>>();
    public const int HistoryWeeks = 26;

    private IReadOnlyList<UsageDay> _days = [];
    private IReadOnlyList<UsageDay> _storedDays = [];
    private int[] _levels = [];
    private int _inspectedIndex = -1;
    private long _maxProviderFresh;

    public UsageViewModel()
    {
        Claude.PropertyChanged += (_, _) => OnProvidersChanged();
        Codex.PropertyChanged += (_, _) => OnProvidersChanged();
        Hermes.PropertyChanged += (_, e) =>
        {
            // Only the switch: the wallet updates every second while Hermes works and must not re-lay the panel.
            if (e.PropertyName == nameof(HermesViewModel.IsEnabled))
            {
                OnProvidersChanged();
            }
        };
    }

    /// <summary>Raised after new history was loaded (the graph rebuilds its cells).</summary>
    public event Action? HistoryChanged;

    public AiProviderViewModel Claude { get; } = new(AiProviderKind.Claude);

    public AiProviderViewModel Codex { get; } = new(AiProviderKind.Codex);

    public HermesViewModel Hermes { get; } = new();

    public IReadOnlyList<UsageDay> Days => _days;

    public IReadOnlyList<int> Levels => _levels;

    public bool ShowClaude => Claude.IsEnabled;

    public bool ShowCodex => Codex.IsEnabled;

    public bool ShowBoth => ShowClaude && ShowCodex;

    public bool ShowHermes => Hermes.IsEnabled;

    /// <summary>How many columns the Usage panel shows (Hermes widens the island).</summary>
    public int ColumnCount => (ShowClaude ? 1 : 0) + (ShowCodex ? 1 : 0) + (ShowHermes ? 1 : 0);

    public bool HasAnyProvider => ShowClaude || ShowCodex || ShowHermes;

    public string FootnoteText => ShowHermes
        ? "Claude and Codex costs are API list prices, not what you pay. Hermes spends real Nous credits, shown in its column and kept out of these totals; its tokens count in the graph."
        : "Costs are API list prices, not what you pay.";

    public bool HasHistory => _days.Any(d => d.HasUsage);

    public int InspectedIndex => _inspectedIndex;

    public bool IsInspecting => _inspectedIndex >= 0 && _inspectedIndex < _days.Count;

    public bool IsNotInspecting => !IsInspecting;

    /// <summary>"Jul 24", or "Jul 24 · pinned" when that day was clicked.</summary>
    public string InspectedDate => IsInspecting
        ? DisplayFormat.ShortDay(_days[_inspectedIndex].Day) + (IsShowingPinned ? " · pinned" : string.Empty)
        : string.Empty;

    public string InspectedClaude => IsInspecting ? DisplayFormat.Tokens(_days[_inspectedIndex].Claude.Fresh) : string.Empty;

    public string InspectedCodex => IsInspecting ? DisplayFormat.Tokens(_days[_inspectedIndex].Codex.Fresh) : string.Empty;

    public double InspectedHermesFraction => Fraction(IsInspecting ? _days[_inspectedIndex].Hermes.Fresh : 0);

    /// <summary>Tokens only: Hermes's money is real spending, shown in its column, never as an API equivalent.</summary>
    public string InspectedHermesText => !IsInspecting ? string.Empty
        : _days[_inspectedIndex].Hermes.Fresh > 0 ? DisplayFormat.Tokens(_days[_inspectedIndex].Hermes.Fresh) + " tokens" : "None";

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
            var all = d.Claude + d.Codex + d.Hermes;
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

    /// <summary>Readout: the day's fresh tokens (every tool together).</summary>
    public string InspectedTooltipTokens => !IsInspecting ? string.Empty
        : _days[_inspectedIndex].FreshTokens == 0 ? "No usage"
        : $"{DisplayFormat.Tokens(_days[_inspectedIndex].FreshTokens)} fresh tokens";

    /// <summary>The readout grows by one line once plan usage has ever been measured (no jump per day).</summary>
    /// <summary>The day area below the hours block: one line taller once plan usage has ever been measured (no jump per day).</summary>
    private bool _isCondensed;

    /// <summary>
    /// The usage strip: only plan usage, the gold totals and the heatmap (plus a day's readout while one is inspected or
    /// pinned). Set by the island; the panel collapses everything else, and the island's shape spring animates the size.
    /// </summary>
    public bool IsCondensed
    {
        get => _isCondensed;
        set
        {
            if (value != _isCondensed)
            {
                _isCondensed = value;
                OnPropertyChanged(nameof(IsCondensed));
            }
        }
    }

    public double ReadoutHeight => (_planPeaks.Count > 0 ? 100 : 82) + (ShowHermes ? 18 : 0);

    // Per-tool bars of the inspected day: Claude above Codex, on one scale (the busiest single-tool day).

    public double InspectedClaudeFraction => Fraction(IsInspecting ? _days[_inspectedIndex].Claude.Fresh : 0);

    public double InspectedCodexFraction => Fraction(IsInspecting ? _days[_inspectedIndex].Codex.Fresh : 0);

    /// <summary>"2.96M · $42.03"; "2.96M · $42.03, partly priced" when some of the tool's models have no list price.</summary>
    public string InspectedClaudeText => IsInspecting ? ToolText(_days[_inspectedIndex].Claude.Fresh, _days[_inspectedIndex].ClaudeApiValueUsd, _days[_inspectedIndex].ClaudePartial) : string.Empty;

    public string InspectedCodexText => !IsInspecting ? string.Empty
        : _days[_inspectedIndex].CodexImported && _days[_inspectedIndex].Codex.Fresh > 0
            ? $"{DisplayFormat.Tokens(_days[_inspectedIndex].Codex.Fresh)} · {DisplayFormat.Usd(_days[_inspectedIndex].CodexApiValueUsd)}, estimated"
            : ToolText(_days[_inspectedIndex].Codex.Fresh, _days[_inspectedIndex].CodexApiValueUsd, _days[_inspectedIndex].CodexPartial);

    public string InspectedSessions => !IsInspecting || !_days[_inspectedIndex].HasUsage ? "No usage"
        : _days[_inspectedIndex].CodexImported ? "Codex from your ChatGPT profile"
        : DisplayFormat.Count(_days[_inspectedIndex].Sessions, "session", "sessions");

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
            : DisplayFormat.Usd(_days[_inspectedIndex].TotalApiValueUsd) + (_days[_inspectedIndex].CodexImported ? ", estimated" : string.Empty);

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

    // Usage by hour of day and Anthropic's peak hours (Claude and Codex together, recent days).

    public const int PeakStatsDays = 28;

    private IReadOnlyList<double> _hourFractions = new double[24];
    private IReadOnlyList<bool> _peakHourMask = new bool[24];
    private string _peakShareText = string.Empty;
    private string _drainText = string.Empty;

    public IReadOnlyList<double> HourFractions => _hourFractions;

    /// <summary>Local hours (0 to 23) that fall in the peak window on a weekday.</summary>
    public IReadOnlyList<bool> PeakHourMask => _peakHourMask;

    /// <summary>"Peak hours (weekdays 8 AM–2 PM ET): 41% of your usage, last 28 days".</summary>
    public string PeakShareText => _peakShareText;

    /// <summary>How fast Claude's 5-hour limit fills per dollar in peak versus off-peak hours, or how far the comparison is.</summary>
    public string DrainText => _drainText;

    public bool HasHourProfile => _peakShareText.Length > 0;

    private HourlyProfile _hourProfile = HourlyProfile.Empty;
    private int _hoveredHour = -1;

    public int HoveredHour => _hoveredHour;

    /// <summary>"2 PM: 12.4M fresh tokens · $4.10 (peak hour)" while a bar is hovered, else a hint.</summary>
    public string HourDetailText
    {
        get
        {
            if (_hoveredHour is < 0 or > 23)
            {
                return "Hover an hour for its tokens, last " + PeakStatsDays + " days";
            }

            var c = System.Globalization.CultureInfo.CurrentCulture;
            var start = new DateTime(2000, 1, 1, _hoveredHour, 0, 0).ToString("h tt", c);
            var end = new DateTime(2000, 1, 1, (_hoveredHour + 1) % 24, 0, 0).ToString("h tt", c);
            var fresh = _hourProfile.FreshByHour[_hoveredHour];
            var peak = _hoveredHour < _peakHourMask.Count && _peakHourMask[_hoveredHour] ? ", peak on weekdays" : string.Empty;
            return fresh == 0
                ? $"{start}–{end}: no usage, last {PeakStatsDays} days{peak}"
                : $"{start}–{end}: {DisplayFormat.Tokens(fresh)} fresh tokens · {DisplayFormat.Usd(_hourProfile.CostByHour[_hoveredHour])}, last {PeakStatsDays} days{peak}";
        }
    }

    /// <summary>The pointer is over the bar of <paramref name="hour"/> (0 to 23), or left the strip (-1).</summary>
    public void HoverHour(int hour)
    {
        var next = hour is >= 0 and <= 23 ? hour : -1;
        if (next == _hoveredHour)
        {
            return;
        }

        _hoveredHour = next;
        OnPropertyChanged(nameof(HoveredHour));
        OnPropertyChanged(nameof(HourDetailText));
    }

    public event Action? HourProfileChanged;

    public void SetPeakStats(HourlyProfile profile, DrainStats drain, PeakWindow window)
    {
        _hourProfile = profile;
        var max = profile.CostByHour.Max();
        _hourFractions = profile.CostByHour.Select(c => max > 0 ? (double)(c / max) : 0).ToList();
        _peakHourMask = LocalPeakHours(window);
        var c = System.Globalization.CultureInfo.CurrentCulture;
        _peakShareText = profile.TotalCost > 0
            ? $"Peak hours ({window.Describe(c)}): {Math.Round(profile.PeakShare * 100):0}% of your usage, last {PeakStatsDays} days"
            : $"Peak hours ({window.Describe(c)}): no usage in the last {PeakStatsDays} days";
        _drainText = drain.PeakFactor is { } factor
            ? $"5h limit per $1: {drain.PeakPercentPerUsd!.Value.ToString("0.00", c)}% peak, {drain.OffPeakPercentPerUsd!.Value.ToString("0.00", c)}% off-peak ({factor.ToString("0.0", c)}×)"
            : $"Limit drain: collecting readings (peak {Math.Min(drain.PeakIntervals, DrainStats.MinIntervals)}/{DrainStats.MinIntervals}, off-peak {Math.Min(drain.OffPeakIntervals, DrainStats.MinIntervals)}/{DrainStats.MinIntervals})";
        OnPropertyChanged(nameof(HourFractions));
        OnPropertyChanged(nameof(PeakHourMask));
        OnPropertyChanged(nameof(PeakShareText));
        OnPropertyChanged(nameof(DrainText));
        OnPropertyChanged(nameof(HasHourProfile));
        OnPropertyChanged(nameof(HourDetailText));
        HourProfileChanged?.Invoke();
    }

    /// <summary>Which local hours are peak hours on a weekday (this week's offset to US Eastern time).</summary>
    private static bool[] LocalPeakHours(PeakWindow window)
    {
        var today = DateTime.Today;
        var weekday = today.AddDays(today.DayOfWeek switch { DayOfWeek.Saturday => 2, DayOfWeek.Sunday => 1, _ => 0 });
        var mask = new bool[24];
        for (var h = 0; h < 24; h++)
        {
            var local = new DateTimeOffset(weekday.AddHours(h + 0.5), TimeZoneInfo.Local.GetUtcOffset(weekday.AddHours(h + 0.5)));
            mask[h] = window.IsPeak(local);
        }

        return mask;
    }

    // Running cost totals of Claude and Codex together (estimated API equivalent, USD).

    private SpendTotals _spend = SpendTotals.Zero;

    public string WeekTotalText => DisplayFormat.Usd(_spend.Week);

    public string MonthTotalText => DisplayFormat.Usd(_spend.Month);

    public string LifetimeTotalText => DisplayFormat.Usd(_spend.Lifetime);

    /// <summary>Some tokens in these totals have no list price, or some days are imported estimates.</summary>
    public bool SpendPartial => _spend.LifetimePartial || _spend.IncludesImported;

    public string SpendNote => (_spend.LifetimePartial, _spend.IncludesImported) switch
    {
        (true, true) => "Partly priced, and Codex days imported from your ChatGPT profile are estimated.",
        (true, false) => "Partly priced: some tokens have no list price.",
        (false, true) => "Includes Codex days imported from your ChatGPT profile (estimated).",
        _ => string.Empty,
    };

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
        OnPropertyChanged(nameof(SpendNote));
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
        _storedDays = days;
        ApplyHistory();
    }

    /// <summary>Hermes's token use changed (it comes from Hermes's own records, not the usage database).</summary>
    public void RefreshHermesHistory()
    {
        if (_storedDays.Count > 0)
        {
            ApplyHistory();
        }
    }

    private void ApplyHistory()
    {
        var hermes = ShowHermes ? Hermes.TokensByDay : new Dictionary<DateOnly, Core.Models.TokenCounts>();
        var days = hermes.Count == 0 ? _storedDays
            : _storedDays.Select(d => hermes.TryGetValue(d.Day, out var t) ? d with { Hermes = t } : d).ToList();
        var showingPinned = IsShowingPinned;
        _days = days;
        // Intensity follows fresh tokens: cache reads would let one long session flatten every other day.
        _levels = UsageAggregator.IntensityLevels(days.Select(d => d.FreshTokens).ToList());
        _maxProviderFresh = days.Count == 0 ? 0 : days.Max(d => Math.Max(Math.Max(d.Claude.Fresh, d.Codex.Fresh), d.Hermes.Fresh));

        // The pinned day is kept by date: live reloads (and the new day at midnight) shift indexes, not the pin.
        if (_pinnedDay is { } pinned)
        {
            var index = IndexOfDay(pinned);
            if (index < 0)
            {
                _pinnedDay = null;
            }
            else if (showingPinned || _inspectedIndex < 0)
            {
                _inspectedIndex = index;
            }
        }

        if (_inspectedIndex >= days.Count)
        {
            _inspectedIndex = -1;
        }

        OnAllPropertiesChanged();
        HistoryChanged?.Invoke();
    }

    // A clicked day stays in the readout ("pinned") until another day is clicked or the island collapses; hovering
    // other days previews them and leaving the graph returns to the pinned day.

    private DateOnly? _pinnedDay;

    /// <summary>The pinned day's index in <see cref="Days"/>, or -1.</summary>
    public int PinnedIndex => _pinnedDay is { } day ? IndexOfDay(day) : -1;

    /// <summary>The readout shows the pinned day (not a hovered one).</summary>
    public bool IsShowingPinned => _pinnedDay is { } day && IsInspecting && _days[_inspectedIndex].Day == day;

    /// <summary>Pins the day at <paramref name="index"/> and shows it.</summary>
    public void Pin(int index)
    {
        if (index < 0 || index >= _days.Count)
        {
            return;
        }

        _pinnedDay = _days[index].Day;
        Inspect(index);
        OnPinChanged();
    }

    /// <summary>The pointer left the graph (or it lost focus): back to the pinned day, or to the summary.</summary>
    public void EndHover() => Inspect(PinnedIndex);

    /// <summary>The island collapsed: forget the pin.</summary>
    public void ClearPin()
    {
        if (_pinnedDay is null)
        {
            return;
        }

        _pinnedDay = null;
        Inspect(-1);
        OnPinChanged();
    }

    private void OnPinChanged()
    {
        OnPropertyChanged(nameof(PinnedIndex));
        OnPropertyChanged(nameof(IsShowingPinned));
        OnPropertyChanged(nameof(InspectedDate));
    }

    private int IndexOfDay(DateOnly day)
    {
        for (var i = 0; i < _days.Count; i++)
        {
            if (_days[i].Day == day)
            {
                return i;
            }
        }

        return -1;
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
        OnPropertyChanged(nameof(IsShowingPinned));
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
        OnPropertyChanged(nameof(InspectedHermesFraction));
        OnPropertyChanged(nameof(InspectedHermesText));
        OnPropertyChanged(nameof(InspectedSessions));
        OnPropertyChanged(nameof(InspectedAccessibleText));
    }

    private void OnProvidersChanged()
    {
        OnPropertyChanged(nameof(ShowClaude));
        OnPropertyChanged(nameof(ShowCodex));
        OnPropertyChanged(nameof(ShowBoth));
        OnPropertyChanged(nameof(ShowHermes));
        OnPropertyChanged(nameof(ColumnCount));
        OnPropertyChanged(nameof(ReadoutHeight));
        OnPropertyChanged(nameof(FootnoteText));
        OnPropertyChanged(nameof(HasAnyProvider));
        OnPropertyChanged(nameof(PeekTitle));
        OnPropertyChanged(nameof(PeekSubtitle));
    }
}

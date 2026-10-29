using System.Globalization;
using Quotalis.Core.Formatting;
using Quotalis.Core.Hermes;
using Quotalis.Core.Models;

namespace Quotalis.ViewModels;

/// <summary>Where the Nous sign-in stands, as the Hermes column needs it.</summary>
public enum HermesWalletState { SignedOut, SigningIn, Ready, Expired, Unavailable, UnknownFormat, Locked }

/// <summary>
/// The Hermes column: a wallet that drains (Nous credits) instead of a plan that fills, today's token use from Hermes's
/// own records, and the model. Hermes's money is real spending and stays out of the API-equivalent totals.
/// </summary>
public sealed class HermesViewModel : ObservableObject
{
    private bool _isEnabled;
    private HermesWalletState _walletState = HermesWalletState.SignedOut;
    private NousWalletReading? _wallet;
    private WalletGauge? _gauge;
    private HermesUsage? _usage;
    private string? _usageProblem;
    private bool _isFast;
    private bool _isPaused;
    private decimal _lowUsd = 5;

    public string DisplayName => "Hermes";

    public string ShortName => "Hermes";

    public Microsoft.UI.Xaml.Media.Brush BrandBrush => Brush("HermesBrush");

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (SetProperty(ref _isEnabled, value))
            {
                OnAllPropertiesChanged();
            }
        }
    }

    /// <summary>"Sign in…": open Settings, Modules, where the Nous sign-in lives.</summary>
    public event Action? SignInRequested;

    public void RequestSignIn() => SignInRequested?.Invoke();

    /// <summary>"Top up Nous Wallet": the portal's billing page with the top-up open.</summary>
    public event Action? TopUpRequested;

    public void RequestTopUp() => TopUpRequested?.Invoke();

    public void SetWallet(HermesWalletState state, NousWalletReading? wallet, WalletGauge? gauge, bool fast, bool paused, decimal lowUsd)
    {
        _walletState = state;
        _wallet = wallet;
        _gauge = gauge;
        _isFast = fast;
        _isPaused = paused;
        _lowUsd = lowUsd;
        OnAllPropertiesChanged();
    }

    public void SetUsage(HermesUsage? usage, string? problem)
    {
        _usage = usage;
        _usageProblem = problem;
        OnAllPropertiesChanged();
    }

    // The wallet ------------------------------------------------------------------------------------------

    public bool HasWallet => _isEnabled && _wallet is not null;

    public bool HasNoWallet => _isEnabled && _wallet is null;

    public WalletLevel Level => _wallet is null ? WalletLevel.Healthy : WalletGauge.Level(_wallet.TotalUsd, _lowUsd);

    public string BalanceText => _wallet is null ? string.Empty : DisplayFormat.Usd(_wallet.TotalUsd);

    /// <summary>The compact island and peek: "$18.42 left".</summary>
    public string CompactValue => _wallet is null ? string.Empty : BalanceText + " left";

    public double WalletFraction => _gauge?.Fraction ?? (_wallet is null ? 0 : 1);

    public Microsoft.UI.Xaml.Media.Brush WalletBrush => Brush(Level switch
    {
        WalletLevel.Empty => "HermesEmptyBrush",
        WalletLevel.Low => "HermesLowBrush",
        _ => "HermesBrush",
    });

    public string FullText => _gauge is null || _gauge.FullUsd <= 0 ? string.Empty : $"Full was {DisplayFormat.Usd(_gauge.FullUsd)} at the last top-up";

    /// <summary>When the number was read and how often it is read now.</summary>
    public string ReadText
    {
        get
        {
            if (_wallet is null)
            {
                return string.Empty;
            }

            var at = "Read " + DisplayFormat.TimeOfDay(_wallet.MeasuredAt.ToLocalTime());
            return _isPaused ? at + " · paused while the PC is locked"
                : _isFast ? at + " · every second while Hermes works"
                : at + " · every 5 min while idle";
        }
    }

    public string WarningText => Level switch
    {
        WalletLevel.Empty => "Wallet empty. Hermes can't run paid models until you top up.",
        WalletLevel.Low => "Running low. Top up soon.",
        _ => string.Empty,
    };

    public bool HasWarning => HasWallet && Level != WalletLevel.Healthy;

    public Microsoft.UI.Xaml.Media.Brush WarningBrush => Brush(Level == WalletLevel.Empty ? "HermesEmptyBrush" : "HermesLowBrush");

    public bool ShowTopUp => HasWallet && Level == WalletLevel.Empty;

    /// <summary>Tooltip: where the total comes from.</summary>
    public string BreakdownText
    {
        get
        {
            if (_wallet is null)
            {
                return string.Empty;
            }

            var parts = new List<string>();
            if (_wallet.SubscriptionRemainingUsd is { } plan)
            {
                parts.Add($"Plan credits {DisplayFormat.Usd(plan)}" + (_wallet.MonthlyCreditsUsd is { } monthly ? $" of {DisplayFormat.Usd(monthly)} a month" : string.Empty));
            }

            if (_wallet.PurchasedRemainingUsd is { } bought)
            {
                parts.Add($"Purchased credits {DisplayFormat.Usd(bought)}");
            }

            if (_wallet.PeriodEnd is { } end)
            {
                parts.Add("Plan renews " + end.ToLocalTime().ToString("MMM d", CultureInfo.CurrentCulture));
            }

            return parts.Count == 0 ? $"Nous wallet {BalanceText}" : string.Join("\n", parts);
        }
    }

    /// <summary>Why there is no balance, in the column.</summary>
    public string WalletStatusText => _walletState switch
    {
        HermesWalletState.SigningIn => "Waiting for the Nous sign-in",
        HermesWalletState.Expired => "Nous sign-in expired",
        HermesWalletState.Unavailable => "Nous Portal not reachable",
        HermesWalletState.UnknownFormat => "Nous answered in an unknown format",
        HermesWalletState.Locked => "Nous wallet coming soon",
        _ => "Not signed in to Nous",
    };

    public bool CanSignIn => HasNoWallet && _walletState is HermesWalletState.SignedOut or HermesWalletState.Expired;

    // Today's token use ---------------------------------------------------------------------------------------

    private HermesDay? Today => _usage?.DayOf(DateOnly.FromDateTime(DateTime.Today));

    public bool HasUsageSource => _isEnabled && (_usage is not null || _usageProblem is not null);

    public bool HasUsage => _isEnabled && _usage is not null;

    public string UsageProblem => _usageProblem ?? string.Empty;

    public bool HasUsageProblem => _isEnabled && _usage is null && _usageProblem is not null;

    public bool HasUsageToday => Today is { } d && d.Tokens.Processed > 0;

    public string TokensText => DisplayFormat.Tokens(Today?.Tokens.Fresh ?? 0);

    public string TokensUnit => "fresh tokens";

    public string InputText => DisplayFormat.Tokens(Today?.Tokens.Input ?? 0);

    public string CacheWriteText => DisplayFormat.Tokens(Today?.Tokens.CacheWrite ?? 0);

    public string OutputText => DisplayFormat.Tokens(Today?.Tokens.Output ?? 0);

    public string CacheReadText => DisplayFormat.Tokens(Today?.Tokens.CacheRead ?? 0);

    public string ReasoningText => DisplayFormat.Tokens(Today?.ReasoningTokens ?? 0);

    public string ApiCallsText => (Today?.ApiCalls ?? 0).ToString("N0", CultureInfo.CurrentCulture);

    public string SessionsText => (Today?.Sessions ?? 0).ToString(CultureInfo.CurrentCulture);

    public string SpentText => Today is { } d ? DisplayFormat.Usd(d.CostUsd) : DisplayFormat.Usd(0);

    public string SpentCaption => Today is { CostEstimated: false } ? "spent today, as Nous billed it" : "spent today, estimated by Hermes";

    /// <summary>The model without its provider prefix ("deepseek-v4.1-flash").</summary>
    public string ModelText => DisplayFormat.ModelLabel(_usage?.CurrentModel?.Split('/')[^1]);

    public bool HasModel => ModelText.Length > 0;

    /// <summary>"deepseek/deepseek-v4.1-flash, last used 22:31": the model comes from Hermes's records (a minute late at most).</summary>
    public string ModelCaption => _usage?.CurrentModelSeen is { } seen ? "last used " + DisplayFormat.TimeOfDay(seen.ToLocalTime()) : string.Empty;

    /// <summary>The pulsing dot: reads run every second because Hermes is working.</summary>
    public bool IsLive => HasWallet && _isFast && !_isPaused;

    /// <summary>Token use per local day, for the heatmap.</summary>
    public IReadOnlyDictionary<DateOnly, TokenCounts> TokensByDay =>
        _usage?.Days.ToDictionary(d => d.Day, d => d.Tokens) ?? new Dictionary<DateOnly, TokenCounts>();

    private static Microsoft.UI.Xaml.Media.Brush Brush(string key) =>
        (Microsoft.UI.Xaml.Media.Brush)Microsoft.UI.Xaml.Application.Current.Resources[key];
}

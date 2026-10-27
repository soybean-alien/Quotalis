using Quotalis.Core.Modules;
using Quotalis.Core.Providers;

namespace Quotalis.ViewModels;

/// <summary>A section of the expanded island. Claude, Codex and Hermes share the Usage tab.</summary>
public enum IslandTab
{
    Usage,
    Git,
}

/// <summary>
/// Root view model: decides what the compact capsule shows, which tabs exist and in which order, and
/// what the medium state ("live activity") says for each kind of event.
/// </summary>
public sealed class IslandViewModel : ObservableObject
{
    private IslandTab _selectedTab = IslandTab.Usage;
    private ActivityKind? _lastEventKind;
    private IReadOnlyList<ModuleId> _order = ModuleCatalog.DefaultOrder;
    private IslandTab? _pendingTab;
    private IReadOnlyList<ModuleId> _favorites = [];
    private ModuleId? _lastActive;
    private ModuleId? _compactModule;
    private long _turn;
    private bool _refreshingCompact;

    public IslandViewModel(UsageViewModel usage, ModuleViewModels modules, bool isDemo)
    {
        Usage = usage;
        Git = modules.Git;
        IsDemo = isDemo;
        Activity.Privacy = Privacy;
        Usage.PropertyChanged += (_, _) => OnCompactChanged();
        Usage.Claude.PropertyChanged += (_, _) => OnCompactChanged();
        Usage.Codex.PropertyChanged += (_, _) => OnCompactChanged();
        Usage.Hermes.PropertyChanged += (_, _) => OnCompactChanged();
        Git.PropertyChanged += (_, _) => OnPrimaryChanged(ModuleId.Git);
    }

    /// <summary>Requests the medium state for an event (duration null means the default).</summary>
    public event Action<TimeSpan?>? ActivityRequested;

    /// <summary>The tabs changed (a module was turned on or off, or reordered).</summary>
    public event Action? TabsChanged;

    public UsageViewModel Usage { get; }

    public GitViewModel Git { get; }

    public ActivityViewModel Activity { get; } = new();

    /// <summary>Camera and microphone in use: shown in every state, independent of the module shown.</summary>
    public PrivacyViewModel Privacy { get; } = new();

    public bool IsDemo { get; }

    // Compact capsule -----------------------------------------------------------------------

    // The classic summary (AI usage) is shown when no module is featured.
    public bool CompactShowClaude => !IsFeaturing && Usage.ShowClaude && ShowProvider(Usage.Claude, Usage.Codex);

    public bool CompactShowCodex => !IsFeaturing && Usage.ShowCodex && ShowProvider(Usage.Codex, Usage.Claude);

    // Featured module (favorites, or the module opened last) ----------------------------------------

    /// <summary>Raised when the user stars or unstars a tab: its modules and their new state (the host persists it).</summary>
    public event Action<IReadOnlyList<ModuleId>, bool>? FavoriteChanged;

    /// <summary>Raised when the user opens a module (the host remembers it).</summary>
    public event Action<ModuleId>? ModuleOpened;

    /// <summary>The featured module changed (the compact view cross-fades).</summary>
    public event Action? CompactModuleChanged;

    public IReadOnlyList<ModuleId> Favorites => _favorites;

    /// <summary>The module the compact island features, or null for the classic summary.</summary>
    public ModuleId? CompactModule => _compactModule;

    public bool IsFeaturing => _compactModule is not null;

    public bool CompactRotates => CompactSelector.Rotates(_favorites.Where(IsModuleEnabled).ToList());

    public string CompactPrimaryLabel => _compactModule switch
    {
        ModuleId.Claude => Usage.Claude.ShortName,
        ModuleId.Codex => Usage.Codex.ShortName,
        ModuleId.Hermes => Usage.Hermes.ShortName,
        _ => string.Empty,
    };

    public bool HasCompactPrimaryLabel => CompactPrimaryLabel.Length > 0;

    public string CompactPrimaryValue => _compactModule switch
    {
        ModuleId.Claude => ProviderValue(Usage.Claude),
        ModuleId.Codex => ProviderValue(Usage.Codex),
        ModuleId.Hermes => Usage.Hermes.HasWallet ? Usage.Hermes.BalanceText : Usage.Hermes.WalletStatusText,
        ModuleId.Git => Git.CompactText,
        _ => string.Empty,
    };

    public bool HasCompactPrimaryValue => CompactPrimaryValue.Length > 0;

    /// <summary>A secondary part after a dot (Git changes). Claude and Codex show no money in the capsule.</summary>
    public string CompactPrimaryDetail => _compactModule switch
    {
        ModuleId.Git => Git.CompactDetail,
        _ => string.Empty,
    };

    public bool HasCompactPrimaryDetail => CompactPrimaryDetail.Length > 0;

    public bool CompactPrimaryShowsIcon => _compactModule is not null;

    public string CompactPrimaryAccessibleName => _compactModule is { } m
        ? string.Join(" ", new[] { ModuleCatalog.DisplayName(m), CompactPrimaryValue, CompactPrimaryDetail }.Where(t => t.Length > 0))
        : string.Empty;

    /// <summary>Applies the saved favorites (already filtered to enabled modules, in tab order).</summary>
    public void SetFavorites(IReadOnlyList<ModuleId> favorites)
    {
        _favorites = favorites;
        OnPropertyChanged(nameof(Favorites));
        RefreshCompactModule();
    }

    public void SetLastActive(ModuleId? module)
    {
        _lastActive = module;
        RefreshCompactModule();
    }

    /// <summary>The enabled modules a tab shows, which its star stands for (Usage: Claude, Codex and Hermes).</summary>
    public IReadOnlyList<ModuleId> FavoriteModulesOf(IslandTab tab) => tab == IslandTab.Usage
        ? new[] { ModuleId.Claude, ModuleId.Codex, ModuleId.Hermes }.Where(IsModuleEnabled).ToList()
        : [TabModule(tab)];

    public bool IsTabFavorite(IslandTab tab) => TabFavorite.IsFavorite(FavoriteModulesOf(tab), _favorites);

    /// <summary>Stars or unstars a tab (see <see cref="TabFavorite"/>).</summary>
    public void ToggleTabFavorite(IslandTab tab)
    {
        var (modules, favorite) = TabFavorite.Toggle(FavoriteModulesOf(tab), _favorites);
        if (modules.Count > 0)
        {
            FavoriteChanged?.Invoke(modules, favorite);
        }
    }

    /// <summary>Next featured favorite (called by the host every rotation interval while compact).</summary>
    public void AdvanceCompactRotation()
    {
        _turn++;
        RefreshCompactModule();
    }

    private bool HasCompactContent(ModuleId module) => module switch
    {
        ModuleId.Claude => Usage.Claude.IsWorthShowingCompact,
        ModuleId.Codex => Usage.Codex.IsWorthShowingCompact,
        ModuleId.Hermes => Usage.Hermes.HasWallet,
        ModuleId.Git => Git.IsReady && Git.Active is not null,
        _ => false,
    };

    private bool IsModuleEnabled(ModuleId module) => module switch
    {
        ModuleId.Claude => Usage.Claude.IsEnabled,
        ModuleId.Codex => Usage.Codex.IsEnabled,
        ModuleId.Hermes => Usage.Hermes.IsEnabled,
        ModuleId.Git => Git.IsEnabled,
        _ => false,
    };

    private static string ProviderValue(AiProviderViewModel provider) =>
        provider.CompactPrimaryValue is { Length: > 0 } value ? value : provider.StatusText;

    private void RefreshCompactModule()
    {
        var favorites = _favorites.Where(IsModuleEnabled).ToList();
        var last = _lastActive is { } l && IsModuleEnabled(l) ? l : (ModuleId?)null;
        var next = CompactSelector.Select(favorites, last, HasCompactContent, _turn);
        if (next != _compactModule)
        {
            _compactModule = next;
            OnCompactChanged();
            CompactModuleChanged?.Invoke();
        }
        else
        {
            OnPrimaryChanged(next);
        }
    }

    /// <summary>A module's content changed; refresh the capsule only if it is the featured one.</summary>
    private void OnPrimaryChanged(ModuleId? module)
    {
        if (module is not null && module == _compactModule)
        {
            OnPropertyChanged(nameof(CompactPrimaryLabel));
            OnPropertyChanged(nameof(HasCompactPrimaryLabel));
            OnPropertyChanged(nameof(CompactPrimaryValue));
            OnPropertyChanged(nameof(HasCompactPrimaryValue));
            OnPropertyChanged(nameof(CompactPrimaryDetail));
            OnPropertyChanged(nameof(HasCompactPrimaryDetail));
            OnPropertyChanged(nameof(CompactPrimaryShowsIcon));
            OnPropertyChanged(nameof(CompactPrimaryAccessibleName));
        }
    }

    public bool CompactIsEmpty => !IsFeaturing && !CompactShowClaude && !CompactShowCodex;

    public string CompactAccessibleName
    {
        get
        {
            var parts = new List<string> { "Quotalis" };
            if (CompactShowClaude) parts.Add(Usage.Claude.CompactAccessibleText);
            if (CompactShowCodex) parts.Add(Usage.Codex.CompactAccessibleText);
            return string.Join(", ", parts);
        }
    }

    // Tabs ------------------------------------------------------------------------------------

    public static string TabName(IslandTab tab) => tab.ToString();

    /// <summary>The module whose icon represents a tab.</summary>
    public static ModuleId TabModule(IslandTab tab) => tab == IslandTab.Git ? ModuleId.Git : ModuleId.Claude;

    public bool IsTabAvailable(IslandTab tab) => tab switch
    {
        IslandTab.Usage => Usage.HasAnyProvider,
        IslandTab.Git => Git.IsEnabled,
        _ => false,
    };

    /// <summary>Enabled tabs in the user's module order (Usage sits where Claude, Codex or Hermes comes first).</summary>
    public IReadOnlyList<IslandTab> AvailableTabs
    {
        get
        {
            var tabs = new List<IslandTab>();
            foreach (var module in _order)
            {
                var tab = TabFor(module);
                if (!tabs.Contains(tab) && IsTabAvailable(tab))
                {
                    tabs.Add(tab);
                }
            }

            return tabs;
        }
    }

    public bool HasTabs => AvailableTabs.Count > 0;

    public bool HasNoTabs => !HasTabs;

    public IslandTab SelectedTab
    {
        get => _selectedTab;
        set => SetProperty(ref _selectedTab, value);
    }

    /// <summary>The tab that shows a module (Claude, Codex and Hermes share Usage).</summary>
    public static IslandTab TabFor(ModuleId module) => module == ModuleId.Git ? IslandTab.Git : IslandTab.Usage;

    /// <summary>The module a tab stands for (Usage: the active provider, else Claude, else Codex, else Hermes).</summary>
    public ModuleId ModuleForTab(IslandTab tab) => tab switch
    {
        IslandTab.Usage when Usage.Codex.IsEnabled && (!Usage.Claude.IsEnabled || (Usage.Codex.IsActive && !Usage.Claude.IsActive)) => ModuleId.Codex,
        IslandTab.Usage when !Usage.Claude.IsEnabled && Usage.Hermes.IsEnabled => ModuleId.Hermes,
        IslandTab.Usage => ModuleId.Claude,
        _ => TabModule(tab),
    };

    /// <summary>
    /// The user picked a tab (click or arrow keys). Only this changes the module opened last; the
    /// tab the island opens on by itself never does.
    /// </summary>
    public void NoteTabOpened(IslandTab tab)
    {
        // Usage holds every provider: keep the one already featured if it is one of them.
        var module = tab == IslandTab.Usage && _compactModule is ModuleId.Claude or ModuleId.Codex or ModuleId.Hermes
            ? _compactModule.Value
            : ModuleForTab(tab);
        ModuleOpened?.Invoke(module);
    }

    public void SetModuleOrder(IReadOnlyList<ModuleId> order)
    {
        if (!order.SequenceEqual(_order))
        {
            _order = order;
            EnsureValidTab();
        }
    }

    /// <summary>Picks the tab that matches what the user just saw, before expanding.</summary>
    public void SelectTabForExpand()
    {
        ModuleId? announced = _lastEventKind == ActivityKind.Usage ? ModuleId.Claude : null;
        var requested = _pendingTab is { } pending ? TabModule(pending) : (ModuleId?)null;
        _lastEventKind = null;
        _pendingTab = null;

        IslandTab? preferred = IslandContent.ChooseTabOnOpen(requested, announced, _compactModule) is { } module ? TabFor(module) : null;
        var tabs = AvailableTabs;
        if (preferred is { } p && tabs.Contains(p))
        {
            SelectedTab = p;
        }
        else if (!tabs.Contains(_selectedTab) && tabs.Count > 0)
        {
            SelectedTab = tabs[0];
        }
    }

    /// <summary>Opens straight on a tab.</summary>
    public void PrepareExpandTo(IslandTab tab)
    {
        _lastEventKind = null;
        _pendingTab = tab;
        if (AvailableTabs.Contains(tab))
        {
            SelectedTab = tab;
        }
    }

    public void EnsureValidTab()
    {
        var tabs = AvailableTabs;
        if (!tabs.Contains(_selectedTab) && tabs.Count > 0)
        {
            SelectedTab = tabs[0];
        }

        OnPropertyChanged(nameof(HasTabs));
        OnPropertyChanged(nameof(HasNoTabs));
        OnPropertyChanged(nameof(AvailableTabs));
        TabsChanged?.Invoke();
    }

    // Activity (medium state) -------------------------------------------------------------------

    /// <summary>Fills the activity with the most relevant live information for a hover peek.</summary>
    public void PreparePeek()
    {
        _lastEventKind = null;
        switch (IslandContent.ChoosePeek(_compactModule, new IslandFacts(Usage.HasAnyProvider)))
        {
            case PeekContent.Featured when _compactModule is { } featured:
                SetFeaturedActivity(featured);
                break;
            case PeekContent.Usage:
                Activity.Set(ActivityKind.Usage, Usage.PeekTitle, Usage.PeekSubtitle, mark: Usage.PeekMark);
                break;
            default:
                Activity.Set(ActivityKind.Info, "Quotalis", "Click to open", glyph: "");
                break;
        }
    }

    public void OnAiActivity(AiActivity activity)
    {
        var provider = activity.Provider == Core.Models.AiProviderKind.Claude ? Usage.Claude : Usage.Codex;
        if (!provider.IsEnabled)
        {
            return;
        }

        var trailing = activity.Kind == AiActivityKind.SessionStarted && provider.IsReady ? provider.CompactValue : string.Empty;
        Activity.Set(ActivityKind.Usage, activity.Title, activity.Detail ?? string.Empty, trailing, mark: provider.ShortName);
        _lastEventKind = ActivityKind.Usage;
        ActivityRequested?.Invoke(null);
    }

    /// <summary>The hover peek of the featured module (no API equivalent here; that lives in the expanded view).</summary>
    private void SetFeaturedActivity(ModuleId module)
    {
        switch (module)
        {
            case ModuleId.Claude or ModuleId.Codex:
                var provider = module == ModuleId.Claude ? Usage.Claude : Usage.Codex;
                Activity.Set(ActivityKind.Usage, provider.DisplayName, provider.PeekLine, provider.PlanPeekTrailing, mark: provider.ShortName);
                break;
            case ModuleId.Hermes:
                Activity.Set(ActivityKind.Usage, "Hermes", Usage.Hermes.HasWallet ? Usage.Hermes.CompactValue + " in the Nous wallet" : Usage.Hermes.WalletStatusText,
                    Usage.Hermes.HasModel ? Usage.Hermes.ModelText : string.Empty, glyph: "");
                break;
            case ModuleId.Git:
                Activity.Set(ActivityKind.Info, Git.Active?.Name ?? "Git", Git.Active is null ? Git.StateTitle : $"{Git.BranchText} · {Git.CompactDetail}", module: ModuleId.Git);
                break;
        }
    }

    /// <summary>
    /// A provider appears in the capsule when it has something to say today. When nothing else is shown, a detected
    /// Claude Code still anchors the capsule.
    /// </summary>
    private static bool ShowProvider(AiProviderViewModel provider, AiProviderViewModel other) =>
        provider.IsWorthShowingCompact
        || (!(other.IsEnabled && other.IsWorthShowingCompact) && provider.Kind == Core.Models.AiProviderKind.Claude && provider.IsReady);

    private void OnCompactChanged()
    {
        // A module's state can change which favorite has something to say.
        if (!_refreshingCompact)
        {
            _refreshingCompact = true;
            try
            {
                RefreshCompactModule();
            }
            finally
            {
                _refreshingCompact = false;
            }
        }

        OnPropertyChanged(nameof(IsFeaturing));
        OnPrimaryChanged(_compactModule);
        OnPropertyChanged(nameof(CompactShowClaude));
        OnPropertyChanged(nameof(CompactShowCodex));
        OnPropertyChanged(nameof(CompactIsEmpty));
        OnPropertyChanged(nameof(CompactAccessibleName));
    }
}

/// <summary>The view models of the modules beside AI usage.</summary>
public sealed record ModuleViewModels(GitViewModel Git)
{
    public static ModuleViewModels Create() => new(new GitViewModel());

    public IEnumerable<ModuleViewModel> All => [Git];
}

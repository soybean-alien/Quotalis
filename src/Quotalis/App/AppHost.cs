using System.Globalization;
using Quotalis.Core.Demo;
using Quotalis.Core.Diagnostics;
using Quotalis.Core.Island;
using Quotalis.Core.Models;
using Quotalis.Core.Modules;
using Quotalis.Core.Placement;
using Quotalis.Core.Plan;
using Quotalis.Core.Pricing;
using Quotalis.Core.Providers;
using Quotalis.Core.Providers.Claude;
using Quotalis.Core.Providers.Codex;
using Quotalis.Core.Settings;
using Quotalis.Core.Storage;
using Quotalis.Core.Usage;
using Quotalis.Platform.Monitors;
using Quotalis.Platform.Startup;
using Quotalis.Platform.Tray;
using Quotalis.Platform.Windowing;
using Quotalis.UI.Island;
using Quotalis.UI.Settings;
using Quotalis.ViewModels;
using Microsoft.UI.Dispatching;

namespace Quotalis;

/// <summary>
/// Composition root. Wires providers, storage, view models, the island window and the tray.
/// Provider events arrive on background threads and are marshalled to the UI thread here;
/// view models and windows are only touched on the UI thread.
/// </summary>
internal sealed class AppHost : IDisposable
{
    private readonly DispatcherQueue _dispatcher;
    private readonly AppOptions _options;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ProviderHost _providers = new();
    private SettingsStore _settings = null!;
    private UsageDatabase _database = null!;
    private UsageHistoryService _history = null!;
    private IslandViewModel _viewModel = null!;
    private IslandStateMachine _state = null!;
    private IslandWindow _window = null!;
    private HostWindow _host = null!;
    private TrayIcon _tray = null!;
    private FullscreenWatcher? _fullscreen;
    private ForegroundWatcher? _foreground;
    private Platform.Privacy.PrivacyWatcher? _privacy;
    private GlobalHotKey? _hotkey;
    private SettingsWindow? _settingsWindow;
    private ModuleHost _modules = null!;
    private UiStateStore _uiState = null!;
    private DispatcherQueueTimer? _rotation;
    private PlanUsageService? _plan;
    private ClaudeWebReader? _claudeWeb;
    private ChatGptUsageReader? _chatGptUsage;
    private HermesService? _hermes;
    private Core.Hermes.HermesUsage? _hermesUsageShown;
    private bool _hermesShown;
    private DispatcherQueueTimer? _planReset;
    private DispatcherQueueTimer? _historyDebounce;
    private DispatcherQueueTimer? _displaySettle;
    private DispatcherQueueTimer? _midnight;
    private Core.Island.AutoHideCoordinator? _autoHide;
    private DispatcherQueueTimer? _autoHideCheck;
    private (IslandAnchor Anchor, double OffsetY) _autoHidePlacement = (IslandAnchor.TopCenter, 0);
    private bool _hiddenByUser;
    private bool _hiddenForFullscreen;

    public AppHost(DispatcherQueue dispatcher, AppOptions options)
    {
        _dispatcher = dispatcher;
        _options = options;
    }

    public event Action? QuitRequested;

    public void Start()
    {
        // Demo and production may run concurrently; each writer needs its own file.
        Log.Initialize(_options.IsDemo ? Path.Combine(AppPaths.Logs, "demo") : AppPaths.Logs);
        if (Environment.GetEnvironmentVariable("DI_LOG_DEBUG") == "1")
        {
            Log.MinimumLevel = LogLevel.Debug;
        }
        Log.Info("startup", "Quotalis starting", new
        {
            version = typeof(AppHost).Assembly.GetName().Version?.ToString(),
            demo = _options.IsDemo,
            autostart = _options.IsAutostart,
            os = Environment.OSVersion.VersionString,
        });

        _settings = new SettingsStore(AppPaths.Settings(_options.IsDemo));
        if (!_options.IsDemo)
        {
            // Hook-written files now live in %USERPROFILE%\.quotalis; bring over copies from the old folder and from the
            // private AppData of packaged apps (Claude desktop) that ran our hooks.
            var adopted = Core.Storage.SharedFileAdoption.Adopt(AppPaths.Shared, AppPaths.LegacySharedFolders(), AppPaths.SharedFiles);
            if (adopted.Count > 0)
            {
                Log.Info("storage", "Shared files adopted", new { files = string.Join(", ", adopted), into = AppPaths.Shared });
            }
        }

        var pricing = ModelPricingService.Load(AppPaths.PricingOverrides);
        _database = OpenDatabase();
        _history = new UsageHistoryService(_database, pricing);

        var settings = _settings.Current;
        var usage = new UsageViewModel();
        var modules = ModuleViewModels.Create();
        _viewModel = new IslandViewModel(usage, modules, _options.IsDemo);
        _modules = new ModuleHost(_dispatcher, modules, _viewModel, _settings, _options.IsDemo) { OpenSettings = OpenSettings };
        ApplyModuleFlags(settings);
        _modules.Apply(settings);
        _uiState = new UiStateStore(_options.IsDemo ? null : AppPaths.UiState);
        if (_options.IsDemo)
        {
            _viewModel.Usage.Claude.SetPlan(DemoData.ClaudePlan(DateTimeOffset.UtcNow), PlanConnection.Connected);
        }
        else
        {
            // The claude.ai reader, the status line bridge (terminal sessions) and calibration all write this one file.
            _plan = new PlanUsageService(new PlanUsageStore(AppPaths.ClaudePlan));
            _plan.Changed += usage => _dispatcher.TryEnqueue(() => OnPlanUsage(usage));
            _plan.Configure(settings.ClaudeEnabled);
        }
        _viewModel.SetLastActive(_uiState.LastActiveModule);
        ApplyFavorites(settings);

        _state = new IslandStateMachine(TimeProvider.System, action => _dispatcher.TryEnqueue(() => action()));
        _autoHide = new Core.Island.AutoHideCoordinator(
            TimeProvider.System,
            action => _dispatcher.TryEnqueue(() => action()),
            () => _foreground?.Probe(),
            DecideRest,
            SetRest);
        _window = new IslandWindow(_viewModel, _state, settings.AlwaysOnTop) { SnapshotMode = IsSnapshot };
        _window.SettingsRequested += OpenSettings;
        _window.VisiblePanelChanged += tab =>
        {
            _modules.OnPanelVisible(tab);
            // Re-read Claude's plan file when the Usage panel opens, so a missed file event can't leave it blank.
            if (tab == ViewModels.IslandTab.Usage)
            {
                _plan?.Refresh();
            }
        };
        _window.DragCompleted += OnDragCompleted;
        _window.PlacementInvalidated += () => _dispatcher.TryEnqueue(PlaceIsland);
        PlaceIsland();

        _host = new HostWindow();
        _host.DisplaySettingsChanged += () => _dispatcher.TryEnqueue(RequestDisplayPlacement);
        if (!_options.IsDemo)
        {
            _claudeWeb = new ClaudeWebReader(_dispatcher, _host.Handle, new PlanUsageStore(AppPaths.ClaudePlan));
            _claudeWeb.Changed += OnClaudeWebChanged;
            _claudeWeb.Configure(settings.ClaudeEnabled && settings.ClaudeWebSignedIn);
            _chatGptUsage = new ChatGptUsageReader(_dispatcher);
            _chatGptUsage.Changed += OnChatGptUsageChanged;
            _chatGptUsage.Configure(settings.CodexEnabled);
            _hermes = new HermesService(_dispatcher, new NousTokenStore(AppPaths.NousSignIn), AppPaths.HermesWallet, AppPaths.HermesUsageCache);
            _hermes.Changed += OnHermesChanged;
            _hermes.Configure(settings);
            _host.SessionActiveChanged += active => _hermes?.SetSessionActive(active);
        }
        else
        {
            // Demo: a made-up wallet and Hermes history, so screenshots show the column without any account.
            var now = DateTimeOffset.UtcNow;
            _viewModel.Usage.Hermes.SetWallet(HermesWalletState.Ready, DemoData.HermesWallet(now), new Core.Hermes.WalletGauge(25m, 18.42m), fast: true, paused: false, 5);
            _viewModel.Usage.Hermes.SetUsage(DemoData.HermesUsage(now, TimeZoneInfo.Local), null);
        }

        _tray = new TrayIcon(_host, AppPaths.Icon)
        {
            IsIslandVisible = () => _window.IsIslandVisible,
        };
        _tray.Activated += () => ShowIsland(expand: true);
        _tray.CommandInvoked += OnTrayCommand;
        if (!IsSnapshot)
        {
            _tray.Add();
            _fullscreen = new FullscreenWatcher(_host, () => _window.MonitorHandle, OnFullscreenChanged);
            _foreground = new ForegroundWatcher(() => _window.MonitorHandle, () => _settings.Current.SmartHideProcesses, info => _autoHide?.ForegroundChanged(info));
            _foreground.Switched += _window.KeepOnTop;
            _privacy = new Platform.Privacy.PrivacyWatcher(state => _dispatcher.TryEnqueue(() => OnPrivacyChanged(state)));
            _hotkey = new GlobalHotKey(_host);
            _hotkey.Pressed += OnShortcut;
            _hotkey.Apply(settings.GlobalShortcutEnabled, ShortcutGesture.FromText(settings.GlobalShortcut));
        }

        WireEvents();

        // The installer may have registered autostart; a fresh install adopts that choice.
        if (_settings.IsFirstRun && !_options.IsDemo && AutostartService.IsEnabled())
        {
            _settings.Update(s => s.StartWithWindows = true);
            settings = _settings.Current;
        }

        SyncAutostart(settings);

        var startHidden = _options.IsAutostart && settings.LaunchHidden;
        if (startHidden)
        {
            _hiddenByUser = true;
            _state.Hide();
        }
        else
        {
            _window.ShowIsland();
        }

        ScheduleMidnight();
        _ = StartBackgroundAsync();
        // "Keep the island open" opens it once the window is up.
        _dispatcher.TryEnqueue(DispatcherQueuePriority.Low, () => ApplyKeepOpen(_settings.Current));
        if (!_options.IsDemo && !IsSnapshot)
        {
            AppUpdater.Shared = new AppUpdater();
            _ = RunUpdateChecksAsync(_shutdown.Token);
        }
        if (IsSnapshot)
        {
            _ = RunSnapshotsAsync(_options.SnapshotDirectory!);
            return;
        }

        if (_options.SettingsSection is { } section)
        {
            OpenSettings(section);
        }
    }

    private bool IsSnapshot => _options.SnapshotDirectory is not null;

    /// <summary>
    /// Development: renders each island state to PNG files and quits. Nothing appears on screen
    /// and no input is simulated; states are driven through the state machine and view model.
    /// </summary>
    private async Task RunSnapshotsAsync(string directory)
    {
        async Task Shot(string name)
        {
            await Task.Delay(1100);
            await _window.SaveSnapshotAsync(Path.Combine(directory, name + ".png"));
        }

        try
        {
            await Task.Delay(4500);
            _state.Dismiss();
            await Shot("01-compact");

            _viewModel.PreparePeek();
            _state.ShowEvent(TimeSpan.FromMinutes(5));
            await Shot("02-peek");

            _viewModel.OnAiActivity(new AiActivity(AiProviderKind.Claude, AiActivityKind.SessionStarted, "Claude Code session started", "quotalis"));
            await Shot("04-activity-claude");

            _state.Dismiss();
            await Task.Delay(600);
            _state.Activate();
            foreach (var tab in _viewModel.AvailableTabs)
            {
                _viewModel.SelectedTab = tab;
                await Shot("10-expanded-" + tab.ToString().ToLowerInvariant());
            }

            // The heatmap tooltip on a busy day (as on hover), then cleared again.
            _viewModel.SelectedTab = IslandTab.Usage;
            var busiest = _viewModel.Usage.Days.Select((d, i) => (d.FreshTokens, i)).Max().i;
            _viewModel.Usage.Inspect(busiest);
            await Shot("11-usage-tooltip");
            _viewModel.Usage.Inspect(-1);

            // The usage strip ("Always display usage"), alone and with a day inspected.
            _window.SetUsageStripMode(UI.Expanded.UsageStripMode.Always);
            await Shot("12-usage-strip");
            _viewModel.Usage.Inspect(busiest);
            await Shot("12-usage-strip-day");
            _viewModel.Usage.Inspect(-1);
            _window.SetUsageStripMode(UI.Expanded.UsageStripMode.Off);

            // A walk over the graph for the demo GIF (tools/make-heatmap-gif.py): hover days, click to pin one, keep
            // hovering while it stays pinned, leave the graph, then pin another day.
            var usage = _viewModel.Usage;
            var active = usage.Days.Select((d, i) => (d, i)).Where(x => x.d.HasUsage).Select(x => x.i).ToList();
            if (active.Count >= 6)
            {
                int Pick(double at) => active[(int)Math.Round(at * (active.Count - 1))];
                var steps = new (string Action, int Day)[]
                {
                    ("hover", Pick(0.15)), ("hover", Pick(0.35)), ("hover", busiest), ("pin", busiest),
                    ("hover", Pick(0.6)), ("hover", Pick(0.85)), ("leave", -1), ("hover", active[^1]), ("pin", active[^1]), ("leave", -1),
                };
                for (var n = 0; n < steps.Length; n++)
                {
                    var (action, day) = steps[n];
                    switch (action)
                    {
                        case "hover": usage.Inspect(day); break;
                        case "pin": usage.Pin(day); break;
                        default: usage.EndHover(); break;
                    }

                    await Shot($"80-walk-{n:00}-{action}");
                }

                usage.ClearPin();
            }

            _state.Dismiss();
            await ExtraSnapshotsAsync(Shot);
        }
        catch (Exception ex)
        {
            Log.Error("snapshot", "Snapshot run failed", ex);
        }
        finally
        {
            QuitRequested?.Invoke();
        }
    }

    /// <summary>Browser notch at rest, its peek, and module states.</summary>
    private async Task ExtraSnapshotsAsync(Func<string, Task> shot)
    {
        _state.SetRest(Core.Island.RestMode.Retracted);
        await shot("20-notch");
        _viewModel.PreparePeek();
        _state.Activate();
        await Task.Delay(1500); // the morph from a 48 px notch travels furthest
        await shot("21-notch-opened");
        _state.Dismiss();
        _state.SetRest(Core.Island.RestMode.Compact);
        await Task.Delay(600);

        // Camera and microphone marks in every state (set directly: the snapshot never watches devices).
        _viewModel.Privacy.State = new Core.Privacy.PrivacyState(Microphone: true, Camera: false);
        await shot("60-privacy-compact-mic");
        _state.SetRest(Core.Island.RestMode.Retracted);
        await Task.Delay(600);
        await shot("61-privacy-notch-mic");
        _viewModel.Privacy.State = new Core.Privacy.PrivacyState(Microphone: true, Camera: true);
        await Task.Delay(600);
        await shot("62-privacy-notch-both");
        _state.SetRest(Core.Island.RestMode.Compact);
        await Task.Delay(600);
        _viewModel.OnAiActivity(new AiActivity(AiProviderKind.Claude, AiActivityKind.SessionStarted, "Claude Code session started", "quotalis"));
        await shot("63-privacy-activity");
        _state.Activate();
        await Task.Delay(600);
        await shot("64-privacy-expanded");
        _state.Dismiss();
        _viewModel.Privacy.State = Core.Privacy.PrivacyState.None;
        await Task.Delay(600);

        // A module state that isn't ready, as a user with an unreadable repository sees it.
        var now = DateTimeOffset.UtcNow;
        _viewModel.Git.Update(new Core.Modules.ModuleStatus(Core.Modules.ModuleState.Error, "Git could not read these repositories."), [], now);
        _state.Activate();
        _viewModel.SelectedTab = IslandTab.Git;
        await shot("40-state-git-error");

        _state.Dismiss();
        await Task.Delay(600);

        _modules.Apply(_settings.Current); // back to the demo content after the empty and error states

        // The compact island featuring each module (as a single favorite).
        foreach (var module in new[] { ModuleId.Claude, ModuleId.Codex, ModuleId.Hermes, ModuleId.Git })
        {
            _viewModel.SetFavorites([module]);
            await shot("50-compact-" + module.ToString().ToLowerInvariant());
        }

        _viewModel.SetFavorites([ModuleId.Claude, ModuleId.Hermes, ModuleId.Git]);
        await shot("51-compact-favorites-turn-0");
        _viewModel.AdvanceCompactRotation();
        await shot("51-compact-favorites-turn-1");
        _viewModel.SetFavorites([]);
        _viewModel.SetLastActive(null);
        await Task.Delay(400);

        await new SettingsWindow(_settings, _options.IsDemo, () => false).SaveSnapshotsAsync(_options.SnapshotDirectory!);
    }

    /// <summary>
    /// Quotalis was started again while running (desktop or Start menu shortcut, pinned taskbar icon, a Stream Deck
    /// launcher). Open the island in front, as the tray icon does, so the start always shows a visible response;
    /// showing an island that is already resting did nothing visible.
    /// </summary>
    public void OnSecondInstance()
    {
        Log.Info("startup", "Second start: opening the island");
        ShowIsland(expand: true);
    }

    public void Dispose()
    {
        _window?.PrepareForExit();
        _shutdown.Cancel();
        _providers.Dispose();
        _modules?.Dispose();
        _plan?.Dispose();
        _claudeWeb?.Dispose();
        _chatGptUsage?.Dispose();
        _hermes?.Dispose();
        _state?.Dispose();
        _fullscreen?.Dispose();
        _foreground?.Dispose();
        _autoHideCheck?.Stop();
        _autoHide?.Dispose();
        _privacy?.Dispose();
        _hotkey?.Dispose();
        _tray?.Dispose();
        _host?.Dispose();
        _database?.Dispose();
        Log.Info("startup", "Quotalis stopped");
        Log.Flush();
    }

    private UsageDatabase OpenDatabase()
    {
        if (_options.IsDemo)
        {
            return UsageDatabase.OpenInMemory();
        }

        try
        {
            var db = UsageDatabase.Open(AppPaths.Database);
            db.PruneEvents(DateOnly.FromDateTime(DateTime.Today).AddDays(-400));
            return db;
        }
        catch (Exception ex)
        {
            // The island still works; history just is not kept this session.
            Log.Error("database", "Usage database unavailable; using a temporary in-memory store", ex);
            return UsageDatabase.OpenInMemory();
        }
    }

    private async Task StartBackgroundAsync()
    {
        try
        {
            if (_options.IsDemo)
            {
                await Task.Run(() => DemoData.SeedHistory(_history, TimeProvider.System));
                await _providers.AddAsync(new DemoAiProvider(DemoData.ClaudeSnapshot(_history)), _shutdown.Token);
                await _providers.AddAsync(new DemoAiProvider(DemoData.CodexSnapshot(_history)), _shutdown.Token);
            }
            else
            {
                // The first log scan allocates a lot. A full garbage collection while WinUI is still building and
                // releasing the island's first visual tree can deadlock the UI thread (seen right after a reboot),
                // so the scan waits until the island has rendered and the dispatcher is idle.
                await WhenDispatcherIdleAsync();
                await Task.Delay(TimeSpan.FromSeconds(2), _shutdown.Token);
                await SyncProvidersAsync(_settings.Current);

                // Days stored before a model had a list price (or before a price changed) get today's prices.
                var repriced = await Task.Run(() => _history.Reprice());
                if (repriced > 0)
                {
                    Log.Info("pricing", "History repriced", new { rows = repriced });
                }
            }

            RequestHistoryReload();
            TrimMemory();
        }
        catch (Exception ex)
        {
            Log.Error("startup", "Background start failed", ex);
        }
    }

    private DateTimeOffset _peakStatsAt = DateTimeOffset.MinValue;
    private PeakWindow? _peakStatsWindow;

    /// <summary>
    /// Usage by hour and the peak share for the last <see cref="UsageViewModel.PeakStatsDays"/> days, and the 5-hour limit
    /// drain from exact readings: Quotalis's calibration log, plus Tokenz's older log when it is still on this PC (read only).
    /// </summary>
    private (HourlyProfile Profile, DrainStats Drain) ComputePeakStats(PeakWindow window)
    {
        var now = DateTimeOffset.UtcNow;
        var events = _database.GetEventsBetween(now.AddDays(-UsageViewModel.PeakStatsDays), now.AddMinutes(1));
        var profile = HourlyProfile.From(events, _history.Pricing, window, TimeZoneInfo.Local);
        if (_options.IsDemo)
        {
            return (profile, new DrainStats(null, null, 0, 0));
        }

        var lines = new List<string>();
        var tokenzLog = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Tokenz", "state", "calibration_log.jsonl");
        foreach (var log in new[] { AppPaths.CalibrationLog, tokenzLog })
        {
            try
            {
                if (File.Exists(log))
                {
                    using var stream = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(stream);
                    while (reader.ReadLine() is { } line)
                    {
                        lines.Add(line);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Debug("plan", "Calibration log unreadable", new { error = ex.GetType().Name });
            }
        }

        var claude = events.Where(e => e.Provider == AiProviderKind.Claude).ToList();
        return (profile, PlanDrain.Analyze(PlanDrain.ParseReadings(lines), claude, _history.Pricing, window));
    }

    /// <summary>
    /// Looks for a new release once a day when "Check for updates daily" is on, starting a couple of minutes after
    /// launch so it never competes with the first log scan. Results only show in Settings, About.
    /// </summary>
    private async Task RunUpdateChecksAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(2), token);
            while (!token.IsCancellationRequested)
            {
                if (_settings.Current.CheckForUpdates && AppUpdater.Shared is { } updater)
                {
                    await updater.CheckQuietlyAsync();
                }

                await Task.Delay(AppUpdater.CheckInterval, token);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Completes once the UI thread has run everything queued ahead of low-priority work.</summary>
    private Task WhenDispatcherIdleAsync()
    {
        var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_dispatcher.TryEnqueue(DispatcherQueuePriority.Low, () => idle.TrySetResult()))
        {
            idle.TrySetResult();
        }

        return idle.Task;
    }

    private void WireEvents()
    {
        _providers.SnapshotChanged += snapshot => _dispatcher.TryEnqueue(() =>
        {
            var vm = snapshot.Provider == AiProviderKind.Claude ? _viewModel.Usage.Claude : _viewModel.Usage.Codex;
            vm.Update(snapshot);
            if (snapshot.Provider == AiProviderKind.Claude)
            {
                _claudeWeb?.OnActivity(); // a reply landed: read claude.ai's usage in a few seconds
            }
            else if (snapshot.IsActive)
            {
                _chatGptUsage?.OnActivity();
            }

            _modules.OnAiSnapshot(snapshot);
        });
        _providers.Activity += activity => _dispatcher.TryEnqueue(() => _viewModel.OnAiActivity(activity));
        _history.Changed += _ => _dispatcher.TryEnqueue(RequestHistoryReload);

        _viewModel.ActivityRequested += duration =>
        {
            var shown = _state.ShowEvent(duration);
            Log.Debug("island", "Activity requested", new { kind = _viewModel.Activity.Kind.ToString(), shown });
        };
        _settings.Changed += settings => _dispatcher.TryEnqueue(() => ApplySettings(settings));

        // Favorites (stars) live in settings; the module opened last in the small UI state file.
        _viewModel.FavoriteChanged += (modules, favorite) => _settings.Update(s =>
        {
            foreach (var module in modules)
            {
                s.SetFavorite(module, favorite);
            }
        });
        _viewModel.Usage.Claude.ConnectPlanRequested += () => OpenSettings("Modules");
        _viewModel.Usage.Hermes.SignInRequested += () => OpenSettings("Modules");
        _viewModel.Usage.Hermes.TopUpRequested += () => OpenInBrowser(Core.Hermes.NousPortalClient.TopUpUrl);
        _state.ModeChanged += (_, mode) =>
        {
            if (mode == Core.Island.IslandMode.Expanded)
            {
                // Reset countdowns are minute-precise; refresh them whenever the island opens.
                _viewModel.Usage.Claude.RefreshPlanClock();
                _viewModel.Usage.Codex.RefreshPlanClock();
            }
            else
            {
                // A day pinned in the usage graph stays until the island rolls back up.
                _viewModel.Usage.ClearPin();
            }
        };
        _viewModel.ModuleOpened += module =>
        {
            _uiState.SetLastActive(module);
            _viewModel.SetLastActive(module);
        };
        _state.ModeChanged += (_, _) => UpdateRotation();
        _viewModel.CompactModuleChanged += UpdateRotation;
    }

    // Settings --------------------------------------------------------------------------------------

    private void ApplySettings(AppSettings settings)
    {
        ApplyModuleFlags(settings);
        _modules.Apply(settings);
        ApplyFavorites(settings);
        _plan?.Configure(settings.ClaudeEnabled);
        _claudeWeb?.Configure(settings.ClaudeEnabled && settings.ClaudeWebSignedIn);
        _chatGptUsage?.Configure(settings.CodexEnabled);
        _hermes?.Configure(settings);
        OnHermesChanged();
        _viewModel.EnsureValidTab();
        _window.SetAlwaysOnTop(settings.AlwaysOnTop && !_settingsActive);
        ApplyKeepOpen(settings);
        PlaceIsland();
        SyncAutostart(settings);
        _settingsWindow?.ApplyTheme(settings.Theme);
        _hotkey?.Apply(settings.GlobalShortcutEnabled, ShortcutGesture.FromText(settings.GlobalShortcut));
        _foreground?.Refresh();
        ApplySmartHide();

        // Changed peak hours re-shade the hour strip and redo the peak comparison.
        if (_peakStatsWindow is { } shown && (shown.StartHour != settings.PeakStartHour || shown.EndHour != settings.PeakEndHour))
        {
            RequestHistoryReload();
        }

        if (!_options.IsDemo)
        {
            _ = SyncProvidersAsync(settings);
        }
    }

    /// <summary>How plan usage reaches Quotalis right now: the claude.ai sign-in, else the status line.</summary>
    private PlanConnection PlanConnectionNow() =>
        ClaudePlanLink.Combine(_claudeWeb?.Status ?? ClaudeWebStatus.SignedOut, ClaudeStatusLineSetup.Inspect(ClaudeStatusLineSetup.SettingsPath()));

    /// <summary>The claude.ai sign-in changed state or read new numbers (those also arrive through the plan file).</summary>
    private void OnClaudeWebChanged()
    {
        _viewModel.Usage.Claude.SetPlan(_plan?.Usage, PlanConnectionNow());
        _settingsWindow?.RefreshPlanConnection();
    }

    /// <summary>The Nous wallet or Hermes's token use changed: update the column, the graph and Settings.</summary>
    private void OnHermesChanged()
    {
        if (_hermes is null)
        {
            return;
        }

        var state = _hermes.WalletLocked ? HermesWalletState.Locked : _hermes.Status switch
        {
            NousStatus.SigningIn => HermesWalletState.SigningIn,
            NousStatus.Expired => HermesWalletState.Expired,
            NousStatus.Unavailable when _hermes.Wallet is null => HermesWalletState.Unavailable,
            NousStatus.UnknownFormat => HermesWalletState.UnknownFormat,
            NousStatus.SignedOut => HermesWalletState.SignedOut,
            _ => HermesWalletState.Ready,
        };
        var hermes = _viewModel.Usage.Hermes;
        hermes.SetWallet(state, _hermes.Wallet, _hermes.Gauge, _hermes.IsFast, _hermes.IsPaused, _settings.Current.HermesLowBalanceUsd);
        // The wallet can change every second; the graph is rebuilt only when Hermes's token use (or the column) did.
        var shown = _settings.Current.HermesEnabled;
        if (!ReferenceEquals(_hermes.Usage, _hermesUsageShown) || shown != _hermesShown)
        {
            _hermesUsageShown = _hermes.Usage;
            _hermesShown = shown;
            hermes.SetUsage(_hermes.Usage, _hermes.UsageProblem);
            _viewModel.Usage.RefreshHermesHistory();
        }
        else
        {
            hermes.SetUsage(_hermes.Usage, _hermes.UsageProblem);
        }
        if (_hermes.Status == NousStatus.SignedIn && !_settings.Current.NousSignedIn)
        {
            _settings.Update(s => s.NousSignedIn = true);
        }
        else if (_hermes.Status is NousStatus.SignedOut or NousStatus.Expired && _settings.Current.NousSignedIn)
        {
            _settings.Update(s => s.NousSignedIn = false);
        }

        _settingsWindow?.RefreshHermes();
    }

    private static void OpenInBrowser(Uri uri)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn("hermes", "Browser could not be opened", new { error = ex.GetType().Name });
        }
    }

    private void OnChatGptUsageChanged()
    {
        var shared = _chatGptUsage?.Latest?.Shared;
        _providers.Providers.OfType<CodexUsageProvider>().FirstOrDefault()?.ApplyAccountLimits(shared?.Current, shared?.Weekly, shared is not null);
        _settingsWindow?.RefreshChatGptUsage();
    }

    /// <summary>
    /// New plan usage from Claude Code: show it, keep the day's measured peak for the history, and
    /// clear the percentage when its window resets.
    /// </summary>
    private void OnPlanUsage(PlanUsage? usage)
    {
        _viewModel.Usage.Claude.SetPlan(usage, PlanConnectionNow());
        if (usage is null)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var day = DateOnly.FromDateTime(usage.MeasuredAt.ToLocalTime().DateTime);
        try
        {
            // Peaks are measured values only; estimates between readings are not recorded.
            if (usage.CurrentAt(now) is { IsEstimated: false } current) _database.RecordPlanPeak(day, AiProviderKind.Claude, "five_hour", current.UsedPercent, usage.MeasuredAt);
            if (usage.WeeklyAt(now) is { IsEstimated: false } weekly) _database.RecordPlanPeak(day, AiProviderKind.Claude, "seven_day", weekly.UsedPercent, usage.MeasuredAt);
        }
        catch (Exception ex)
        {
            Log.Debug("plan", "Plan peak not recorded", new { error = ex.GetType().Name });
        }

        RequestHistoryReload();
        var next = new[] { usage.CurrentAt(now)?.ResetsAt, usage.WeeklyAt(now)?.ResetsAt }.Where(r => r is not null).Min();
        _planReset?.Stop();
        if (next is { } reset && reset - now < TimeSpan.FromDays(8))
        {
            _planReset ??= _dispatcher.CreateTimer();
            _planReset.IsRepeating = false;
            _planReset.Interval = reset - now + TimeSpan.FromSeconds(1);
            _planReset.Tick -= OnPlanReset;
            _planReset.Tick += OnPlanReset;
            _planReset.Start();
        }
    }

    private void OnPlanReset(DispatcherQueueTimer sender, object args) => _viewModel.Usage.Claude.RefreshPlanClock();

    /// <summary>Enabled favorites, in tab order, feature in the compact island.</summary>
    private void ApplyFavorites(AppSettings settings)
    {
        _viewModel.SetFavorites(CompactSelector.EffectiveFavorites(settings.FavoriteModules, settings.Order, settings.IsEnabled));
        UpdateRotation();
    }

    /// <summary>Favorites take turns only while the compact capsule is on screen; otherwise no timer runs.</summary>
    private void UpdateRotation()
    {
        if (_state is null)
        {
            return;
        }

        var compact = _state.Mode == Core.Island.IslandMode.Compact;
        var needed = _viewModel.CompactRotates && compact;
        if (needed && _rotation is null)
        {
            _rotation = _dispatcher.CreateTimer();
            _rotation.Interval = CompactSelector.RotationInterval;
            _rotation.Tick += (_, _) => _viewModel.AdvanceCompactRotation();
        }

        if (needed && _rotation is { IsRunning: false })
        {
            _rotation.Start();
        }
        else if (!needed && _rotation is { IsRunning: true })
        {
            _rotation.Stop();
        }
    }

    private void ApplyModuleFlags(AppSettings settings)
    {
        _viewModel.Usage.Claude.IsEnabled = settings.ClaudeEnabled;
        _viewModel.Usage.Codex.IsEnabled = settings.CodexEnabled;
        _viewModel.Usage.Hermes.IsEnabled = settings.HermesEnabled || IsSnapshot;
    }

    private async Task SyncProvidersAsync(AppSettings settings)
    {
        var running = _providers.Providers.Select(p => p.Kind).ToHashSet();
        foreach (var (kind, enabled) in new[] { (AiProviderKind.Claude, settings.ClaudeEnabled), (AiProviderKind.Codex, settings.CodexEnabled) })
        {
            if (enabled && !running.Contains(kind))
            {
                IAiUsageProvider provider = kind == AiProviderKind.Claude
                    ? new ClaudeUsageProvider(ClaudeUsageProvider.DefaultRoot(), _history, _database)
                    : new CodexUsageProvider(CodexUsageProvider.DefaultRoot(), _history, _database,
                        publisher: new Quotalis.Core.Usage.CodexLimitPublisher(AppPaths.CodexPlan, AppPaths.CodexLimitLog));
                await _providers.AddAsync(provider, _shutdown.Token);
                if (provider is CodexUsageProvider codex)
                {
                    var shared = _chatGptUsage?.Latest?.Shared;
                    codex.ApplyAccountLimits(shared?.Current, shared?.Weekly, shared is not null);
                }
            }
            else if (!enabled && running.Contains(kind))
            {
                _providers.Remove(kind);
            }
        }
    }

    private void SyncAutostart(AppSettings settings)
    {
        if (_options.IsDemo)
        {
            return;
        }

        AutostartService.RemoveLegacyEntry();
        if (AutostartService.IsEnabled() != settings.StartWithWindows)
        {
            AutostartService.SetEnabled(settings.StartWithWindows);
        }
    }

    // Placement --------------------------------------------------------------------------------------

    private void PlaceIsland()
    {
        try
        {
            var s = _settings.Current;
            var placement = IslandPlacement.Resolve(MonitorService.GetMonitors(), s.MonitorId, s.MonitorDevice, s.Anchor, s.OffsetX, s.OffsetY);
            if (placement.IsFallback)
            {
                Log.Info("window", "Saved monitor not found; using the primary monitor", new { device = s.MonitorDevice });
            }

            _window.Place(placement.Monitor, placement.Anchor, placement.OffsetX, placement.OffsetY);
            _foreground?.Refresh();
            ApplySmartHide();
        }
        catch (Exception ex)
        {
            Log.Error("window", "Island placement failed", ex);
        }
    }

    /// <summary>
    /// Connecting, disconnecting or docking sends a burst of display messages, and the monitor list
    /// passes through intermediate layouts on the way. Place the island once things are quiet.
    /// </summary>
    private void RequestDisplayPlacement()
    {
        if (_displaySettle is null)
        {
            _displaySettle = _dispatcher.CreateTimer();
            _displaySettle.IsRepeating = false;
            _displaySettle.Interval = TimeSpan.FromMilliseconds(400);
            _displaySettle.Tick += (_, _) => OnDisplaysSettled();
        }

        _displaySettle.Stop();
        _displaySettle.Start();
    }

    private void OnDisplaysSettled()
    {
        var monitors = MonitorService.GetMonitors();
        Log.Info("window", "Displays changed", new
        {
            monitors = monitors.Select(m => new { device = m.DeviceName, m.Bounds.Width, m.Bounds.Height, m.Scale, primary = m.IsPrimary }).ToArray(),
        });
        PlaceIsland();
        _settingsWindow?.RefreshMonitors();
    }

    private void OnDragCompleted(PixelRect capsule)
    {
        var drop = IslandPlacement.FromDrop(MonitorService.GetMonitors(), capsule);
        Log.Info("window", "Island moved", new { device = drop.Monitor.DeviceName, anchor = drop.Anchor.ToString(), drop.OffsetX, drop.OffsetY });
        _settings.Update(s =>
        {
            s.MonitorDevice = drop.Monitor.IsPrimary ? null : drop.Monitor.DeviceName;
            s.MonitorId = drop.Monitor.IsPrimary ? null : drop.Monitor.Id;
            s.Anchor = drop.Anchor;
            s.OffsetX = drop.OffsetX;
            s.OffsetY = drop.OffsetY;
        });
    }

    // Visibility ---------------------------------------------------------------------------------------

    /// <summary>
    /// "Keep the island open" and "Always display usage": either keeps the island open (turning one on opens it now; the
    /// state machine then ignores outside clicks). Always display usage folds it into the usage strip: at rest only, or
    /// always when both are on.
    /// </summary>
    private void ApplyKeepOpen(AppSettings settings)
    {
        var keep = settings.KeepIslandOpen || settings.AlwaysDisplayUsage;
        var turnedOn = keep && !_state.KeepExpanded;
        _state.KeepExpanded = keep;
        if (!IsSnapshot)
        {
            _window.SetUsageStripMode(!settings.AlwaysDisplayUsage ? UI.Expanded.UsageStripMode.Off
                : settings.KeepIslandOpen ? UI.Expanded.UsageStripMode.Always : UI.Expanded.UsageStripMode.AtRest);
        }

        if (turnedOn && _state.Mode is Core.Island.IslandMode.Compact or Core.Island.IslandMode.Activity && !IsSnapshot)
        {
            ShowIsland(expand: true);
        }
    }

    private void ShowIsland(bool expand)
    {
        _hiddenByUser = false;
        _hiddenForFullscreen = false;
        _state.Show();
        _window.ShowIsland();
        if (expand)
        {
            _window.ExpandFromShortcut();
        }
    }

    private void HideIsland()
    {
        _hiddenByUser = true;
        _state.Hide();
    }

    /// <summary>
    /// Smart Auto-Hide: rest as a notch (or hidden) while a maximized browser is in front of a
    /// top-center island; rest as the compact capsule otherwise. Called when the rule itself changes
    /// (settings, placement, camera or microphone); foreground changes go straight to
    /// <see cref="Core.Island.AutoHideCoordinator"/> (latest event wins, stepping aside at once).
    /// </summary>
    private void ApplySmartHide()
    {
        var s = _settings.Current;
        var placement = IslandPlacement.Resolve(MonitorService.GetMonitors(), s.MonitorId, s.MonitorDevice, s.Anchor, s.OffsetX, s.OffsetY);
        _autoHidePlacement = (placement.Anchor, placement.OffsetY);
        _autoHide?.Reevaluate();
        UpdateAutoHideCheck();
    }

    private Core.Island.RestMode DecideRest(Core.Island.ForegroundInfo? foreground) =>
        Core.Island.SmartHidePolicy.Decide(_settings.Current, _autoHidePlacement.Anchor, _autoHidePlacement.OffsetY, foreground, _viewModel.Privacy.IsActive);

    private void SetRest(Core.Island.RestMode rest)
    {
        if (rest != _state.Rest)
        {
            Log.Info("window", "Smart Auto-Hide", new { rest = rest.ToString(), process = _foreground?.Current?.ProcessName });
            _state.SetRest(rest);
        }
    }

    /// <summary>
    /// The fallback for a foreground event Windows never delivered: every 750 ms, compare the window
    /// in front with the applied rest (a few cheap Win32 reads, no allocation of note). Runs only while
    /// Smart Auto-Hide can apply (enabled, top center, near the top edge).
    /// </summary>
    private void UpdateAutoHideCheck()
    {
        var s = _settings.Current;
        var applies = _foreground is not null && s.SmartHideEnabled && _autoHidePlacement.Anchor == IslandAnchor.TopCenter
            && _autoHidePlacement.OffsetY <= Core.Island.SmartHidePolicy.MaxTopOffset;
        if (!applies)
        {
            _autoHideCheck?.Stop();
            return;
        }

        if (_autoHideCheck is null)
        {
            _autoHideCheck = _dispatcher.CreateTimer();
            _autoHideCheck.Interval = TimeSpan.FromMilliseconds(750);
            _autoHideCheck.IsRepeating = true;
            _autoHideCheck.Tick += (_, _) => _autoHide?.Reconcile(_state.Rest);
        }

        if (!_autoHideCheck.IsRunning)
        {
            _autoHideCheck.Start();
        }
    }

    /// <summary>
    /// Camera or microphone started or stopped: the marks update in every state. Hidden by Smart
    /// Auto-Hide, the island comes back as the notch (never opened) so the marks stay visible.
    /// </summary>
    private void OnPrivacyChanged(Core.Privacy.PrivacyState state)
    {
        _viewModel.Privacy.State = state;
        ApplySmartHide();
    }

    /// <summary>Ctrl+Alt+C (default): open from anywhere (including hidden), close when expanded.</summary>
    private void OnShortcut()
    {
        _hiddenByUser = false;
        _hiddenForFullscreen = false;
        _window.ShowIsland();
        _window.ToggleFromShortcut();
    }

    /// <summary>Steps aside while a fullscreen app (video, game, presentation) owns the island's monitor.</summary>
    private void OnFullscreenChanged(bool fullscreen)
    {
        _dispatcher.TryEnqueue(() =>
        {
            if (_hiddenByUser)
            {
                return;
            }

            Log.Info("window", fullscreen ? "Fullscreen app on the island monitor; stepping aside" : "Fullscreen app closed", new { hiddenByUser = _hiddenByUser });
            if (fullscreen && !_hiddenForFullscreen)
            {
                _hiddenForFullscreen = true;
                _state.Hide();
            }
            else if (!fullscreen && _hiddenForFullscreen)
            {
                _hiddenForFullscreen = false;
                _state.Show();
            }
        });
    }

    private void OnTrayCommand(TrayCommand command)
    {
        switch (command)
        {
            case TrayCommand.Show:
                ShowIsland(expand: false);
                break;
            case TrayCommand.Hide:
                HideIsland();
                break;
            case TrayCommand.Settings:
                OpenSettings();
                break;
            case TrayCommand.Quit:
                QuitRequested?.Invoke();
                break;
        }
    }

    private void OpenSettings() => OpenSettings(null);

    private bool _settingsActive;

    private void SetSettingsActive(bool active)
    {
        _settingsActive = active;
        _window.SetAlwaysOnTop(_settings.Current.AlwaysOnTop && !active);
        if (!active)
        {
            _window.KeepOnTop();
        }
    }

    private void OpenSettings(string? section)
    {
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow(_settings, _options.IsDemo, () => _hotkey?.IsInUse ?? false);
            _settingsWindow.Closed += (_, _) =>
            {
                _settingsWindow = null;
                SetSettingsActive(false);
            };
            // While Settings is the active window the island leaves the always-on-top band, so Settings is never
            // drawn behind it (the island also re-claims the top whenever another window comes to the front).
            _settingsWindow.Activated += (_, args) => SetSettingsActive(args.WindowActivationState != Microsoft.UI.Xaml.WindowActivationState.Deactivated);
            _settingsWindow.QuitRequested += OnQuitFromSettings;
            _settingsWindow.ClaudeWeb = _claudeWeb;
            _settingsWindow.ChatGptUsage = _chatGptUsage;
            _settingsWindow.RefreshChatGptUsage();
            _settingsWindow.Hermes = _hermes;
            _settingsWindow.RefreshHermes();
        }

        if (section is not null)
        {
            _settingsWindow.ShowSection(section);
        }

        _settingsWindow.ShowAndFocus();
    }

    /// <summary>
    /// Settings: Quit Quotalis. Ends the whole app like the tray's Quit (closing Settings or
    /// hiding the island keep it running). Posted, so the click handler returns before windows close.
    /// </summary>
    private void OnQuitFromSettings()
    {
        Log.Info("startup", "Quit requested from Settings");
        _dispatcher.TryEnqueue(() => QuitRequested?.Invoke());
    }

    /// <summary>
    /// The first scan parses thousands of log lines; afterwards the steady state is small. Compacting
    /// once and returning unused pages keeps the long-running footprint low.
    /// </summary>
    private static void TrimMemory()
    {
        try
        {
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            Platform.Native.NativeMethods.EmptyWorkingSet(process.Handle);
        }
        catch (Exception ex)
        {
            Log.Debug("startup", "Memory trim skipped", new { error = ex.GetType().Name });
        }
    }

    // History ------------------------------------------------------------------------------------------

    private void RequestHistoryReload()
    {
        if (_historyDebounce is null)
        {
            _historyDebounce = _dispatcher.CreateTimer();
            _historyDebounce.IsRepeating = false;
            _historyDebounce.Interval = TimeSpan.FromMilliseconds(1500);
            _historyDebounce.Tick += (_, _) => _ = ReloadHistoryAsync();
        }

        _historyDebounce.Stop();
        _historyDebounce.Start();
    }

    private async Task ReloadHistoryAsync()
    {
        try
        {
            var firstDay = CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
            var todayRow = ((int)DateTime.Today.DayOfWeek - (int)firstDay + 7) % 7;
            var days = UsageViewModel.HistoryWeeks * 7 + todayRow + 1;
            var history = await Task.Run(() => _history.GetHistory(days));
            _viewModel.Usage.SetHistory(history);
            var totals = await Task.Run(() => _history.GetSpendTotals(firstDay));
            _viewModel.Usage.SetSpendTotals(totals);
            var settings = _settings.Current;
            var window = new PeakWindow(settings.PeakStartHour, settings.PeakEndHour, PeakWindow.Eastern);
            if (DateTimeOffset.UtcNow - _peakStatsAt > TimeSpan.FromMinutes(1) || window != _peakStatsWindow)
            {
                _peakStatsAt = DateTimeOffset.UtcNow;
                _peakStatsWindow = window;
                var (profile, drain) = await Task.Run(() => ComputePeakStats(window));
                _viewModel.Usage.SetPeakStats(profile, drain, window);
            }
            if (history.Count > 0)
            {
                var peaks = await Task.Run(() => _database.GetPlanPeaks(AiProviderKind.Claude, history[0].Day, history[^1].Day));
                _viewModel.Usage.SetPlanPeaks(peaks);
            }
        }
        catch (Exception ex)
        {
            Log.Error("database", "History could not be loaded", ex);
        }
    }

    private void ScheduleMidnight()
    {
        _midnight ??= _dispatcher.CreateTimer();
        _midnight.IsRepeating = false;
        _midnight.Interval = DateTime.Today.AddDays(1) - DateTime.Now + TimeSpan.FromSeconds(3);
        _midnight.Tick += OnMidnight;
        _midnight.Start();
    }

    private void OnMidnight(DispatcherQueueTimer sender, object args)
    {
        sender.Tick -= OnMidnight;
        RequestHistoryReload();
        ScheduleMidnight();
    }
}

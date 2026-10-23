using System.Globalization;
using Claudex.Core.Awake;
using Claudex.Core.Calendar;
using Claudex.Core.Demo;
using Claudex.Core.Diagnostics;
using Claudex.Core.Focus;
using Claudex.Core.Island;
using Claudex.Core.Models;
using Claudex.Core.Modules;
using Claudex.Core.Placement;
using Claudex.Core.Plan;
using Claudex.Core.Pricing;
using Claudex.Core.Providers;
using Claudex.Core.Providers.Claude;
using Claudex.Core.Providers.Codex;
using Claudex.Core.Settings;
using Claudex.Core.Storage;
using Claudex.Core.Usage;
using Claudex.Platform.Media;
using Claudex.Platform.Monitors;
using Claudex.Platform.Power;
using Claudex.Platform.Startup;
using Claudex.Platform.Tray;
using Claudex.Platform.Windowing;
using Claudex.UI.Island;
using Claudex.UI.Settings;
using Claudex.ViewModels;
using Microsoft.UI.Dispatching;

namespace Claudex;

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
    private FocusTimer _focusTimer = null!;
    private FocusHistory _focusHistory = null!;
    private IMediaSource _media = null!;
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
    private ISecretStore _secrets = null!;
    private UiStateStore _uiState = null!;
    private DispatcherQueueTimer? _rotation;
    private PlanUsageService? _plan;
    private ClaudeWebReader? _claudeWeb;
    private ChatGptUsageReader? _chatGptUsage;
    private DispatcherQueueTimer? _planReset;
    private DispatcherQueueTimer? _historyDebounce;
    private DispatcherQueueTimer? _displaySettle;
    private DispatcherQueueTimer? _midnight;
    private Core.Island.AutoHideCoordinator? _autoHide;
    private DispatcherQueueTimer? _autoHideCheck;
    private (IslandAnchor Anchor, double OffsetY) _autoHidePlacement = (IslandAnchor.TopCenter, 0);
    private FocusState _lastFocusState = FocusState.Idle;
    private DateTimeOffset? _lastFocusStartedAt;
    private FocusSessionStore? _focusStore;
    private AwakeController? _awake;
    private ScheduledTaskBackstop? _awakeBackstop;
    private bool _awakePowerEvents;
    private bool _awakeQuiet;
    private bool _mediaPrimed;
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
        if (_options.HomeCloudUploadPath is { } uploadPath)
        {
            StartHomeCloudUpload(uploadPath);
            return;
        }

        // Demo and production may run concurrently; each writer needs its own file.
        Log.Initialize(_options.IsDemo ? Path.Combine(AppPaths.Logs, "demo") : AppPaths.Logs);
        if (Environment.GetEnvironmentVariable("DI_LOG_DEBUG") == "1")
        {
            Log.MinimumLevel = LogLevel.Debug;
        }
        Log.Info("startup", "Claudex starting", new
        {
            version = typeof(AppHost).Assembly.GetName().Version?.ToString(),
            demo = _options.IsDemo,
            autostart = _options.IsAutostart,
            os = Environment.OSVersion.VersionString,
        });

        _settings = new SettingsStore(AppPaths.Settings(_options.IsDemo));
        if (!_options.IsDemo)
        {
            // Hook-written files now live in %USERPROFILE%\.claudex; bring over copies from the old folder and from the
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
        _focusHistory = new FocusHistory(_database);
        _focusTimer = new FocusTimer();
        _media = _options.IsDemo ? new DemoMediaSource(AppPaths.Assets) : new MediaSessionService();

        var settings = _settings.Current;
        var usage = new UsageViewModel();
        var music = new MusicViewModel(_media);
        var focus = new FocusViewModel(_focusTimer, _focusHistory, settings.CustomFocusMinutes,
            minutes => _settings.Update(s => s.CustomFocusMinutes = minutes));
        var modules = ModuleViewModels.Create();
        _viewModel = new IslandViewModel(usage, music, focus, modules, _options.IsDemo);
        CreateAwake(modules.Awake, settings);
        // Private calendar links live in Windows Credential Manager; demo mode keeps them in memory.
        _secrets = _options.IsDemo ? new MemorySecretStore() : new CredentialManagerSecretStore();
        MigrateCalendarLinks();
        settings = _settings.Current;
        _modules = new ModuleHost(_dispatcher, modules, _viewModel, _settings, _options.IsDemo, _secrets) { OpenSettings = OpenSettings };
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
        _window.ConfigureHomeCloud(settings.HomeCloudActive, settings.HomeCloudTargetPath, settings.HomeCloudDropSide);
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
        }

        _tray = new TrayIcon(_host, AppPaths.Icon)
        {
            IsIslandVisible = () => _window.IsIslandVisible,
            IsFocusAvailable = () => _settings.Current.FocusEnabled && !_focusTimer.Snapshot.IsActive,
            IsTasksAvailable = () => _settings.Current.TasksEnabled,
            AwakeState = () => (_settings.Current.AwakeEnabled, _awake?.Snapshot.IsActive ?? false),
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

        RestoreAwakeSession(settings);
        WireEvents();
        RestoreFocusSession(settings);

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

            _viewModel.OnTrackChanged();
            await Shot("03-activity-music");

            _viewModel.OnAiActivity(new AiActivity(AiProviderKind.Claude, AiActivityKind.SessionStarted, "Claude Code session started", "claudex"));
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
        _viewModel.OnAiActivity(new AiActivity(AiProviderKind.Claude, AiActivityKind.SessionStarted, "Claude Code session started", "claudex"));
        await shot("63-privacy-activity");
        _state.Activate();
        await Task.Delay(600);
        await shot("64-privacy-expanded");
        _state.Dismiss();
        _viewModel.Privacy.State = Core.Privacy.PrivacyState.None;
        await Task.Delay(600);

        // Non-ready module states, as a user without gh, calendars or a readable repository sees them.
        var now = DateTimeOffset.UtcNow;
        _viewModel.GitHub.Update(new Core.Modules.ModuleStatus(Core.Modules.ModuleState.Unavailable, "Install the GitHub CLI and run “gh auth login” to see pull requests, CI and notifications."), null, now);
        _viewModel.Calendar.Update(new Core.Modules.ModuleStatus(Core.Modules.ModuleState.Empty, "Add a calendar link (iCal format) or an .ics file in Settings."), [], [], now);
        _viewModel.Git.Update(new Core.Modules.ModuleStatus(Core.Modules.ModuleState.Error, "Git could not read these repositories."), [], now);
        _state.Activate();
        foreach (var (tab, name) in new[] { (IslandTab.GitHub, "github-unavailable"), (IslandTab.Calendar, "calendar-empty"), (IslandTab.Git, "git-error") })
        {
            _viewModel.SelectedTab = tab;
            await shot("40-state-" + name);
        }

        _state.Dismiss();
        await Task.Delay(600);

        _modules.Apply(_settings.Current); // back to the demo content after the empty and error states

        // The compact island featuring each module (as a single favorite).
        foreach (var module in new[] { ModuleId.Claude, ModuleId.Codex, ModuleId.Music, ModuleId.Focus, ModuleId.System, ModuleId.Calendar, ModuleId.Git, ModuleId.GitHub, ModuleId.Tasks, ModuleId.Awake })
        {
            _viewModel.SetFavorites([module]);
            await shot("50-compact-" + module.ToString().ToLowerInvariant());
        }

        _viewModel.SetFavorites([ModuleId.Claude, ModuleId.Music, ModuleId.Calendar]);
        await shot("51-compact-favorites-turn-0");
        _viewModel.AdvanceCompactRotation();
        await shot("51-compact-favorites-turn-1");
        _viewModel.SetFavorites([]);
        _viewModel.SetLastActive(null);
        await Task.Delay(400);

        await AwakeSnapshotsAsync(shot);
        await DropSnapshotsAsync(shot);

        await new SettingsWindow(_settings, _options.IsDemo, () => false, _secrets).SaveSnapshotsAsync(_options.SnapshotDirectory!);
    }

    /// <summary>An awake session (demo power control: nothing on the system changes), timed and indefinite.</summary>
    private async Task AwakeSnapshotsAsync(Func<string, Task> shot)
    {
        if (_awake is null)
        {
            return;
        }

        _awakeQuiet = true;
        _awake.StartTimed(TimeSpan.FromHours(4));
        await Task.Delay(400);
        await shot("70-awake-compact");
        _viewModel.OnAwakeStarted();
        await shot("71-awake-activity");
        _state.Dismiss();
        await Task.Delay(400);
        _viewModel.PrepareExpandTo(IslandTab.Awake);
        _state.Activate();
        await shot("72-awake-expanded");
        _awake.StartIndefinite();
        await shot("73-awake-indefinite");
        _state.Dismiss();
        await Task.Delay(400);
        await shot("75-awake-compact-indefinite");
        _state.Activate();
        _awake.Stop();
        await shot("74-awake-idle");
        _state.Dismiss();
        await Task.Delay(600);
    }

    /// <summary>
    /// The Home Cloud drop zone on either side, uploading, after success and after an error, and opened
    /// from the notch. Driven through the state machine and the window's drop feedback; nothing is copied.
    /// </summary>
    private async Task DropSnapshotsAsync(Func<string, Task> shot)
    {
        var s = _settings.Current;

        // A result lingers only briefly: capture it without the long settle delay of a morph.
        async Task Quick(string name)
        {
            await Task.Delay(300);
            await _window.SaveSnapshotAsync(Path.Combine(_options.SnapshotDirectory!, name + ".png"));
        }

        // The shown result closes the zone by itself.
        Task BackToRest() => Task.Delay(IslandStateMachine.DropFailureLinger + TimeSpan.FromMilliseconds(600));

        foreach (var side in new[] { Core.HomeCloud.HomeCloudDropSide.Right, Core.HomeCloud.HomeCloudDropSide.Left })
        {
            _window.ConfigureHomeCloud(true, s.HomeCloudTargetPath, side);
            _state.FileDragEntered();
            await shot("drop-hover-" + side.ToString().ToLowerInvariant());
            _state.FileDragLeft();
            await Task.Delay(600);
        }

        _window.ConfigureHomeCloud(true, s.HomeCloudTargetPath, Core.HomeCloud.HomeCloudDropSide.Right);
        _state.FileDragEntered();
        _state.BeginDropUpload();
        _window.DropFeedback.Report(new Core.HomeCloud.HomeCloudProgress(4_200_000, 10_000_000, 3, 5));
        await shot("drop-progress");
        _window.ShowDropResult(Core.HomeCloud.HomeCloudUploadResult.Succeeded(5, 10_000_000, TimeSpan.FromSeconds(3)));
        await Quick("drop-success");
        await BackToRest();

        _state.FileDragEntered();
        _state.BeginDropUpload();
        _window.ShowDropResult(Core.HomeCloud.HomeCloudUploadResult.Failed("Target not reachable"));
        await Quick("drop-error");
        await BackToRest();

        _state.SetRest(Core.Island.RestMode.Retracted);
        await Task.Delay(600);
        _state.FileDragEntered();
        await Task.Delay(400); // the morph from a 48 px notch travels furthest
        await shot("drop-from-notch");
        _state.FileDragLeft();
        _state.SetRest(Core.Island.RestMode.Compact);
        await Task.Delay(600);

        _window.ConfigureHomeCloud(s.HomeCloudActive, s.HomeCloudTargetPath, s.HomeCloudDropSide);
    }

    /// <summary>
    /// Development (<c>--homecloud-upload=path</c>): sends one file or folder through the same drop
    /// pipeline a real drop takes (drop zone, uploader, feedback), from a hidden island at the user's
    /// placement, then quits with exit code 0 on success. Runs beside the user's island: no tray, no
    /// watchers, no providers, an in-memory database, and the settings are read but never written.
    /// </summary>
    private void StartHomeCloudUpload(string path)
    {
        Log.Initialize(Path.Combine(AppPaths.Logs, "homecloud-upload"));
        Log.Info("homecloud", "Upload debug run starting", new { demo = _options.IsDemo });

        // Read only: nothing on this path calls Update, so the file is never written.
        _settings = new SettingsStore(AppPaths.Settings(_options.IsDemo));
        var settings = _settings.Current;
        _database = UsageDatabase.OpenInMemory();
        _focusHistory = new FocusHistory(_database);
        _focusTimer = new FocusTimer();
        _media = new DemoMediaSource(AppPaths.Assets); // never started: the island shows no music
        var focus = new FocusViewModel(_focusTimer, _focusHistory, settings.CustomFocusMinutes, _ => { });
        _viewModel = new IslandViewModel(new UsageViewModel(), new MusicViewModel(_media), focus, ModuleViewModels.Create(), _options.IsDemo);
        _state = new IslandStateMachine(TimeProvider.System, action => _dispatcher.TryEnqueue(() => action()));
        _window = new IslandWindow(_viewModel, _state, settings.AlwaysOnTop);
        _window.PlacementInvalidated += () => _dispatcher.TryEnqueue(PlaceIsland);
        _window.ConfigureHomeCloud(settings.HomeCloudActive, settings.HomeCloudTargetPath, settings.HomeCloudDropSide);
        PlaceIsland();

        // Only the drop zone shows; afterwards the island hides again, like a drop onto a hidden island.
        _state.Hide();
        _ = RunHomeCloudUploadAsync(path, settings.HomeCloudActive);
    }

    private async Task RunHomeCloudUploadAsync(string path, bool enabled)
    {
        var exitCode = 1;
        try
        {
            if (!enabled)
            {
                Log.Warn("homecloud", "Upload debug run: Home Cloud is off or has no target folder");
                exitCode = 2;
                return;
            }

            await Task.Delay(500);
            var closed = new TaskCompletionSource();
            _state.ModeChanged += (from, _) =>
            {
                if (from == Core.Island.IslandMode.Drop)
                {
                    closed.TrySetResult();
                }
            };

            // Enter and drop in one go: the zone's drag re-check would close a hover with no real drag.
            _state.FileDragEntered();
            var result = await _window.UploadDroppedAsync([path]);
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(15));
            exitCode = result.Status == Core.HomeCloud.HomeCloudUploadStatus.Succeeded ? 0 : 1;
            Log.Info("homecloud", "Upload debug run finished", new { status = result.Status.ToString(), files = result.FileCount, bytes = result.Bytes, exitCode });
        }
        catch (Exception ex)
        {
            // The type only: IO messages name paths.
            Log.Error("homecloud", "Upload debug run failed", data: new { error = ex.GetType().Name });
        }
        finally
        {
            Program.ExitCode = exitCode;
            QuitRequested?.Invoke();
        }
    }

    /// <summary>
    /// Claudex was started again while running (desktop or Start menu shortcut, pinned taskbar icon, a Stream Deck
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
        // Quit ends an awake session: sleep and the lid work as usual again once the app is gone.
        if (_awake is { Snapshot.IsActive: true })
        {
            _awake.Stop(AwakeEndReason.Quit);
        }

        _awake?.Dispose();
        _awakeBackstop?.Flush(TimeSpan.FromSeconds(3));
        _window?.PrepareForExit();
        _shutdown.Cancel();
        _providers.Dispose();
        _focusTimer?.Dispose();
        _modules?.Dispose();
        _plan?.Dispose();
        _claudeWeb?.Dispose();
        _chatGptUsage?.Dispose();
        _media?.Dispose();
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
        Log.Info("startup", "Claudex stopped");
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

    /// <summary>
    /// The Awake session: real power control and the scheduled backstop, or in demo mode stand-ins
    /// that never touch the system.
    /// </summary>
    private void CreateAwake(AwakeViewModel viewModel, AppSettings settings)
    {
        IPowerControl power = _options.IsDemo ? new DemoPowerControl() : new WindowsPowerControl();
        _awakeBackstop = _options.IsDemo || Environment.ProcessPath is not { } exe ? null : new ScheduledTaskBackstop(exe, AppPaths.Root);
        _awake = new AwakeController(power, (IAwakeBackstop?)_awakeBackstop ?? new NoBackstop(), new AwakeJournalStore(_options.IsDemo ? null : AppPaths.AwakeSession));
        viewModel.Attach(_awake, settings.AwakeCustomMinutes, minutes => _settings.Update(s => s.AwakeCustomMinutes = minutes));
    }

    /// <summary>
    /// A session still running when the app closed (a restart, a crash) continues; one that ended
    /// meanwhile has its lid setting put back. Quietly: no "staying awake" announcement.
    /// </summary>
    private void RestoreAwakeSession(AppSettings settings)
    {
        if (_awake is null)
        {
            return;
        }

        var action = _awake.Recover();
        if (action == AwakeStartupAction.Resume && !settings.AwakeEnabled)
        {
            _awake.Stop(AwakeEndReason.Disabled);
        }

        _viewModel.Awake.Update(_awake.Snapshot);
        UpdateAwakePowerEvents(_awake.Snapshot.IsActive);
    }

    /// <summary>Battery, power source and lid notifications, only while a session runs.</summary>
    private void UpdateAwakePowerEvents(bool active)
    {
        if (_options.IsDemo || _host is null || active == _awakePowerEvents)
        {
            return;
        }

        if (active)
        {
            _host.RegisterPowerSettings(PowerNative.BatteryPercentageRemaining, PowerNative.AcDcPowerSource, PowerNative.LidSwitchStateChange);
        }
        else
        {
            _host.UnregisterPowerSettings();
        }

        _awakePowerEvents = active;
    }

    private void OnPowerSettingChanged(Guid setting, uint value)
    {
        if (setting == PowerNative.LidSwitchStateChange)
        {
            Log.Info("awake", value == 0 ? "Lid closed" : "Lid opened", new { active = _awake?.Snapshot.IsActive });
            return;
        }

        CheckAwakeBattery();
    }

    private void CheckAwakeBattery()
    {
        if (_awake is { Snapshot.IsActive: true } && !_options.IsDemo)
        {
            var (percent, onMains) = WindowsPowerControl.ReadBattery();
            _awake.OnBattery(percent, onMains, _settings.Current.AwakeBatteryFloor);
        }
    }

    /// <summary>A session running or paused when the app closed continues (or completes) now.</summary>
    private void RestoreFocusSession(AppSettings settings)
    {
        if (_options.IsDemo)
        {
            return;
        }

        _focusStore = new FocusSessionStore(AppPaths.FocusSession);
        var saved = _focusStore.Load();
        if (saved is null || !settings.FocusEnabled)
        {
            _focusStore.Save(null);
            return;
        }

        // Restoring is not a new start: no "focus started" peek.
        _lastFocusState = saved.State;
        _lastFocusStartedAt = saved.StartedAt;
        _focusTimer.RestoreSession(saved);
        Log.Info("focus", "Session restored", new { state = _focusTimer.Snapshot.State.ToString() });
    }

    private async Task StartBackgroundAsync()
    {
        try
        {
            if (_options.IsDemo)
            {
                await Task.Run(() => DemoData.SeedHistory(_history, _database, TimeProvider.System));
                await _providers.AddAsync(new DemoAiProvider(DemoData.ClaudeSnapshot(_history)), _shutdown.Token);
                await _providers.AddAsync(new DemoAiProvider(DemoData.CodexSnapshot(_history)), _shutdown.Token);
                _dispatcher.TryEnqueue(() =>
                {
                    _focusTimer.Restore(TimeSpan.FromMinutes(50), TimeSpan.FromSeconds(42 * 60 + 13));
                    _viewModel.Focus.RefreshToday();
                });
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

            await _media.StartAsync();
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
    /// drain from exact readings: Claudex's calibration log, plus Tokenz's older log when it is still on this PC (read only).
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

        _media.Changed += snapshot => _dispatcher.TryEnqueue(() =>
        {
            // The first snapshot after launch is the current state, not a song change.
            if (!_mediaPrimed)
            {
                _mediaPrimed = true;
                _viewModel.Music.TrackChanged -= _viewModel.OnTrackChanged;
                _viewModel.Music.Update(snapshot);
                _viewModel.Music.TrackChanged += _viewModel.OnTrackChanged;
                return;
            }

            _viewModel.Music.Update(snapshot);
        });

        _focusTimer.Changed += snapshot => _dispatcher.TryEnqueue(() =>
        {
            _viewModel.Focus.Update(snapshot);
            _viewModel.RefreshActivityTrailing();
            if (snapshot.State == FocusState.Running && _lastFocusState == FocusState.Idle)
            {
                _viewModel.OnFocusStarted(snapshot);
            }

            // Persist on start, pause, resume and stop; never per tick.
            if (snapshot.State != _lastFocusState || snapshot.StartedAt != _lastFocusStartedAt)
            {
                _focusStore?.Save(_focusTimer.Capture());
            }

            _lastFocusState = snapshot.State;
            _lastFocusStartedAt = snapshot.StartedAt;
        });
        _focusTimer.SessionEnded += record =>
        {
            _focusHistory.Record(record);

            _dispatcher.TryEnqueue(() =>
            {
                _viewModel.Focus.RefreshToday();
                _viewModel.OnFocusEnded(record.Completed, TimeSpan.FromSeconds(record.FocusedSeconds));
            });
        };

        if (_awake is not null)
        {
            _awake.Changed += snapshot => _dispatcher.TryEnqueue(() =>
            {
                var started = snapshot.IsActive && !_viewModel.Awake.Snapshot.IsActive;
                _viewModel.Awake.Update(snapshot);
                _viewModel.RefreshActivityTrailing();
                if (started && !_awakeQuiet)
                {
                    _viewModel.OnAwakeStarted();
                }

                _awakeQuiet = false;
                UpdateAwakePowerEvents(snapshot.IsActive);
            });
            _awake.Ended += reason => _dispatcher.TryEnqueue(() => _viewModel.OnAwakeEnded(reason));
        }

        _host.PowerSettingChanged += OnPowerSettingChanged;

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
        _viewModel.EnsureValidTab();
        _window.SetAlwaysOnTop(settings.AlwaysOnTop && !_settingsActive);
        ApplyKeepOpen(settings);
        _window.ConfigureHomeCloud(settings.HomeCloudActive, settings.HomeCloudTargetPath, settings.HomeCloudDropSide);
        PlaceIsland();
        SyncAutostart(settings);
        _settingsWindow?.ApplyTheme(settings.Theme);
        _hotkey?.Apply(settings.GlobalShortcutEnabled, ShortcutGesture.FromText(settings.GlobalShortcut));
        _foreground?.Refresh();
        ApplySmartHide();
        if (!settings.FocusEnabled && _focusTimer.Snapshot.IsActive)
        {
            _focusTimer.Stop();
        }

        if (!settings.AwakeEnabled && _awake is { Snapshot.IsActive: true })
        {
            _awake.Stop(AwakeEndReason.Disabled);
        }

        // A changed battery floor applies at once.
        CheckAwakeBattery();

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

    /// <summary>How plan usage reaches Claudex right now: the claude.ai sign-in, else the status line.</summary>
    private PlanConnection PlanConnectionNow() =>
        ClaudePlanLink.Combine(_claudeWeb?.Status ?? ClaudeWebStatus.SignedOut, ClaudeStatusLineSetup.Inspect(ClaudeStatusLineSetup.SettingsPath()));

    /// <summary>The claude.ai sign-in changed state or read new numbers (those also arrive through the plan file).</summary>
    private void OnClaudeWebChanged()
    {
        _viewModel.Usage.Claude.SetPlan(_plan?.Usage, PlanConnectionNow());
        _settingsWindow?.RefreshPlanConnection();
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

    /// <summary>Earlier versions saved calendar links in settings.json; move them into Credential Manager.</summary>
    private void MigrateCalendarLinks()
    {
        if (_settings.Current.CalendarSources.Count == 0)
        {
            return;
        }

        try
        {
            var count = _settings.Current.CalendarSources.Count;
            _settings.Update(s => CalendarFeeds.MigratePlainText(s, _secrets));
            Log.Info("calendar", "Calendar links moved to Windows Credential Manager", new { count });
        }
        catch (Exception ex)
        {
            Log.Warn("calendar", "Calendar links could not be moved; they stay in settings for now", new { error = ex.GetType().Name });
        }
    }

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
        _modules.SetSystemInCompact(compact && _viewModel.CompactModule == ModuleId.System);
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
        _viewModel.Music.IsEnabled = settings.MusicEnabled;
        _viewModel.Focus.IsEnabled = settings.FocusEnabled;
        _viewModel.Awake.IsEnabled = settings.AwakeEnabled;
        _viewModel.Awake.BatteryFloor = settings.AwakeBatteryFloor;
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
                        publisher: new Claudex.Core.Usage.CodexLimitPublisher(AppPaths.CodexPlan, AppPaths.CodexLimitLog));
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
            case TrayCommand.StartFocus:
                if (!_focusTimer.Snapshot.IsActive)
                {
                    ShowIsland(expand: false);
                    _focusTimer.Start(TimeSpan.FromMinutes(25));
                }

                break;
            case TrayCommand.NewTask:
                if (_settings.Current.TasksEnabled)
                {
                    ShowIsland(expand: false);
                    _viewModel.PrepareExpandTo(IslandTab.Tasks);
                    _window.ExpandToTaskCapture();
                }

                break;
            case TrayCommand.Awake when _awake is { Snapshot.IsActive: true }:
                _awake.Stop();
                break;
            case TrayCommand.Awake when _settings.Current.AwakeEnabled:
                _viewModel.PrepareExpandTo(IslandTab.Awake);
                ShowIsland(expand: true);
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
            _settingsWindow = new SettingsWindow(_settings, _options.IsDemo, () => _hotkey?.IsInUse ?? false, _secrets);
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
        }

        if (section is not null)
        {
            _settingsWindow.ShowSection(section);
        }

        _settingsWindow.ShowAndFocus();
    }

    /// <summary>
    /// Settings: Quit Claudex. Ends the whole app like the tray's Quit (closing Settings or
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
        _viewModel.Focus.RefreshToday();
        RequestHistoryReload();
        ScheduleMidnight();
    }
}

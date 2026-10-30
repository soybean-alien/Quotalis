using Quotalis.Core.Diagnostics;
using Quotalis.Core.Git;
using Quotalis.Core.Hermes;
using Quotalis.Core.Models;
using Quotalis.Core.Plan;
using Quotalis.Core.Pricing;
using Quotalis.Core.Providers;
using Quotalis.Core.Providers.Claude;
using Quotalis.Core.Providers.Codex;
using Quotalis.Core.Settings;
using Quotalis.Core.Storage;
using Quotalis.Core.Usage;

namespace Quotalis.Linux;

/// <summary>
/// The background service (<c>quotalis service</c>). Runs Core's readers, the same ones the Windows island uses, and keeps
/// <see cref="AppPaths.State"/> current for the bar and the terminal screen. All updates are debounced into one write.
/// </summary>
internal sealed class UsageService : IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan HermesInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan GitInterval = TimeSpan.FromMinutes(1);

    private readonly CancellationTokenSource _shutdown = new();
    private readonly ProviderHost _providers = new();
    private readonly GitService _git = new();
    private readonly CodexAccountClient _codexAccount = new();
    private readonly SemaphoreSlim _changed = new(0);
    private SettingsStore _settings = null!;
    private UsageDatabase _database = null!;
    private UsageHistoryService _history = null!;
    private PlanUsageService _plan = null!;
    private HermesUsage? _hermes;
    private string? _hermesProblem;
    private ContributionCalendar? _calendar;
    private string? _calendarNote;

    public async Task RunAsync(CancellationToken stop)
    {
        using var linked = stop.Register(_shutdown.Cancel);
        Directory.CreateDirectory(AppPaths.Root);
        Directory.CreateDirectory(AppPaths.Shared);
        Log.Initialize(AppPaths.Logs);
        Log.Info("startup", "Quotalis service starting", new { version = typeof(UsageService).Assembly.GetName().Version?.ToString() });

        _settings = new SettingsStore(AppPaths.Settings);
        _database = OpenDatabase();
        _history = new UsageHistoryService(_database, ModelPricingService.Load(AppPaths.PricingOverrides));
        var settings = _settings.Current;

        _plan = new PlanUsageService(new PlanUsageStore(AppPaths.ClaudePlan));
        _plan.Changed += OnPlanUsage;
        _plan.Configure(settings.ClaudeEnabled);

        _providers.SnapshotChanged += OnSnapshot;
        _history.Changed += _ => Signal();
        _git.Changed += Signal;
        _git.RecentChanged += recent => _settings.Update(s => s.GitRecentRepositories = recent.ToList());
        _git.Configure(settings.GitEnabled, settings.GitRepositories, settings.GitRecentRepositories);
        _git.SetVisible(settings.GitEnabled);

        Publish();
        await StartProvidersAsync(settings);
        Reprice();

        var token = _shutdown.Token;
        var loops = new List<Task> { HermesLoopAsync(token), GitLoopAsync(token), PublishLoopAsync(token) };
        if (settings.CodexEnabled)
        {
            loops.Add(ChatGptLoopAsync(token));
        }

        try
        {
            await Task.WhenAll(loops);
        }
        catch (OperationCanceledException)
        {
        }

        Log.Info("startup", "Quotalis service stopped");
        Log.Flush();
    }

    private UsageDatabase OpenDatabase()
    {
        try
        {
            var db = UsageDatabase.Open(AppPaths.Database);
            db.PruneEvents(DateOnly.FromDateTime(DateTime.Today).AddDays(-400));
            return db;
        }
        catch (Exception ex)
        {
            // Quotalis still works; history just is not kept this session.
            Log.Error("database", "Usage database unavailable; using a temporary in-memory store", ex);
            return UsageDatabase.OpenInMemory();
        }
    }

    private async Task StartProvidersAsync(AppSettings settings)
    {
        if (settings.ClaudeEnabled)
        {
            await _providers.AddAsync(new ClaudeUsageProvider(ClaudeUsageProvider.DefaultRoot(), _history, _database), _shutdown.Token);
        }

        if (settings.CodexEnabled)
        {
            await _providers.AddAsync(new CodexUsageProvider(CodexUsageProvider.DefaultRoot(), _history, _database,
                publisher: new CodexLimitPublisher(AppPaths.CodexPlan, AppPaths.CodexLimitLog)), _shutdown.Token);
        }
    }

    private void Reprice()
    {
        try
        {
            var repriced = _history.Reprice();
            if (repriced > 0)
            {
                Log.Info("pricing", "History repriced", new { rows = repriced });
            }
        }
        catch (Exception ex)
        {
            Log.Error("pricing", "History could not be repriced", ex);
        }
    }

    private void OnSnapshot(AiUsageSnapshot snapshot)
    {
        if (snapshot.CurrentProjectPath is { } path && snapshot.IsActive)
        {
            _git.NoteProjectPath(path);
        }

        Signal();
    }

    /// <summary>Keeps the day's measured peaks for the history. Estimates between readings are not recorded.</summary>
    private void OnPlanUsage(PlanUsage? usage)
    {
        if (usage is not null)
        {
            var now = DateTimeOffset.UtcNow;
            var day = DateOnly.FromDateTime(usage.MeasuredAt.ToLocalTime().DateTime);
            try
            {
                if (usage.CurrentAt(now) is { IsEstimated: false } current) _database.RecordPlanPeak(day, AiProviderKind.Claude, "five_hour", current.UsedPercent, usage.MeasuredAt);
                if (usage.WeeklyAt(now) is { IsEstimated: false } weekly) _database.RecordPlanPeak(day, AiProviderKind.Claude, "seven_day", weekly.UsedPercent, usage.MeasuredAt);
            }
            catch (Exception ex)
            {
                Log.Debug("plan", "Plan peak not recorded", new { error = ex.GetType().Name });
            }
        }

        Signal();
    }

    private void Signal()
    {
        if (_changed.CurrentCount == 0)
        {
            _changed.Release();
        }
    }

    private async Task PublishLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            // A heartbeat write too, so a reader can tell a stopped service from a quiet one.
            await _changed.WaitAsync(Heartbeat, token);
            await Task.Delay(Debounce, token);
            Publish();
        }
    }

    private void Publish()
    {
        try
        {
            var now = DateTimeOffset.UtcNow;
            var settings = _settings.Current;
            var today = _history.Today;
            var totals = _history.GetSpendTotals(DayOfWeek.Sunday);
            var connection = ClaudeStatusLineSetup.Inspect(ClaudeStatusLineSetup.SettingsPath());
            var hermes = HermesReader.IsEnabled(settings) ? _hermes : null;
            AppStateFile.Write(AppPaths.State, new AppState
            {
                GeneratedAt = now,
                Claude = StateBuilder.Claude(_providers.GetSnapshot(AiProviderKind.Claude), _plan.Usage, connection, now),
                Codex = StateBuilder.Codex(_providers.GetSnapshot(AiProviderKind.Codex)),
                Hermes = StateBuilder.Hermes(HermesReader.IsEnabled(settings), hermes, _hermesProblem, today),
                Days = StateBuilder.Days(_history, hermes, today),
                WeekUsd = totals.Week,
                MonthUsd = totals.Month,
                TotalsPartial = totals.WeekPartial || totals.MonthPartial,
                Git = settings.GitEnabled ? StateBuilder.Git(_git, _calendar, _calendarNote) : new GitState(),
            });
        }
        catch (Exception ex)
        {
            Log.Error("state", "State could not be written", ex);
        }
    }

    private async Task HermesLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var settings = _settings.Current;
            if (HermesReader.IsEnabled(settings))
            {
                try
                {
                    var (usage, problem) = await HermesReader.ReadAsync(settings, token);
                    _hermes = usage ?? _hermes;
                    _hermesProblem = problem;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Log.Warn("hermes", "Hermes usage read failed", new { error = ex.GetType().Name });
                    _hermesProblem = "Hermes usage could not be read";
                }

                Signal();
            }

            await Task.Delay(HermesInterval, token);
        }
    }

    /// <summary>The bar has no "panel opened" moment, so working-tree edits are picked up by a steady refresh.</summary>
    private async Task GitLoopAsync(CancellationToken token)
    {
        var calendarAt = DateTimeOffset.MinValue;
        while (!token.IsCancellationRequested && _settings.Current.GitEnabled)
        {
            try
            {
                await _git.RefreshAsync();
                var now = DateTimeOffset.UtcNow;
                if (now - calendarAt >= GitHubContributions.RefreshEvery)
                {
                    var (status, calendar) = await GitHubContributions.ReadAsync(now);
                    if (status == GitHubContributions.ReadStatus.Ok)
                    {
                        calendarAt = now;
                        _calendar = calendar;
                    }

                    _calendarNote = status switch
                    {
                        GitHubContributions.ReadStatus.NoCli => "Install the GitHub CLI and sign in to see contributions",
                        GitHubContributions.ReadStatus.NotSignedIn => "Run gh auth login to see contributions",
                        GitHubContributions.ReadStatus.OtherAccount => $"Contributions show for {GitHubContributions.Login} only",
                        GitHubContributions.ReadStatus.Failed => "Contributions could not be read",
                        _ => null,
                    };
                    Signal();
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One bad read must not end the loop; the next pass tries again.
                Log.Warn("git", "Git refresh failed", new { error = ex.GetType().Name });
            }

            await Task.Delay(GitInterval, token);
        }
    }

    /// <summary>The account's plan limits, when <c>quotalis signin chatgpt</c> has been run. Sends no AI request.</summary>
    private async Task ChatGptLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var next = TimeSpan.FromMinutes(2);
            try
            {
                if (CodexAccountClient.IsAvailable && await _codexAccount.IsSignedInAsync(token))
                {
                    var body = await _codexAccount.ReadLimitsAsync(token);
                    var shared = ChatGptPlanUsageParser.Parse(body, DateTimeOffset.UtcNow)?.Shared;
                    _providers.Providers.OfType<CodexUsageProvider>().FirstOrDefault()?.ApplyAccountLimits(shared?.Current, shared?.Weekly, shared is not null);
                }
                else
                {
                    next = TimeSpan.FromMinutes(5);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Warn("chatgpt-usage", "Usage read failed", new { error = ex.GetType().Name });
                next = TimeSpan.FromMinutes(5);
            }

            await Task.Delay(next, token);
        }
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        _providers.Dispose();
        _plan?.Dispose();
        _git.Dispose();
        _codexAccount.Dispose();
        _database?.Dispose();
        _shutdown.Dispose();
    }
}

using Quotalis.Core.Demo;
using Quotalis.Core.Diagnostics;
using Quotalis.Core.Git;
using Quotalis.Core.Models;
using Quotalis.Core.Modules;
using Quotalis.Core.Settings;
using Quotalis.ViewModels;
using Microsoft.UI.Dispatching;

namespace Quotalis;

/// <summary>
/// Owns the Git module: creates its service, applies settings, and moves its results to the view model on the UI
/// thread. A disabled module's service is configured off and does no work: no watchers, timers or processes.
/// </summary>
internal sealed class ModuleHost : IDisposable
{
    private readonly DispatcherQueue _dispatcher;
    private readonly ModuleViewModels _vms;
    private readonly IslandViewModel _island;
    private readonly SettingsStore _settings;
    private readonly bool _demo;
    private readonly GitService _git = new();
    private DateTimeOffset _contributionsReadAt = DateTimeOffset.MinValue;
    private bool _contributionsBusy;

    public ModuleHost(DispatcherQueue dispatcher, ModuleViewModels vms, IslandViewModel island, SettingsStore settings, bool demo)
    {
        _dispatcher = dispatcher;
        _vms = vms;
        _island = island;
        _settings = settings;
        _demo = demo;

        _git.Changed += () => Post(UpdateGit);
        _git.RecentChanged += recent => Post(() => _settings.Update(s => s.GitRecentRepositories = recent.ToList()));
        _vms.Git.RepositorySelected += root => _git.SetActive(root);
        foreach (var vm in _vms.All)
        {
            vm.ActionRequested += OnAction;
        }
    }

    /// <summary>Opens Settings on a section (wired by the host).</summary>
    public Action<string>? OpenSettings { get; set; }

    /// <summary>Applies module switches and configuration. Call on the UI thread.</summary>
    public void Apply(AppSettings settings)
    {
        _island.SetModuleOrder(settings.Order);
        if (_demo)
        {
            var now = DateTimeOffset.UtcNow;
            _vms.Git.Update(settings.GitEnabled ? ModuleStatus.Ready : ModuleStatus.Disabled, settings.GitEnabled ? DemoModules.Repositories(now) : [], now);
            _vms.Git.SetContributions(settings.GitEnabled ? DemoModules.Contributions(now) : null, null);
            _island.EnsureValidTab();
            return;
        }

        _git.Configure(settings.GitEnabled, settings.GitRepositories, settings.GitRecentRepositories);
        UpdateGit();
        if (settings.GitEnabled)
        {
            _ = RefreshContributionsAsync();
        }
    }

    /// <summary>An AI session reported activity: its folder may be a repository to follow.</summary>
    public void OnAiSnapshot(AiUsageSnapshot snapshot)
    {
        if (!_demo && snapshot.CurrentProjectPath is { } path && snapshot.IsActive)
        {
            _git.NoteProjectPath(path);
        }
    }

    /// <summary>The expanded island shows <paramref name="tab"/> (null: collapsed).</summary>
    public void OnPanelVisible(IslandTab? tab)
    {
        _git.SetVisible(tab == IslandTab.Git);
        if (!_demo && tab == IslandTab.Git)
        {
            _ = _git.RefreshAsync();
            _ = RefreshContributionsAsync();
        }
    }

    public void Dispose() => _git.Dispose();

    private void Post(Action action) => _dispatcher.TryEnqueue(() =>
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Log.Error("modules", "Module update failed", ex);
        }
    });

    /// <summary>soybean-alien's GitHub calendar, read through the signed-in GitHub CLI at most every 15 minutes.</summary>
    private async Task RefreshContributionsAsync()
    {
        var now = DateTimeOffset.UtcNow;
        if (_contributionsBusy || now - _contributionsReadAt < GitHubContributions.RefreshEvery)
        {
            return;
        }

        _contributionsBusy = true;
        try
        {
            var (status, calendar) = await GitHubContributions.ReadAsync(now);
            if (status == GitHubContributions.ReadStatus.Ok)
            {
                _contributionsReadAt = now;
            }

            var note = status switch
            {
                GitHubContributions.ReadStatus.NoCli => "Install the GitHub CLI and sign in to see your GitHub contributions.",
                GitHubContributions.ReadStatus.NotSignedIn => "Sign in to the GitHub CLI (gh auth login) to see your GitHub contributions.",
                GitHubContributions.ReadStatus.OtherAccount => $"The GitHub CLI is signed in to another account; contributions show for {GitHubContributions.Login} only.",
                GitHubContributions.ReadStatus.Failed => "GitHub contributions could not be read. Quotalis tries again when you open Git.",
                _ => null,
            };
            Log.Info("git", "GitHub contributions", new { status = status.ToString(), days = calendar?.Days.Count ?? 0 });
            Post(() => _vms.Git.SetContributions(calendar, note));
        }
        finally
        {
            _contributionsBusy = false;
        }
    }

    private void UpdateGit()
    {
        _vms.Git.Update(_git.Status, _git.Repositories, DateTimeOffset.UtcNow);
        _island.EnsureValidTab();
    }

    private void OnAction(ModuleViewModel vm)
    {
        switch (vm.Status.State)
        {
            case ModuleState.Empty:
            case ModuleState.Disabled:
                OpenSettings?.Invoke("Modules");
                break;
            case ModuleState.Unavailable when vm.Module == ModuleId.Git:
                OpenLink("https://git-scm.com/download/win");
                break;
            case ModuleState.Error when vm.Module == ModuleId.Git:
                _ = _git.RefreshAsync();
                break;
        }
    }

    private static void OpenLink(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn("modules", "Link could not be opened", new { error = ex.GetType().Name });
        }
    }
}

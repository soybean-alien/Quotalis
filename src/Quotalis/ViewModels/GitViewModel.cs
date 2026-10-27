using Quotalis.Core.Formatting;
using Quotalis.Core.Git;
using Quotalis.Core.Modules;

namespace Quotalis.ViewModels;

/// <summary>One other repository in the Git panel's short list.</summary>
public sealed record GitRepoRow(string Root, string Name, string Branch, string Changes);

/// <summary>Local repository status: the active project first, a few recent ones below.</summary>
public sealed class GitViewModel : ModuleViewModel
{
    public const int MaxOtherRepositories = 3;

    private GitRepoStatus? _active;
    private IReadOnlyList<GitRepoRow> _others = [];
    private DateTimeOffset _now = DateTimeOffset.UtcNow;

    public GitViewModel()
        : base(ModuleId.Git)
    {
    }

    /// <summary>The user picked another repository.</summary>
    public event Action<string>? RepositorySelected;

    public GitRepoStatus? Active => _active;

    public string RepoName => _active?.Name ?? string.Empty;

    public string BranchText => _active is null ? string.Empty
        : _active.Branch ?? (_active.DetachedAt is { } at ? $"detached at {at}" : "detached");

    public string ChangesFigure => _active is null ? string.Empty : _active.IsClean ? "Clean" : _active.Changed.ToString(System.Globalization.CultureInfo.CurrentCulture);

    public string ChangesCaption => _active is null || _active.IsClean ? "no uncommitted changes"
        : _active.Changed == 1 ? "uncommitted change" : "uncommitted changes";

    public string ChangesDetail
    {
        get
        {
            if (_active is null || _active.IsClean)
            {
                return string.Empty;
            }

            var parts = new List<string>();
            if (_active.Staged > 0) parts.Add($"{_active.Staged} staged");
            if (_active.Modified > 0) parts.Add($"{_active.Modified} modified");
            if (_active.Untracked > 0) parts.Add($"{_active.Untracked} new");
            if (_active.Conflicts > 0) parts.Add(DisplayFormat.Count(_active.Conflicts, "conflict", "conflicts"));
            return string.Join(", ", parts);
        }
    }

    public string SyncText => _active switch
    {
        null => string.Empty,
        { HasUpstream: false } => "Not published",
        { Ahead: 0, Behind: 0 } => "Up to date",
        { Ahead: var a, Behind: 0 } => $"{a} to push",
        { Ahead: 0, Behind: var b } => $"{b} to pull",
        { Ahead: var a, Behind: var b } => $"{a} to push, {b} to pull",
    };

    public string SyncGlyph => _active switch
    {
        { HasUpstream: false } => "", // cloud
        { Ahead: 0, Behind: 0 } => "", // check
        _ => "", // sync
    };

    public string LastCommitSubject => _active?.LastCommitSubject ?? "No commits yet";

    public string LastCommitAgo => _active?.LastCommitAt is { } at ? DisplayFormat.Ago(_now - at) : string.Empty;

    /// <summary>The featured repository: its branch.</summary>
    public string CompactText => _active is null ? string.Empty : BranchText;

    /// <summary>"3 changes" or "clean".</summary>
    public string CompactDetail => _active is null ? string.Empty
        : _active.IsClean ? "clean" : DisplayFormat.Count(_active.Changed, "change", "changes");

    public IReadOnlyList<GitRepoRow> Others => _others;

    public bool HasOthers => _others.Count > 0;

    public string AccessibleSummary => _active is null ? "No repository"
        : $"{RepoName}, branch {BranchText}, {(_active.IsClean ? "clean" : $"{_active.Changed} {ChangesCaption}")}, {SyncText}";

    public void Update(ModuleStatus status, IReadOnlyList<GitRepoStatus> repositories, DateTimeOffset now)
    {
        _now = now;
        _active = repositories.FirstOrDefault();
        _others = repositories.Skip(1).Take(MaxOtherRepositories)
            .Select(r => new GitRepoRow(r.Root, r.Name, r.Branch ?? "detached", r.IsClean ? "clean" : DisplayFormat.Count(r.Changed, "change", "changes")))
            .ToList();
        SetStatus(status.IsReady && _active is null ? ModuleStatus.Loading : status);
        OnAllPropertiesChanged();
    }

    public void Select(string root) => RepositorySelected?.Invoke(root);

    // GitHub contributions (soybean-alien's profile calendar) -------------------------------------------

    public const int ContributionWeeks = 26;

    private IReadOnlyList<ContributionDay> _contributions = [];
    private string? _contributionsNote;
    private int _inspected = -1;
    private int _pinned = -1;

    /// <summary>Raised after new days arrived (the grid rebuilds its cells).</summary>
    public event Action? ContributionsChanged;

    public IReadOnlyList<ContributionDay> Contributions => _contributions;

    public bool HasContributions => _contributions.Count > 0;

    public bool HasContributionsNote => _contributions.Count == 0 && _contributionsNote is not null;

    public string ContributionsNote => _contributionsNote ?? string.Empty;

    public string ContributionsTitle => $"GitHub contributions · {GitHubContributions.Login}";

    public string ContributionsTotal => $"{_contributions.Sum(d => d.Count).ToString("N0", System.Globalization.CultureInfo.CurrentCulture)} in {ContributionWeeks} weeks";

    public string ContributionsSummary => DisplayFormat.Count(_contributions.Count(d => d.Count > 0), "active day", "active days");

    /// <summary>"Oct 24: 3 contributions" for the hovered or pinned day, else a hint.</summary>
    public string ContributionsReadout
    {
        get
        {
            var index = _inspected >= 0 ? _inspected : _pinned;
            if (index < 0 || index >= _contributions.Count)
            {
                return "Hover a day for its contributions, or click it to keep it here.";
            }

            var day = _contributions[index];
            var when = day.Date.ToString("MMM d", System.Globalization.CultureInfo.CurrentCulture);
            return $"{when}: {DisplayFormat.Count(day.Count, "contribution", "contributions")}{(index == _pinned && _inspected < 0 ? " · pinned" : string.Empty)}";
        }
    }

    public int PinnedContribution => _pinned;

    public void SetContributions(ContributionCalendar? calendar, string? note)
    {
        _contributions = calendar?.LastWeeks(ContributionWeeks) ?? [];
        _contributionsNote = note;
        _inspected = -1;
        _pinned = -1;
        OnPropertyChanged(nameof(Contributions));
        OnPropertyChanged(nameof(HasContributions));
        OnPropertyChanged(nameof(HasContributionsNote));
        OnPropertyChanged(nameof(ContributionsNote));
        OnPropertyChanged(nameof(ContributionsTotal));
        OnPropertyChanged(nameof(ContributionsSummary));
        OnPropertyChanged(nameof(ContributionsReadout));
        ContributionsChanged?.Invoke();
    }

    public void InspectContribution(int index)
    {
        _inspected = index;
        OnPropertyChanged(nameof(ContributionsReadout));
    }

    public void PinContribution(int index)
    {
        _pinned = _pinned == index ? -1 : index;
        OnPropertyChanged(nameof(PinnedContribution));
        OnPropertyChanged(nameof(ContributionsReadout));
    }

    protected override string EmptyTitle => "No repository yet";

    protected override string UnavailableTitle => "Git not found";

    protected override string UnavailableActionLabel => "Get Git";
}

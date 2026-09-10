using Harness.Core.Models;
using Harness.Workspace;

namespace Harness.App.ViewModels;

public sealed class GitHubWindowViewModel : ObservableObject
{
    private IReadOnlyList<GitHubRepositoryItem> _allRepositories = [];
    private GitHubRepositoryItem? _selectedRepository;
    private string _connectionStatus = "Checking GitHub connection…";
    private string _status = "Loading repository state…";
    private string _localState = "CHECKING";
    private string _localStateColor = "#8993A3";
    private string _branch = "—";
    private string _origin = "Not attached";
    private bool _isAuthenticated;
    private bool _isGitAvailable = true;
    private bool _isGitHubCliAvailable = true;
    private bool _isRepository;
    private bool _isBusy;

    public GitHubWindowViewModel(string workspaceName, string workspacePath)
    {
        WorkspaceName = workspaceName;
        WorkspacePath = workspacePath;
    }

    public string WorkspaceName { get; }
    public string WorkspacePath { get; }
    public BatchObservableCollection<GitHubRepositoryItem> Repositories { get; } = [];
    public string ConnectionStatus { get => _connectionStatus; private set => SetProperty(ref _connectionStatus, value); }
    public string Status { get => _status; set => SetProperty(ref _status, value); }
    public string LocalState { get => _localState; private set => SetProperty(ref _localState, value); }
    public string LocalStateColor { get => _localStateColor; private set => SetProperty(ref _localStateColor, value); }
    public string Branch { get => _branch; private set => SetProperty(ref _branch, value); }
    public string Origin { get => _origin; private set => SetProperty(ref _origin, value); }
    public bool IsAuthenticated { get => _isAuthenticated; private set => SetProperty(ref _isAuthenticated, value); }
    public bool IsGitAvailable { get => _isGitAvailable; private set => SetProperty(ref _isGitAvailable, value); }
    public bool IsGitHubCliAvailable { get => _isGitHubCliAvailable; private set => SetProperty(ref _isGitHubCliAvailable, value); }
    public bool NeedsToolSetup => !IsGitAvailable || !IsGitHubCliAvailable;
    public bool IsRepository { get => _isRepository; private set => SetProperty(ref _isRepository, value); }
    public bool IsBusy { get => _isBusy; private set => SetProperty(ref _isBusy, value); }
    public bool HasRepositories => Repositories.Count > 0;
    public bool HasSelection => SelectedRepository is not null;
    public string RepositoryCount => $"{Repositories.Count} REPOS";

    public GitHubRepositoryItem? SelectedRepository
    {
        get => _selectedRepository;
        set
        {
            if (!SetProperty(ref _selectedRepository, value)) return;
            RaisePropertyChanged(nameof(HasSelection));
        }
    }

    public void Apply(
        GitHubConnectionStatus connection,
        WorkingTreeSnapshot local,
        string? origin,
        IReadOnlyList<GitHubRepository> repositories)
    {
        IsAuthenticated = connection.IsAuthenticated;
        IsGitHubCliAvailable = connection.IsCliInstalled;
        IsGitAvailable = !(local.Error?.Contains("not installed", StringComparison.OrdinalIgnoreCase) ?? false)
            && !(local.Error?.Contains("not available on PATH", StringComparison.OrdinalIgnoreCase) ?? false);
        RaisePropertyChanged(nameof(NeedsToolSetup));
        ConnectionStatus = connection.Message;
        IsRepository = local.IsRepository;
        Branch = local.Branch ?? "—";
        Origin = string.IsNullOrWhiteSpace(origin) ? "Not attached" : origin;
        LocalState = !local.IsRepository
            ? "NOT INITIALIZED"
            : string.IsNullOrWhiteSpace(origin)
                ? "LOCAL ONLY"
                : "CONNECTED";
        LocalStateColor = !local.IsRepository
            ? "#8993A3"
            : string.IsNullOrWhiteSpace(origin)
                ? "#E2A84A"
                : "#65C7D0";
        _allRepositories = repositories.Select(GitHubRepositoryItem.FromModel).ToArray();
        ApplyFilter(string.Empty, "all");
        Status = connection.IsAuthenticated
            ? $"Loaded {repositories.Count} repositories"
            : connection.Message;
    }

    public void ApplyFilter(string? search, string visibility)
    {
        var selectedName = SelectedRepository?.NameWithOwner;
        var query = search?.Trim() ?? string.Empty;
        var filtered = _allRepositories.Where(repository =>
            (query.Length == 0
             || repository.NameWithOwner.Contains(query, StringComparison.OrdinalIgnoreCase)
             || repository.Description.Contains(query, StringComparison.OrdinalIgnoreCase))
            && (visibility switch
            {
                "public" => !repository.IsPrivate,
                "private" => repository.IsPrivate,
                _ => true
            }));
        Repositories.ReplaceAll(filtered);
        SelectedRepository = Repositories.FirstOrDefault(repository => repository.NameWithOwner == selectedName)
            ?? Repositories.FirstOrDefault();
        RaisePropertyChanged(nameof(HasRepositories));
        RaisePropertyChanged(nameof(RepositoryCount));
    }

    public void SetBusy(bool busy, string? status = null)
    {
        IsBusy = busy;
        if (status is not null) Status = status;
    }
}

public sealed record GitHubRepositoryItem(
    GitHubRepository Repository,
    string NameWithOwner,
    string Name,
    string Description,
    string Visibility,
    string Updated,
    string DefaultBranch,
    bool IsPrivate,
    string AccentColor)
{
    public static GitHubRepositoryItem FromModel(GitHubRepository repository) => new(
        repository,
        repository.NameWithOwner,
        repository.Name,
        string.IsNullOrWhiteSpace(repository.Description) ? "No description" : repository.Description,
        repository.IsPrivate ? "PRIVATE" : "PUBLIC",
        repository.UpdatedAt == DateTimeOffset.MinValue ? "Updated time not reported" : $"Updated {repository.UpdatedAt.ToLocalTime():g}",
        string.IsNullOrWhiteSpace(repository.DefaultBranch) ? "Default branch not reported" : repository.DefaultBranch,
        repository.IsPrivate,
        repository.IsPrivate ? "#E2A84A" : "#65C7D0");
}

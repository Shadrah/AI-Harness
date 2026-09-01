using Harness.Core.Models;

namespace Harness.App.ViewModels;

public sealed class SettingsWindowViewModel : ObservableObject
{
    private bool _restoreLastWorkspace;
    private bool _showActivityTrace;
    private bool _showUsageInspector;
    private bool _showContextInspector;
    private bool _showTurnDiffInspector;
    private string _personalInstructions;
    private string _status = "Ready";
    private string _githubStatus = "Checking GitHub connection…";
    private string _remoteUrl = "";
    private string _commitMessage = "";
    private string _repositoryName = "";
    private bool _privateRepository = true;
    private string _gitAuthorName;
    private string _gitAuthorEmail;
    private string _defaultGitBranch;

    public SettingsWindowViewModel(HarnessApplicationSettings settings, string workspacePath)
    {
        _restoreLastWorkspace = settings.RestoreLastWorkspace;
        _showActivityTrace = settings.ShowActivityTrace;
        _showUsageInspector = settings.ShowUsageInspector;
        _showContextInspector = settings.ShowContextInspector;
        _showTurnDiffInspector = settings.ShowTurnDiffInspector;
        _personalInstructions = settings.PersonalInstructions;
        _gitAuthorName = settings.GitAuthorName;
        _gitAuthorEmail = settings.GitAuthorEmail;
        _defaultGitBranch = string.IsNullOrWhiteSpace(settings.DefaultGitBranch) ? "main" : settings.DefaultGitBranch;
        WorkspacePath = workspacePath;
    }

    public string WorkspacePath { get; }
    public bool RestoreLastWorkspace { get => _restoreLastWorkspace; set => SetProperty(ref _restoreLastWorkspace, value); }
    public bool ShowActivityTrace { get => _showActivityTrace; set => SetProperty(ref _showActivityTrace, value); }
    public bool ShowUsageInspector { get => _showUsageInspector; set => SetProperty(ref _showUsageInspector, value); }
    public bool ShowContextInspector { get => _showContextInspector; set => SetProperty(ref _showContextInspector, value); }
    public bool ShowTurnDiffInspector { get => _showTurnDiffInspector; set => SetProperty(ref _showTurnDiffInspector, value); }
    public string PersonalInstructions { get => _personalInstructions; set => SetProperty(ref _personalInstructions, value); }
    public string Status { get => _status; set => SetProperty(ref _status, value); }
    public string GitHubStatus { get => _githubStatus; set => SetProperty(ref _githubStatus, value); }
    public string RemoteUrl { get => _remoteUrl; set => SetProperty(ref _remoteUrl, value); }
    public string CommitMessage { get => _commitMessage; set => SetProperty(ref _commitMessage, value); }
    public string RepositoryName { get => _repositoryName; set => SetProperty(ref _repositoryName, value); }
    public bool PrivateRepository { get => _privateRepository; set => SetProperty(ref _privateRepository, value); }
    public string GitAuthorName { get => _gitAuthorName; set => SetProperty(ref _gitAuthorName, value); }
    public string GitAuthorEmail { get => _gitAuthorEmail; set => SetProperty(ref _gitAuthorEmail, value); }
    public string DefaultGitBranch { get => _defaultGitBranch; set => SetProperty(ref _defaultGitBranch, value); }

    public HarnessApplicationSettings ToSettings() => new(
        RestoreLastWorkspace,
        ShowActivityTrace,
        ShowUsageInspector,
        ShowContextInspector,
        ShowTurnDiffInspector,
        PersonalInstructions?.Trim() ?? "",
        WorkspacePath,
        GitAuthorName?.Trim() ?? "",
        GitAuthorEmail?.Trim() ?? "",
        string.IsNullOrWhiteSpace(DefaultGitBranch) ? "main" : DefaultGitBranch.Trim());
}

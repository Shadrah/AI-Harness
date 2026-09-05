using System.Collections.ObjectModel;
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
    private PermissionModeOption _selectedPermissionMode;
    private string _skillSearchText = "";
    private string _selectedSkillCategory = "All";
    private string _skillCatalogStatus = "Loading the local catalog…";
    private string _skillResultSummary = "LOCAL CATALOG";
    private string _skillReportedSummary = "0 REPORTED";
    private string _skillSourceSummary = "0 SOURCES";
    private string _selectedSkillSource = "All sources";
    private string _selectedSkillStatus = "All status";
    private string _selectedSkillSort = "Recently indexed";
    private SkillCompatibilityOption _selectedSkillCompatibility = SkillCompatibilityOption.All;
    private SkillCatalogItem? _selectedSkill;
    private readonly HashSet<string> _storedHiddenModels;
    private readonly HashSet<string> _storedFavoriteModels;
    private readonly List<string> _storedModelOrder;
    private bool _promptForSubscriptionHandoff;
    private double _subscriptionHandoffThresholdPercent;
    private string? _activeCodexIdentityId;

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
        _selectedPermissionMode = PermissionModeOption.Resolve(settings.PermissionMode);
        _storedHiddenModels = (settings.HiddenModelIds ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _storedFavoriteModels = (settings.FavoriteModelIds ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _storedModelOrder = (settings.ModelOrder ?? []).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        _activeCodexIdentityId = settings.ActiveCodexIdentityId;
        _promptForSubscriptionHandoff = settings.PromptForSubscriptionHandoff;
        _subscriptionHandoffThresholdPercent = Math.Clamp(settings.SubscriptionHandoffThresholdPercent, 1, 25);
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
    public IReadOnlyList<PermissionModeOption> PermissionModes { get; } = PermissionModeOption.All;
    public PermissionModeOption SelectedPermissionMode { get => _selectedPermissionMode; set => SetProperty(ref _selectedPermissionMode, value); }
    public bool PromptForSubscriptionHandoff { get => _promptForSubscriptionHandoff; set => SetProperty(ref _promptForSubscriptionHandoff, value); }
    public double SubscriptionHandoffThresholdPercent { get => _subscriptionHandoffThresholdPercent; set => SetProperty(ref _subscriptionHandoffThresholdPercent, Math.Clamp(value, 1, 25)); }
    public string? ActiveCodexIdentityId { get => _activeCodexIdentityId; set => SetProperty(ref _activeCodexIdentityId, value); }
    public BatchObservableCollection<SkillCatalogItem> Skills { get; } = [];
    public BatchObservableCollection<ModelPreferenceItem> ModelPreferences { get; } = [];
    public bool HasModelPreferences => ModelPreferences.Count > 0;
    public BatchObservableCollection<SkillSourceItem> SkillSourceLedger { get; } = [];
    public BatchObservableCollection<string> SkillSources { get; } = ["All sources"];
    public ObservableCollection<SkillCompatibilityOption> SkillCompatibilityOptions { get; } = [SkillCompatibilityOption.All];
    public IReadOnlyList<string> SkillCategories { get; } =
    [
        "All", "Game development", "Frontend", "Backend", "DevOps", "Testing",
        "Security", "Data", "Documents", "Media", "Research", "Productivity", "Other"
    ];
    public string SkillSearchText { get => _skillSearchText; set => SetProperty(ref _skillSearchText, value); }
    public string SelectedSkillCategory { get => _selectedSkillCategory; set => SetProperty(ref _selectedSkillCategory, value); }
    public string SkillCatalogStatus { get => _skillCatalogStatus; set => SetProperty(ref _skillCatalogStatus, value); }
    public string SkillResultSummary { get => _skillResultSummary; set => SetProperty(ref _skillResultSummary, value); }
    public string SkillReportedSummary { get => _skillReportedSummary; set => SetProperty(ref _skillReportedSummary, value); }
    public string SkillSourceSummary { get => _skillSourceSummary; set => SetProperty(ref _skillSourceSummary, value); }
    public IReadOnlyList<string> SkillStatuses { get; } = ["All status", "Available", "Installed"];
    public IReadOnlyList<string> SkillSorts { get; } = ["Recently indexed", "Model compatibility", "Name", "Source"];
    public string SelectedSkillSource { get => _selectedSkillSource; set => SetProperty(ref _selectedSkillSource, value); }
    public string SelectedSkillStatus { get => _selectedSkillStatus; set => SetProperty(ref _selectedSkillStatus, value); }
    public string SelectedSkillSort { get => _selectedSkillSort; set => SetProperty(ref _selectedSkillSort, value); }
    public SkillCompatibilityOption SelectedSkillCompatibility { get => _selectedSkillCompatibility; set => SetProperty(ref _selectedSkillCompatibility, value); }
    public SkillCatalogItem? SelectedSkill
    {
        get => _selectedSkill;
        set
        {
            if (!SetProperty(ref _selectedSkill, value)) return;
            RaisePropertyChanged(nameof(HasSelectedSkill));
            RaisePropertyChanged(nameof(CanInstallSelectedSkill));
        }
    }
    public bool HasSelectedSkill => SelectedSkill is not null;
    public bool CanInstallSelectedSkill => SelectedSkill is { IsInstalled: false };

    public void SetCompatibilityTargets(IEnumerable<SkillCompatibilityOption> targets)
    {
        var selectedId = SelectedSkillCompatibility.Id;
        SkillCompatibilityOptions.Clear();
        SkillCompatibilityOptions.Add(SkillCompatibilityOption.All);
        foreach (var target in targets
                     .GroupBy(target => target.Id, StringComparer.OrdinalIgnoreCase)
                     .Select(group => group.First()))
            SkillCompatibilityOptions.Add(target);
        SelectedSkillCompatibility = SkillCompatibilityOptions.FirstOrDefault(target => target.Id == selectedId)
            ?? SkillCompatibilityOption.All;
    }

    public void SetModelPreferences(IEnumerable<SkillCompatibilityOption> targets)
    {
        var order = _storedModelOrder.Select((key, index) => (key, index))
            .ToDictionary(item => item.key, item => item.index, StringComparer.OrdinalIgnoreCase);
        var preferences = targets
            .Where(target => !target.IsAll)
            .GroupBy(target => MainWindowViewModel.ModelPreferenceKey(target.ProviderId, target.ModelId), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Select(target =>
            {
                var key = MainWindowViewModel.ModelPreferenceKey(target.ProviderId, target.ModelId);
                return new ModelPreferenceItem(
                    key,
                    target.ProviderId,
                    target.ModelId,
                    target.DisplayName,
                    !_storedHiddenModels.Contains(key),
                    _storedFavoriteModels.Contains(key));
            })
            .OrderByDescending(item => item.IsFavorite)
            .ThenBy(item => order.GetValueOrDefault(item.Key, int.MaxValue))
            .ThenBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        ModelPreferences.ReplaceAll(preferences);
        RaisePropertyChanged(nameof(HasModelPreferences));
    }

    public void MoveModel(ModelPreferenceItem item, int direction)
    {
        var index = ModelPreferences.IndexOf(item);
        var destination = index + direction;
        if (index < 0 || destination < 0 || destination >= ModelPreferences.Count) return;
        if (ModelPreferences[destination].IsFavorite != item.IsFavorite) return;
        ModelPreferences.Move(index, destination);
    }

    public void ReplaceSkills(
        IEnumerable<SkillCatalogEntry> entries,
        IReadOnlyList<InstalledSkill> installed,
        IReadOnlyList<SkillCatalogSource>? sources = null)
    {
        var selectedId = SelectedSkill?.Entry.Id;
        var installedIds = installed.Where(item => item.Enabled).Select(item => item.CatalogId).ToHashSet(StringComparer.Ordinal);
        var filtered = entries.Select(entry => new SkillCatalogItem(
            entry,
            installedIds.Contains(entry.Id),
            SelectedSkillCompatibility));
        if (!string.Equals(SelectedSkillSource, "All sources", StringComparison.OrdinalIgnoreCase))
            filtered = filtered.Where(item => item.Repository.Equals(SelectedSkillSource, StringComparison.OrdinalIgnoreCase));
        if (string.Equals(SelectedSkillStatus, "Installed", StringComparison.OrdinalIgnoreCase))
            filtered = filtered.Where(item => item.IsInstalled);
        else if (string.Equals(SelectedSkillStatus, "Available", StringComparison.OrdinalIgnoreCase))
            filtered = filtered.Where(item => !item.IsInstalled);
        if (!SelectedSkillCompatibility.IsAll)
            filtered = filtered.Where(item => item.IsCompatibleWithSelectedModel);
        filtered = SelectedSkillSort switch
        {
            "Model compatibility" => filtered.OrderByDescending(item => item.IsCompatibleWithSelectedModel)
                .ThenBy(item => item.CompatibilityRank)
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase),
            "Name" => filtered.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase),
            "Source" => filtered.OrderBy(item => item.Repository, StringComparer.OrdinalIgnoreCase).ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase),
            _ => filtered.OrderByDescending(item => item.Entry.RefreshedAt).ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
        };
        Skills.ReplaceAll(filtered);
        SelectedSkill = Skills.FirstOrDefault(item => item.Entry.Id == selectedId) ?? Skills.FirstOrDefault();
        SkillResultSummary = $"{Skills.Count:N0} MATCH{(Skills.Count == 1 ? string.Empty : "ES")}";
        if (sources is not null)
        {
            SkillSourceLedger.ReplaceAll(sources.Select(source => new SkillSourceItem(source)));
            var currentSource = SelectedSkillSource;
            SkillSources.ReplaceAll(new[] { "All sources" }.Concat(
                sources.Select(source => source.Repository).Distinct(StringComparer.OrdinalIgnoreCase)));
            SelectedSkillSource = SkillSources.Contains(currentSource) ? currentSource : "All sources";
            SkillReportedSummary = $"{sources.Sum(source => (long)source.ReportedSkillCount):N0} REPORTED";
            SkillSourceSummary = $"{sources.Count:N0} SOURCE{(sources.Count == 1 ? string.Empty : "S")} · {sources.Sum(source => (long)source.IndexedSkillCount):N0} CATALOGED · {sources.Sum(source => (long)source.DescribedSkillCount):N0} DESCRIBED";
        }
        SkillCatalogStatus = Skills.Count == 0
            ? "No cached skills match. Search GitHub to discover more."
            : "Showing cached descriptions from indexed sources. Search narrows GitHub directly; packages download only after Install is confirmed.";
    }

    public HarnessApplicationSettings ToSettings()
    {
        if (ModelPreferences.Count > 0 && ModelPreferences.All(item => !item.IsEnabled))
            throw new InvalidOperationException("Keep at least one connected model visible. Sign out of a provider to remove its account instead.");
        var known = ModelPreferences.Select(item => item.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hidden = _storedHiddenModels.Where(key => !known.Contains(key))
            .Concat(ModelPreferences.Where(item => !item.IsEnabled).Select(item => item.Key))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var favorites = _storedFavoriteModels.Where(key => !known.Contains(key))
            .Concat(ModelPreferences.Where(item => item.IsFavorite).Select(item => item.Key))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var order = _storedModelOrder.Where(key => !known.Contains(key))
            .Concat(ModelPreferences.Select(item => item.Key))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return new HarnessApplicationSettings(
            RestoreLastWorkspace,
            ShowActivityTrace,
            ShowUsageInspector,
            ShowContextInspector,
            ShowTurnDiffInspector,
            PersonalInstructions?.Trim() ?? "",
            WorkspacePath,
            GitAuthorName?.Trim() ?? "",
            GitAuthorEmail?.Trim() ?? "",
            string.IsNullOrWhiteSpace(DefaultGitBranch) ? "main" : DefaultGitBranch.Trim(),
            SelectedPermissionMode.Id,
            hidden,
            favorites,
            order,
            ActiveCodexIdentityId,
            PromptForSubscriptionHandoff,
            SubscriptionHandoffThresholdPercent);
    }
}

public sealed class ModelPreferenceItem : ObservableObject
{
    private bool _isEnabled;
    private bool _isFavorite;

    public ModelPreferenceItem(
        string key,
        string providerId,
        string modelId,
        string displayName,
        bool isEnabled,
        bool isFavorite)
    {
        Key = key;
        ProviderId = providerId;
        ModelId = modelId;
        DisplayName = displayName;
        _isEnabled = isEnabled;
        _isFavorite = isFavorite;
    }

    public string Key { get; }
    public string ProviderId { get; }
    public string ModelId { get; }
    public string DisplayName { get; }
    public string ProviderLabel => ProviderId.Replace('-', ' ').Replace('_', ' ').ToUpperInvariant();
    public bool IsEnabled { get => _isEnabled; set => SetProperty(ref _isEnabled, value); }
    public bool IsFavorite
    {
        get => _isFavorite;
        set
        {
            if (SetProperty(ref _isFavorite, value)) RaisePropertyChanged(nameof(FavoriteGlyph));
        }
    }
    public string FavoriteGlyph => IsFavorite ? "★" : "☆";
}

public sealed record SkillCatalogItem(
    SkillCatalogEntry Entry,
    bool IsInstalled,
    SkillCompatibilityOption? SelectedCompatibility = null)
{
    public bool CanInstall => !IsInstalled;
    public string Name => Entry.Name;
    public string Description => Entry.Description;
    public string Category => Entry.Category.ToUpperInvariant();
    public string Repository => Entry.Repository;
    public string SourcePath => Entry.SkillPath;
    public string Compatibility => Entry.Compatibility;
    public bool IsCompatibleWithSelectedModel => SelectedCompatibility is null
        || SelectedCompatibility.IsAll
        || SupportsProvider(Entry.Compatibility, SelectedCompatibility.ProviderId);
    public int CompatibilityRank => Entry.Compatibility switch
    {
        "Portable Agent Skill" => 0,
        "Codex extension" or "Claude Code extension" => 1,
        "Mixed provider extensions" => 2,
        _ => 3
    };
    public string CompatibilityStatus => SelectedCompatibility is null || SelectedCompatibility.IsAll
        ? Entry.Compatibility
        : IsCompatibleWithSelectedModel
            ? $"Compatible · {SelectedCompatibility.ModelId}"
            : $"Not compatible · {SelectedCompatibility.ModelId}";
    public string TrustState => Entry.TrustState;
    public string SourceRevision => Entry.SourceRevision.Length > 10
        ? Entry.SourceRevision[..10]
        : Entry.SourceRevision;
    public string InstallState => IsInstalled ? "INSTALLED" : "AVAILABLE";
    public string InstallColor => IsInstalled ? "#65C7D0" : "#8993A3";
    public string SourceLine => $"{Repository}  ·  {SourceRevision}";
    public string SpineColor => Entry.Category switch
    {
        "Game development" => "#65C7D0",
        "Security" => "#E2A84A",
        "Media" => "#A88AD9",
        "Data" => "#7DCB91",
        _ => "#536071"
    };

    private static bool SupportsProvider(string compatibility, string providerId)
    {
        if (compatibility.Equals("Portable Agent Skill", StringComparison.OrdinalIgnoreCase)) return true;
        if (compatibility.Equals("Codex extension", StringComparison.OrdinalIgnoreCase))
            return providerId.Contains("openai", StringComparison.OrdinalIgnoreCase)
                || providerId.Contains("codex", StringComparison.OrdinalIgnoreCase);
        if (compatibility.Equals("Claude Code extension", StringComparison.OrdinalIgnoreCase))
            return providerId.Contains("anthropic", StringComparison.OrdinalIgnoreCase)
                || providerId.Contains("claude", StringComparison.OrdinalIgnoreCase);
        return false;
    }
}

public sealed record SkillCompatibilityOption(
    string Id,
    string ProviderId,
    string ModelId,
    string DisplayName,
    bool IsAll = false)
{
    public static SkillCompatibilityOption All { get; } = new(
        "all", "", "", "All connected models", true);

    public override string ToString() => DisplayName;
}

public sealed record SkillSourceItem(SkillCatalogSource Source)
{
    public string Repository => Source.Repository;
    public string Count => $"{Source.ReportedSkillCount:N0} REPORTED";
    public string Indexed => $"{Source.IndexedSkillCount:N0} CATALOGED · {Source.DescribedSkillCount:N0} DESCRIBED";
    public string State => Source.IndexState;
    public string StateColor => Source.IndexState.StartsWith("COMPLETE", StringComparison.OrdinalIgnoreCase)
        ? "#65C7D0"
        : "#E2A84A";
}

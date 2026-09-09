using System.Collections.ObjectModel;
using Harness.Core.Models;
using Harness.Workspace;

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
    private InstalledSkillItem? _selectedSkillInstallation;
    private IReadOnlyList<InstalledSkill> _installedSkills = [];
    private IReadOnlyList<SkillInstallTarget> _skillInstallTargets = [];
    private readonly HashSet<string> _storedHiddenModels;
    private readonly HashSet<string> _storedFavoriteModels;
    private readonly List<string> _storedModelOrder;
    private bool _promptForSubscriptionHandoff;
    private double _subscriptionHandoffThresholdPercent;
    private string? _activeCodexIdentityId;
    private string? _activeClaudeIdentityId;
    private string _subscriptionHandoffMode;

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
        _activeClaudeIdentityId = settings.ActiveClaudeIdentityId;
        _promptForSubscriptionHandoff = settings.PromptForSubscriptionHandoff;
        _subscriptionHandoffMode = settings.SubscriptionHandoffMode is "manual" or "suggest" or "automatic"
            ? settings.SubscriptionHandoffMode
            : settings.PromptForSubscriptionHandoff ? "suggest" : "manual";
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
    public string? ActiveClaudeIdentityId { get => _activeClaudeIdentityId; set => SetProperty(ref _activeClaudeIdentityId, value); }
    public IReadOnlyList<string> SubscriptionHandoffModes { get; } = ["manual", "suggest", "automatic"];
    public string SubscriptionHandoffMode
    {
        get => _subscriptionHandoffMode;
        set
        {
            var normalized = value is "manual" or "suggest" or "automatic" ? value : "suggest";
            if (!SetProperty(ref _subscriptionHandoffMode, normalized)) return;
            PromptForSubscriptionHandoff = normalized != "manual";
        }
    }
    public BatchObservableCollection<SkillCatalogItem> Skills { get; } = [];
    public BatchObservableCollection<InstalledSkillItem> SelectedSkillInstallations { get; } = [];
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
            RaisePropertyChanged(nameof(SkillInstallButtonLabel));
            RefreshSelectedSkillInstallations();
        }
    }
    public bool HasSelectedSkill => SelectedSkill is not null;
    public bool CanInstallSelectedSkill => SelectedSkill is not null;
    public string SkillInstallButtonLabel => SelectedSkill?.IsInstalled == true ? "ADD TARGET…" : "INSTALL…";
    public InstalledSkillItem? SelectedSkillInstallation
    {
        get => _selectedSkillInstallation;
        set
        {
            if (!SetProperty(ref _selectedSkillInstallation, value)) return;
            RaisePropertyChanged(nameof(HasSelectedSkillInstallation));
            RaisePropertyChanged(nameof(CanUpdateSelectedSkill));
            RaisePropertyChanged(nameof(SkillEnableButtonLabel));
        }
    }
    public bool HasSelectedSkillInstallation => SelectedSkillInstallation is not null;
    public bool CanUpdateSelectedSkill => SelectedSkillInstallation?.HasUpdate == true;
    public string SkillEnableButtonLabel => SelectedSkillInstallation?.Installation.Enabled == true ? "DISABLE" : "ENABLE";

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
        var current = ModelPreferences.ToDictionary(item => item.Key, StringComparer.OrdinalIgnoreCase);
        var currentOrder = ModelPreferences.Select((item, index) => (item.Key, index))
            .ToDictionary(item => item.Key, item => item.index, StringComparer.OrdinalIgnoreCase);
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
                    current.TryGetValue(key, out var existing)
                        ? existing.IsEnabled : !_storedHiddenModels.Contains(key),
                    current.TryGetValue(key, out existing)
                        ? existing.IsFavorite : _storedFavoriteModels.Contains(key));
            })
            .OrderByDescending(item => item.IsFavorite)
            .ThenBy(item => currentOrder.GetValueOrDefault(item.Key, int.MaxValue))
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
        IReadOnlyList<SkillCatalogSource>? sources = null,
        IReadOnlyList<SkillInstallTarget>? installTargets = null)
    {
        _installedSkills = installed;
        if (installTargets is not null) _skillInstallTargets = installTargets;
        var selectedId = SelectedSkill?.Entry.Id;
        var relevantInstallations = installed.ToArray();
        var filtered = entries.Select(entry => new SkillCatalogItem(
            entry,
            relevantInstallations.Any(item => item.CatalogId.Equals(entry.Id, StringComparison.Ordinal)
                && (SelectedSkillCompatibility.IsAll
                    || (item.ProviderId.Equals(SelectedSkillCompatibility.ProviderId, StringComparison.Ordinal)
                        && (string.IsNullOrWhiteSpace(item.ModelId)
                            || item.ModelId.Equals(SelectedSkillCompatibility.ModelId, StringComparison.Ordinal))
                        && (!item.Scope.Equals("WORKSPACE", StringComparison.OrdinalIgnoreCase)
                            || PathsEqual(item.WorkspacePath, WorkspacePath))))),
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
        var nextSelectedSkill = Skills.FirstOrDefault(item => item.Entry.Id == selectedId) ?? Skills.FirstOrDefault();
        SelectedSkill = null;
        SelectedSkill = nextSelectedSkill;
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

    private void RefreshSelectedSkillInstallations()
    {
        var selectedInstallationId = SelectedSkillInstallation?.Installation.Id;
        if (SelectedSkill is null)
        {
            SelectedSkillInstallations.Clear();
            SelectedSkillInstallation = null;
            return;
        }
        var currentRevision = SelectedSkill.Entry.SourceRevision;
        var items = _installedSkills
            .Where(item => item.CatalogId.Equals(SelectedSkill.Entry.Id, StringComparison.Ordinal))
            .Where(item => !item.Scope.Equals("WORKSPACE", StringComparison.OrdinalIgnoreCase)
                           || PathsEqual(item.WorkspacePath, WorkspacePath))
            .Select(item => new InstalledSkillItem(item, ResolveTargetName(item), currentRevision))
            .OrderByDescending(item => item.Installation.Enabled)
            .ThenBy(item => item.TargetDisplay, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        SelectedSkillInstallations.ReplaceAll(items);
        SelectedSkillInstallation = items.FirstOrDefault(item => item.Installation.Id == selectedInstallationId)
            ?? items.FirstOrDefault();
    }

    private string ResolveTargetName(InstalledSkill installed)
    {
        var target = _skillInstallTargets.FirstOrDefault(item =>
            item.ProviderId.Equals(installed.ProviderId, StringComparison.Ordinal)
            && string.Equals(item.ModelId, installed.ModelId, StringComparison.Ordinal));
        if (target is not null) return target.DisplayName;
        return string.IsNullOrWhiteSpace(installed.ModelId)
            ? $"{installed.ProviderId} · all compatible models"
            : $"{installed.ProviderId} · {installed.ModelId}";
    }

    private static bool PathsEqual(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        try
        {
            return string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
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
            RestoreLastWorkspace: RestoreLastWorkspace,
            ShowActivityTrace: ShowActivityTrace,
            ShowUsageInspector: ShowUsageInspector,
            ShowContextInspector: ShowContextInspector,
            ShowTurnDiffInspector: ShowTurnDiffInspector,
            PersonalInstructions: PersonalInstructions?.Trim() ?? "",
            LastWorkspacePath: WorkspacePath,
            GitAuthorName: GitAuthorName?.Trim() ?? "",
            GitAuthorEmail: GitAuthorEmail?.Trim() ?? "",
            DefaultGitBranch: string.IsNullOrWhiteSpace(DefaultGitBranch) ? "main" : DefaultGitBranch.Trim(),
            PermissionMode: SelectedPermissionMode.Id,
            HiddenModelIds: hidden,
            FavoriteModelIds: favorites,
            ModelOrder: order,
            ActiveCodexIdentityId: ActiveCodexIdentityId,
            PromptForSubscriptionHandoff: SubscriptionHandoffMode != "manual",
            SubscriptionHandoffThresholdPercent: SubscriptionHandoffThresholdPercent,
            ActiveClaudeIdentityId: ActiveClaudeIdentityId,
            SubscriptionHandoffMode: SubscriptionHandoffMode);
    }
}

public sealed class InstalledSkillItem : ObservableObject
{
    private string _integrityStatus = "Integrity not checked";

    public InstalledSkillItem(InstalledSkill installation, string targetDisplay, string currentRevision)
    {
        Installation = installation;
        TargetDisplay = targetDisplay;
        CurrentRevision = currentRevision;
    }

    public InstalledSkill Installation { get; }
    public string TargetDisplay { get; }
    public string CurrentRevision { get; }
    public bool HasUpdate => !Installation.SourceRevision.Equals(CurrentRevision, StringComparison.Ordinal);
    public string ScopeStatus => $"{Installation.Scope} · {(Installation.Enabled ? "ENABLED" : "DISABLED")}";
    public string RevisionStatus => $"Installed {Short(Installation.SourceRevision)}"
        + (HasUpdate ? $" · Update {Short(CurrentRevision)} available" : " · Current catalog revision");
    public string IntegrityStatus { get => _integrityStatus; private set => SetProperty(ref _integrityStatus, value); }

    public void SetIntegrity(ManagedSkillIntegrity integrity) => IntegrityStatus = integrity switch
    {
        ManagedSkillIntegrity.Unchanged => "Provider copy matches its installed baseline",
        ManagedSkillIntegrity.Modified => "Provider copy has local modifications",
        _ => "Integrity baseline unavailable; changes will require confirmation"
    };

    public override string ToString() => TargetDisplay;
    private static string Short(string value) => value[..Math.Min(10, value.Length)];
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
    public bool CanInstall => true;
    public string Name => Entry.Name;
    public string Description => Entry.Description;
    public string Category => Entry.Category.ToUpperInvariant();
    public string Repository => Entry.Repository;
    public string SourcePath => Entry.SkillPath;
    public string Compatibility => Entry.Compatibility;
    public bool IsCompatibleWithSelectedModel => SelectedCompatibility is null
        || SelectedCompatibility.IsAll
        || SupportsProvider(Entry.Compatibility, SelectedCompatibility.CompatibilityId);
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
    public string InstallState => IsInstalled ? "ADD TARGET" : "AVAILABLE";
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
            return providerId.Equals("openai-codex", StringComparison.OrdinalIgnoreCase);
        if (compatibility.Equals("Claude Code extension", StringComparison.OrdinalIgnoreCase))
            return providerId.Equals("anthropic-claude", StringComparison.OrdinalIgnoreCase);
        return false;
    }
}

public sealed record SkillCompatibilityOption(
    string Id,
    string ProviderId,
    string ModelId,
    string DisplayName,
    bool IsAll = false,
    string? CompatibilityProviderId = null)
{
    public static SkillCompatibilityOption All { get; } = new(
        "all", "", "", "All connected models", true);

    public string CompatibilityId => CompatibilityProviderId ?? ProviderId;

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

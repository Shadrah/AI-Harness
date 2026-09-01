using System.Collections.ObjectModel;
using System.Text.Json;
using Harness.Core.Models;
using Harness.Workspace;

namespace Harness.App.ViewModels;

public sealed class MainWindowViewModel : ObservableObject
{
    private ModelOption? _selectedModel;
    private ReasoningLevelOption? _selectedReasoningLevel;
    private ServiceTierOption? _selectedServiceTier;
    private string _promptText = string.Empty;
    private string _connectionStatus = "CONNECTING";
    private string _usageStatus = "WAITING FOR RUNTIME";
    private string _usageDetail = "Usage has not been reported.";
    private string? _imagePath;
    private bool _isRunning;
    private long? _totalTokens;
    private long? _contextWindowTokens;
    private bool _isCompactingContext;
    private long? _lastCompactionTokenTotal;
    private readonly Dictionary<string, ChatMessageItem> _streamingAssistantMessages = [];
    private string _workspacePath;
    private readonly Dictionary<string, ExecutionItem> _executionItems = [];
    private string _turnDiff = string.Empty;
    private bool _isInstallingRuntime;
    private bool _showRuntimeSetup;
    private bool _showAuthenticationAction;
    private string _effectiveReasoning = "NOT CONFIRMED";
    private string _effectiveServiceTier = "DEFAULT";
    private string? _activeSessionId;
    private TaskItem? _selectedTask;
    private string? _repositoryRoot;
    private string _workingTreeStatus = "CHECKING";
    private string _branchStatus = "GIT —";
    private bool _isRepository;
    private bool _hasRepositoryRemote;
    private bool _isGitHubConnected;
    private bool _showActivityTrace = true;
    private bool _showUsageInspector = true;
    private bool _showContextInspector = true;
    private bool _showTurnDiffInspector = true;
    private ExecutionItem? _selectedExecutionItem;
    private WorkspaceItem? _selectedWorkspace;

    public MainWindowViewModel(bool previewData = false)
    {
        _workspacePath = ResolveInitialWorkspace();
        Workspaces.Add(new WorkspaceItem(
            string.Empty,
            new DirectoryInfo(WorkspacePath).Name,
            WorkspacePath,
            "Transparent",
            "#65C7D0"));
        if (previewData)
        {
            SeedPreviewData();
            return;
        }

        Activity.Add(ActivityItem.Now(
            "SYSTEM",
            "Connecting to the local Codex runtime",
            "#8993A3",
            true));
    }

    public string WorkspacePath => _workspacePath;
    public string WorkspaceName => new DirectoryInfo(WorkspacePath).Name;
    public string WorkspaceSummary => WorkspacePath;
    public string? ImagePath => _imagePath;
    public ObservableCollection<ModelOption> Models { get; } = [];
    public ObservableCollection<CapabilityItem> Capabilities { get; } = [];
    public ObservableCollection<UsageWindowItem> UsageWindows { get; } = [];
    public ObservableCollection<WorkspaceItem> Workspaces { get; } = [];
    public ObservableCollection<TaskItem> Tasks { get; } = [];
    public ObservableCollection<ActivityItem> Activity { get; } = [];
    public ObservableCollection<ChatMessageItem> Messages { get; } = [];
    public ObservableCollection<FileChangeItem> ChangedFiles { get; } = [];
    public ObservableCollection<ContextFileItem> ContextFiles { get; } = [];
    public ObservableCollection<WorkingTreeFileItem> WorkingTreeFiles { get; } = [];
    public ObservableCollection<ExecutionItem> ExecutionItems { get; } = [];
    public event EventHandler<MessagePersistenceRequestedEventArgs>? MessagePersistenceRequested;
    public ExecutionItem? SelectedExecutionItem
    {
        get => _selectedExecutionItem;
        set => SetProperty(ref _selectedExecutionItem, value);
    }
    public WorkspaceItem? SelectedWorkspace
    {
        get => _selectedWorkspace;
        set => SetProperty(ref _selectedWorkspace, value);
    }
    public string ExecutionStatus => ExecutionItems.Count == 0
        ? "NO ACTIVITY"
        : $"{ExecutionItems.Count} EVENT{(ExecutionItems.Count == 1 ? string.Empty : "S")}";

    public TaskItem? SelectedTask
    {
        get => _selectedTask;
        set
        {
            if (SetProperty(ref _selectedTask, value))
            {
                RaisePropertyChanged(nameof(CurrentSessionTitle));
            }
        }
    }

    public string CurrentSessionTitle => SelectedTask?.Title ?? "New session";
    public bool HasContextFiles => ContextFiles.Count > 0;
    public string ContextStatus => HasContextFiles
        ? $"{ContextFiles.Count} ATTACHED"
        : "NOT LOADED";
    public string? RepositoryRoot => _repositoryRoot;
    public string WorkingTreeStatus
    {
        get => _workingTreeStatus;
        private set => SetProperty(ref _workingTreeStatus, value);
    }
    public string BranchStatus
    {
        get => _branchStatus;
        private set => SetProperty(ref _branchStatus, value);
    }
    public bool IsRepository
    {
        get => _isRepository;
        private set
        {
            if (SetProperty(ref _isRepository, value))
            {
                RaisePropertyChanged(nameof(RepositoryDockLabel));
                RaisePropertyChanged(nameof(CanUseRepositoryActions));
            }
        }
    }
    public bool HasRepositoryRemote
    {
        get => _hasRepositoryRemote;
        private set
        {
            if (SetProperty(ref _hasRepositoryRemote, value))
            {
                RaisePropertyChanged(nameof(RepositoryDockLabel));
                RaisePropertyChanged(nameof(CanUseRemoteActions));
            }
        }
    }
    public string RepositoryDockLabel => !IsRepository
        ? "SET UP GIT"
        : !HasRepositoryRemote ? $"{BranchStatus} · ATTACH REMOTE"
        : !IsGitHubConnected ? $"{BranchStatus} · SIGN IN TO GITHUB"
        : BranchStatus;
    public bool CanUseRepositoryActions => IsRepository && !IsRunning;
    public bool CanUseRemoteActions => IsRepository && HasRepositoryRemote && IsGitHubConnected && !IsRunning;
    public bool IsGitHubConnected
    {
        get => _isGitHubConnected;
        private set
        {
            if (SetProperty(ref _isGitHubConnected, value))
            {
                RaisePropertyChanged(nameof(RepositoryDockLabel));
                RaisePropertyChanged(nameof(CanUseRemoteActions));
            }
        }
    }
    public bool ShowActivityTrace { get => _showActivityTrace; private set => SetProperty(ref _showActivityTrace, value); }
    public bool ShowUsageInspector { get => _showUsageInspector; private set => SetProperty(ref _showUsageInspector, value); }
    public bool ShowContextInspector { get => _showContextInspector; private set => SetProperty(ref _showContextInspector, value); }
    public bool ShowTurnDiffInspector { get => _showTurnDiffInspector; private set => SetProperty(ref _showTurnDiffInspector, value); }

    public void ApplyApplicationSettings(HarnessApplicationSettings settings)
    {
        ShowActivityTrace = settings.ShowActivityTrace;
        ShowUsageInspector = settings.ShowUsageInspector;
        ShowContextInspector = settings.ShowContextInspector;
        ShowTurnDiffInspector = settings.ShowTurnDiffInspector;
    }

    public ModelOption? SelectedModel
    {
        get => _selectedModel;
        set
        {
            if (!SetProperty(ref _selectedModel, value))
            {
                return;
            }

            RaisePropertyChanged(nameof(ActiveModelName));
            RaisePropertyChanged(nameof(ActiveProviderLabel));
            RaisePropertyChanged(nameof(SupportsVision));
            RaisePropertyChanged(nameof(VisionStatus));
            RaisePropertyChanged(nameof(ReasoningLevels));
            RaisePropertyChanged(nameof(ServiceTiers));
            RaisePropertyChanged(nameof(HasServiceTiers));
            SelectedReasoningLevel = value is null ? null : GetDefaultReasoningLevel(value);
            SelectedServiceTier = value is null ? null : GetDefaultServiceTier(value);
            RefreshCapabilities();
            RaisePropertyChanged(nameof(CanSend));
        }
    }

    public ReasoningLevelOption? SelectedReasoningLevel
    {
        get => _selectedReasoningLevel;
        set
        {
            if (SetProperty(ref _selectedReasoningLevel, value))
            {
                _effectiveReasoning = "PENDING NEXT TURN";
                RaisePropertyChanged(nameof(ModelSettingsStatus));
            }
        }
    }

    public ServiceTierOption? SelectedServiceTier
    {
        get => _selectedServiceTier;
        set
        {
            if (SetProperty(ref _selectedServiceTier, value))
            {
                _effectiveServiceTier = "PENDING NEXT TURN";
                RaisePropertyChanged(nameof(ModelSettingsStatus));
            }
        }
    }

    public string PromptText
    {
        get => _promptText;
        set
        {
            if (SetProperty(ref _promptText, value))
            {
                RaisePropertyChanged(nameof(CanSend));
            }
        }
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (SetProperty(ref _isRunning, value))
            {
                RaisePropertyChanged(nameof(CanSend));
                RaisePropertyChanged(nameof(RunStatus));
                RaisePropertyChanged(nameof(RunButtonLabel));
                RaisePropertyChanged(nameof(CanUseRepositoryActions));
                RaisePropertyChanged(nameof(CanUseRemoteActions));
            }
        }
    }

    public bool IsInstallingRuntime
    {
        get => _isInstallingRuntime;
        private set
        {
            if (SetProperty(ref _isInstallingRuntime, value))
            {
                RaisePropertyChanged(nameof(RuntimeActionLabel));
            }
        }
    }

    public bool ShowRuntimeSetup
    {
        get => _showRuntimeSetup;
        private set => SetProperty(ref _showRuntimeSetup, value);
    }

    public bool ShowAuthenticationAction
    {
        get => _showAuthenticationAction;
        private set => SetProperty(ref _showAuthenticationAction, value);
    }

    public string ConnectionStatus
    {
        get => _connectionStatus;
        private set => SetProperty(ref _connectionStatus, value);
    }

    public string UsageStatus
    {
        get => _usageStatus;
        private set => SetProperty(ref _usageStatus, value);
    }

    public string UsageDetail
    {
        get => _usageDetail;
        private set => SetProperty(ref _usageDetail, value);
    }

    public IReadOnlyList<ReasoningLevelOption> ReasoningLevels =>
        SelectedModel?.ReasoningLevels ?? [];
    public IReadOnlyList<ServiceTierOption> ServiceTiers =>
        SelectedModel?.ServiceTiers ?? [];
    public bool HasServiceTiers => ServiceTiers.Count > 0;

    public string ActiveModelName => SelectedModel?.ModelName ?? "No model connected";
    public string ActiveProviderLabel => SelectedModel?.ProviderLabel ?? "RUNTIME UNAVAILABLE";
    public bool SupportsVision => SelectedModel?.Capabilities.Contains("VISION") == true;
    public string VisionStatus => SelectedModel is null
        ? "NO MODEL"
        : _imagePath is not null
            ? $"IMAGE · {Path.GetFileName(_imagePath)}"
            : SupportsVision ? "VISION SUPPORTED" : "TEXT + TOOLS";
    public bool CanSend => SelectedModel is not null
        && !IsRunning
        && !string.IsNullOrWhiteSpace(PromptText);
    public string RunStatus => IsRunning ? "RUNNING" : "READY";
    public string RunButtonLabel => IsRunning ? "STOP" : "RUN  ↵";
    public string RuntimeActionLabel => IsInstallingRuntime
        ? "UPDATING RUNTIME…"
        : "INSTALL / UPDATE CODEX RUNTIME";
    public string TokenStatus => _totalTokens is { } tokens
        ? _contextWindowTokens is { } window && window > 0
            ? $"CONTEXT  {Math.Clamp(tokens * 100d / window, 0, 100):0}% · {tokens:N0} / {window:N0}"
            : $"CONTEXT  {tokens:N0} / LIMIT NOT REPORTED"
        : "CONTEXT  —";
    public double ContextUsagePercent => _totalTokens is { } tokens
        && _contextWindowTokens is { } window && window > 0
            ? Math.Clamp(tokens * 100d / window, 0, 100)
            : 0;
    public string ContextWindowStatus => _isCompactingContext
        ? "Compaction requested · waiting for the provider to publish the smaller context"
        : _totalTokens is { } tokens && _contextWindowTokens is { } window && window > 0
            ? $"{tokens:N0} of {window:N0} tokens · {Math.Max(0, window - tokens):N0} available"
            : "The provider has not reported this session's context limit yet.";
    public bool HasContextWindow => _contextWindowTokens is > 0;
    public bool IsContextCompacting => _isCompactingContext;
    public string TurnDiff => _turnDiff;
    public bool HasTurnDiff => !string.IsNullOrWhiteSpace(_turnDiff);
    public string ModelSettingsStatus
    {
        get
        {
            var requestedEffort = SelectedReasoningLevel?.DisplayName ?? "Provider default";
            var requestedTier = SelectedServiceTier?.DisplayName ?? "Provider default";
            return $"Reasoning {requestedEffort} → {_effectiveReasoning} · Tier {requestedTier} → {_effectiveServiceTier}";
        }
    }

    public void ApplyModels(
        IReadOnlyList<ModelDescriptor> descriptors,
        string runtimeSource = "CODEX RUNTIME")
    {
        Models.Clear();
        foreach (var descriptor in descriptors)
        {
            Models.Add(ModelOption.FromDescriptor(descriptor, runtimeSource));
        }

        SelectedModel = Models.FirstOrDefault(model => model.IsDefault) ?? Models.FirstOrDefault();
        ShowRuntimeSetup = Models.Count == 0;
        ConnectionStatus = Models.Count > 0 ? "CODEX CONNECTED" : "NO MODELS REPORTED";
        Activity.Add(ActivityItem.Now(
            "RUNTIME",
            Models.Count > 0
                ? $"Loaded {Models.Count} models from Codex"
                : "Codex returned an empty model catalog",
            Models.Count > 0 ? "#65C7D0" : "#E2A84A"));
    }

    public void ApplyWorkspaceSnapshot(WorkspaceSessionSnapshot snapshot)
    {
        _workspacePath = snapshot.Project.RootPath;
        Tasks.Clear();
        for (var index = 0; index < snapshot.Sessions.Count; index++)
        {
            var session = snapshot.Sessions[index];
            Tasks.Add(TaskItem.FromStored(session, index + 1));
        }

        ApplyStoredSession(snapshot.ActiveSession, snapshot.Messages, snapshot.Attachments);
        RaisePropertyChanged(nameof(WorkspacePath));
        RaisePropertyChanged(nameof(WorkspaceName));
        RaisePropertyChanged(nameof(WorkspaceSummary));
    }

    public void ApplyWorkspaceCatalog(IReadOnlyList<StoredProject> projects, string activeProjectId)
    {
        // A ListBox publishes selection changes while its collection mutates.
        // Reconcile in place so the navigator never passes through an empty
        // state and never loses the selected object during a workspace switch.
        if (projects.Count == 0) return;

        var projectIds = projects.Select(project => project.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var project in projects)
        {
            var active = string.Equals(project.Id, activeProjectId, StringComparison.Ordinal);
            var item = Workspaces.FirstOrDefault(workspace => workspace.ProjectId == project.Id);
            if (item is null)
            {
                item = new WorkspaceItem(project.Id, project.Name, project.RootPath);
                Workspaces.Add(item);
            }
            item.Update(project.Name, project.RootPath, active);
        }
        for (var index = Workspaces.Count - 1; index >= 0; index--)
        {
            if (!projectIds.Contains(Workspaces[index].ProjectId)) Workspaces.RemoveAt(index);
        }
        SelectedWorkspace = Workspaces.FirstOrDefault(workspace => workspace.ProjectId == activeProjectId);
    }

    public void ApplyStoredSession(
        StoredSession session,
        IReadOnlyList<StoredMessage> messages,
        IReadOnlyList<StoredAttachment>? attachments = null)
    {
        _activeSessionId = session.Id;
        SelectedTask = Tasks.FirstOrDefault(task => task.SessionId == session.Id);
        Messages.Clear();
        foreach (var message in messages)
        {
            if (message.Status != "IMPORTED" && message.Role is "YOU" or "HARNESS" or "REPORT")
            {
                Messages.Add(ChatMessageItem.FromStored(message));
            }
        }

        ContextFiles.Clear();
        foreach (var attachment in attachments ?? [])
        {
            ContextFiles.Add(ContextFileItem.FromStored(attachment));
        }
        RaisePropertyChanged(nameof(HasContextFiles));
        RaisePropertyChanged(nameof(ContextStatus));

        ChangedFiles.Clear();
        _executionItems.Clear();
        ExecutionItems.Clear();
        SelectedExecutionItem = null;
        RaisePropertyChanged(nameof(ExecutionStatus));
        SetTurnDiff(string.Empty);
        Activity.Clear();
        Activity.Add(ActivityItem.Now(
            "SESSION",
            messages.Count > 0
                ? $"Restored {messages.Count} persisted records"
                : "New durable session",
            "#65C7D0"));
        _totalTokens = null;
        _contextWindowTokens = null;
        _isCompactingContext = false;
        _lastCompactionTokenTotal = null;
        SetImage(null);
        RaisePropertyChanged(nameof(TokenStatus));
        RaisePropertyChanged(nameof(ContextUsagePercent));
        RaisePropertyChanged(nameof(ContextWindowStatus));
        RaisePropertyChanged(nameof(HasContextWindow));
    }

    public void AddStoredSession(StoredSession session)
    {
        var task = TaskItem.FromStored(session, Tasks.Count + 1);
        Tasks.Insert(0, task);
        for (var index = 0; index < Tasks.Count; index++)
        {
            Tasks[index].Index = (index + 1).ToString("00");
        }
        ApplyStoredSession(session, []);
    }

    public void AddContextFile(StoredAttachment attachment)
    {
        if (ContextFiles.Any(item => item.Id == attachment.Id))
        {
            return;
        }

        ContextFiles.Add(ContextFileItem.FromStored(attachment));
        RaisePropertyChanged(nameof(HasContextFiles));
        RaisePropertyChanged(nameof(ContextStatus));
        Activity.Add(ActivityItem.Now(
            "CONTEXT",
            $"Attached {attachment.DisplayName}",
            "#65C7D0"));
    }

    public void RemoveContextFile(string attachmentId)
    {
        var item = ContextFiles.FirstOrDefault(context => context.Id == attachmentId);
        if (item is null)
        {
            return;
        }

        ContextFiles.Remove(item);
        RaisePropertyChanged(nameof(HasContextFiles));
        RaisePropertyChanged(nameof(ContextStatus));
        Activity.Add(ActivityItem.Now("CONTEXT", $"Detached {item.DisplayName}", "#8993A3"));
    }

    public void RenameStoredSession(string sessionId, string title)
    {
        var task = Tasks.FirstOrDefault(item => item.SessionId == sessionId);
        if (task is null)
        {
            return;
        }

        task.Title = title;
        if (ReferenceEquals(task, SelectedTask))
        {
            RaisePropertyChanged(nameof(CurrentSessionTitle));
        }
    }

    public void RemoveStoredSession(string sessionId)
    {
        var task = Tasks.FirstOrDefault(item => item.SessionId == sessionId);
        if (task is not null)
        {
            Tasks.Remove(task);
        }
        for (var index = 0; index < Tasks.Count; index++)
        {
            Tasks[index].Index = (index + 1).ToString("00");
        }
    }

    public void ApplySessionModelSettings(StoredSession session)
    {
        if (string.IsNullOrWhiteSpace(session.ModelId))
        {
            return;
        }

        SelectedModel = Models.FirstOrDefault(model =>
            string.Equals(model.ModelName, session.ModelId, StringComparison.OrdinalIgnoreCase))
            ?? SelectedModel;
        SelectedReasoningLevel = ReasoningLevels.FirstOrDefault(level =>
            string.Equals(level.Id, session.ReasoningEffort, StringComparison.OrdinalIgnoreCase))
            ?? SelectedReasoningLevel;
        SelectedServiceTier = ServiceTiers.FirstOrDefault(tier =>
            string.Equals(tier.Id, session.ServiceTier, StringComparison.OrdinalIgnoreCase))
            ?? SelectedServiceTier;
    }

    public void ApplyUsage(ProviderUsageSnapshot snapshot)
    {
        UsageWindows.Clear();
        foreach (var window in snapshot.Windows)
        {
            UsageWindows.Add(UsageWindowItem.FromSnapshot(window));
        }

        var plan = string.IsNullOrWhiteSpace(snapshot.PlanName)
            ? "PLAN NOT REPORTED"
            : snapshot.PlanName.ToUpperInvariant();
        UsageStatus = $"LIVE · {plan}";
        UsageDetail = $"Updated {snapshot.CapturedAt.ToLocalTime():MMM d, h:mm:ss tt}";
        ShowAuthenticationAction = false;
    }

    public void SetUsageUnavailable(string detail)
    {
        UsageWindows.Clear();
        UsageStatus = "UNAVAILABLE";
        UsageDetail = detail;
        ShowAuthenticationAction = detail.Contains("auth", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("sign in", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("unauthorized", StringComparison.OrdinalIgnoreCase);
    }

    public void SetUsageAuthenticating()
    {
        UsageStatus = "SIGN-IN OPENED";
        UsageDetail = "Complete OpenAI sign-in in your browser. Usage will refresh automatically.";
        ShowAuthenticationAction = false;
    }

    public void SetRuntimeInstallState(string state, string detail, bool isRunning)
    {
        IsInstallingRuntime = isRunning;
        ConnectionStatus = state;
        ShowRuntimeSetup = state.Contains("FAILED", StringComparison.OrdinalIgnoreCase);
        Activity.Add(ActivityItem.Now("RUNTIME", detail, isRunning ? "#E2A84A" : "#65C7D0", isRunning));
    }

    public void SetConnectionFailure(string detail)
    {
        Models.Clear();
        SelectedModel = null;
        ConnectionStatus = "CODEX OFFLINE";
        UsageStatus = "UNAVAILABLE";
        UsageDetail = detail;
        ShowRuntimeSetup = true;
        ShowAuthenticationAction = false;
        Activity.Add(ActivityItem.Now("ERROR", detail, "#E2A84A"));
    }

    public string BeginTurn()
    {
        var prompt = PromptText.Trim();
        var visiblePrompt = _imagePath is null
            ? prompt
            : $"{prompt}\n\n[Image: {Path.GetFileName(_imagePath)}]";
        Messages.Add(ChatMessageItem.User(visiblePrompt));
        RequestMessagePersistence(Messages[^1]);
        PromptText = string.Empty;
        IsRunning = true;
        _streamingAssistantMessages.Clear();
        _executionItems.Clear();
        ExecutionItems.Clear();
        SelectedExecutionItem = null;
        RaisePropertyChanged(nameof(ExecutionStatus));
        ChangedFiles.Clear();
        SetTurnDiff(string.Empty);
        Activity.Add(ActivityItem.Now("MODEL", "Turn started", "#65C7D0", true));
        return prompt;
    }

    public void SetImage(string? path)
    {
        _imagePath = path;
        RaisePropertyChanged(nameof(ImagePath));
        RaisePropertyChanged(nameof(VisionStatus));
    }

    public void SetWorkspace(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (string.Equals(fullPath, _workspacePath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _workspacePath = fullPath;
        Messages.Clear();
        ChangedFiles.Clear();
        _executionItems.Clear();
        ExecutionItems.Clear();
        SelectedExecutionItem = null;
        RaisePropertyChanged(nameof(ExecutionStatus));
        SetTurnDiff(string.Empty);
        Activity.Clear();
        Activity.Add(ActivityItem.Now("WORKSPACE", $"Opened {fullPath}", "#65C7D0"));
        _totalTokens = null;
        _contextWindowTokens = null;
        _isCompactingContext = false;
        _lastCompactionTokenTotal = null;
        SetImage(null);
        RaisePropertyChanged(nameof(WorkspacePath));
        RaisePropertyChanged(nameof(WorkspaceName));
        RaisePropertyChanged(nameof(WorkspaceSummary));
        RaisePropertyChanged(nameof(TokenStatus));
        RaisePropertyChanged(nameof(ContextUsagePercent));
        RaisePropertyChanged(nameof(ContextWindowStatus));
        RaisePropertyChanged(nameof(HasContextWindow));
    }

    public void StartAssistantMessage(string itemId)
    {
        if (_streamingAssistantMessages.ContainsKey(itemId)) return;
        var message = ChatMessageItem.Assistant(string.Empty);
        _streamingAssistantMessages[itemId] = message;
        Messages.Add(message);
    }

    public void AppendAssistantDelta(string itemId, string delta)
    {
        if (!_streamingAssistantMessages.TryGetValue(itemId, out var message))
        {
            StartAssistantMessage(itemId);
            message = _streamingAssistantMessages[itemId];
        }
        message.Append(delta);
    }

    public void CompleteAssistant(string itemId, string? authoritativeText = null)
    {
        if (!_streamingAssistantMessages.TryGetValue(itemId, out var message))
        {
            if (string.IsNullOrWhiteSpace(authoritativeText))
            {
                return;
            }
            StartAssistantMessage(itemId);
            message = _streamingAssistantMessages[itemId];
            message.ReplaceText(authoritativeText);
        }
        else if (!string.IsNullOrWhiteSpace(authoritativeText))
        {
            // The completed item is authoritative for this one response item only.
            // Keeping messages keyed by provider item prevents later commentary or a
            // final answer from overwriting an earlier response in the same turn.
            message.ReplaceText(authoritativeText);
        }
        message.SetStatus("COMPLETED");
        RequestMessagePersistence(message);
        _streamingAssistantMessages.Remove(itemId);
    }

    public void StartExecutionItem(
        string itemId,
        string kind,
        string title,
        string detail,
        string color,
        bool monospace = false)
    {
        if (_executionItems.ContainsKey(itemId))
        {
            return;
        }

        var item = new ExecutionItem(itemId, kind, title, detail, color, monospace);
        _executionItems[itemId] = item;
        ExecutionItems.Add(item);
        SelectedExecutionItem ??= item;
        if (ExecutionItems.Count > 200)
        {
            var removed = ExecutionItems[0];
            ExecutionItems.RemoveAt(0);
            _executionItems.Remove(removed.Id);
        }
        RaisePropertyChanged(nameof(ExecutionStatus));
        Activity.Add(ActivityItem.Now(kind, title, color, true));
    }

    public void AppendExecutionDelta(
        string itemId,
        string kind,
        string title,
        string delta,
        string color,
        bool monospace = false)
    {
        if (!_executionItems.TryGetValue(itemId, out var item))
        {
            StartExecutionItem(itemId, kind, title, string.Empty, color, monospace);
            item = _executionItems[itemId];
        }

        item.AppendBounded(delta);
    }

    public void CompleteExecutionItem(
        string itemId,
        string status,
        string? authoritativeText = null)
    {
        if (!_executionItems.TryGetValue(itemId, out var item))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(authoritativeText))
        {
            item.ReplaceTextBounded(authoritativeText);
        }

        item.SetStatus(status);
    }

    public void ApplyFileChanges(string itemId, JsonElement changes, string status)
    {
        var combined = new List<string>();
        if (changes.ValueKind == JsonValueKind.Array)
        {
            foreach (var change in changes.EnumerateArray())
            {
                var path = ReadString(change, "path", "Unknown file");
                var diff = ReadString(change, "diff", string.Empty);
                var kind = change.TryGetProperty("kind", out var kindElement)
                    ? ReadString(kindElement, "type", kindElement.ToString())
                    : "change";
                var existing = ChangedFiles.FirstOrDefault(file =>
                    string.Equals(file.Path, path, StringComparison.OrdinalIgnoreCase));
                if (existing is not null) ChangedFiles.Remove(existing);
                ChangedFiles.Add(new FileChangeItem(path, kind.ToUpperInvariant(), diff));
                if (!string.IsNullOrWhiteSpace(diff))
                {
                    combined.Add(diff);
                }
            }
        }

        var text = combined.Count > 0
            ? string.Join(Environment.NewLine, combined)
            : $"{ChangedFiles.Count} file change(s) · {status}";
        if (!_executionItems.ContainsKey(itemId))
        {
            StartExecutionItem(itemId, "FILES", "Workspace changes", text, "#E2A84A", true);
        }
        else
        {
            CompleteExecutionItem(itemId, status, text);
        }
        if (combined.Count > 0)
        {
            SetTurnDiff(string.Join(Environment.NewLine, combined));
        }
    }

    public void SetTurnDiff(string diff)
    {
        _turnDiff = diff;
        RaisePropertyChanged(nameof(TurnDiff));
        RaisePropertyChanged(nameof(HasTurnDiff));
    }

    public void ApplyResolvedDiffs(IReadOnlyDictionary<string, string> diffs)
    {
        var combined = new List<string>();
        foreach (var entry in diffs)
        {
            var existing = ChangedFiles.FirstOrDefault(file =>
                string.Equals(file.Path.Replace('\\', '/'), entry.Key.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                var index = ChangedFiles.IndexOf(existing);
                ChangedFiles[index] = existing with { Diff = entry.Value };
            }
            if (!string.IsNullOrWhiteSpace(entry.Value)) combined.Add(entry.Value);
        }
        if (combined.Count > 0) SetTurnDiff(string.Join(Environment.NewLine + Environment.NewLine, combined));
    }

    public void AddActivity(string kind, string detail, string color = "#8993A3", bool isRunning = false) =>
        Activity.Add(ActivityItem.Now(kind, detail, color, isRunning));

    public void CompleteTurn(string? error = null)
    {
        IsRunning = false;
        foreach (var message in _streamingAssistantMessages.Values)
        {
            message.SetStatus(error is null ? "COMPLETED" : "FAILED");
            RequestMessagePersistence(message);
        }
        _streamingAssistantMessages.Clear();
        var report = BuildTurnReport(error);
        if (report is not null)
        {
            Messages.Add(report);
            RequestMessagePersistence(report);
        }
        Activity.Add(ActivityItem.Now(
            error is null ? "MODEL" : "ERROR",
            error ?? "Turn completed",
            error is null ? "#65C7D0" : "#E2A84A"));
    }

    private ChatMessageItem? BuildTurnReport(string? error)
    {
        if (_executionItems.Count == 0 && ChangedFiles.Count == 0) return null;
        var commands = ExecutionItems.Where(item => item.Kind is "COMMAND" or "OUTPUT").ToArray();
        var tools = ExecutionItems.Count(item => item.Kind is "TOOL" or "WEB");
        var failures = ExecutionItems.Where(item =>
            item.Status.Contains("FAIL", StringComparison.OrdinalIgnoreCase)
            || (item.Status.StartsWith("EXIT ", StringComparison.OrdinalIgnoreCase)
                && item.Status != "EXIT 0")).ToArray();
        if (error is null && ChangedFiles.Count == 0 && failures.Length == 0) return null;
        var lines = new List<string> { error is null ? "Workspace updated" : $"Task stopped: {error}" };
        if (ChangedFiles.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("Changes");
            foreach (var file in ChangedFiles.Take(24))
            {
                var parsed = UnifiedDiffParser.Parse(file.Diff);
                var counts = parsed.AddedLines + parsed.RemovedLines > 0
                    ? $" (+{parsed.AddedLines} −{parsed.RemovedLines})"
                    : string.Empty;
                lines.Add($"• {file.Path} · {file.Kind}{counts}");
            }
            if (ChangedFiles.Count > 24) lines.Add($"• …and {ChangedFiles.Count - 24} more files");
        }
        if (commands.Length > 0 && failures.Length == 0)
        {
            lines.Add(string.Empty);
            lines.Add($"Verification completed · {commands.Length} command{(commands.Length == 1 ? string.Empty : "s")}");
        }
        if (failures.Length > 0)
        {
            lines.Add(string.Empty);
            lines.Add($"Attention: {failures.Length} execution event{(failures.Length == 1 ? string.Empty : "s")} did not complete successfully. Open Activity for details.");
        }
        return ChatMessageItem.Report(string.Join(Environment.NewLine, lines), error is null ? "COMPLETED" : "FAILED");
    }

    public void UpdateTokenUsage(long totalTokens, long? contextWindowTokens = null)
    {
        _totalTokens = totalTokens;
        if (contextWindowTokens is > 0) _contextWindowTokens = contextWindowTokens;
        RaisePropertyChanged(nameof(TokenStatus));
        RaisePropertyChanged(nameof(ContextUsagePercent));
        RaisePropertyChanged(nameof(ContextWindowStatus));
        RaisePropertyChanged(nameof(HasContextWindow));
    }

    public void SetContextCompaction(bool active)
    {
        if (_isCompactingContext && !active) _lastCompactionTokenTotal = _totalTokens;
        _isCompactingContext = active;
        RaisePropertyChanged(nameof(ContextWindowStatus));
        RaisePropertyChanged(nameof(IsContextCompacting));
    }

    public bool ShouldCompactContext(double thresholdPercent = 85) =>
        !_isCompactingContext
        && _totalTokens is { } tokens
        && _contextWindowTokens is { } window
        && window > 0
        && tokens * 100d / window >= thresholdPercent
        && (_lastCompactionTokenTotal is null
            || Math.Abs(tokens - _lastCompactionTokenTotal.Value) >= window / 10);

    public void ApplyEffectiveModelSettings(string? effort, string? serviceTier)
    {
        _effectiveReasoning = string.IsNullOrWhiteSpace(effort)
            ? "PROVIDER DEFAULT"
            : effort.ToUpperInvariant();
        _effectiveServiceTier = string.IsNullOrWhiteSpace(serviceTier)
            ? "STANDARD (PROVIDER DEFAULT)"
            : serviceTier.ToUpperInvariant();
        RaisePropertyChanged(nameof(ModelSettingsStatus));
        Activity.Add(ActivityItem.Now(
            "MODEL",
            $"Effective reasoning: {_effectiveReasoning}; service tier: {_effectiveServiceTier}",
            "#65C7D0"));
    }

    public void ApplyWorkingTree(WorkingTreeSnapshot snapshot)
    {
        WorkingTreeFiles.Clear();
        _repositoryRoot = snapshot.RepositoryRoot;
        RaisePropertyChanged(nameof(RepositoryRoot));
        if (!snapshot.IsRepository)
        {
            IsRepository = false;
            HasRepositoryRemote = false;
            WorkingTreeStatus = "NOT A REPOSITORY";
            BranchStatus = "GIT —";
            return;
        }

        IsRepository = true;

        foreach (var file in snapshot.Files)
        {
            WorkingTreeFiles.Add(WorkingTreeFileItem.FromModel(file));
        }
        WorkingTreeStatus = snapshot.Files.Count == 0
            ? "CLEAN"
            : $"{snapshot.Files.Count} CHANGED";
        BranchStatus = $"GIT  {snapshot.Branch ?? "UNKNOWN"}";
        RaisePropertyChanged(nameof(RepositoryDockLabel));
        RaisePropertyChanged(nameof(CanUseRepositoryActions));
    }

    public void ApplyRepositoryRemote(string? remoteUrl)
    {
        HasRepositoryRemote = !string.IsNullOrWhiteSpace(remoteUrl);
    }

    public void ApplyGitHubConnection(bool isAuthenticated) => IsGitHubConnected = isAuthenticated;

    private static string ReadString(JsonElement element, string property, string fallback) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var child)
        && child.ValueKind == JsonValueKind.String
            ? child.GetString() ?? fallback
            : fallback;

    private void RequestMessagePersistence(ChatMessageItem item)
    {
        if (_activeSessionId is not null)
        {
            MessagePersistenceRequested?.Invoke(
                this,
                new MessagePersistenceRequestedEventArgs(_activeSessionId, item));
        }
    }

    private static string ResolveInitialWorkspace()
    {
        static string? FindProjectRoot(string start)
        {
            var current = new DirectoryInfo(Path.GetFullPath(start));
            while (current is not null)
            {
                if (Directory.Exists(Path.Combine(current.FullName, ".git"))
                    || current.EnumerateFiles("*.sln", SearchOption.TopDirectoryOnly).Any())
                {
                    return current.FullName;
                }

                current = current.Parent;
            }

            return null;
        }

        return FindProjectRoot(Environment.CurrentDirectory)
            ?? FindProjectRoot(AppContext.BaseDirectory)
            ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    }

    private static ReasoningLevelOption? GetDefaultReasoningLevel(ModelOption model) =>
        model.ReasoningLevels.FirstOrDefault(level => level.IsDefault)
        ?? model.ReasoningLevels.FirstOrDefault();

    private static ServiceTierOption? GetDefaultServiceTier(ModelOption model) =>
        model.ServiceTiers.FirstOrDefault(tier => tier.IsDefault)
        ?? model.ServiceTiers.FirstOrDefault();

    private void RefreshCapabilities()
    {
        string[] capabilityNames =
            ["TEXT", "VISION", "TOOLS", "REASONING", "CACHE", "AUDIO IN", "IMAGE GEN"];

        Capabilities.Clear();
        foreach (var capability in capabilityNames)
        {
            Capabilities.Add(new CapabilityItem(
                capability,
                SelectedModel?.Capabilities.Contains(capability) == true ? 1.0 : 0.28));
        }
    }

    private void SeedPreviewData()
    {
        Tasks.Add(new TaskItem("preview-session", "New session", "READY", "01"));
        var descriptor = new ModelDescriptor(
            "preview",
            "gpt-5.6-sol",
            "gpt-5.6-sol",
            ModelCapability.Text | ModelCapability.Vision | ModelCapability.ToolUse | ModelCapability.Reasoning,
            ReasoningLevels:
            [
                new("none", "None"),
                new("low", "Low"),
                new("medium", "Medium", IsDefault: true),
                new("high", "High"),
                new("xhigh", "XHigh"),
                new("max", "Max")
            ],
            ServiceTiers:
            [
                new(null, "Standard", "Provider default service tier", IsDefault: true),
                new("priority", "Fast", "Preview provider service tier")
            ],
            IsDefault: true);
        ApplyModels([descriptor]);
        ConnectionStatus = "PREVIEW DATA";
        UsageStatus = "PREVIEW DATA";
        UsageDetail = "Visual-check fixture; excluded from application startup.";
        UsageWindows.Add(new UsageWindowItem("5 HOUR WINDOW", 68, "Preview reset time"));
        UsageWindows.Add(new UsageWindowItem("WEEKLY LIMIT", 41, "Preview reset time"));
        ContextFiles.Add(new ContextFileItem(
            "preview-context",
            "AGENTS.md",
            "E:\\Dev Projects\\AI Harness\\AGENTS.md",
            "text/plain",
            1840));
        RaisePropertyChanged(nameof(HasContextFiles));
        RaisePropertyChanged(nameof(ContextStatus));
        ApplyWorkingTree(new WorkingTreeSnapshot(
            true,
            "E:\\Dev Projects\\AI Harness",
            "codex/persistence",
            [new WorkingTreeFile("src/Harness.App/Views/MainWindow.axaml", ' ', 'M', false)]));
        Messages.Add(ChatMessageItem.User(
            "Build this as a standalone GUI—not Electron. Keep the compactness of tmux."));
        var previewAssistant = ChatMessageItem.Assistant(
            "The product boundary is a cross-platform C# desktop application with capability-driven providers.");
        previewAssistant.SetStatus("COMPLETED");
        Messages.Add(previewAssistant);
        StartExecutionItem(
            "preview-command",
            "COMMAND",
            "dotnet build Harness.sln -c Release",
            "E:\\Dev Projects\\AI Harness",
            "#E2A84A",
            true);
        CompleteExecutionItem(
            "preview-command",
            "EXIT 0",
            "Build succeeded.\n0 Warning(s)\n0 Error(s)");
        ChangedFiles.Add(new FileChangeItem(
            "src/Harness.App/Views/MainWindow.axaml",
            "MODIFY",
            "@@ preview diff @@"));
        SetTurnDiff("--- a/MainWindow.axaml\n+++ b/MainWindow.axaml\n@@ preview diff @@");
        CompleteTurn();
    }
}

public sealed class WorkspaceItem : ObservableObject
{
    private string _name;
    private string _path;
    private string _background;
    private string _dotColor;

    public WorkspaceItem(
        string projectId,
        string name,
        string path,
        string background = "Transparent",
        string dotColor = "#596574")
    {
        ProjectId = projectId;
        _name = name;
        _path = path;
        _background = background;
        _dotColor = dotColor;
    }

    public string ProjectId { get; }
    public string Name { get => _name; private set => SetProperty(ref _name, value); }
    public string Path { get => _path; private set => SetProperty(ref _path, value); }
    public string Background { get => _background; private set => SetProperty(ref _background, value); }
    public string DotColor { get => _dotColor; private set => SetProperty(ref _dotColor, value); }

    public void Update(string name, string path, bool active)
    {
        Name = name;
        Path = path;
        Background = "Transparent";
        DotColor = active ? "#65C7D0" : "#596574";
    }
}
public sealed class TaskItem : ObservableObject
{
    private string _index;
    private string _title;

    public TaskItem(string sessionId, string title, string state, string index)
    {
        SessionId = sessionId;
        _title = title;
        State = state;
        _index = index;
    }

    public string SessionId { get; }
    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }
    public string State { get; }
    public string Index
    {
        get => _index;
        set => SetProperty(ref _index, value);
    }

    public static TaskItem FromStored(StoredSession session, int index) =>
        new(session.Id, session.Title, "READY", index.ToString("00"));
}

public sealed record MessagePersistenceRequestedEventArgs(
    string SessionId,
    ChatMessageItem Message);
public sealed record ActivityItem(
    string Kind,
    string Detail,
    string Time,
    string Color,
    bool IsActive)
{
    public static ActivityItem Now(string kind, string detail, string color, bool isActive = false) =>
        new(kind, detail, DateTimeOffset.Now.ToString("h:mm"), color, isActive);
}

public sealed record CapabilityItem(string Name, double Opacity);
public sealed record ReasoningLevelOption(
    string Id,
    string DisplayName,
    bool IsDefault = false,
    string? Description = null);
public sealed record ServiceTierOption(
    string? Id,
    string DisplayName,
    bool IsDefault = false,
    string? Description = null);
public sealed record UsageWindowItem(string Label, double RemainingPercent, string ResetText)
{
    public string RemainingText => $"{RemainingPercent:0}% LEFT";

    public static UsageWindowItem FromSnapshot(UsageWindowSnapshot snapshot) =>
        new(
            snapshot.DisplayName,
            snapshot.RemainingPercent,
            snapshot.ResetsAt is { } reset
                ? $"Resets {reset.ToLocalTime():ddd, MMM d · h:mm tt}"
                : "Reset time not reported");
}

public sealed record FileChangeItem(string Path, string Kind, string Diff);

public sealed class ExecutionItem : ObservableObject
{
    private const int MaximumDetailCharacters = 48 * 1024;
    private string _detail;
    private string _status = "RUNNING";

    public ExecutionItem(string id, string kind, string title, string detail, string color, bool monospace)
    {
        Id = id;
        Kind = kind;
        Title = title;
        Color = color;
        IsMonospace = monospace;
        _detail = Bound(detail);
        StartedAt = DateTimeOffset.UtcNow;
    }

    public string Id { get; }
    public string Kind { get; }
    public string Title { get; }
    public string Color { get; }
    public bool IsMonospace { get; }
    public string FontFamily => IsMonospace ? "Cascadia Mono, JetBrains Mono, Consolas" : "Inter";
    public DateTimeOffset StartedAt { get; }
    public string Time => StartedAt.ToLocalTime().ToString("h:mm:ss");
    public string Detail { get => _detail; private set => SetProperty(ref _detail, value); }
    public string Status { get => _status; private set => SetProperty(ref _status, value.ToUpperInvariant()); }

    public void AppendBounded(string value) => Detail = Bound(Detail + value);
    public void ReplaceTextBounded(string value) => Detail = Bound(value);
    public void SetStatus(string value) => Status = value;

    private static string Bound(string value) => value.Length <= MaximumDetailCharacters
        ? value
        : $"[Earlier output omitted; showing the last {MaximumDetailCharacters:N0} characters.]\n" + value[^MaximumDetailCharacters..];
}

public sealed record ContextFileItem(
    string Id,
    string DisplayName,
    string StoredPath,
    string? MediaType,
    long ByteLength)
{
    public string SizeText => ByteLength switch
    {
        >= 1024 * 1024 => $"{ByteLength / 1024d / 1024d:0.0} MB",
        >= 1024 => $"{ByteLength / 1024d:0.0} KB",
        _ => $"{ByteLength} B"
    };

    public static ContextFileItem FromStored(StoredAttachment attachment) => new(
        attachment.Id,
        attachment.DisplayName,
        attachment.StoredPath,
        attachment.MediaType,
        attachment.ByteLength);
}

public sealed record WorkingTreeFileItem(
    string RelativePath,
    string StatusCode,
    string StatusLabel,
    bool IsStaged,
    bool CanStage,
    bool CanUnstage,
    bool CanRevert,
    WorkingTreeFile Source)
{
    public static WorkingTreeFileItem FromModel(WorkingTreeFile file) => new(
        file.RelativePath,
        file.StatusCode,
        file.IsUntracked
            ? "UNTRACKED"
            : file.IsStaged && file.HasWorkTreeChanges
                ? "STAGED + MODIFIED"
                : file.IsStaged
                    ? "STAGED"
                    : "MODIFIED",
        file.IsStaged,
        file.IsUntracked || file.HasWorkTreeChanges,
        file.IsStaged,
        file.HasWorkTreeChanges,
        file);
}

public sealed record ModelOption(
    string DisplayName,
    string ModelName,
    string ProviderLabel,
    IReadOnlySet<string> Capabilities,
    IReadOnlyList<ReasoningLevelOption> ReasoningLevels,
    IReadOnlyList<ServiceTierOption> ServiceTiers,
    bool IsDefault)
{
    public static ModelOption FromDescriptor(
        ModelDescriptor descriptor,
        string runtimeSource = "CODEX RUNTIME")
    {
        var capabilities = Enum.GetValues<ModelCapability>()
            .Where(capability => capability != ModelCapability.None && descriptor.Supports(capability))
            .Select(CapabilityName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var reasoning = descriptor.ReasoningLevels?
            .Select(level => new ReasoningLevelOption(
                level.Id,
                level.DisplayName,
                level.IsDefault,
                level.Description))
            .ToArray() ?? [];
        var serviceTiers = descriptor.ServiceTiers?
            .Select(tier => new ServiceTierOption(
                tier.Id,
                tier.DisplayName,
                tier.IsDefault,
                tier.Description))
            .ToArray() ?? [];

        return new ModelOption(
            $"{FormatProviderName(descriptor.ProviderId)} · {descriptor.DisplayName}",
            descriptor.ModelId,
            $"{FormatProviderName(descriptor.ProviderId).ToUpperInvariant()} · {runtimeSource}",
            capabilities,
            reasoning,
            serviceTiers,
            descriptor.IsDefault);
    }

    private static string FormatProviderName(string providerId) =>
        string.Join(
            " ",
            providerId
                .Split(['-', '_', '.'], StringSplitOptions.RemoveEmptyEntries)
                .Select(part => char.ToUpperInvariant(part[0]) + part[1..]));

    private static string CapabilityName(ModelCapability capability) => capability switch
    {
        ModelCapability.Text => "TEXT",
        ModelCapability.Vision => "VISION",
        ModelCapability.ToolUse => "TOOLS",
        ModelCapability.Reasoning => "REASONING",
        ModelCapability.ImageGeneration => "IMAGE GEN",
        ModelCapability.AudioInput => "AUDIO IN",
        ModelCapability.AudioOutput => "AUDIO OUT",
        ModelCapability.PromptCaching => "CACHE",
        ModelCapability.ComputerUse => "COMPUTER",
        _ => capability.ToString().ToUpperInvariant()
    };
}

public sealed class ChatMessageItem : ObservableObject
{
    private string _text;
    private string _status;

    private ChatMessageItem(
        string id,
        string role,
        string title,
        string text,
        string color,
        string background,
        string fontFamily,
        string status,
        DateTimeOffset createdAt)
    {
        Id = id;
        Role = role;
        Title = title;
        _text = text;
        Color = color;
        Background = background;
        FontFamily = fontFamily;
        _status = status;
        CreatedAt = createdAt;
        Time = createdAt.ToLocalTime().ToString("h:mm");
    }

    public string Id { get; }
    public string Role { get; }
    public string Title { get; }
    public string Color { get; }
    public string Background { get; }
    public string FontFamily { get; }
    public string Time { get; }
    public DateTimeOffset CreatedAt { get; }
    public bool IsMonospace => FontFamily.Contains("Mono", StringComparison.OrdinalIgnoreCase)
        || FontFamily.Contains("Consolas", StringComparison.OrdinalIgnoreCase);
    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }
    public string Text
    {
        get => _text;
        private set => SetProperty(ref _text, value);
    }

    public static ChatMessageItem User(string text) =>
        New("YOU", "Prompt", text, "#8993A3", "Transparent", "Inter", string.Empty);
    public static ChatMessageItem Assistant(string text) =>
        New("HARNESS", "Response", text, "#65C7D0", "Transparent", "Inter", "STREAMING");
    public static ChatMessageItem Report(string text, string status)
    {
        var item = New("REPORT", "Turn report", text, "#65C7D0", "#151A21", "Inter", status);
        return item;
    }
    public static ChatMessageItem Operation(
        string role,
        string title,
        string text,
        string color,
        bool monospace) =>
        New(
            role,
            title,
            text,
            color,
            "#151A21",
            monospace ? "Cascadia Mono, JetBrains Mono, Consolas" : "Inter",
            "RUNNING");
    public static ChatMessageItem FromStored(StoredMessage message) =>
        new(
            message.Id,
            message.Role,
            message.Title,
            message.Text,
            message.Color,
            message.Role is "YOU" or "HARNESS" ? "Transparent" : "#151A21",
            message.Monospace ? "Cascadia Mono, JetBrains Mono, Consolas" : "Inter",
            message.Status,
            message.CreatedAt);

    private static ChatMessageItem New(
        string role,
        string title,
        string text,
        string color,
        string background,
        string fontFamily,
        string status) =>
        new(
            Guid.NewGuid().ToString("N"),
            role,
            title,
            text,
            color,
            background,
            fontFamily,
            status,
            DateTimeOffset.UtcNow);
    public void Append(string delta) => Text += delta;
    public void ReplaceText(string text) => Text = text;
    public void SetStatus(string status) => Status = status.ToUpperInvariant();
}

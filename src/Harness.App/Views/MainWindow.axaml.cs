using System.Text.Json;
using System.Diagnostics;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Harness.App.ViewModels;
using Harness.Core.Models;
using Harness.Providers.Codex;
using Harness.Storage;
using Harness.Workspace;

namespace Harness.App.Views;

public sealed partial class MainWindow : Window
{
    private readonly CancellationTokenSource _lifetime = new();
    private CodexAppServerClient? _codex;
    private Task? _notificationTask;
    private Task? _requestTask;
    private string? _threadId;
    private bool _automaticRuntimeUpdateStarted;
    private HarnessStore? _store;
    private StoredProject? _activeProject;
    private StoredSession? _activeSession;
    private readonly HashSet<Task> _pendingPersistence = [];
    private readonly object _persistenceLock = new();
    private readonly GitWorkspaceClient _git = new();
    private readonly GitHubCliClient _github = new();
    private WorkingTreeWindow? _workingTreeWindow;
    private HarnessApplicationSettings _applicationSettings = new();
    private SettingsWindow? _settingsWindow;
    private ExecutionWindow? _executionWindow;
    private StoredImportSource? _activeImportSource;
    private bool _importContextApplied;
    private const string ImportContextAppliedEvent = "harness/importContextBriefV2Applied";
    private const string ContextFilesAppliedEvent = "harness/contextFilesAppliedV1";
    private readonly HashSet<string> _appliedContextContentIds = new(StringComparer.OrdinalIgnoreCase);
    private string? _appliedContextThreadId;
    private long _compactionAttempt;
    private CancellationTokenSource? _workspaceSwitch;
    private long _workspaceSwitchVersion;
    private readonly object _deltaLock = new();
    private readonly Dictionary<string, PendingUiDelta> _pendingUiDeltas = [];
    private readonly HashSet<string> _pendingGeneratedImagePaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _deltaFlushTimer;
    private long _modelSettingsPersistenceVersion;
    private long _conversationRestoreVersion;
    private long _conversationAdvanceVersion;
    private bool _suppressPermissionModeChange;
    private string _lastPermissionMode = "ask";
    private bool _isClosing;
    private bool _providerConfigurationRefreshPending;

    public MainWindow() : this(usePreviewData: false)
    {
    }

    public MainWindow(bool usePreviewData)
    {
        InitializeComponent();
        PromptBox.AddHandler(
            InputElement.KeyDownEvent,
            PromptBox_OnKeyDown,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        DataContext = new MainWindowViewModel(usePreviewData);
        ViewModel.ConversationRestored += ViewModel_OnConversationRestored;
        ViewModel.ConversationAdvanced += ViewModel_OnConversationAdvanced;
        _deltaFlushTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _deltaFlushTimer.Tick += (_, _) => FlushPendingUiDeltas();
        _deltaFlushTimer.Start();

        if (!usePreviewData)
        {
            Opened += MainWindow_OnOpened;
        }

        Closed += MainWindow_OnClosed;
    }

    private MainWindowViewModel ViewModel => (MainWindowViewModel)DataContext!;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == WindowStateProperty && MaximizeButton is not null)
        {
            UpdateMaximizeButton();
        }
    }

    private async void MainWindow_OnOpened(object? sender, EventArgs e)
    {
        await InitializePersistenceAsync();
        _ = WarmSkillCatalogAsync();
        await RefreshWorkingTreeAsync();
        await ConnectCodexAsync();
        await RefreshApiConnectionsAsync();
    }

    private async Task InitializePersistenceAsync()
    {
        try
        {
            _store = new HarnessStore(HarnessStore.DefaultDatabasePath);
            await _store.InitializeAsync(_lifetime.Token);
            _applicationSettings = await _store.LoadApplicationSettingsAsync(_lifetime.Token);
            _suppressPermissionModeChange = true;
            ViewModel.ApplyApplicationSettings(_applicationSettings);
            _lastPermissionMode = ViewModel.SelectedPermissionMode.Id;
            _suppressPermissionModeChange = false;
            var initialWorkspace = _applicationSettings.RestoreLastWorkspace
                && !string.IsNullOrWhiteSpace(_applicationSettings.LastWorkspacePath)
                && Directory.Exists(_applicationSettings.LastWorkspacePath)
                    ? _applicationSettings.LastWorkspacePath
                    : ViewModel.WorkspacePath;
            var snapshot = await _store.OpenWorkspaceAsync(
                initialWorkspace,
                _lifetime.Token);
            _activeProject = snapshot.Project;
            _activeSession = snapshot.ActiveSession;
            ViewModel.ApplyWorkspaceSnapshot(snapshot);
            await RefreshWorkspaceCatalogAsync(snapshot.Project.Id);
            await LoadImportStateAsync(snapshot.ActiveSession.Id);
            ViewModel.MessagePersistenceRequested += ViewModel_OnMessagePersistenceRequested;
            ViewModel.AddActivity("STORAGE", "Durable session store ready", "#65C7D0");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ViewModel.AddActivity("STORAGE", CleanError(exception), "#E2A84A");
        }
    }

    private async Task ConnectCodexAsync()
    {
        try
        {
            _codex = await CodexAppServerClient.StartAsync(_lifetime.Token);
            _notificationTask = ListenForNotificationsAsync(_codex, _lifetime.Token);
            _requestTask = ListenForServerRequestsAsync(_codex, _lifetime.Token);
            await ReloadModelsAsync();
            await RefreshUsageAsync();
            await ResumeActiveThreadAsync();
            if ((!_codex.Runtime.HarnessOwned || !_codex.Runtime.CodeToolsAvailable)
                && !_automaticRuntimeUpdateStarted)
            {
                _automaticRuntimeUpdateStarted = true;
                _ = InstallRuntimeAsync();
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ViewModel.SetConnectionFailure(CleanError(exception));
        }
    }

    private async void MainWindow_OnClosed(object? sender, EventArgs e)
    {
        _isClosing = true;
        _settingsWindow?.Close();
        _settingsWindow = null;
        _executionWindow?.Close();
        _executionWindow = null;
        _workingTreeWindow?.Close();
        _workingTreeWindow = null;
        _lifetime.Cancel();
        if (_apiTurnTask is not null) await _apiTurnTask;
        _workspaceSwitch?.Cancel();
        _workspaceSwitch?.Dispose();
        _workspaceSwitch = null;
        _deltaFlushTimer.Stop();
        ViewModel.ConversationRestored -= ViewModel_OnConversationRestored;
        ViewModel.ConversationAdvanced -= ViewModel_OnConversationAdvanced;
        await StopCodexAsync();
        await FlushPersistenceAsync();
        if (_store is not null)
        {
            ViewModel.MessagePersistenceRequested -= ViewModel_OnMessagePersistenceRequested;
            await _store.DisposeAsync();
            _store = null;
        }
        _lifetime.Dispose();
    }

    private void ViewModel_OnConversationRestored(object? sender, EventArgs e)
    {
        QueueSessionModelSettingsPersistence();
        var version = Interlocked.Increment(ref _conversationRestoreVersion);
        Dispatcher.UIThread.Post(() =>
        {
            if (version != Interlocked.Read(ref _conversationRestoreVersion)) return;
            ConversationScrollViewer.UpdateLayout();
            ConversationScrollViewer.ScrollToEnd();
        }, DispatcherPriority.Background);
    }

    private void ViewModel_OnConversationAdvanced(object? sender, EventArgs e)
    {
        var version = Interlocked.Increment(ref _conversationAdvanceVersion);
        Dispatcher.UIThread.Post(() =>
        {
            if (version != Interlocked.Read(ref _conversationAdvanceVersion)) return;
            ConversationScrollViewer.UpdateLayout();
            ConversationScrollViewer.ScrollToEnd();
        }, DispatcherPriority.Background);
    }

    private async void PermissionMode_OnChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressPermissionModeChange) return;
        var selected = ViewModel.SelectedPermissionMode;
        if (selected.Id == _lastPermissionMode) return;
        if (selected.Id == "full" && _lastPermissionMode != "full"
            && !await ConfirmFullAccessAsync())
        {
            _suppressPermissionModeChange = true;
            ViewModel.SelectedPermissionMode = PermissionModeOption.Resolve(_lastPermissionMode);
            _suppressPermissionModeChange = false;
            return;
        }

        _lastPermissionMode = selected.Id;
        await SaveApplicationSettingsAsync(_applicationSettings with { PermissionMode = selected.Id });
        ViewModel.AddActivity("PERMISSIONS", selected.Description, selected.Id == "full" ? "#E2A84A" : "#65C7D0");
    }

    private void SessionModelSetting_OnChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_applyingProviderModels) return;
        if (ViewModel.SelectedModel?.ProviderId.StartsWith("api-", StringComparison.Ordinal) == true)
            ViewModel.ApplyApiUsage(null, null, null, FindSelectedApiModel()?.Descriptor.ContextWindow);
        else _ = RefreshUsageAsync();
        QueueSessionModelSettingsPersistence();
    }

    private void QueueSessionModelSettingsPersistence()
    {
        if (_isClosing || _applyingProviderModels) return;
        if (_activeSession is { ProviderId.Length: > 0 } session
            && ViewModel.SelectedModel is { } model
            && !string.Equals(session.ProviderId, model.ProviderId, StringComparison.OrdinalIgnoreCase))
        {
            _threadId = null;
        }
        var version = Interlocked.Increment(ref _modelSettingsPersistenceVersion);
        TrackPersistence(PersistSessionModelSettingsAsync(version));
    }

    private async Task PersistSessionModelSettingsAsync(long version)
    {
        await Task.Delay(175);
        if (version != Interlocked.Read(ref _modelSettingsPersistenceVersion)) return;
        var store = _store;
        var session = _activeSession;
        var model = ViewModel.SelectedModel;
        if (store is null || session is null || model is null) return;

        var providerChanged = !string.IsNullOrWhiteSpace(session.ProviderId)
            && !string.Equals(session.ProviderId, model.ProviderId, StringComparison.OrdinalIgnoreCase);
        await store.UpdateSessionModelSettingsAsync(
            session.Id,
            model.ProviderId,
            model.ModelName,
            ViewModel.SelectedReasoningLevel?.Id,
            ViewModel.SelectedServiceTier?.Id);
        if (_activeSession?.Id != session.Id) return;
        if (providerChanged) _threadId = null;
        _activeSession = session with
        {
            ProviderId = model.ProviderId,
            ProviderThreadId = providerChanged ? null : session.ProviderThreadId,
            ModelId = model.ModelName,
            ReasoningEffort = ViewModel.SelectedReasoningLevel?.Id,
            ServiceTier = ViewModel.SelectedServiceTier?.Id,
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    private void OpenExecution_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_executionWindow is not null)
        {
            _executionWindow.Activate();
            return;
        }
        _executionWindow = new ExecutionWindow { DataContext = ViewModel };
        _executionWindow.Closed += (_, _) => _executionWindow = null;
        _executionWindow.Show(this);
    }

    private void OpenSettings_OnClick(object? sender, RoutedEventArgs e) => OpenSettings(openSkills: false);

    private void OpenSkills_OnClick(object? sender, RoutedEventArgs e) => OpenSettings(openSkills: true);

    private void OpenSettings(bool openSkills)
    {
        if (_store is null) return;
        if (_settingsWindow is not null)
        {
            if (openSkills) _settingsWindow.ShowSkills();
            _settingsWindow.Activate();
            return;
        }
        _settingsWindow = new SettingsWindow(
            _applicationSettings,
            ViewModel.WorkspacePath,
            SaveApplicationSettingsAsync,
            ImportConversationAsync,
            ImportHarnessProjectAsync,
            ChooseProjectFromSettingsAsync,
            () => RefreshWorkingTreeAsync(),
            _store,
            BuildSkillInstallTargets(),
            BuildSkillCompatibilityTargets(),
            openSkills,
            RefreshApiConnectionsAsync);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show(this);
    }

    private IReadOnlyList<SkillInstallTarget> BuildSkillInstallTargets()
    {
        if (ViewModel.Models.Count == 0) return [];
        return
        [
            new SkillInstallTarget(
                "openai-codex",
                $"OpenAI Codex · all connected models ({ViewModel.Models.Count})")
        ];
    }

    private IReadOnlyList<SkillCompatibilityOption> BuildSkillCompatibilityTargets() =>
        ViewModel.Models.Select(model => new SkillCompatibilityOption(
            $"openai-codex:{model.ModelName}",
            "openai-codex",
            model.ModelName,
            model.DisplayName)).ToArray();

    private async Task WarmSkillCatalogAsync()
    {
        if (_store is null) return;
        try
        {
            var cachedSources = await _store.ListSkillSourcesAsync(_lifetime.Token);
            if (cachedSources.Count > 0
                && cachedSources.All(source => source.IndexState.StartsWith("COMPLETE PATH INDEX", StringComparison.OrdinalIgnoreCase))
                && DateTimeOffset.UtcNow - cachedSources.Max(source => source.RefreshedAt) < TimeSpan.FromHours(24)) return;
            if (cachedSources.Count == 0)
            {
                var discovered = await _github.DiscoverSkillRepositoriesAsync(
                    query: null,
                    category: null,
                    repository: null,
                    maxRepositories: 1,
                    skillsPerRepository: 12,
                    hydrateMetadata: false,
                    cancellationToken: _lifetime.Token);
                await _store.UpsertSkillInventoriesAsync(discovered, _lifetime.Token);
                cachedSources = await _store.ListSkillSourcesAsync(_lifetime.Token);
            }
            if (cachedSources.Count < 6)
            {
                var candidates = await _github.DiscoverSkillSourceCandidatesAsync(6, _lifetime.Token);
                await _store.UpsertSkillInventoriesAsync(
                    candidates.Select(source => new SkillRepositoryInventory(source, [])).ToArray(),
                    _lifetime.Token);
                cachedSources = await _store.ListSkillSourcesAsync(_lifetime.Token);
            }
            foreach (var source in cachedSources)
            {
                var inventory = await _github.IndexSkillRepositoryTreeAsync(source, _lifetime.Token);
                await _store.UpsertSkillInventoriesAsync([inventory], _lifetime.Token);
                if (inventory.Skills.Count == 0)
                    await _store.RemoveSkillSourceIfEmptyAsync(source.Repository, _lifetime.Token);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch
        {
            // Startup catalog refresh is deliberately non-blocking. The Skills
            // section retains cached metadata and surfaces interactive failures.
        }
    }

    private async Task SaveApplicationSettingsAsync(HarnessApplicationSettings settings)
    {
        if (_store is null) return;
        _applicationSettings = settings with { LastWorkspacePath = ViewModel.WorkspacePath };
        await _store.SaveApplicationSettingsAsync(_applicationSettings, _lifetime.Token);
        _suppressPermissionModeChange = true;
        ViewModel.ApplyApplicationSettings(_applicationSettings);
        _lastPermissionMode = ViewModel.SelectedPermissionMode.Id;
        _suppressPermissionModeChange = false;
        _providerConfigurationRefreshPending = true;
        await RefreshActiveThreadConfigurationAsync();
    }

    private async Task ImportConversationAsync(ConversationImportPlan plan)
    {
        if (_store is null || _activeProject is null) return;
        var result = await _store.ImportConversationAsync(_activeProject.Id, plan, _lifetime.Token);
        var loaded = await _store.LoadSessionAsync(result.Session.Id, _lifetime.Token);
        _activeSession = loaded.Session;
        _threadId = null;
        ViewModel.AddStoredSession(result.Session);
        ViewModel.ApplyStoredSession(loaded.Session, loaded.Messages, loaded.Attachments);
        await LoadImportStateAsync(loaded.Session.Id);
        ViewModel.AddActivity("IMPORT", $"Imported {result.MessageCount} messages · source retained", "#65C7D0");
    }

    private async Task ImportHarnessProjectAsync(HarnessImportProject project)
    {
        if (_store is null || string.IsNullOrWhiteSpace(project.WorkspacePath))
            throw new InvalidOperationException("Choose an available project folder before importing.");

        var workspacePath = Path.GetFullPath(project.WorkspacePath);
        if (!Directory.Exists(workspacePath))
            throw new DirectoryNotFoundException($"Project folder is unavailable: {workspacePath}");

        var workspace = await _store.OpenWorkspaceAsync(workspacePath, _lifetime.Token);
        if (workspace.Sessions.Count == 1
            && workspace.ActiveSession.Title == "New session"
            && workspace.Messages.Count == 0)
        {
            await _store.DeleteSessionAsync(workspace.ActiveSession.Id, _lifetime.Token);
        }

        var importedSessions = 0;
        var importedMessages = 0;
        var skippedSessions = 0;
        foreach (var candidate in project.Conversations.OrderBy(candidate => candidate.UpdatedAt))
        {
            if (await _store.HasImportedSourceAsync(workspace.Project.Id, candidate.SourcePath, _lifetime.Token))
            {
                skippedSessions++;
                continue;
            }
            var result = await _store.ImportConversationAsync(workspace.Project.Id, candidate.Plan, _lifetime.Token);
            importedSessions++;
            importedMessages += result.MessageCount;
            foreach (var contextFile in project.ContextFiles)
            {
                await _store.AddAttachmentAsync(result.Session.Id, contextFile, cancellationToken: _lifetime.Token);
            }
        }

        await LoadWorkspaceAsync(workspacePath);
        ViewModel.AddActivity(
            "IMPORT",
            $"Imported {project.SourceHarness} project · {importedSessions} chats · {importedMessages} messages · {project.ContextFiles.Count} context files"
            + (skippedSessions == 0 ? string.Empty : $" · {skippedSessions} already imported"),
            "#65C7D0");
    }

    private async Task LoadImportStateAsync(string sessionId)
    {
        _appliedContextContentIds.Clear();
        _appliedContextThreadId = null;
        if (_store is null)
        {
            _activeImportSource = null;
            _importContextApplied = false;
            return;
        }
        var latestContextPayload = await _store.GetLatestProviderEventPayloadAsync(
            sessionId,
            ContextFilesAppliedEvent,
            _lifetime.Token);
        if (!string.IsNullOrWhiteSpace(latestContextPayload))
        {
            try
            {
                using var contextEvent = JsonDocument.Parse(latestContextPayload);
                var root = contextEvent.RootElement;
                if (root.TryGetProperty("threadId", out var threadIdElement)
                    && threadIdElement.ValueKind == JsonValueKind.String)
                {
                    _appliedContextThreadId = threadIdElement.GetString();
                }
                if (root.TryGetProperty("contentIds", out var contentIdsElement)
                    && contentIdsElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var contentId in contentIdsElement.EnumerateArray())
                    {
                        if (contentId.ValueKind == JsonValueKind.String
                            && contentId.GetString() is { Length: > 0 } value)
                        {
                            _appliedContextContentIds.Add(value);
                        }
                    }
                }
            }
            catch (JsonException exception)
            {
                ViewModel.AddActivity("CONTEXT", $"Stored attachment state could not be read: {CleanError(exception)}", "#E2A84A");
                _appliedContextContentIds.Clear();
                _appliedContextThreadId = null;
            }
        }
        _activeImportSource = await _store.GetImportSourceAsync(sessionId, _lifetime.Token);
        _importContextApplied = _activeImportSource is not null
            && await _store.HasProviderEventAsync(sessionId, ImportContextAppliedEvent, _lifetime.Token);
        var latestTokenPayload = await _store.GetLatestProviderEventPayloadAsync(
            sessionId,
            "thread/tokenUsage/updated",
            _lifetime.Token);
        if (!string.IsNullOrWhiteSpace(latestTokenPayload))
        {
            try
            {
                using var tokenEvent = JsonDocument.Parse(latestTokenPayload);
                ApplyTokenUsage(tokenEvent.RootElement);
            }
            catch (JsonException exception)
            {
                ViewModel.AddActivity("CONTEXT", $"Stored provider telemetry could not be read: {CleanError(exception)}", "#E2A84A");
            }
        }
        if (_activeImportSource is not null)
        {
            ViewModel.AddActivity(
                "IMPORT",
                _importContextApplied
                    ? "Imported continuity is active in the provider thread"
                    : "Imported continuity is ready for the next model turn",
                "#65C7D0");
        }
    }

    private async Task ChooseProjectFromSettingsAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Open a project folder",
            AllowMultiple = false
        });
        var path = folders.FirstOrDefault()?.TryGetLocalPath();
        if (path is not null) await LoadWorkspaceAsync(path);
    }

    private async void WorkspaceSelection_OnChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ViewModel.SelectedWorkspace is not { } selected
            || string.Equals(selected.Path, ViewModel.WorkspacePath, StringComparison.OrdinalIgnoreCase)) return;
        var previous = ViewModel.Workspaces.FirstOrDefault(workspace =>
            string.Equals(workspace.Path, ViewModel.WorkspacePath, StringComparison.OrdinalIgnoreCase));
        if (ViewModel.IsRunning)
        {
            ViewModel.SelectedWorkspace = previous;
            ViewModel.AddActivity("WORKSPACE", "Finish or stop the active turn before switching projects", "#E2A84A");
            return;
        }
        if (!Directory.Exists(selected.Path))
        {
            ViewModel.SelectedWorkspace = previous;
            ViewModel.AddActivity("WORKSPACE", $"Project folder is unavailable: {selected.Path}", "#E2A84A");
            return;
        }

        _workspaceSwitch?.Cancel();
        _workspaceSwitch?.Dispose();
        _workspaceSwitch = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var switchCts = _workspaceSwitch;
        var version = Interlocked.Increment(ref _workspaceSwitchVersion);
        ViewModel.BeginRepositoryRefresh(selected.Name);
        try
        {
            if (!await LoadWorkspaceAsync(selected.Path, version, switchCts.Token)
                && version == Interlocked.Read(ref _workspaceSwitchVersion))
            {
                ViewModel.SelectedWorkspace = previous;
            }
        }
        catch (OperationCanceledException) when (switchCts.IsCancellationRequested)
        {
        }
    }

    private async Task RefreshWorkspaceCatalogAsync(string activeProjectId)
    {
        if (_store is null) return;
        var projects = await _store.ListProjectsAsync(_lifetime.Token);
        ViewModel.ApplyWorkspaceCatalog(projects, activeProjectId);
    }

    private async Task StopCodexAsync()
    {
        var client = _codex;
        _codex = null;
        if (client is not null)
        {
            await client.DisposeAsync();
        }

        if (_notificationTask is not null)
        {
            try
            {
                await _notificationTask;
            }
            catch (OperationCanceledException)
            {
            }
        }
        _notificationTask = null;

        if (_requestTask is not null)
        {
            try
            {
                await _requestTask;
            }
            catch (OperationCanceledException)
            {
            }
        }
        _requestTask = null;
        _threadId = null;
    }

    private async void SendPrompt_OnClick(object? sender, RoutedEventArgs e) =>
        await SendPromptAsync();

    private async void RunOrStop_OnClick(object? sender, RoutedEventArgs e)
    {
        if (!ViewModel.IsRunning)
        {
            await SendPromptAsync();
            return;
        }

        if (_apiTurnCancellation is not null)
        {
            _apiTurnCancellation.Cancel();
            return;
        }

        if (_codex is null || _threadId is null)
        {
            return;
        }

        try
        {
            ViewModel.AddActivity("MODEL", "Stopping active turn", "#E2A84A");
            await _codex.InterruptTurnAsync(_threadId, _lifetime.Token);
        }
        catch (Exception exception)
        {
            ViewModel.CompleteTurn(CleanError(exception));
        }
    }

    private async void AttachImage_OnClick(object? sender, RoutedEventArgs e)
    {
        await AttachTurnFilesAsync(
            "image",
            "Attach images to this turn",
            new FilePickerFileType("Images")
            {
                Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp", "*.gif", "*.bmp"]
            });
    }

    private async void AttachVideo_OnClick(object? sender, RoutedEventArgs e)
    {
        await AttachTurnFilesAsync(
            "video",
            "Attach videos to this turn",
            new FilePickerFileType("Videos")
            {
                Patterns = ["*.mp4", "*.mov", "*.webm", "*.mkv", "*.avi", "*.m4v"]
            });
    }

    private async void AttachText_OnClick(object? sender, RoutedEventArgs e)
    {
        await AttachTurnFilesAsync(
            "text",
            "Attach text or code to this turn",
            new FilePickerFileType("Text and code")
            {
                Patterns =
                [
                    "*.txt", "*.md", "*.json", "*.jsonl", "*.yaml", "*.yml", "*.xml",
                    "*.csv", "*.tsv", "*.cs", "*.csproj", "*.sln", "*.py", "*.js", "*.ts",
                    "*.tsx", "*.jsx", "*.html", "*.css", "*.scss", "*.rs", "*.go", "*.java",
                    "*.c", "*.cpp", "*.h", "*.hpp", "*.toml", "*.ini", "*.log", "*.sql",
                    "*.sh", "*.ps1", "*.bat"
                ]
            });
    }

    private async Task AttachTurnFilesAsync(string kind, string title, FilePickerFileType fileType)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = true,
            FileTypeFilter = [fileType]
        });
        var paths = files
            .Select(file => file.TryGetLocalPath())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Cast<string>()
            .ToArray();
        if (paths.Length == 0) return;
        ViewModel.AddTurnAttachments(paths, kind);
        ViewModel.AddActivity("ATTACH", $"Added {paths.Length} {kind} file{(paths.Length == 1 ? string.Empty : "s")} to the next turn", "#65C7D0");
    }

    private void RemoveTurnAttachment_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string id }) ViewModel.RemoveTurnAttachment(id);
    }

    private void ChatImagePreview_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed
            || sender is not Control { Tag: string path }) return;
        OpenChatImage(path);
        e.Handled = true;
    }

    private void OpenChatImage_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: string path }) OpenChatImage(path);
    }

    private async void CopyChatImagePath_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string path } button || Clipboard is null) return;
        await Clipboard.SetTextAsync(path);
        button.Content = "COPIED";
        ViewModel.AddActivity("IMAGE", $"Copied {Path.GetFileName(path)} path", "#65C7D0");
        await Task.Delay(1_200);
        if (button.IsAttachedToVisualTree()) button.Content = "COPY PATH";
    }

    private void OpenChatImage(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                ViewModel.AddActivity("IMAGE", $"Image no longer exists · {path}", "#E2A84A");
                return;
            }
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            ViewModel.AddActivity("IMAGE", $"Opened {Path.GetFileName(path)}", "#65C7D0");
        }
        catch (Exception exception)
        {
            ViewModel.AddActivity("IMAGE", $"Could not open image · {exception.Message}", "#E2A84A");
        }
    }

    private async void ManageContext_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_store is null || _activeSession is null || ViewModel.IsRunning)
        {
            return;
        }

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Attach context files",
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("Context files")
                {
                    Patterns = ["*"]
                }
            ]
        });

        foreach (var file in files)
        {
            var path = file.TryGetLocalPath();
            if (path is null)
            {
                continue;
            }

            try
            {
                var attachment = await _store.AddAttachmentAsync(
                    _activeSession.Id,
                    path,
                    _lifetime.Token);
                ViewModel.AddContextFile(attachment);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                ViewModel.AddActivity(
                    "CONTEXT",
                    $"{Path.GetFileName(path)}: {CleanError(exception)}",
                    "#E2A84A");
            }
        }
    }

    private async void RemoveContext_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_store is null
            || sender is not Button { Tag: string attachmentId }
            || ViewModel.IsRunning)
        {
            return;
        }

        try
        {
            await _store.RemoveAttachmentAsync(attachmentId, _lifetime.Token);
            ViewModel.RemoveContextFile(attachmentId);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ViewModel.AddActivity("CONTEXT", CleanError(exception), "#E2A84A");
        }
    }

    private async void OpenWorkspace_OnClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel.IsRunning)
        {
            ViewModel.AddActivity("WORKSPACE", "Wait for the active turn to finish", "#E2A84A");
            return;
        }

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Open a workspace",
            AllowMultiple = false
        });
        var path = folders.FirstOrDefault()?.TryGetLocalPath();
        if (path is null)
        {
            return;
        }

        if (_store is null)
        {
            _threadId = null;
            ViewModel.SetWorkspace(path);
            return;
        }

        await LoadWorkspaceAsync(path);
    }

    private async Task<bool> LoadWorkspaceAsync(
        string path,
        long? switchVersion = null,
        CancellationToken cancellationToken = default)
    {
        if (_store is null)
        {
            return false;
        }

        try
        {
            _workingTreeWindow?.Close();
            _workingTreeWindow = null;
            var token = cancellationToken.CanBeCanceled ? cancellationToken : _lifetime.Token;
            var snapshot = await _store.OpenWorkspaceAsync(path, token);
            if (switchVersion is { } requested
                && requested != Interlocked.Read(ref _workspaceSwitchVersion)) return false;
            _activeProject = snapshot.Project;
            _activeSession = snapshot.ActiveSession;
            _threadId = null;
            ViewModel.ApplyWorkspaceSnapshot(snapshot);
            await RefreshWorkspaceCatalogAsync(snapshot.Project.Id);
            if (switchVersion is { } catalogVersion
                && catalogVersion != Interlocked.Read(ref _workspaceSwitchVersion)) return false;
            ViewModel.ApplySessionModelSettings(snapshot.ActiveSession);
            await LoadImportStateAsync(snapshot.ActiveSession.Id);
            if (switchVersion is { } importVersion
                && importVersion != Interlocked.Read(ref _workspaceSwitchVersion)) return false;
            _applicationSettings = _applicationSettings with { LastWorkspacePath = path };
            await _store.SaveApplicationSettingsAsync(_applicationSettings, token);
            _ = CompleteWorkspaceActivationAsync(path, switchVersion, token);
            return true;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested || cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception exception)
        {
            ViewModel.AddActivity("STORAGE", CleanError(exception), "#E2A84A");
            return false;
        }
    }

    private async Task CompleteWorkspaceActivationAsync(
        string workspacePath,
        long? switchVersion,
        CancellationToken cancellationToken)
    {
        try
        {
            if (switchVersion is { } requested
                && requested != Interlocked.Read(ref _workspaceSwitchVersion)) return;
            await RefreshWorkingTreeAsync(workspacePath, cancellationToken);
            if (switchVersion is { } current
                && current != Interlocked.Read(ref _workspaceSwitchVersion)) return;
            await ResumeActiveThreadAsync();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested || cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ViewModel.AddActivity("WORKSPACE", CleanError(exception), "#E2A84A");
        }
    }

    private async void NewSession_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_store is null || _activeProject is null || ViewModel.IsRunning)
        {
            return;
        }

        try
        {
            var session = await _store.CreateSessionAsync(
                _activeProject.Id,
                "New session",
                _lifetime.Token);
            _activeSession = session;
            _activeImportSource = null;
            _importContextApplied = false;
            _appliedContextContentIds.Clear();
            _appliedContextThreadId = null;
            _threadId = null;
            ViewModel.AddStoredSession(session);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ViewModel.AddActivity("STORAGE", CleanError(exception), "#E2A84A");
        }
    }

    private async void RenameSession_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_store is null
            || sender is not Button { Tag: string sessionId }
            || ViewModel.IsRunning)
        {
            return;
        }

        var task = ViewModel.Tasks.FirstOrDefault(item => item.SessionId == sessionId);
        if (task is null)
        {
            return;
        }

        var input = new TextBox
        {
            Text = task.Title,
            MinWidth = 360,
            SelectionStart = 0,
            SelectionEnd = task.Title.Length
        };
        var save = new Button { Content = "SAVE", Classes = { "primary" } };
        var cancel = new Button { Content = "CANCEL" };
        var dialog = new Window
        {
            Title = "Rename session",
            Width = 460,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(18),
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = "SESSION NAME", Classes = { "micro" } },
                    input,
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { cancel, save }
                    }
                }
            }
        };
        save.Click += (_, _) => dialog.Close(input.Text?.Trim());
        cancel.Click += (_, _) => dialog.Close(null);
        var title = await dialog.ShowDialog<string?>(this);
        if (string.IsNullOrWhiteSpace(title))
        {
            return;
        }

        try
        {
            await FlushPersistenceAsync();
            await _store.RenameSessionAsync(sessionId, title, _lifetime.Token);
            ViewModel.RenameStoredSession(sessionId, title);
            if (_activeSession?.Id == sessionId)
            {
                _activeSession = _activeSession with
                {
                    Title = title,
                    UpdatedAt = DateTimeOffset.UtcNow
                };
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ViewModel.AddActivity("STORAGE", CleanError(exception), "#E2A84A");
        }
    }

    private async void DeleteSession_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_store is null
            || _activeProject is null
            || sender is not Button { Tag: string sessionId }
            || ViewModel.IsRunning)
        {
            return;
        }

        var task = ViewModel.Tasks.FirstOrDefault(item => item.SessionId == sessionId);
        if (task is null || !await ConfirmDeleteSessionAsync(task.Title))
        {
            return;
        }

        try
        {
            await FlushPersistenceAsync();
            await _store.DeleteSessionAsync(sessionId, _lifetime.Token);
            var deletedActiveSession = _activeSession?.Id == sessionId;
            ViewModel.RemoveStoredSession(sessionId);
            if (!deletedActiveSession)
            {
                return;
            }

            StoredSession nextSession;
            IReadOnlyList<StoredMessage> messages;
            IReadOnlyList<StoredAttachment> attachments;
            if (ViewModel.Tasks.Count == 0)
            {
                nextSession = await _store.CreateSessionAsync(
                    _activeProject.Id,
                    "New session",
                    _lifetime.Token);
                messages = [];
                attachments = [];
                _activeSession = nextSession;
                ViewModel.AddStoredSession(nextSession);
                _activeImportSource = null;
                _importContextApplied = false;
            }
            else
            {
                var loaded = await _store.LoadSessionAsync(
                    ViewModel.Tasks[0].SessionId,
                    _lifetime.Token);
                nextSession = loaded.Session;
                messages = loaded.Messages;
                attachments = loaded.Attachments;
                _activeSession = nextSession;
                ViewModel.ApplyStoredSession(nextSession, messages, attachments);
                await LoadImportStateAsync(nextSession.Id);
            }

            _threadId = null;
            ViewModel.ApplySessionModelSettings(nextSession);
            await ResumeActiveThreadAsync();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ViewModel.AddActivity("STORAGE", CleanError(exception), "#E2A84A");
        }
    }

    private async Task<bool> ConfirmDeleteSessionAsync(string title)
    {
        var delete = new Button { Content = "DELETE", Classes = { "primary" } };
        var cancel = new Button { Content = "CANCEL" };
        var dialog = new Window
        {
            Title = "Delete session",
            Width = 460,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(18),
                Spacing = 14,
                Children =
                {
                    new TextBlock
                    {
                        Text = $"Delete ‘{title}’ and its locally stored history?",
                        TextWrapping = TextWrapping.Wrap
                    },
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { cancel, delete }
                    }
                }
            }
        };
        delete.Click += (_, _) => dialog.Close(true);
        cancel.Click += (_, _) => dialog.Close(false);
        return await dialog.ShowDialog<bool>(this);
    }

    private async void TaskSelection_OnChanged(object? sender, SelectionChangedEventArgs e)
    {
        var selected = ViewModel.SelectedTask;
        if (_store is null
            || selected is null
            || selected.SessionId == _activeSession?.Id
            || ViewModel.IsRunning)
        {
            return;
        }

        try
        {
            var loaded = await _store.LoadSessionAsync(selected.SessionId, _lifetime.Token);
            _activeSession = loaded.Session;
            _threadId = null;
            ViewModel.ApplyStoredSession(loaded.Session, loaded.Messages, loaded.Attachments);
            await LoadImportStateAsync(loaded.Session.Id);
            ViewModel.ApplySessionModelSettings(loaded.Session);
            await ResumeActiveThreadAsync();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ViewModel.AddActivity("STORAGE", CleanError(exception), "#E2A84A");
        }
    }

    private async void ConnectOpenAi_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_codex is null)
        {
            return;
        }

        try
        {
            var login = await _codex.StartChatGptDeviceCodeLoginAsync(_lifetime.Token);
            ViewModel.SetUsageAuthenticating();
            await Launcher.LaunchUriAsync(new Uri(login.VerificationUrl));
            await ShowDeviceCodeDialogAsync(login);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ViewModel.SetUsageUnavailable(CleanError(exception));
        }
    }

    private async Task ShowDeviceCodeDialogAsync(CodexDeviceCodeLoginStart login)
    {
        var copy = new Button { Content = "COPY CODE" };
        var open = new Button { Content = "OPEN SIGN-IN PAGE", Classes = { "primary" } };
        var close = new Button { Content = "CLOSE" };
        var dialog = new Window
        {
            Title = "Connect OpenAI",
            Width = 520,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Spacing = 14,
                Children =
                {
                    new TextBlock
                    {
                        Text = "CONNECT OPENAI",
                        Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#65C7D0")),
                        FontWeight = Avalonia.Media.FontWeight.SemiBold
                    },
                    new TextBlock
                    {
                        Text = "Sign in on the OpenAI page, then enter this one-time code:",
                        TextWrapping = TextWrapping.Wrap
                    },
                    new TextBlock
                    {
                        Text = login.UserCode,
                        FontFamily = "Cascadia Mono, JetBrains Mono, Consolas",
                        FontSize = 26,
                        FontWeight = Avalonia.Media.FontWeight.SemiBold,
                        Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#D8DEE8"))
                    },
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { close, copy, open }
                    }
                }
            }
        };
        copy.Click += async (_, _) =>
        {
            if (Clipboard is not null)
            {
                await Clipboard.SetTextAsync(login.UserCode);
            }
        };
        open.Click += async (_, _) =>
            await Launcher.LaunchUriAsync(new Uri(login.VerificationUrl));
        close.Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(this);
    }

    private async void InstallRuntime_OnClick(object? sender, RoutedEventArgs e)
    {
        await InstallRuntimeAsync();
    }

    private async Task InstallRuntimeAsync()
    {
        if (ViewModel.IsInstallingRuntime || ViewModel.IsRunning)
        {
            return;
        }

        try
        {
            ViewModel.SetRuntimeInstallState("UPDATING RUNTIME", "Preparing Codex runtime update", true);
            var progress = new Progress<CodexRuntimeInstallProgress>(update =>
                ViewModel.SetRuntimeInstallState(update.State, update.Detail, update.State != "READY"));
            var installer = new CodexRuntimeInstaller();
            await installer.InstallLatestAsync(progress, _lifetime.Token);
            while (ViewModel.IsRunning)
            {
                await Task.Delay(250, _lifetime.Token);
            }
            await StopCodexAsync();
            await ConnectCodexAsync();
            ViewModel.SetRuntimeInstallState("CODEX CONNECTED", "Harness-managed runtime connected", false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ViewModel.SetRuntimeInstallState("RUNTIME UPDATE FAILED", CleanError(exception), false);
        }
    }

    private async void OpenDiff_OnClick(object? sender, RoutedEventArgs e)
    {
        if (!ViewModel.HasTurnDiff)
        {
            return;
        }

        var viewer = new DiffWindow
        {
            DataContext = new DiffWindowViewModel("Current turn", ViewModel.TurnDiff)
        };
        await viewer.ShowDialog(this);
    }

    private async Task RefreshWorkingTreeAsync(
        string? expectedWorkspacePath = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var workspacePath = expectedWorkspacePath ?? ViewModel.WorkspacePath;
            var token = cancellationToken.CanBeCanceled ? cancellationToken : _lifetime.Token;
            var snapshot = await _git.ReadStatusAsync(workspacePath, token);
            if (!string.Equals(workspacePath, ViewModel.WorkspacePath, StringComparison.OrdinalIgnoreCase)) return;
            ViewModel.ApplyWorkingTree(snapshot);
            var remote = snapshot.RepositoryRoot is { } root
                ? await _git.GetRemoteUrlAsync(root, token)
                : null;
            if (!string.Equals(workspacePath, ViewModel.WorkspacePath, StringComparison.OrdinalIgnoreCase)) return;
            ViewModel.ApplyRepositoryRemote(remote);
            var github = await _github.GetConnectionStatusAsync(token);
            if (!string.Equals(workspacePath, ViewModel.WorkspacePath, StringComparison.OrdinalIgnoreCase)) return;
            ViewModel.ApplyGitHubConnection(github.IsAuthenticated);
            ViewModel.SetRepositoryOperationStatus(snapshot.IsRepository
                ? $"{snapshot.Branch ?? "Current branch"} ready"
                : "Git is not configured for this workspace");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested || cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async void RepositoryDock_OnClick(object? sender, RoutedEventArgs e)
    {
        await ShowRepositorySetupAsync();
    }

    private async void RepositoryCommit_OnClick(object? sender, RoutedEventArgs e)
    {
        var workspacePath = ViewModel.WorkspacePath;
        var repository = await ResolveRepositoryForActionAsync(workspacePath, "commit");
        if (repository is null) return;
        if (repository.Snapshot.Files.Count == 0)
        {
            ViewModel.SetRepositoryOperationStatus($"Nothing to commit · {repository.Branch} is clean");
            return;
        }
        var request = await AskForCommitMessageAsync(repository.Root);
        if (request is null) return;
        repository = await ResolveRepositoryForActionAsync(workspacePath, "commit");
        if (repository is null) return;
        var committed = false;
        await RunRepositoryActionAsync(
            "COMMIT",
            $"Creating commit on {repository.Branch}…",
            () => committed
                ? $"Changes committed on {repository.Branch}"
                : $"Nothing to commit · {repository.Branch} is clean",
            async () =>
            {
                await _git.ConfigureIdentityAsync(repository.Root, request.Identity.Name, request.Identity.Email, _lifetime.Token);
                await SaveGitIdentityDefaultsAsync(request.Identity);
                committed = await _git.CommitAllAsync(repository.Root, request.Message, _lifetime.Token);
            });
    }

    private async void RepositoryPull_OnClick(object? sender, RoutedEventArgs e)
    {
        var workspacePath = ViewModel.WorkspacePath;
        var repository = await ResolveRepositoryForActionAsync(workspacePath, "pull");
        if (repository is null) return;
        await RunRepositoryActionAsync(
            "PULL", $"Pulling {repository.Branch} from origin…", () => $"{repository.Branch} is up to date",
            () => _git.PullAsync(repository.Root, _lifetime.Token));
    }

    private async void RepositoryPush_OnClick(object? sender, RoutedEventArgs e)
    {
        var workspacePath = ViewModel.WorkspacePath;
        var repository = await ResolveRepositoryForActionAsync(workspacePath, "push");
        if (repository is null) return;
        IReadOnlyList<GitExcludedFile> excluded = [];
        await RunRepositoryActionAsync(
            "PUSH",
            $"Checking and pushing {repository.Branch}…",
            () => excluded.Count == 0
                ? $"{repository.Branch} pushed"
                : $"{repository.Branch} pushed · {excluded.Count} oversized file(s) excluded",
            async () =>
            {
                excluded = await _git.ExcludeOversizedFilesAsync(repository.Root, cancellationToken: _lifetime.Token);
                var trackedOversized = excluded.Any(file => file.WasTracked);
                if (trackedOversized && await _git.GetCommitCountAsync(repository.Root, _lifetime.Token) > 1)
                    throw new InvalidOperationException("An oversized file exists in multi-commit history. Use Git LFS or git filter-repo before pushing.");
                if (trackedOversized)
                {
                    await _git.PrepareForInitialPushAsync(
                        repository.Root,
                        "Initial commit",
                        amendSingleInitialCommit: true,
                        cancellationToken: _lifetime.Token);
                }
                await _git.PushAsync(repository.Root, _lifetime.Token);
            });
    }

    private async Task<ActiveRepository?> ResolveRepositoryForActionAsync(string workspacePath, string action)
    {
        var snapshot = await _git.ReadStatusAsync(workspacePath, _lifetime.Token);
        if (!string.Equals(workspacePath, ViewModel.WorkspacePath, StringComparison.OrdinalIgnoreCase))
        {
            ViewModel.SetRepositoryOperationStatus($"Cancelled {action} · active workspace changed");
            return null;
        }
        ViewModel.ApplyWorkingTree(snapshot);
        if (!snapshot.IsRepository || snapshot.RepositoryRoot is null)
        {
            ViewModel.SetRepositoryOperationStatus($"Cannot {action} · Git is not configured", isError: true);
            return null;
        }
        return new ActiveRepository(
            snapshot.RepositoryRoot,
            snapshot.Branch ?? "current branch",
            snapshot);
    }

    private async Task RunRepositoryActionAsync(
        string kind,
        string pending,
        Func<string> success,
        Func<Task> action)
    {
        try
        {
            ViewModel.SetRepositoryOperationStatus(pending, isPending: true);
            ViewModel.AddActivity(kind, pending, "#E2A84A", true);
            await action();
            var completed = success();
            ViewModel.SetRepositoryOperationStatus(completed);
            ViewModel.AddActivity(kind, completed, "#65C7D0");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            var error = CleanError(exception);
            ViewModel.SetRepositoryOperationStatus(error, isError: true);
            ViewModel.AddActivity(kind, error, "#E2A84A");
        }
        finally
        {
            await RefreshWorkingTreeAsync();
        }
    }

    private async Task<CommitRequest?> AskForCommitMessageAsync(string repositoryRoot)
    {
        var identity = await ResolveGitIdentityAsync(repositoryRoot);
        var message = new TextBox { Watermark = "Describe the change", MinWidth = 430 };
        var authorName = new TextBox { Text = identity.Name, Watermark = "Commit author name" };
        var authorEmail = new TextBox { Text = identity.Email, Watermark = "name@example.com" };
        var identityGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,10,*") };
        Grid.SetColumn(authorEmail, 2);
        identityGrid.Children.Add(authorName);
        identityGrid.Children.Add(authorEmail);
        var commit = new Button { Content = "COMMIT ALL CHANGES", Classes = { "primary" } };
        var cancel = new Button { Content = "CANCEL", Classes = { "ghost" } };
        var dialog = CreateWorkspaceDialog(
            "Commit workspace changes",
            new StackPanel
            {
                Spacing = 11,
                Children =
                {
                    new TextBlock { Text = "Commit changes", FontSize = 19, FontWeight = FontWeight.SemiBold },
                    new TextBlock
                    {
                        Text = "All current workspace changes, including new files, will be staged and committed.",
                        Classes = { "muted" },
                        TextWrapping = TextWrapping.Wrap
                    },
                    message,
                    new TextBlock { Text = "COMMIT IDENTITY", Classes = { "micro" } },
                    identityGrid,
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Spacing = 7,
                        Children = { cancel, commit }
                    }
                }
            });
        commit.Click += (_, _) => dialog.Close(new CommitRequest(
            message.Text?.Trim() ?? string.Empty,
            new GitIdentity(authorName.Text?.Trim() ?? string.Empty, authorEmail.Text?.Trim() ?? string.Empty)));
        cancel.Click += (_, _) => dialog.Close(null);
        dialog.Opened += (_, _) => message.Focus();
        return await dialog.ShowDialog<CommitRequest?>(this);
    }

    private async Task ShowRepositorySetupAsync()
    {
        var workspacePath = ViewModel.WorkspacePath;
        var repositorySnapshot = await _git.ReadStatusAsync(workspacePath, _lifetime.Token);
        var currentRemote = repositorySnapshot.RepositoryRoot is { } repositoryRoot
            ? await _git.GetRemoteUrlAsync(repositoryRoot, _lifetime.Token)
            : null;
        var remoteUrl = new TextBox
        {
            Text = currentRemote ?? string.Empty,
            Watermark = "https://github.com/owner/repository.git",
            MinWidth = 460
        };
        var repositoryName = new TextBox { Text = ViewModel.WorkspaceName, Watermark = "repository-name" };
        var branchName = new TextBox
        {
            Text = !string.IsNullOrWhiteSpace(repositorySnapshot.Branch)
                ? repositorySnapshot.Branch
                : string.IsNullOrWhiteSpace(_applicationSettings.DefaultGitBranch)
                ? "main"
                : _applicationSettings.DefaultGitBranch,
            Watermark = "main"
        };
        var privateRepository = new CheckBox { Content = "Private repository", IsChecked = true };
        var githubConnection = await _github.GetConnectionStatusAsync(_lifetime.Token);
        var identity = await ResolveGitIdentityAsync(ViewModel.WorkspacePath);
        var authorName = new TextBox { Text = identity.Name, Watermark = "Commit author name" };
        var authorEmail = new TextBox { Text = identity.Email, Watermark = "name@example.com" };
        var setupIdentityGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,10,*") };
        Grid.SetColumn(authorEmail, 2);
        setupIdentityGrid.Children.Add(authorName);
        setupIdentityGrid.Children.Add(authorEmail);
        var status = new TextBlock
        {
            Text = githubConnection.Message,
            Classes = { "muted" },
            TextWrapping = TextWrapping.Wrap
        };
        var initialize = new Button { Content = "INITIALIZE LOCAL", Classes = { "ghost" } };
        var setBranch = new Button { Content = "SET BRANCH", Classes = { "ghost" } };
        var openWorkingTree = new Button
        {
            Content = "OPEN WORKING TREE",
            Classes = { "ghost" },
            IsEnabled = repositorySnapshot.IsRepository
        };
        var attach = new Button { Content = "ATTACH ORIGIN", Classes = { "ghost" } };
        var signIn = new Button { Content = "SIGN IN TO GITHUB", Classes = { "ghost" } };
        var create = new Button { Content = "CREATE ON GITHUB", Classes = { "primary" } };
        var cancel = new Button { Content = "CANCEL", Classes = { "ghost" } };
        var shouldOpenWorkingTree = false;
        var dialog = CreateWorkspaceDialog(
            "Repository controls",
            new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = "Repository controls", FontSize = 19, FontWeight = FontWeight.SemiBold },
                    new TextBlock
                    {
                        Text = "Manage this workspace's local repository, current branch, origin remote, and GitHub publication without leaving the workspace.",
                        Classes = { "muted" },
                        TextWrapping = TextWrapping.Wrap
                    },
                    status,
                    new TextBlock { Text = "LOCAL REPOSITORY", Classes = { "micro" } },
                    new TextBlock
                    {
                        Text = repositorySnapshot.IsRepository
                            ? $"Current branch: {repositorySnapshot.Branch ?? "no commits"}"
                            : "Git has not been initialized for this workspace.",
                        Classes = { "muted" }
                    },
                    new StackPanel
                    {
                        Spacing = 4,
                        Children =
                        {
                            new TextBlock { Text = "CURRENT / INITIAL BRANCH", Classes = { "micro" }, FontSize = 8 },
                            branchName
                        }
                    },
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        Spacing = 7,
                        Children = { initialize, setBranch, openWorkingTree }
                    },
                    new Border { Height = 1, Background = Brush.Parse("#29313C") },
                    new TextBlock { Text = "ORIGIN REMOTE", Classes = { "micro" } },
                    remoteUrl,
                    attach,
                    new Border { Height = 1, Background = Brush.Parse("#29313C") },
                    new TextBlock { Text = "NEW GITHUB REPOSITORY", Classes = { "micro" } },
                    new TextBlock { Text = "Harness will commit the current workspace and push it immediately.", Classes = { "muted" } },
                    new StackPanel
                    {
                        Spacing = 4,
                        Children =
                        {
                            new TextBlock { Text = "REPOSITORY NAME", Classes = { "micro" }, FontSize = 8 },
                            repositoryName
                        }
                    },
                    privateRepository,
                    new TextBlock { Text = "COMMIT IDENTITY", Classes = { "micro" } },
                    setupIdentityGrid,
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        Spacing = 7,
                        Children = { signIn, create }
                    },
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Children = { cancel }
                    }
                }
            });

        async Task RunSetupAsync(string pending, Func<string> success, Func<Task> action)
        {
            try
            {
                status.Text = pending;
                await action();
                await RefreshWorkingTreeAsync();
                ViewModel.SetRepositoryOperationStatus(success());
                dialog.Close(true);
            }
            catch (Exception exception)
            {
                var error = CleanError(exception);
                status.Text = error;
                status.Foreground = Brush.Parse("#E2A84A");
                ViewModel.SetRepositoryOperationStatus(error, isError: true);
            }
        }

        initialize.Click += async (_, _) => await RunSetupAsync(
            "Initializing local repository…",
            () => $"Local Git repository initialized on {branchName.Text?.Trim()}",
            async () =>
            {
                await _git.InitializeRepositoryAsync(workspacePath, _lifetime.Token, branchName.Text ?? "main");
                await _git.RenameCurrentBranchAsync(workspacePath, branchName.Text ?? "main", _lifetime.Token);
                await SaveDefaultGitBranchAsync(branchName.Text);
            });
        var branchResult = $"Current branch set to {branchName.Text?.Trim()}";
        setBranch.Click += async (_, _) => await RunSetupAsync(
            $"Setting branch to {branchName.Text?.Trim()}…",
            () => branchResult,
            async () =>
            {
                await _git.InitializeRepositoryAsync(workspacePath, _lifetime.Token, branchName.Text ?? "main");
                branchResult = await RenameWorkspaceBranchAsync(
                    workspacePath,
                    branchName.Text ?? "main",
                    _lifetime.Token);
                await SaveDefaultGitBranchAsync(branchName.Text);
            });
        attach.Click += async (_, _) => await RunSetupAsync(
            "Attaching origin…",
            () => "Origin remote attached",
            async () =>
            {
                await _git.InitializeRepositoryAsync(workspacePath, _lifetime.Token, branchName.Text ?? "main");
                await _git.RenameCurrentBranchAsync(workspacePath, branchName.Text ?? "main", _lifetime.Token);
                await _git.SetOriginAsync(workspacePath, remoteUrl.Text ?? string.Empty, _lifetime.Token);
                await SaveDefaultGitBranchAsync(branchName.Text);
            });
        var publishResult = "Repository published to GitHub";
        create.Click += async (_, _) => await RunSetupAsync(
            "Committing workspace and publishing to GitHub…",
            () => publishResult,
            async () =>
            {
                var commitIdentity = new GitIdentity(
                    authorName.Text?.Trim() ?? string.Empty,
                    authorEmail.Text?.Trim() ?? string.Empty);
                var excluded = await PublishRepositoryAsync(
                    repositoryName.Text ?? string.Empty,
                    privateRepository.IsChecked == true,
                    branchName.Text ?? "main",
                    commitIdentity);
                publishResult = excluded.Count == 0
                    ? "Repository committed and pushed"
                    : $"Repository pushed · {excluded.Count} oversized file(s) excluded";
            });
        signIn.Click += async (_, _) =>
        {
            try
            {
                status.Text = "Complete GitHub sign-in in your browser…";
                await _github.SignInAsync(_lifetime.Token);
                await RefreshWorkingTreeAsync();
                var connection = await _github.GetConnectionStatusAsync(_lifetime.Token);
                status.Text = connection.Message;
                status.Foreground = Brush.Parse("#65C7D0");
                try
                {
                    var profile = await _github.GetAuthenticatedUserAsync(_lifetime.Token);
                    if (string.IsNullOrWhiteSpace(authorName.Text)) authorName.Text = profile.Name;
                    if (string.IsNullOrWhiteSpace(authorEmail.Text)) authorEmail.Text = profile.Email;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    status.Text += $" · Enter commit identity below ({CleanError(exception)})";
                }
            }
            catch (Exception exception)
            {
                status.Text = CleanError(exception);
                status.Foreground = Brush.Parse("#E2A84A");
            }
        };
        openWorkingTree.Click += (_, _) =>
        {
            shouldOpenWorkingTree = true;
            dialog.Close(false);
        };
        cancel.Click += (_, _) => dialog.Close(false);
        await dialog.ShowDialog<bool>(this);
        if (shouldOpenWorkingTree) OpenWorkingTreeModule_OnClick(null, new RoutedEventArgs());
    }

    private async Task<GitIdentity> ResolveGitIdentityAsync(string workspacePath)
    {
        var current = await _git.ReadIdentityAsync(workspacePath, _lifetime.Token);
        var name = !string.IsNullOrWhiteSpace(_applicationSettings.GitAuthorName)
            ? _applicationSettings.GitAuthorName
            : current.Name;
        var email = !string.IsNullOrWhiteSpace(_applicationSettings.GitAuthorEmail)
            ? _applicationSettings.GitAuthorEmail
            : current.Email;
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(email))
        {
            var connection = await _github.GetConnectionStatusAsync(_lifetime.Token);
            if (connection.IsAuthenticated)
            {
                try
                {
                    var profile = await _github.GetAuthenticatedUserAsync(_lifetime.Token);
                    if (string.IsNullOrWhiteSpace(name)) name = profile.Name;
                    if (string.IsNullOrWhiteSpace(email)) email = profile.Email;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    ViewModel.AddActivity("GIT", $"Enter commit identity manually: {CleanError(exception)}", "#E2A84A");
                }
            }
        }
        return new GitIdentity(name, email);
    }

    private async Task SaveGitIdentityDefaultsAsync(GitIdentity identity, string? defaultBranch = null)
    {
        if (_store is null) return;
        _applicationSettings = _applicationSettings with
        {
            GitAuthorName = identity.Name.Trim(),
            GitAuthorEmail = identity.Email.Trim(),
            DefaultGitBranch = string.IsNullOrWhiteSpace(defaultBranch)
                ? _applicationSettings.DefaultGitBranch
                : defaultBranch.Trim()
        };
        await _store.SaveApplicationSettingsAsync(_applicationSettings, _lifetime.Token);
    }

    private async Task SaveDefaultGitBranchAsync(string? branchName)
    {
        if (_store is null || string.IsNullOrWhiteSpace(branchName)) return;
        _applicationSettings = _applicationSettings with { DefaultGitBranch = branchName.Trim() };
        await _store.SaveApplicationSettingsAsync(_applicationSettings, _lifetime.Token);
    }

    private async Task<string> RenameWorkspaceBranchAsync(
        string repositoryRoot,
        string branchName,
        CancellationToken cancellationToken,
        bool makeDefault = true)
    {
        var snapshot = await _git.ReadStatusAsync(repositoryRoot, cancellationToken);
        if (!snapshot.IsRepository || snapshot.RepositoryRoot is null)
            throw new InvalidOperationException("Initialize Git before renaming the branch.");

        var normalized = branchName.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            throw new InvalidOperationException("A branch name is required.");
        var oldBranch = snapshot.Branch;
        var remote = await _git.GetRemoteUrlAsync(snapshot.RepositoryRoot, cancellationToken);
        var isGitHubRemote = !string.IsNullOrWhiteSpace(remote)
            && remote.Contains("github.com", StringComparison.OrdinalIgnoreCase);
        string? oldRemoteDefault = null;
        if (isGitHubRemote)
        {
            var connection = await _github.GetConnectionStatusAsync(cancellationToken);
            if (!connection.IsAuthenticated)
                throw new InvalidOperationException("Connect GitHub before renaming a published branch.");
            oldRemoteDefault = await _github.GetDefaultBranchAsync(snapshot.RepositoryRoot, cancellationToken);
        }

        if (!string.Equals(oldBranch, normalized, StringComparison.Ordinal))
            await _git.RenameCurrentBranchAsync(snapshot.RepositoryRoot, normalized, cancellationToken);

        if (await _git.GetCommitCountAsync(snapshot.RepositoryRoot, cancellationToken) == 0)
            return $"Initial branch set to {normalized}; it will be published with the first commit";
        if (string.IsNullOrWhiteSpace(remote))
            return $"Local branch renamed to {normalized}";

        await _git.PushAsync(snapshot.RepositoryRoot, cancellationToken);
        if (!isGitHubRemote)
            return $"Branch {normalized} pushed; update the remote default branch in your hosting provider";

        var shouldChangeDefault = makeDefault
            || string.Equals(oldRemoteDefault, oldBranch, StringComparison.Ordinal);
        if (shouldChangeDefault)
            await _github.SetDefaultBranchAsync(snapshot.RepositoryRoot, normalized, cancellationToken);
        var branchToRemove = !string.IsNullOrWhiteSpace(oldBranch)
            && !string.Equals(oldBranch, normalized, StringComparison.Ordinal)
                ? oldBranch
                : makeDefault
                  && !string.IsNullOrWhiteSpace(oldRemoteDefault)
                  && !string.Equals(oldRemoteDefault, normalized, StringComparison.Ordinal)
                    ? oldRemoteDefault
                    : null;
        var removedOldBranch = branchToRemove is not null
            && await _git.RemoteBranchExistsAsync(snapshot.RepositoryRoot, branchToRemove, cancellationToken);
        if (removedOldBranch)
            await _git.DeleteRemoteBranchAsync(snapshot.RepositoryRoot, branchToRemove!, cancellationToken);
        return removedOldBranch
            ? $"Renamed {branchToRemove} to {normalized} locally and on GitHub"
            : shouldChangeDefault
                ? $"Branch {normalized} is published and set as the GitHub default"
                : $"Renamed the published branch to {normalized}";
    }

    private async Task<IReadOnlyList<GitExcludedFile>> PublishRepositoryAsync(
        string repositoryName,
        bool isPrivate,
        string branchName,
        GitIdentity identity)
    {
        var connection = await _github.GetConnectionStatusAsync(_lifetime.Token);
        if (!connection.IsAuthenticated)
            throw new InvalidOperationException("Sign in to GitHub before creating a repository.");
        await _git.InitializeRepositoryAsync(ViewModel.WorkspacePath, _lifetime.Token, branchName);
        await _git.RenameCurrentBranchAsync(ViewModel.WorkspacePath, branchName, _lifetime.Token);
        await _git.ConfigureIdentityAsync(
            ViewModel.WorkspacePath,
            identity.Name,
            identity.Email,
            _lifetime.Token);
        await SaveGitIdentityDefaultsAsync(identity, branchName);
        var excluded = await _git.ExcludeOversizedFilesAsync(
            ViewModel.WorkspacePath,
            cancellationToken: _lifetime.Token);
        var trackedOversized = excluded.Any(file => file.WasTracked);
        if (trackedOversized
            && await _git.GetCommitCountAsync(ViewModel.WorkspacePath, _lifetime.Token) > 1)
            throw new InvalidOperationException("An oversized file exists in multi-commit history. Use Git LFS or git filter-repo before publishing.");
        await _git.PrepareForInitialPushAsync(
            ViewModel.WorkspacePath,
            "Initial commit",
            amendSingleInitialCommit: trackedOversized,
            cancellationToken: _lifetime.Token);
        var remote = await _git.GetRemoteUrlAsync(ViewModel.WorkspacePath, _lifetime.Token);
        if (string.IsNullOrWhiteSpace(remote))
        {
            var existing = await _github.GetRepositoryUrlAsync(repositoryName, _lifetime.Token);
            if (existing is null)
            {
                await _github.CreateRepositoryAsync(
                    ViewModel.WorkspacePath,
                    repositoryName,
                    isPrivate,
                    _lifetime.Token);
                return excluded;
            }
            await _git.SetOriginAsync(ViewModel.WorkspacePath, existing, _lifetime.Token);
        }
        await _git.PushAsync(ViewModel.WorkspacePath, _lifetime.Token);
        return excluded;
    }

    private sealed record CommitRequest(string Message, GitIdentity Identity);
    private sealed record ActiveRepository(string Root, string Branch, WorkingTreeSnapshot Snapshot);

    private static Window CreateWorkspaceDialog(string title, Control content) => new()
    {
        Title = title,
        Width = 540,
        SizeToContent = SizeToContent.Height,
        MaxHeight = 760,
        CanResize = false,
        WindowStartupLocation = WindowStartupLocation.CenterOwner,
        Background = Brush.Parse("#11151B"),
        Content = new Border
        {
            Padding = new Thickness(18),
            Child = new ScrollViewer
            {
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                Content = content
            }
        }
    };

    private void OpenWorkingTreeModule_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_workingTreeWindow is not null)
        {
            _workingTreeWindow.Activate();
            return;
        }

        var module = new WorkingTreeWindow(
            _git,
            ViewModel.WorkspacePath,
            (root, branch, token) => RenameWorkspaceBranchAsync(root, branch, token, makeDefault: false));
        module.WorkingTreeChanged += async (_, _) => await RefreshWorkingTreeAsync();
        module.Closed += async (_, _) =>
        {
            await RefreshWorkingTreeAsync();
            _workingTreeWindow = null;
        };
        _workingTreeWindow = module;
        module.Show(this);
    }

    private async void PromptBox_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control)
            || e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            if (sender is TextBox textBox)
            {
                InsertLineBreak(textBox);
            }
            return;
        }

        await SendPromptAsync();
    }

    private static void InsertLineBreak(TextBox textBox)
    {
        var text = textBox.Text ?? string.Empty;
        var selectionStart = Math.Min(textBox.SelectionStart, textBox.SelectionEnd);
        var selectionEnd = Math.Max(textBox.SelectionStart, textBox.SelectionEnd);
        var updated = string.Concat(
            text.AsSpan(0, selectionStart),
            Environment.NewLine,
            text.AsSpan(selectionEnd));
        textBox.Text = updated;
        textBox.CaretIndex = selectionStart + Environment.NewLine.Length;
        textBox.SelectionStart = textBox.CaretIndex;
        textBox.SelectionEnd = textBox.CaretIndex;
    }

    private async Task SendPromptAsync()
    {
        var model = ViewModel.SelectedModel;
        if (model?.ProviderId.StartsWith("api-", StringComparison.Ordinal) == true)
        {
            if (_apiTurnCancellation is not null) return;
            _apiTurnTask = SendApiPromptAsync();
            await _apiTurnTask;
            return;
        }
        if (_codex is null || model is null || !ViewModel.CanSend)
        {
            return;
        }

        var turnAttachments = ViewModel.TurnAttachments
            .Select(attachment => new FilePart(
                attachment.FullPath,
                attachment.MediaType,
                attachment.DisplayName,
                attachment.Id))
            .ToArray();
        var requestedEffort = ViewModel.SelectedReasoningLevel?.Id;
        var requestedTier = ViewModel.SelectedServiceTier?.Id;
        var requestedTierLabel = ViewModel.SelectedServiceTier?.DisplayName ?? "provider default";
        ImportedContextEnvelope? importedContext = null;
        if ((_activeImportSource is not null && !_importContextApplied) || _threadId is null)
        {
            if (_store is null || _activeSession is null) return;
            try
            {
                var importedSession = await _store.LoadSessionAsync(_activeSession.Id, _lifetime.Token);
                if (importedSession.Messages.Count > 0)
                {
                    importedContext = ImportedConversationContextBuilder.Build(
                        _activeImportSource,
                        importedSession.Messages);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                ViewModel.AddActivity("CONTEXT", $"Could not prepare background continuity: {CleanError(exception)}", "#E2A84A");
                return;
            }
        }

        var attachedContextFiles = ViewModel.ContextFiles.ToArray();
        var prompt = ViewModel.BeginTurn();
        var providerPrompt = importedContext is null
            ? prompt
            : $"{importedContext.Text}\n# Current user request\n{prompt}";
        NameSessionFromFirstPrompt(prompt);
        ViewModel.AddActivity(
            "MODEL",
            $"Requested {model.ModelName} · reasoning {requestedEffort ?? "provider default"} · tier {requestedTierLabel} · turn files {turnAttachments.Length} · context {attachedContextFiles.Length}",
            "#65C7D0");
        if (importedContext is not null)
        {
            ViewModel.AddActivity(
                _activeImportSource is null ? "CONTEXT" : "IMPORT",
                $"Compact continuity brief: {importedContext.IncludedMessages}/{importedContext.TotalMessages} records · {importedContext.Text.Length:N0} characters. Raw export retained locally, not sent.",
                "#65C7D0");
        }
        try
        {
            if (_threadId is null)
            {
                _threadId = await _codex.StartThreadAsync(
                    ViewModel.WorkspacePath,
                    model.ModelName,
                    ViewModel.SelectedPermissionMode.Id,
                    _applicationSettings.PersonalInstructions,
                    _lifetime.Token);
                _providerConfigurationRefreshPending = false;
                if (_store is not null && _activeSession is not null)
                {
                    _activeSession = _activeSession with
                    {
                        ProviderId = _codex.Id,
                        ProviderThreadId = _threadId,
                        ModelId = model.ModelName,
                        ReasoningEffort = requestedEffort,
                        ServiceTier = requestedTier,
                        UpdatedAt = DateTimeOffset.UtcNow
                    };
                    TrackPersistence(_store.UpdateSessionConnectionAsync(
                        _activeSession.Id,
                        _codex.Id,
                        _threadId,
                        model.ModelName,
                        requestedEffort,
                        requestedTier));
                }
            }
            var contextStateMatchesThread = string.Equals(
                _appliedContextThreadId,
                _threadId,
                StringComparison.Ordinal);
            var pendingContextFiles = attachedContextFiles
                .Where(file => !contextStateMatchesThread || !_appliedContextContentIds.Contains(file.Sha256))
                .Select(file => new FilePart(
                    file.StoredPath,
                    file.MediaType,
                    file.DisplayName,
                    file.Sha256))
                .ToArray();
            await _codex.StartTurnAsync(
                _threadId,
                providerPrompt,
                model.ModelName,
                requestedEffort,
                requestedTier,
                ViewModel.SelectedPermissionMode.Id,
                turnAttachments,
                pendingContextFiles,
                _lifetime.Token);
            if (pendingContextFiles.Length > 0 && _store is not null && _activeSession is not null)
            {
                if (!contextStateMatchesThread)
                {
                    _appliedContextContentIds.Clear();
                }
                foreach (var file in pendingContextFiles)
                {
                    if (file.ContentId is { Length: > 0 } contentId)
                    {
                        _appliedContextContentIds.Add(contentId);
                    }
                }
                _appliedContextThreadId = _threadId;
                TrackPersistence(_store.AppendProviderEventAsync(
                    _activeSession.Id,
                    ContextFilesAppliedEvent,
                    JsonSerializer.Serialize(new
                    {
                        threadId = _appliedContextThreadId,
                        contentIds = _appliedContextContentIds.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
                        appliedAt = DateTimeOffset.UtcNow
                    })));
                ViewModel.AddActivity(
                    "CONTEXT",
                    $"Delivered {pendingContextFiles.Length} attached file{(pendingContextFiles.Length == 1 ? string.Empty : "s")} to the provider thread",
                    "#65C7D0");
            }
            if (importedContext is not null && _store is not null && _activeSession is not null)
            {
                if (_activeImportSource is not null) _importContextApplied = true;
                TrackPersistence(_store.AppendProviderEventAsync(
                    _activeSession.Id,
                    _activeImportSource is null
                        ? "harness/sessionContextReconstructed"
                        : ImportContextAppliedEvent,
                    JsonSerializer.Serialize(new
                    {
                        importedContext.TotalMessages,
                        importedContext.IncludedMessages,
                        importedContext.OmittedMessages,
                        sourceId = _activeImportSource?.Id,
                        appliedAt = DateTimeOffset.UtcNow
                    })));
            }
            ViewModel.ClearTurnAttachments();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ViewModel.CompleteTurn(CleanError(exception));
        }
    }

    private void NameSessionFromFirstPrompt(string prompt)
    {
        if (_store is null
            || _activeSession is null
            || !string.Equals(_activeSession.Title, "New session", StringComparison.Ordinal))
        {
            return;
        }

        var singleLine = string.Join(
            " ",
            prompt.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (singleLine.Length == 0)
        {
            return;
        }

        const int maximumTitleLength = 52;
        var title = singleLine.Length <= maximumTitleLength
            ? singleLine
            : $"{singleLine[..(maximumTitleLength - 1)].TrimEnd()}…";
        _activeSession = _activeSession with
        {
            Title = title,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        ViewModel.RenameStoredSession(_activeSession.Id, title);
        TrackPersistence(_store.RenameSessionAsync(_activeSession.Id, title));
    }

    private async Task ListenForNotificationsAsync(
        CodexAppServerClient client,
        CancellationToken cancellationToken)
    {
        await foreach (var notification in client.Notifications(cancellationToken))
        {
            if (!ShouldRouteCodexNotification(notification)) continue;
            PersistProviderEvent(notification);
            if (TryQueueUiDelta(notification)) continue;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                FlushPendingUiDeltas();
                if (ShouldRouteCodexNotification(notification)) HandleNotification(notification);
            });
        }
    }

    private bool ShouldRouteCodexNotification(CodexNotification notification)
    {
        if (notification.Method.StartsWith("account/", StringComparison.Ordinal)) return true;
        if (ViewModel.SelectedModel?.ProviderId.StartsWith("api-", StringComparison.Ordinal) == true) return false;
        var reportedThread = GetNullableString(notification.Parameters, "threadId");
        return reportedThread is null || string.Equals(reportedThread, _threadId, StringComparison.Ordinal);
    }

    private bool TryQueueUiDelta(CodexNotification notification)
    {
        if (!notification.Method.EndsWith("Delta", StringComparison.OrdinalIgnoreCase)
            || !TryGetString(notification.Parameters, "delta", out var delta)) return false;
        var itemId = GetNullableString(notification.Parameters, "itemId") ?? notification.Method;
        var key = notification.Method + "\0" + itemId;
        lock (_deltaLock)
        {
            if (!_pendingUiDeltas.TryGetValue(key, out var pending))
            {
                pending = new PendingUiDelta(notification.Method, itemId);
                _pendingUiDeltas[key] = pending;
            }
            pending.Append(delta);
        }
        return true;
    }

    private void FlushPendingUiDeltas()
    {
        PendingUiDelta[] pending;
        lock (_deltaLock)
        {
            if (_pendingUiDeltas.Count == 0) return;
            pending = [.. _pendingUiDeltas.Values];
            _pendingUiDeltas.Clear();
        }
        if (ViewModel.SelectedModel?.ProviderId.StartsWith("api-", StringComparison.Ordinal) == true) return;
        foreach (var delta in pending)
        {
            switch (delta.Method)
            {
                case "item/agentMessage/delta":
                    ViewModel.AppendAssistantDelta(delta.ItemId, delta.Text);
                    break;
                case "item/reasoning/summaryTextDelta":
                case "item/reasoning/textDelta":
                    ViewModel.AppendExecutionDelta(delta.ItemId, "REASONING", "Working", delta.Text, "#8993A3");
                    break;
                case "item/plan/delta":
                    ViewModel.AppendExecutionDelta(delta.ItemId, "PLAN", "Plan", delta.Text, "#65C7D0");
                    break;
                case "item/commandExecution/outputDelta":
                case "command/exec/outputDelta":
                case "process/outputDelta":
                    ViewModel.AppendExecutionDelta(delta.ItemId, "OUTPUT", "Command output", delta.Text, "#E2A84A", true);
                    break;
            }
        }
    }

    private async Task ListenForServerRequestsAsync(
        CodexAppServerClient client,
        CancellationToken cancellationToken)
    {
        await foreach (var request in client.ServerRequests(cancellationToken))
        {
            if (ViewModel.SelectedModel?.ProviderId.StartsWith("api-", StringComparison.Ordinal) == true)
            {
                await client.RejectServerRequestAsync(request, "The active session uses another provider.", cancellationToken);
                continue;
            }
            if (request.Method is not (
                "item/commandExecution/requestApproval"
                or "item/fileChange/requestApproval"
                or "item/permissions/requestApproval"))
            {
                await client.RejectServerRequestAsync(
                    request,
                    $"Harness does not yet support {request.Method}.",
                    cancellationToken);
                continue;
            }

            var approved = await ShowApprovalOnUiThreadAsync(request);
            if (request.Method == "item/permissions/requestApproval")
            {
                var granted = approved
                    && request.Parameters.TryGetProperty("permissions", out var requested)
                        ? requested.Clone()
                        : JsonSerializer.SerializeToElement(new { });
                await client.RespondToServerRequestAsync(
                    request,
                    new { permissions = granted, scope = "turn" },
                    cancellationToken);
            }
            else
            {
                await client.RespondToServerRequestAsync(
                    request,
                    new { decision = approved ? "accept" : "decline" },
                    cancellationToken);
            }
        }
    }

    private Task<bool> ShowApprovalOnUiThreadAsync(CodexServerRequest request)
    {
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                completion.TrySetResult(await ShowApprovalDialogAsync(request));
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        });
        return completion.Task;
    }

    private async Task<bool> ShowApprovalDialogAsync(CodexServerRequest request)
    {
        var parameters = request.Parameters;
        var isCommand = request.Method.Contains("commandExecution", StringComparison.Ordinal);
        var isPermissionExpansion = request.Method.Contains("permissions", StringComparison.Ordinal);
        var itemId = GetNullableString(parameters, "itemId") ?? $"approval-{Guid.NewGuid():N}";
        var title = isCommand
            ? "Approve command"
            : isPermissionExpansion ? "Approve expanded access" : "Approve file changes";
        var subject = isCommand
            ? GetNullableString(parameters, "command") ?? "Command was not reported."
            : GetNullableString(parameters, "reason")
              ?? (isPermissionExpansion
                  ? GetJsonValue(parameters, "permissions")
                  : "The model wants to modify workspace files.");
        var cwd = GetNullableString(parameters, "cwd");
        var detail = string.IsNullOrWhiteSpace(cwd) ? subject : $"{subject}\n\nWorking directory: {cwd}";
        ViewModel.StartExecutionItem(itemId, "APPROVAL", title, detail, "#E2A84A", isCommand);

        var approve = new Button { Content = "APPROVE", Classes = { "primary" }, MinWidth = 100 };
        var decline = new Button { Content = "DECLINE", MinWidth = 100 };
        var buttons = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Spacing = 8,
            Children = { decline, approve }
        };
        var dialog = new Window
        {
            Title = title,
            Width = 620,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(18),
                Spacing = 14,
                Children =
                {
                    new TextBlock
                    {
                        Text = title.ToUpperInvariant(),
                        Foreground = Avalonia.Media.Brushes.Orange,
                        FontWeight = Avalonia.Media.FontWeight.SemiBold
                    },
                    new TextBlock
                    {
                        Text = detail,
                        TextWrapping = TextWrapping.Wrap,
                        FontFamily = isCommand
                            ? "Cascadia Mono, JetBrains Mono, Consolas"
                            : "Inter"
                    },
                    buttons
                }
            }
        };
        approve.Click += (_, _) => dialog.Close(true);
        decline.Click += (_, _) => dialog.Close(false);
        var approved = await dialog.ShowDialog<bool>(this);
        ViewModel.CompleteExecutionItem(itemId, approved ? "APPROVED" : "DECLINED");
        return approved;
    }

    private async Task<bool> ConfirmFullAccessAsync()
    {
        var enable = new Button { Content = "ENABLE FULL ACCESS", Classes = { "primary" }, MinWidth = 160 };
        var cancel = new Button { Content = "KEEP SAFEGUARDS", MinWidth = 140 };
        var dialog = new Window
        {
            Title = "Enable full access",
            Width = 560,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Spacing = 14,
                Children =
                {
                    new TextBlock
                    {
                        Text = "FULL ACCESS",
                        Foreground = Avalonia.Media.Brushes.Orange,
                        FontWeight = Avalonia.Media.FontWeight.SemiBold
                    },
                    new TextBlock
                    {
                        Text = "This disables command approval and the workspace sandbox for this provider thread. The model can read, change, or delete anything your account can access.",
                        TextWrapping = TextWrapping.Wrap
                    },
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { cancel, enable }
                    }
                }
            }
        };
        enable.Click += (_, _) => dialog.Close(true);
        cancel.Click += (_, _) => dialog.Close(false);
        return await dialog.ShowDialog<bool>(this);
    }

    private void HandleNotification(CodexNotification notification)
    {
        var parameters = notification.Parameters;
        switch (notification.Method)
        {
            case "item/agentMessage/delta":
                if (TryGetString(parameters, "delta", out var delta))
                {
                    var itemId = GetNullableString(parameters, "itemId") ?? "turn-assistant";
                    ViewModel.AppendAssistantDelta(itemId, delta);
                }
                break;

            case "item/started":
                StartItem(parameters);
                break;

            case "item/completed":
                CompleteItem(parameters);
                break;

            case "item/reasoning/summaryTextDelta":
            case "item/reasoning/textDelta":
                AppendExecutionDelta(parameters, "REASONING", "Working", "#8993A3");
                break;

            case "item/plan/delta":
                AppendExecutionDelta(parameters, "PLAN", "Plan", "#65C7D0");
                break;

            case "item/commandExecution/outputDelta":
            case "command/exec/outputDelta":
            case "process/outputDelta":
                AppendExecutionDelta(parameters, "OUTPUT", "Command output", "#E2A84A", true);
                break;

            case "item/fileChange/patchUpdated":
                ApplyFileChanges(parameters, "RUNNING");
                break;

            case "turn/diff/updated":
                if (TryGetString(parameters, "diff", out var diff))
                {
                    ViewModel.SetTurnDiff(diff);
                }
                break;

            case "thread/tokenUsage/updated":
                ApplyTokenUsage(parameters);
                break;

            case "thread/compacted":
                Interlocked.Increment(ref _compactionAttempt);
                ViewModel.ConfirmContextCompacted();
                ViewModel.AddActivity("CONTEXT", "Provider context compacted into a continuation summary", "#65C7D0");
                break;

            case "thread/settings/updated":
                if (parameters.TryGetProperty("threadSettings", out var settings))
                {
                    ViewModel.ApplyEffectiveModelSettings(
                        GetNullableString(settings, "effort"),
                        GetNullableString(settings, "serviceTier"));
                }
                break;

            case "turn/completed":
                _ = CompleteTurnAsync(parameters);
                break;

            case "error":
                var willRetry = parameters.TryGetProperty("willRetry", out var retryElement)
                    && retryElement.ValueKind == JsonValueKind.True;
                var errorText = parameters.TryGetProperty("error", out var errorElement)
                    ? GetDisplayValue(errorElement, "message")
                    : "The provider reported an unknown error.";
                ViewModel.StartExecutionItem(
                    $"error-{Guid.NewGuid():N}",
                    "ERROR",
                    willRetry ? "Provider error · retrying" : "Provider error",
                    errorText,
                    "#E2A84A");
                break;

            case "account/login/completed":
                if (parameters.TryGetProperty("success", out var success)
                    && success.ValueKind == JsonValueKind.True)
                {
                    _ = RefreshUsageAsync();
                    _ = ReloadModelsAsync();
                }
                else
                {
                    ViewModel.SetUsageUnavailable(GetDisplayValue(parameters, "error"));
                }
                break;

            case "account/rateLimits/updated":
                _ = RefreshUsageAsync();
                break;
        }
    }

    private void ApplyTokenUsage(JsonElement parameters)
    {
        var activeInput = TryGetInt64(
            parameters,
            ["tokenUsage", "last", "inputTokens"],
            out var reportedInput)
                ? reportedInput
                : (long?)null;
        var cumulative = TryGetInt64(
            parameters,
            ["tokenUsage", "total", "totalTokens"],
            out var reportedCumulative)
                ? reportedCumulative
                : (long?)null;
        var contextWindow = TryGetInt64(
            parameters,
            ["tokenUsage", "modelContextWindow"],
            out var reportedWindow)
                ? reportedWindow
                : (long?)null;

        ViewModel.UpdateTokenUsage(activeInput, cumulative, contextWindow);
        if (ViewModel.IsContextCompacting
            && activeInput is > 0
            && contextWindow is > 0
            && activeInput.Value * 100d / contextWindow.Value < 70)
        {
            Interlocked.Increment(ref _compactionAttempt);
            ViewModel.SetContextCompaction(false);
            ViewModel.AddActivity("CONTEXT", "Compaction confirmed by provider active-context telemetry", "#65C7D0");
        }
    }

    private async Task ReloadModelsAsync()
    {
        if (_codex is null || ViewModel.IsRunning)
        {
            return;
        }

        var client = _codex;
        var models = new List<Harness.Core.Models.ModelDescriptor>();
        await foreach (var model in client.GetModelsAsync(_lifetime.Token))
        {
            models.Add(model);
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _applyingProviderModels = true;
            try
            {
                ViewModel.ApplyProviderModels(client.Id, models, "OpenAI Codex", client.Runtime.SourceLabel);
                if (_activeSession is not null) ViewModel.ApplySessionModelSettings(_activeSession);
            }
            finally { _applyingProviderModels = false; }
        });
    }

    private async Task ResumeActiveThreadAsync()
    {
        if (_activeImportSource is not null && !_importContextApplied)
        {
            _threadId = null;
            ViewModel.AddActivity(
                "IMPORT",
                "Starting a fresh provider thread for the compact continuity format",
                "#65C7D0");
            return;
        }
        if (_codex is null
            || _activeSession?.ProviderThreadId is not { Length: > 0 } providerThreadId
            || !string.Equals(_activeSession.ProviderId, _codex.Id, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            _threadId = await _codex.ResumeThreadAsync(
                providerThreadId,
                ViewModel.WorkspacePath,
                _activeSession.ModelId,
                ViewModel.SelectedPermissionMode.Id,
                _applicationSettings.PersonalInstructions,
                _lifetime.Token);
            ViewModel.AddActivity("SESSION", "Provider thread resumed", "#65C7D0");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _threadId = null;
            ViewModel.AddActivity(
                "SESSION",
                $"Provider thread could not resume: {CleanError(exception)}",
                "#E2A84A");
        }
    }

    private async Task RefreshActiveThreadConfigurationAsync()
    {
        if (_codex is null
            || _activeSession?.ProviderThreadId is not { Length: > 0 } providerThreadId
            || !string.Equals(_activeSession.ProviderId, _codex.Id, StringComparison.Ordinal)
            || ViewModel.IsRunning)
        {
            return;
        }

        try
        {
            _threadId = await _codex.ResumeThreadAsync(
                providerThreadId,
                ViewModel.WorkspacePath,
                _activeSession.ModelId,
                ViewModel.SelectedPermissionMode.Id,
                _applicationSettings.PersonalInstructions,
                _lifetime.Token);
            _providerConfigurationRefreshPending = false;
            ViewModel.AddActivity(
                "SESSION",
                $"Applied personalization and {ViewModel.SelectedPermissionMode.DisplayName.ToLowerInvariant()} permissions to the provider thread",
                "#65C7D0");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ViewModel.AddActivity("SESSION", $"Could not apply provider settings: {CleanError(exception)}", "#E2A84A");
        }
    }

    private void ViewModel_OnMessagePersistenceRequested(
        object? sender,
        MessagePersistenceRequestedEventArgs request)
    {
        if (_store is null)
        {
            return;
        }

        var message = request.Message;
        TrackPersistence(_store.UpsertMessageAsync(new StoredMessage(
            message.Id,
            request.SessionId,
            0,
            message.Role,
            message.Title,
            message.Text,
            message.Status,
            message.Color,
            message.IsMonospace,
            message.CreatedAt)));
    }

    private void PersistProviderEvent(CodexNotification notification)
    {
        if (_store is not null
            && _activeSession is not null
            && ViewModel.SelectedModel?.ProviderId == "openai-codex"
            && !notification.Method.EndsWith("Delta", StringComparison.OrdinalIgnoreCase))
        {
            TrackPersistence(_store.AppendProviderEventAsync(
                _activeSession.Id,
                notification.Method,
                notification.Parameters.GetRawText()));
        }
    }

    private void TrackPersistence(Task task)
    {
        lock (_persistenceLock)
        {
            _pendingPersistence.Add(task);
        }
        _ = ObservePersistenceAsync(task);
    }

    private async Task ObservePersistenceAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception exception)
        {
            if (!_lifetime.IsCancellationRequested)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                    ViewModel.AddActivity("STORAGE", CleanError(exception), "#E2A84A"));
            }
        }
        finally
        {
            lock (_persistenceLock)
            {
                _pendingPersistence.Remove(task);
            }
        }
    }

    private async Task FlushPersistenceAsync()
    {
        Task[] pending;
        lock (_persistenceLock)
        {
            pending = [.. _pendingPersistence];
        }

        try
        {
            await Task.WhenAll(pending);
        }
        catch
        {
            // Individual persistence errors have already been surfaced by the observer.
        }
    }

    private void StartItem(JsonElement parameters)
    {
        if (!parameters.TryGetProperty("item", out var item)
            || !TryGetString(item, "type", out var type)
            || !TryGetString(item, "id", out var itemId))
        {
            return;
        }

        switch (type)
        {
            case "agentMessage":
                ViewModel.StartAssistantMessage(itemId);
                break;
            case "commandExecution":
                ViewModel.StartExecutionItem(
                    itemId,
                    "COMMAND",
                    GetDisplayValue(item, "command"),
                    GetDisplayValue(item, "cwd"),
                    "#E2A84A",
                    true);
                break;
            case "fileChange":
                ViewModel.StartExecutionItem(
                    itemId,
                    "FILES",
                    "Applying workspace changes",
                    string.Empty,
                    "#E2A84A",
                    true);
                break;
            case "webSearch":
                ViewModel.StartExecutionItem(
                    itemId,
                    "WEB",
                    "Searching the web",
                    GetDisplayValue(item, "query"),
                    "#65C7D0");
                break;
            case "mcpToolCall":
                ViewModel.StartExecutionItem(
                    itemId,
                    "TOOL",
                    $"{GetDisplayValue(item, "server")} · {GetDisplayValue(item, "tool")}",
                    GetJsonValue(item, "arguments"),
                    "#E2A84A",
                    true);
                break;
            case "dynamicToolCall":
                ViewModel.StartExecutionItem(
                    itemId,
                    "TOOL",
                    GetDisplayValue(item, "tool"),
                    GetJsonValue(item, "arguments"),
                    "#E2A84A",
                    true);
                break;
            case "reasoning":
                ViewModel.StartExecutionItem(
                    itemId,
                    "REASONING",
                    "Working",
                    string.Empty,
                    "#8993A3");
                break;
            case "plan":
                ViewModel.StartExecutionItem(
                    itemId,
                    "PLAN",
                    "Plan",
                    string.Empty,
                    "#65C7D0");
                break;
            case "imageGeneration":
                ViewModel.StartExecutionItem(
                    itemId,
                    "IMAGE",
                    "Generating image",
                    string.Empty,
                    "#65C7D0");
                break;
        }
    }

    private void CompleteItem(JsonElement parameters)
    {
        if (!parameters.TryGetProperty("item", out var item)
            || !TryGetString(item, "type", out var type)
            || !TryGetString(item, "id", out var itemId))
        {
            return;
        }

        var status = GetDisplayValue(item, "status");
        switch (type)
        {
            case "agentMessage":
                ViewModel.CompleteAssistant(
                    itemId,
                    AppendPendingGeneratedImageLinks(GetDisplayValue(item, "text")));
                break;
            case "commandExecution":
                var output = GetNullableString(item, "aggregatedOutput");
                var exit = item.TryGetProperty("exitCode", out var exitCode)
                    && exitCode.ValueKind == JsonValueKind.Number
                    ? $"EXIT {exitCode.GetInt32()}"
                    : status;
                ViewModel.CompleteExecutionItem(itemId, exit, output);
                break;
            case "fileChange":
                if (item.TryGetProperty("changes", out var changes))
                {
                    ViewModel.ApplyFileChanges(itemId, changes, status);
                }
                break;
            case "reasoning":
                var reasoning = ReadStringArray(item, "summary");
                if (string.IsNullOrWhiteSpace(reasoning))
                {
                    reasoning = ReadStringArray(item, "content");
                }
                ViewModel.CompleteExecutionItem(itemId, "COMPLETED", reasoning);
                break;
            case "plan":
                ViewModel.CompleteExecutionItem(itemId, "COMPLETED", GetNullableString(item, "text"));
                break;
            case "mcpToolCall":
            case "dynamicToolCall":
                var result = GetJsonValue(item, "result");
                if (result == "Activity started")
                {
                    result = GetJsonValue(item, "contentItems");
                }
                ViewModel.CompleteExecutionItem(itemId, status, result);
                break;
            case "webSearch":
                ViewModel.CompleteExecutionItem(itemId, "COMPLETED");
                break;
            case "imageGeneration":
                var savedPath = GetNullableString(item, "savedPath") ?? GetNullableString(item, "path");
                if (!string.IsNullOrWhiteSpace(savedPath) && File.Exists(savedPath))
                {
                    _pendingGeneratedImagePaths.Add(Path.GetFullPath(savedPath));
                }
                ViewModel.CompleteExecutionItem(itemId, status, savedPath);
                break;
        }
    }

    private string AppendPendingGeneratedImageLinks(string text)
    {
        if (_pendingGeneratedImagePaths.Count == 0) return text;
        var builder = new System.Text.StringBuilder(text.TrimEnd());
        foreach (var path in _pendingGeneratedImagePaths)
        {
            if (text.Contains(path, StringComparison.OrdinalIgnoreCase)
                || text.Contains(path.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)) continue;
            builder.AppendLine();
            builder.AppendLine();
            builder.Append("[Generated image](<");
            builder.Append(path.Replace('\\', '/'));
            builder.Append(">)");
        }
        _pendingGeneratedImagePaths.Clear();
        return builder.ToString();
    }

    private void AppendExecutionDelta(
        JsonElement parameters,
        string kind,
        string title,
        string color,
        bool monospace = false)
    {
        if (TryGetString(parameters, "itemId", out var itemId)
            && TryGetString(parameters, "delta", out var delta))
        {
            ViewModel.AppendExecutionDelta(itemId, kind, title, delta, color, monospace);
        }
    }

    private void ApplyFileChanges(JsonElement parameters, string status)
    {
        if (TryGetString(parameters, "itemId", out var itemId)
            && parameters.TryGetProperty("changes", out var changes))
        {
            ViewModel.ApplyFileChanges(itemId, changes, status);
        }
    }

    private async Task CompleteTurnAsync(JsonElement parameters)
    {
        string? error = null;
        if (parameters.TryGetProperty("turn", out var turn)
            && TryGetString(turn, "status", out var status)
            && !string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase))
        {
            error = turn.TryGetProperty("error", out var errorElement)
                ? GetDisplayValue(errorElement, "message")
                : $"Turn ended with status {status}.";
        }

        try
        {
            await ResolveTurnDiffsFromWorkspaceAsync();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ViewModel.AddActivity("DIFF", $"Could not resolve the final turn diff: {CleanError(exception)}", "#E2A84A");
        }
        finally
        {
            if (_pendingGeneratedImagePaths.Count > 0)
            {
                ViewModel.AddGeneratedImages(_pendingGeneratedImagePaths);
                _pendingGeneratedImagePaths.Clear();
            }
            ViewModel.CompleteTurn(error);
        }

        if (_providerConfigurationRefreshPending)
            await RefreshActiveThreadConfigurationAsync();

        await CompactContextIfNeededAsync();

        try
        {
            await RefreshWorkingTreeAsync();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        _ = RefreshUsageAsync();
    }

    private async Task CompactContextIfNeededAsync()
    {
        if (_codex is null || _threadId is null || !ViewModel.ShouldCompactContext()) return;
        var attempt = Interlocked.Increment(ref _compactionAttempt);
        try
        {
            ViewModel.SetContextCompaction(true);
            ViewModel.AddActivity("CONTEXT", "Context reached 85%; requesting provider-native compaction", "#E2A84A");
            using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            requestTimeout.CancelAfter(TimeSpan.FromSeconds(15));
            await _codex.CompactThreadAsync(_threadId, requestTimeout.Token);
            ViewModel.AddActivity("CONTEXT", "Provider accepted the compaction request; waiting for confirmation", "#E2A84A", true);
            _ = WatchCompactionAsync(attempt);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (OperationCanceledException)
        {
            Interlocked.Increment(ref _compactionAttempt);
            ViewModel.SetContextCompaction(false);
            ViewModel.AddActivity("CONTEXT", "The provider did not acknowledge compaction within 15 seconds", "#E2A84A");
        }
        catch (Exception exception)
        {
            Interlocked.Increment(ref _compactionAttempt);
            ViewModel.SetContextCompaction(false);
            ViewModel.AddActivity("CONTEXT", $"Compaction could not start: {CleanError(exception)}", "#E2A84A");
        }
    }

    private async Task WatchCompactionAsync(long attempt)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(45), _lifetime.Token);
            if (attempt != Interlocked.Read(ref _compactionAttempt) || !ViewModel.IsContextCompacting) return;
            Interlocked.Increment(ref _compactionAttempt);
            ViewModel.SetContextCompaction(false);
            ViewModel.AddActivity(
                "CONTEXT",
                "The provider did not publish compaction confirmation within 45 seconds. Harness will re-check usage after the next turn.",
                "#E2A84A");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    private async Task ResolveTurnDiffsFromWorkspaceAsync()
    {
        if (ViewModel.ChangedFiles.Count == 0) return;
        var snapshot = await _git.ReadStatusAsync(ViewModel.WorkspacePath, _lifetime.Token);
        if (!snapshot.IsRepository || snapshot.RepositoryRoot is null) return;
        var diffs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var changed in ViewModel.ChangedFiles)
        {
            var normalized = changed.Path.Replace('\\', '/').TrimStart('.', '/');
            var file = snapshot.Files.FirstOrDefault(candidate =>
                string.Equals(candidate.RelativePath.Replace('\\', '/'), normalized, StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith('/' + candidate.RelativePath.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));
            if (file is null) continue;
            var diff = await _git.GetDiffAsync(snapshot.RepositoryRoot, file, _lifetime.Token);
            if (!string.IsNullOrWhiteSpace(diff)) diffs[changed.Path] = diff;
        }
        ViewModel.ApplyResolvedDiffs(diffs);
    }

    private async Task RefreshUsageAsync()
    {
        if (_codex is null || ViewModel.SelectedModel?.ProviderId != _codex.Id)
        {
            return;
        }

        try
        {
            var usage = await _codex.GetUsageAsync(_lifetime.Token);
            if (usage is not null)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (ViewModel.SelectedModel?.ProviderId == _codex.Id) ViewModel.ApplyUsage(usage);
                });
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (ViewModel.SelectedModel?.ProviderId == _codex.Id) ViewModel.SetUsageUnavailable(CleanError(exception));
            });
        }
    }

    private static bool TryGetString(JsonElement element, string property, out string value)
    {
        value = string.Empty;
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(property, out var child)
            && child.ValueKind == JsonValueKind.String
            && (value = child.GetString() ?? string.Empty).Length > 0;
    }

    private static bool TryGetInt64(
        JsonElement element,
        IReadOnlyList<string> path,
        out long value)
    {
        foreach (var segment in path)
        {
            if (element.ValueKind != JsonValueKind.Object
                || !element.TryGetProperty(segment, out element))
            {
                value = 0;
                return false;
            }
        }

        return element.TryGetInt64(out value);
    }

    private static string GetDisplayValue(JsonElement element, string property)
    {
        if (TryGetString(element, property, out var value))
        {
            return value;
        }

        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(property, out var child))
        {
            return child.ToString();
        }

        return "Activity started";
    }

    private static string? GetNullableString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var child)
        && child.ValueKind == JsonValueKind.String
            ? child.GetString()
            : null;

    private static string GetJsonValue(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(property, out var child)
            || child.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return "Activity started";
        }

        return child.ValueKind == JsonValueKind.String
            ? child.GetString() ?? string.Empty
            : child.GetRawText();
    }

    private static string ReadStringArray(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(property, out var child)
            || child.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        return string.Join(
            Environment.NewLine,
            child.EnumerateArray()
                .Where(value => value.ValueKind == JsonValueKind.String)
                .Select(value => value.GetString())
                .Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    private static string CleanError(Exception exception)
    {
        const int maximumLength = 360;
        var message = exception.Message.ReplaceLineEndings(" ").Trim();
        return message.Length <= maximumLength
            ? message
            : $"{message[..maximumLength]}…";
    }

    private void TitleBar_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            ToggleMaximized();
            e.Handled = true;
            return;
        }

        BeginMoveDrag(e);
    }

    private void MinimizeWindow_OnClick(object? sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void MaximizeWindow_OnClick(object? sender, RoutedEventArgs e) =>
        ToggleMaximized();

    private void CloseWindow_OnClick(object? sender, RoutedEventArgs e) => Close();

    private void ToggleMaximized()
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
        UpdateMaximizeButton();
    }

    private void UpdateMaximizeButton()
    {
        var isMaximized = WindowState == WindowState.Maximized;
        MaximizeButton.Content = isMaximized
            ? new Grid
            {
                Width = 13,
                Height = 13,
                Children =
                {
                    new Border
                    {
                        Width = 9,
                        Height = 9,
                        BorderBrush = Brush.Parse("#D8DEE8"),
                        BorderThickness = new Thickness(1.25),
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top
                    },
                    new Border
                    {
                        Width = 9,
                        Height = 9,
                        Background = Brush.Parse("#151A21"),
                        BorderBrush = Brush.Parse("#D8DEE8"),
                        BorderThickness = new Thickness(1.25),
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom
                    }
                }
            }
            : new Border
            {
                Width = 11,
                Height = 11,
                BorderBrush = Brush.Parse("#D8DEE8"),
                BorderThickness = new Thickness(1.5)
            };
        ToolTip.SetTip(MaximizeButton, isMaximized ? "Restore" : "Maximize");
        AutomationProperties.SetName(
            MaximizeButton,
            isMaximized ? "Restore window" : "Maximize window");
    }

    private sealed class PendingUiDelta(string method, string itemId)
    {
        private const int MaximumBatchCharacters = 64 * 1024;
        private readonly System.Text.StringBuilder _text = new();
        public string Method { get; } = method;
        public string ItemId { get; } = itemId;
        public string Text => _text.ToString();
        public void Append(string value)
        {
            _text.Append(value);
            if (_text.Length > MaximumBatchCharacters)
                _text.Remove(0, _text.Length - MaximumBatchCharacters);
        }
    }
}

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Harness.App.ViewModels;
using Harness.App.Services;
using Harness.Core.Models;
using Harness.Providers.Codex;
using Harness.Storage;
using Harness.Workspace;

namespace Harness.App.Views;

public sealed partial class SettingsWindow : Window
{
    private readonly Func<HarnessApplicationSettings, Task> _save;
    private readonly Func<ConversationImportPlan, Task> _import;
    private readonly Func<HarnessImportProject, Task> _importProject;
    private readonly Func<Task> _openProject;
    private readonly Func<Task> _repositoryChanged;
    private readonly GitHubCliClient _github = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly HarnessStore? _store;
    private IReadOnlyList<SkillInstallTarget> _skillTargets;
    private readonly Func<IReadOnlyList<SkillInstallTarget>>? _readSkillTargets;
    private readonly Func<IReadOnlyList<SkillCompatibilityOption>>? _readSkillCompatibilityTargets;
    private readonly Func<bool>? _canChangeSkills;
    private readonly bool _openSkillsOnLaunch;
    private readonly Func<string, CancellationToken, Task<PortableBackupSummary>>? _createPortableBackup;
    private readonly SubscriptionIdentityActions? _subscriptionIdentityActions;
    private readonly PortableBackupService _portableBackupService = new();
    private CancellationTokenSource? _skillIntegrityCancellation;
    private int _skillLifecycleActive;
    private bool _loadingSkillCatalog;
    public event EventHandler<SettingsActivityEventArgs>? ActivityRecorded;

    public SettingsWindow() : this(
        new HarnessApplicationSettings(),
        Environment.CurrentDirectory,
        _ => Task.CompletedTask,
        _ => Task.CompletedTask,
        _ => Task.CompletedTask,
        () => Task.CompletedTask,
        () => Task.CompletedTask,
        null,
        [],
        [],
        false,
        null,
        null,
        null,
        null,
        null,
        null)
    {
    }

    public SettingsWindow(bool usePreviewData) : this()
    {
        if (usePreviewData) Opened -= SettingsWindow_OnOpened;
    }

    public SettingsWindow(
        HarnessApplicationSettings settings,
        string workspacePath,
        Func<HarnessApplicationSettings, Task> save,
        Func<ConversationImportPlan, Task> import,
        Func<HarnessImportProject, Task> importProject,
        Func<Task> openProject,
        Func<Task> repositoryChanged,
        HarnessStore? store = null,
        IReadOnlyList<SkillInstallTarget>? skillTargets = null,
        IReadOnlyList<SkillCompatibilityOption>? compatibilityTargets = null,
        bool openSkillsOnLaunch = false,
        Func<Task>? apiConnectionsChanged = null,
        Func<CancellationToken, Task<SubscriptionConnectionSnapshot>>? readCodexConnection = null,
        Func<CancellationToken, Task<CodexDeviceCodeLoginStart>>? beginCodexSignIn = null,
        Func<CancellationToken, Task>? signOutCodex = null,
        Func<string, CancellationToken, Task<PortableBackupSummary>>? createPortableBackup = null,
        SubscriptionIdentityActions? subscriptionIdentityActions = null,
        Func<IReadOnlyList<SkillInstallTarget>>? readSkillTargets = null,
        Func<IReadOnlyList<SkillCompatibilityOption>>? readSkillCompatibilityTargets = null,
        Func<bool>? canChangeSkills = null)
    {
        InitializeComponent();
        _save = save;
        _import = import;
        _importProject = importProject;
        _openProject = openProject;
        _repositoryChanged = repositoryChanged;
        _store = store;
        _skillTargets = skillTargets ?? [];
        _readSkillTargets = readSkillTargets;
        _readSkillCompatibilityTargets = readSkillCompatibilityTargets;
        _canChangeSkills = canChangeSkills;
        _openSkillsOnLaunch = openSkillsOnLaunch;
        _apiConnectionsChanged = apiConnectionsChanged;
        _readCodexConnection = readCodexConnection;
        _beginCodexSignIn = beginCodexSignIn;
        _signOutCodex = signOutCodex;
        _createPortableBackup = createPortableBackup;
        _subscriptionIdentityActions = subscriptionIdentityActions;
        DataContext = new SettingsWindowViewModel(settings, workspacePath);
        ViewModel.SetCompatibilityTargets(compatibilityTargets ?? []);
        ViewModel.SetModelPreferences(compatibilityTargets ?? []);
        ApiProviderPicker.ItemsSource = Harness.Providers.Api.ApiProviderDefinition.All;
        ApiProviderPicker.SelectedIndex = 0;
        Opened += SettingsWindow_OnOpened;
        Closed += (_, _) => _lifetime.Cancel();
    }

    private SettingsWindowViewModel ViewModel => (SettingsWindowViewModel)DataContext!;

    private void RecordActivity(
        string kind,
        string title,
        string detail = "",
        string outcome = "INFO",
        bool isMilestone = false,
        string color = "#65C7D0") =>
        ActivityRecorded?.Invoke(
            this,
            new SettingsActivityEventArgs(kind, title, detail, outcome, isMilestone, color));

    public void ShowSkills()
    {
        SettingsTabs.SelectedIndex = 5;
        SkillSearchBox?.Focus();
    }

    private async void SettingsWindow_OnOpened(object? sender, EventArgs e)
    {
        await RunAsync("Loading settings…", async () =>
        {
            if (_openSkillsOnLaunch) ShowSkills();
            await LoadApiConnectionsAsync();
            await RefreshSubscriptionIdentitiesAsync();
            await RefreshCodexConnectionAsync();
            await LoadSkillCatalogAsync();
            await RefreshGitHubAsync();
        });
    }

    private async void Save_OnClick(object? sender, RoutedEventArgs e)
    {
        await RunAsync("Saving settings…", async () =>
        {
            await _save(ViewModel.ToSettings());
            ViewModel.Status = "Settings saved";
            RecordActivity("SETTINGS", "Settings saved");
        });
    }

    private void Close_OnClick(object? sender, RoutedEventArgs e) => Close();

    private async void ExportBackup_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_createPortableBackup is null)
        {
            ViewModel.Status = "Open Settings from the Harness workspace to create a backup";
            return;
        }
        var destination = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export portable Harness backup",
            SuggestedFileName = $"Harness-{DateTime.Now:yyyy-MM-dd}.harness-backup",
            FileTypeChoices = [new FilePickerFileType("Harness portable backup") { Patterns = ["*.harness-backup"] }]
        });
        var path = destination?.TryGetLocalPath();
        if (path is null) return;
        await RunAsync("Creating portable backup…", async () =>
        {
            var summary = await _createPortableBackup(path, _lifetime.Token);
            ViewModel.Status = $"Backup exported · {summary.DisplaySize} · {summary.PayloadCount:N0} files";
            RecordActivity(
                "BACKUP",
                "Portable backup exported",
                $"{summary.DisplaySize} · {summary.PayloadCount:N0} files · {Path.GetFileName(path)}",
                "COMPLETED",
                true);
        });
    }

    private async void RestoreBackup_OnClick(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a portable Harness backup",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Harness portable backup") { Patterns = ["*.harness-backup"] }]
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path is null) return;
        if (!await ConfirmPortableRestoreAsync(path)) return;
        await RunAsync("Validating and staging backup…", async () =>
        {
            var summary = await _portableBackupService.StageRestoreAsync(path, _lifetime.Token);
            ViewModel.Status = $"Restore staged from {summary.CreatedAtUtc.ToLocalTime():g} · close and reopen Harness to apply";
            RecordActivity(
                "BACKUP",
                "Portable restore staged",
                $"Backup from {summary.CreatedAtUtc.ToLocalTime():g} will apply after restart",
                "READY",
                true,
                "#E2A84A");
        });
    }

    private async Task<bool> ConfirmPortableRestoreAsync(string path)
    {
        var restore = new Button { Content = "STAGE RESTORE", Classes = { "primary" } };
        var cancel = new Button { Content = "CANCEL" };
        var dialog = new Window
        {
            Title = "Restore Harness backup",
            Width = 620,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(20),
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = "PORTABLE RESTORE", Classes = { "micro" } },
                    new TextBlock { Text = Path.GetFileName(path), FontSize = 19, FontWeight = Avalonia.Media.FontWeight.SemiBold, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    new TextBlock { Text = "The archive will be validated now and applied the next time Harness starts. It replaces Harness history, tasks, settings, cached context, and API connection metadata.", TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    new TextBlock { Text = "Workspace source files are never changed. Credentials and sign-ins are not restored. Harness keeps the current database as harness.db.before-restore.", Classes = { "muted" }, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Spacing = 8, Children = { cancel, restore } }
                }
            }
        };
        restore.Click += (_, _) => dialog.Close(true);
        cancel.Click += (_, _) => dialog.Close(false);
        return await dialog.ShowDialog<bool>(this);
    }

    private async void OpenDiagnostics_OnClick(object? sender, RoutedEventArgs e)
    {
        await RunAsync("Opening diagnostics…", async () =>
        {
            await Task.Run(() =>
            {
                Directory.CreateDirectory(CrashDiagnosticsService.Shared.DiagnosticsDirectory);
                Process.Start(new ProcessStartInfo
                {
                    FileName = CrashDiagnosticsService.Shared.DiagnosticsDirectory,
                    UseShellExecute = true
                });
            });
            ViewModel.Status = "Diagnostics folder opened";
        });
    }

    private async void ImportConversation_OnClick(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import a conversation export",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Conversation exports") { Patterns = ["*.md", "*.txt", "*.json", "*.jsonl"] }
            ]
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path is null) return;
        await RunAsync("Scanning export…", async () =>
        {
            var plan = await ConversationImportScanner.ScanAsync(path, _lifetime.Token);
            if (!await ConfirmImportAsync(plan)) return;
            await _import(plan);
            ViewModel.Status = $"Imported {plan.Messages.Count} messages from {Path.GetFileName(path)}";
        });
    }

    private async void ScanHarnesses_OnClick(object? sender, RoutedEventArgs e)
    {
        await RunAsync("Scanning installed harness histories...", async () =>
        {
            var inventory = await HarnessHistoryScanner.ScanKnownSourcesAsync(_lifetime.Token);
            if (inventory.Conversations.Count == 0)
            {
                ViewModel.Status = inventory.Diagnostics.Count == 0
                    ? "No importable conversations found"
                    : string.Join(" | ", inventory.Diagnostics);
                return;
            }

            var selected = await ChooseHarnessProjectAsync(inventory);
            if (selected is null) return;
            if (string.IsNullOrWhiteSpace(selected.WorkspacePath) || !Directory.Exists(selected.WorkspacePath))
            {
                var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
                {
                    Title = $"Choose the local folder for {selected.ProjectName}",
                    AllowMultiple = false
                });
                var path = folders.FirstOrDefault()?.TryGetLocalPath();
                if (path is null) return;
                selected = selected with
                {
                    WorkspacePath = path,
                    ProjectName = new DirectoryInfo(path).Name
                };
            }
            if (!await ConfirmProjectImportAsync(selected)) return;
            await _importProject(selected);
            ViewModel.Status = $"Imported {selected.ProjectName} from {selected.SourceHarness}";
        });
    }

    private async Task<HarnessImportProject?> ChooseHarnessProjectAsync(HarnessImportInventory inventory)
    {
        var picker = new ComboBox
        {
            ItemsSource = inventory.Projects,
            SelectedIndex = 0,
            MinWidth = 620,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch
        };
        var details = new TextBlock
        {
            Classes = { "muted" },
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            MaxHeight = 130
        };
        void UpdateDetails()
        {
            if (picker.SelectedItem is HarnessImportProject project)
            {
                var conversations = string.Join("\n", project.Conversations.Take(8).Select(candidate =>
                    $"• {(candidate.IsPrimaryContinuation ? "LATEST CONTINUATION · " : string.Empty)}{candidate.Title} · {candidate.UpdatedAt.LocalDateTime:g}"));
                if (project.Conversations.Count > 8)
                    conversations += $"\n• …and {project.Conversations.Count - 8} more";
                details.Text = $"{project.SourceHarness}\n{project.WorkspacePath ?? "Project folder unavailable — you will choose one"}\n\n{conversations}";
            }
        }
        picker.SelectionChanged += (_, _) => UpdateDetails();
        UpdateDetails();

        var choose = new Button { Content = "PREVIEW PROJECT", Classes = { "primary" } };
        var cancel = new Button { Content = "CANCEL" };
        var dialog = new Window
        {
            Title = "Import from another harness",
            Width = 720,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(20),
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = $"FOUND {inventory.Projects.Count} PROJECTS · {inventory.Conversations.Count} CHATS", Classes = { "micro" } },
                    picker,
                    details,
                    new TextBlock
                    {
                        Text = "Choose a source project, not an isolated timestamp. Harness creates or opens its workspace, imports every detected chat, and attaches recognized project instruction files.",
                        Classes = { "muted" },
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap
                    },
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { cancel, choose }
                    }
                }
            }
        };
        choose.Click += (_, _) => dialog.Close(picker.SelectedItem as HarnessImportProject);
        cancel.Click += (_, _) => dialog.Close(null);
        return await dialog.ShowDialog<HarnessImportProject?>(this);
    }

    private async Task<bool> ConfirmProjectImportAsync(HarnessImportProject project)
    {
        var import = new Button { Content = "IMPORT PROJECT", Classes = { "primary" } };
        var cancel = new Button { Content = "CANCEL" };
        var dialog = new Window
        {
            Title = "Project import preview",
            Width = 620,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(20),
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = "PROJECT IMPORT", Classes = { "micro" } },
                    new TextBlock { Text = $"{project.SourceHarness} · {project.ProjectName}", FontSize = 20, FontWeight = Avalonia.Media.FontWeight.SemiBold },
                    new TextBlock { Text = project.WorkspacePath ?? "Workspace unavailable", Classes = { "muted", "mono" }, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    new TextBlock { Text = $"{project.Conversations.Count} chats · {project.MessageCount} messages · {project.ContextFiles.Count} context files" },
                    new TextBlock { Text = $"Opens on: {project.PrimaryConversation.Title}", Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#65C7D0")), TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    new TextBlock
                    {
                        Text = "Each chat becomes a separate Harness task under this workspace. Imported transcripts remain local and are represented to a model by a small continuation brief only when that task is continued.",
                        Classes = { "muted" },
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap
                    },
                    new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Spacing = 8, Children = { cancel, import } }
                }
            }
        };
        import.Click += (_, _) => dialog.Close(true);
        cancel.Click += (_, _) => dialog.Close(false);
        return await dialog.ShowDialog<bool>(this);
    }

    private async Task<bool> ConfirmImportAsync(ConversationImportPlan plan)
    {
        var import = new Button { Content = "IMPORT", Classes = { "primary" } };
        var cancel = new Button { Content = "CANCEL" };
        var dialog = new Window
        {
            Title = "Import preview",
            Width = 560,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(20), Spacing = 12,
                Children =
                {
                    new TextBlock { Text = "IMPORT PREVIEW", Classes = { "micro" } },
                    new TextBlock { Text = $"{plan.Messages.Count} messages · {plan.SourceKind}\nNew session: {plan.SuggestedTitle}" },
                    new TextBlock { Text = string.Join("\n", plan.Warnings), Classes = { "muted" }, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Spacing = 8, Children = { cancel, import } }
                }
            }
        };
        import.Click += (_, _) => dialog.Close(true);
        cancel.Click += (_, _) => dialog.Close(false);
        return await dialog.ShowDialog<bool>(this);
    }

    private async void OpenProject_OnClick(object? sender, RoutedEventArgs e) =>
        await RunAsync("Opening project…", _openProject);

    private async void RefreshGitHub_OnClick(object? sender, RoutedEventArgs e) => await RefreshGitHubAsync();
    private async void GitHubSignIn_OnClick(object? sender, RoutedEventArgs e) => await RunAsync("Opening GitHub device sign-in…", async () =>
    {
        await _github.SignInAsync(_lifetime.Token);
        await RefreshGitHubAsync();
        await _repositoryChanged();
        RecordActivity("GITHUB", "GitHub account connected", outcome: "COMPLETED", isMilestone: true);
        try
        {
            var profile = await _github.GetAuthenticatedUserAsync(_lifetime.Token);
            if (string.IsNullOrWhiteSpace(ViewModel.GitAuthorName)) ViewModel.GitAuthorName = profile.Name;
            if (string.IsNullOrWhiteSpace(ViewModel.GitAuthorEmail)) ViewModel.GitAuthorEmail = profile.Email;
            await _save(ViewModel.ToSettings());
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ViewModel.Status = $"GitHub connected: {exception.Message}";
        }
    });
    private async Task RefreshGitHubAsync()
    {
        await RunAsync("Checking GitHub connection…", async () =>
        {
            ViewModel.GitHubStatus = await _github.ReadStatusAsync(_lifetime.Token);
            ViewModel.Status = "Ready";
        });
    }

    private async Task LoadSkillCatalogAsync()
    {
        if (_loadingSkillCatalog) return;
        if (_store is null)
        {
            ViewModel.SkillCatalogStatus = "The Skills Library is available after Harness storage starts.";
            return;
        }
        _loadingSkillCatalog = true;
        try
        {
            if (_readSkillCompatibilityTargets is not null)
                ViewModel.SetCompatibilityTargets(_readSkillCompatibilityTargets());
            if (_readSkillTargets is not null) _skillTargets = _readSkillTargets();
            var search = ViewModel.SkillSearchText;
            var category = ViewModel.SelectedSkillCategory;
            var source = ViewModel.SelectedSkillSource;
            var provider = ViewModel.SelectedSkillCompatibility.IsAll
                ? null
                : ViewModel.SelectedSkillCompatibility.CompatibilityId;
            var snapshot = await Task.Run(async () =>
            {
                var entries = await _store.SearchSkillCatalogAsync(search, category, source, provider, _lifetime.Token);
                var installed = await _store.ListInstalledSkillsAsync(_lifetime.Token);
                var sources = await _store.ListSkillSourcesAsync(_lifetime.Token);
                return (entries, installed, sources);
            }, _lifetime.Token);
            ViewModel.ReplaceSkills(snapshot.entries, snapshot.installed, snapshot.sources, _skillTargets);
        }
        finally
        {
            _loadingSkillCatalog = false;
        }
    }

    private async void SearchSkills_OnClick(object? sender, RoutedEventArgs e) => await SearchSkillsAsync();

    private async void SyncSkillCatalog_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_store is null) return;
        await SearchSkillsAsync();
        await RunAsync("Building complete repository path indexes…", async () =>
        {
            var candidates = await Task.Run(
                () => _github.DiscoverSkillSourceCandidatesAsync(18, _lifetime.Token), _lifetime.Token);
            var sources = await Task.Run(async () =>
            {
                await _store.UpsertSkillInventoriesAsync(
                    candidates.Select(source => new SkillRepositoryInventory(source, [])).ToArray(),
                    _lifetime.Token);
                return await _store.ListSkillSourcesAsync(_lifetime.Token);
            }, _lifetime.Token);
            var completed = 0;
            foreach (var source in sources)
            {
                ViewModel.SkillCatalogStatus = $"Indexing every SKILL.md path in {source.Repository} · {completed}/{sources.Count} sources complete…";
                var inventory = await Task.Run(
                    () => _github.IndexSkillRepositoryTreeAsync(source, _lifetime.Token), _lifetime.Token);
                await Task.Run(async () =>
                {
                    await _store.UpsertSkillInventoriesAsync([inventory], _lifetime.Token);
                    if (inventory.Skills.Count == 0)
                        await _store.RemoveSkillSourceIfEmptyAsync(source.Repository, _lifetime.Token);
                }, _lifetime.Token);
                completed++;
                await LoadSkillCatalogAsync();
            }
            ViewModel.Status = $"Cataloged every skill path in {completed} GitHub source{(completed == 1 ? string.Empty : "s")}";
            ViewModel.SkillCatalogStatus = "Complete path indexes are local. Search hydrates matching descriptions; installation remains an explicit per-skill action.";
        });
    }

    private async void SkillSearch_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await SearchSkillsAsync();
    }

    private async void SkillCategory_OnChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _loadingSkillCatalog) return;
        await RunAsync("Filtering the local skill catalog…", LoadSkillCatalogAsync);
    }

    private async void SkillFilter_OnChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _loadingSkillCatalog) return;
        await RunAsync("Filtering the local skill catalog…", LoadSkillCatalogAsync);
    }

    private async void SkillTopic_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string category }) return;
        ViewModel.SelectedSkillCategory = category;
        await RunAsync("Filtering the local skill catalog…", LoadSkillCatalogAsync);
    }

    private async Task SearchSkillsAsync()
    {
        if (_store is null) return;
        await RunAsync("Searching the local skill catalog…", async () =>
        {
            await LoadSkillCatalogAsync();
            ViewModel.SkillCatalogStatus = "Finding repositories, reading their total skill counts, and caching matching descriptions…";
            var searchText = ViewModel.SkillSearchText;
            var category = ViewModel.SelectedSkillCategory;
            var selectedSource = ViewModel.SelectedSkillSource;
            var inventories = await Task.Run(() => _github.DiscoverSkillRepositoriesAsync(
                searchText,
                category,
                repository: selectedSource,
                maxRepositories: 3,
                skillsPerRepository: 18,
                cancellationToken: _lifetime.Token,
                hydrateMetadata: true), _lifetime.Token);
            await Task.Run(() => _store.UpsertSkillInventoriesAsync(inventories, _lifetime.Token), _lifetime.Token);
            await LoadSkillCatalogAsync();
            var discovered = inventories.Sum(inventory => inventory.Skills.Count);
            var reported = inventories.Sum(inventory => (long)inventory.Source.ReportedSkillCount);
            ViewModel.Status = discovered == 0
                ? "GitHub returned no additional skills"
                : $"Cached {discovered:N0} matching descriptions from sources reporting {reported:N0} skills";
            ViewModel.SkillCatalogStatus = discovered == 0
                ? "No GitHub skills matched this search."
                : $"Source inventory refreshed · {reported:N0} reported skills · {discovered:N0} matching descriptions cached this pass.";
        });
    }

    private void ViewSkillSource_OnClick(object? sender, RoutedEventArgs e)
    {
        var url = ViewModel.SelectedSkill?.Entry.SourceUrl;
        if (string.IsNullOrWhiteSpace(url)) return;
        Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
    }

    private async void InstallSkill_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: SkillCatalogItem item }) ViewModel.SelectedSkill = item;
        if (_store is null || ViewModel.SelectedSkill is not { } selected) return;
        await RunSkillLifecycleAsync($"Inspecting {selected.Name} without downloading it…", async () =>
        {
            var compatibleTargets = CompatibleTargets(selected.Entry).ToArray();
            if (compatibleTargets.Length == 0)
                throw new InvalidOperationException($"No connected provider can install a skill labeled {selected.Compatibility}.");
            var inspection = await Task.Run(
                () => _github.InspectSkillPackageAsync(selected.Entry, _lifetime.Token), _lifetime.Token);
            var request = await ConfirmSkillInstallAsync(selected.Entry, inspection, compatibleTargets);
            if (request is null)
            {
                ViewModel.Status = "Skill installation canceled before download";
                return;
            }

            var workspaceScope = request.Scope == "WORKSPACE" ? ViewModel.WorkspacePath : null;
            var installId = SkillPackageInstaller.CreateInstallId(
                selected.Entry.Id, request.Target.ProviderId, request.Scope, workspaceScope, request.Target.ModelId);
            var existing = await Task.Run(() => _store.ListInstalledSkillsAsync(_lifetime.Token), _lifetime.Token);
            if (existing.Any(item => item.Id.Equals(installId, StringComparison.Ordinal)))
                throw new InvalidOperationException($"{selected.Name} already has that target and scope. Select its installed target to enable or update it.");

            ViewModel.SkillCatalogStatus = $"Downloading the confirmed {selected.Name} package…";
            var package = await Task.Run(() => _github.DownloadSkillPackageAsync(
                selected.Entry,
                inspection,
                SkillPackageInstaller.DefaultPackageRoot,
                _lifetime.Token), _lifetime.Token);
            var installPath = request.Target.SetupKind switch
            {
                "filesystem" => await Task.Run(() => SkillPackageInstaller.InstallCodexAsync(
                    package, selected.Entry, request.Scope, ViewModel.WorkspacePath, _lifetime.Token), _lifetime.Token),
                "harness-api" => await Task.Run(() => SkillPackageInstaller.InstallHarnessApiAsync(
                    package, selected.Entry, request.Target.ProviderId, request.Scope, ViewModel.WorkspacePath,
                    request.Target.ModelId, _lifetime.Token), _lifetime.Token),
                _ => throw new InvalidOperationException($"The {request.Target.DisplayName} setup adapter is not implemented yet.")
            };
            var installed = new InstalledSkill(
                installId,
                selected.Entry.Id,
                selected.Entry.Name,
                selected.Entry.SourceRevision,
                package.PackagePath,
                installPath,
                request.Scope,
                workspaceScope,
                request.Target.ProviderId,
                request.Target.ModelId,
                package.ContentSha256,
                true,
                DateTimeOffset.UtcNow);
            await Task.Run(() => _store.SaveInstalledSkillAsync(installed, _lifetime.Token), _lifetime.Token);
            await LoadSkillCatalogAsync();
            ViewModel.Status = $"Installed {selected.Name} for {request.Target.DisplayName}";
            ViewModel.SkillCatalogStatus = request.Target.SetupKind == "harness-api"
                ? $"Installed at {installPath}. It is available to this connection on the next model turn."
                : $"Installed at {installPath}. Codex detects skill changes automatically.";
            RecordActivity(
                "SKILL",
                $"Installed · {selected.Name}",
                $"{request.Target.DisplayName} · {request.Scope.ToLowerInvariant()} scope",
                "COMPLETED",
                true);
        });
    }

    private async void SkillInstallation_OnChanged(object? sender, SelectionChangedEventArgs e)
    {
        _skillIntegrityCancellation?.Cancel();
        _skillIntegrityCancellation?.Dispose();
        var selected = ViewModel.SelectedSkillInstallation;
        if (selected is null) return;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _skillIntegrityCancellation = cancellation;
        try
        {
            var integrity = await Task.Run(
                () => SkillPackageInstaller.InspectManagedCopyAsync(selected.Installation, cancellation.Token),
                cancellation.Token);
            if (ReferenceEquals(ViewModel.SelectedSkillInstallation, selected))
                selected.SetIntegrity(integrity);
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (ReferenceEquals(_skillIntegrityCancellation, cancellation)) _skillIntegrityCancellation = null;
            cancellation.Dispose();
        }
    }

    private async void UpdateSkill_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_store is null || ViewModel.SelectedSkill is not { } selected
            || ViewModel.SelectedSkillInstallation is not { } target || !target.HasUpdate) return;
        await RunSkillLifecycleAsync($"Inspecting the {selected.Name} update…", async () =>
        {
            var installation = target.Installation;
            var integrity = await Task.Run(
                () => SkillPackageInstaller.InspectManagedCopyAsync(installation, _lifetime.Token), _lifetime.Token);
            target.SetIntegrity(integrity);
            var inspection = await Task.Run(
                () => _github.InspectSkillPackageAsync(selected.Entry, _lifetime.Token), _lifetime.Token);
            var changes = await Task.Run(
                () => DescribeSkillUpdateAsync(installation.PackagePath, inspection, _lifetime.Token), _lifetime.Token);
            var warning = IntegrityWarning(integrity);
            var confirmed = await ConfirmSkillLifecycleAsync(
                $"Update {selected.Name}",
                $"TARGET\n{target.TargetDisplay}\n\nVERSION\n{ShortRevision(installation.SourceRevision)} → {ShortRevision(selected.Entry.SourceRevision)}\n\nPACKAGE CHANGES\n{changes}\n\n{warning}\nThe pinned package downloads only after you confirm. Scripts are not run and permissions do not change.",
                integrity == ManagedSkillIntegrity.Modified ? "REPLACE MODIFIED COPY" : "DOWNLOAD UPDATE");
            if (!confirmed) { ViewModel.Status = "Skill update canceled before download"; return; }
            var package = await Task.Run(() => _github.DownloadSkillPackageAsync(
                selected.Entry, inspection, SkillPackageInstaller.DefaultPackageRoot, _lifetime.Token), _lifetime.Token);
            var update = await Task.Run(
                () => SkillPackageInstaller.UpdateAsync(installation, package, selected.Entry, _lifetime.Token),
                _lifetime.Token);
            var updated = installation with
            {
                Name = selected.Entry.Name,
                SourceRevision = selected.Entry.SourceRevision,
                PackagePath = package.PackagePath,
                InstallPath = update.InstallPath,
                ContentSha256 = package.ContentSha256,
                InstalledAt = DateTimeOffset.UtcNow
            };
            try
            {
                await Task.Run(() => _store.SaveInstalledSkillAsync(updated, _lifetime.Token), _lifetime.Token);
                SkillPackageInstaller.CommitUpdate(update);
            }
            catch
            {
                await Task.Run(() => SkillPackageInstaller.RollbackUpdateAsync(update, installation.Enabled, _lifetime.Token), _lifetime.Token);
                throw;
            }
            await LoadSkillCatalogAsync();
            ViewModel.Status = $"Updated {selected.Name} for {target.TargetDisplay}";
            ViewModel.SkillCatalogStatus = "The provider-facing copy and provenance now point to the reviewed revision.";
            RecordActivity("SKILL", $"Updated · {selected.Name}",
                $"{target.TargetDisplay} · {ShortRevision(installation.SourceRevision)} → {ShortRevision(selected.Entry.SourceRevision)}",
                "COMPLETED", true);
        });
    }

    private async void ToggleSkill_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_store is null || ViewModel.SelectedSkill is not { } selected
            || ViewModel.SelectedSkillInstallation is not { } target) return;
        var enabling = !target.Installation.Enabled;
        var verb = enabling ? "Enable" : "Disable";
        await RunSkillLifecycleAsync($"{verb}ing {selected.Name}…", async () =>
        {
            if (!await ConfirmSkillLifecycleAsync(
                    $"{verb} {selected.Name}",
                    enabling
                        ? $"Make this skill available to {target.TargetDisplay} in its saved {target.Installation.Scope.ToLowerInvariant()} scope? No package scripts will run."
                        : $"Remove this skill from {target.TargetDisplay}'s active discovery path? Its files and provenance are retained so it can be enabled again.",
                    verb.ToUpperInvariant()))
            {
                ViewModel.Status = $"Skill {verb.ToLowerInvariant()} canceled";
                return;
            }
            var changed = await Task.Run(
                () => SkillPackageInstaller.SetEnabledAsync(target.Installation, enabling, _lifetime.Token),
                _lifetime.Token);
            try
            {
                await Task.Run(() => _store.SaveInstalledSkillAsync(changed, _lifetime.Token), _lifetime.Token);
            }
            catch
            {
                await Task.Run(() => SkillPackageInstaller.SetEnabledAsync(changed, !enabling, _lifetime.Token), _lifetime.Token);
                throw;
            }
            await LoadSkillCatalogAsync();
            ViewModel.Status = $"{(enabling ? "Enabled" : "Disabled")} {selected.Name} for {target.TargetDisplay}";
            RecordActivity("SKILL", $"{(enabling ? "Enabled" : "Disabled")} · {selected.Name}",
                $"{target.TargetDisplay} · {target.Installation.Scope.ToLowerInvariant()} scope", "COMPLETED", true);
        });
    }

    private async void RemoveSkill_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_store is null || ViewModel.SelectedSkill is not { } selected
            || ViewModel.SelectedSkillInstallation is not { } target) return;
        await RunSkillLifecycleAsync($"Inspecting {selected.Name} before removal…", async () =>
        {
            var installation = target.Installation;
            var integrity = await Task.Run(
                () => SkillPackageInstaller.InspectManagedCopyAsync(installation, _lifetime.Token), _lifetime.Token);
            target.SetIntegrity(integrity);
            var copyExists = await Task.Run(() => Directory.Exists(installation.InstallPath), _lifetime.Token);
            if (!await ConfirmSkillLifecycleAsync(
                    $"Remove {selected.Name}",
                    copyExists
                        ? $"Remove this installation from {target.TargetDisplay}?\n\n{IntegrityWarning(integrity)}\nThe provider-facing folder is moved to a local recovery path. The pinned package cache is retained."
                        : $"The provider-facing copy is already missing. Remove its stale installed-state record for {target.TargetDisplay}? The pinned package cache is retained.",
                    integrity == ManagedSkillIntegrity.Modified ? "REMOVE MODIFIED COPY" : copyExists ? "REMOVE" : "CLEAR MISSING RECORD")) return;
            string? recovery = null;
            if (copyExists)
                recovery = await Task.Run(
                    () => SkillPackageInstaller.RemoveRecoverablyAsync(installation, _lifetime.Token), _lifetime.Token);
            try
            {
                await Task.Run(() => _store.DeleteInstalledSkillAsync(installation.Id, _lifetime.Token), _lifetime.Token);
            }
            catch
            {
                if (recovery is not null)
                    await Task.Run(() => SkillPackageInstaller.RestoreRemovedAsync(recovery, installation, _lifetime.Token), _lifetime.Token);
                throw;
            }
            await LoadSkillCatalogAsync();
            ViewModel.Status = $"Removed {selected.Name} from {target.TargetDisplay}";
            ViewModel.SkillCatalogStatus = recovery is null
                ? "Cleared the missing provider copy's stale installation record."
                : $"Removed from the provider. Recovery copy: {recovery}";
            RecordActivity("SKILL", $"Removed · {selected.Name}", target.TargetDisplay, "COMPLETED", true);
        });
    }

    private async Task<bool> ConfirmSkillLifecycleAsync(string title, string message, string confirmLabel)
    {
        var confirm = new Button { Content = confirmLabel, Classes = { "primary" } };
        var cancel = new Button { Content = "CANCEL" };
        var dialog = new Window
        {
            Title = title, Width = 620, SizeToContent = SizeToContent.Height, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(20), Spacing = 14,
                Children =
                {
                    new TextBlock { Text = title, FontSize = 20, FontWeight = Avalonia.Media.FontWeight.SemiBold },
                    new ScrollViewer { MaxHeight = 440, Content = new TextBlock
                        { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap } },
                    new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Spacing = 8,
                        Children = { cancel, confirm } }
                }
            }
        };
        confirm.Click += (_, _) => dialog.Close(true);
        cancel.Click += (_, _) => dialog.Close(false);
        return await dialog.ShowDialog<bool>(this);
    }

    private async Task RunSkillLifecycleAsync(string status, Func<Task> action)
    {
        if (_canChangeSkills?.Invoke() == false)
        {
            ViewModel.Status = "Finish the active turn or reopen Settings for the current workspace before changing installed skills.";
            return;
        }
        if (Interlocked.Exchange(ref _skillLifecycleActive, 1) != 0)
        {
            ViewModel.Status = "Finish the current skill operation before starting another.";
            return;
        }
        try { await RunAsync(status, action); }
        finally { Volatile.Write(ref _skillLifecycleActive, 0); }
    }

    private static async Task<string> DescribeSkillUpdateAsync(
        string packagePath,
        SkillPackageInspection next,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(packagePath))
            return $"Previous cached package unavailable · {next.FileCount:N0} files in the new revision";
        var previous = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in EnumerateSkillFilesSafely(packagePath)
                     .Where(path => !Path.GetFileName(path).Equals(".harness-package.json", StringComparison.OrdinalIgnoreCase)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = await File.ReadAllBytesAsync(file, cancellationToken);
            var prefix = Encoding.UTF8.GetBytes($"blob {bytes.Length}\0");
            var payload = new byte[prefix.Length + bytes.Length];
            Buffer.BlockCopy(prefix, 0, payload, 0, prefix.Length);
            Buffer.BlockCopy(bytes, 0, payload, prefix.Length, bytes.Length);
            previous[Path.GetRelativePath(packagePath, file).Replace(Path.DirectorySeparatorChar, '/')] =
                Convert.ToHexString(SHA1.HashData(payload)).ToLowerInvariant();
        }
        var upcoming = next.Files.ToDictionary(file => file.Path.Replace('\\', '/'), file => file.Sha,
            StringComparer.OrdinalIgnoreCase);
        var added = upcoming.Keys.Except(previous.Keys, StringComparer.OrdinalIgnoreCase).Order().ToArray();
        var removed = previous.Keys.Except(upcoming.Keys, StringComparer.OrdinalIgnoreCase).Order().ToArray();
        var changed = upcoming.Keys.Intersect(previous.Keys, StringComparer.OrdinalIgnoreCase)
            .Where(path => !upcoming[path].Equals(previous[path], StringComparison.OrdinalIgnoreCase)).Order().ToArray();
        var names = added.Select(path => "+ " + path).Concat(changed.Select(path => "~ " + path))
            .Concat(removed.Select(path => "- " + path)).Take(16).ToArray();
        var total = added.Length + changed.Length + removed.Length;
        return total == 0 ? "No file-content changes detected in GitHub metadata."
            : $"{added.Length:N0} added · {changed.Length:N0} changed · {removed.Length:N0} removed\n"
              + string.Join('\n', names) + (total > names.Length ? $"\n… {total - names.Length:N0} more" : string.Empty);
    }

    private static string IntegrityWarning(ManagedSkillIntegrity integrity) => integrity switch
    {
        ManagedSkillIntegrity.Unchanged => "LOCAL COPY\nMatches its installed integrity baseline.",
        ManagedSkillIntegrity.Modified => "WARNING\nThe provider-facing copy has local modifications. Continuing replaces or removes those changes.",
        _ => "CAUTION\nThis older installation has no integrity baseline. Harness cannot prove whether its provider-facing copy was modified."
    };

    private static IReadOnlyList<string> EnumerateSkillFilesSafely(string root)
    {
        var files = new List<string>();
        var pending = new Stack<string>();
        pending.Push(Path.GetFullPath(root));
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new UnauthorizedAccessException("The cached package contains a symbolic link or junction.");
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new UnauthorizedAccessException("The cached package contains a symbolic link or junction.");
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
                else files.Add(entry);
            }
        }
        return files;
    }

    private static string ShortRevision(string revision) => revision[..Math.Min(10, revision.Length)];

    private IEnumerable<SkillInstallTarget> CompatibleTargets(SkillCatalogEntry skill)
    {
        if (_readSkillTargets is not null) _skillTargets = _readSkillTargets();
        var anthropicOnly = skill.Compatibility.Equals("Claude Code extension", StringComparison.OrdinalIgnoreCase);
        var openAiOnly = skill.Compatibility.Equals("Codex extension", StringComparison.OrdinalIgnoreCase);
        if (skill.Compatibility.Equals("Mixed provider extensions", StringComparison.OrdinalIgnoreCase)) return [];
        return _skillTargets.Where(target =>
            (!anthropicOnly || target.CompatibilityId.Equals("anthropic-claude", StringComparison.OrdinalIgnoreCase))
            && (!openAiOnly || target.CompatibilityId.Equals("openai-codex", StringComparison.OrdinalIgnoreCase)));
    }

    private async Task<SkillInstallChoice?> ConfirmSkillInstallAsync(
        SkillCatalogEntry skill,
        SkillPackageInspection inspection,
        IReadOnlyList<SkillInstallTarget> targets)
    {
        var target = new ComboBox { ItemsSource = targets, SelectedIndex = 0, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
        var scope = new ComboBox { ItemsSource = new[] { "Current workspace", "All workspaces" }, SelectedIndex = 0, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
        var install = new Button { Content = "DOWNLOAD AND INSTALL", Classes = { "primary" } };
        var cancel = new Button { Content = "CANCEL" };
        var compatibilityUnknown = skill.Compatibility.Contains("unverified", StringComparison.OrdinalIgnoreCase);
        var acceptUnknown = new CheckBox
        {
            Content = "Install despite unverified model compatibility",
            IsVisible = compatibilityUnknown,
            IsChecked = !compatibilityUnknown
        };
        install.IsEnabled = acceptUnknown.IsChecked == true;
        acceptUnknown.IsCheckedChanged += (_, _) => install.IsEnabled = acceptUnknown.IsChecked == true;
        var warnings = inspection.Warnings.Count == 0
            ? "No package-level warnings were found. The GitHub source is still unreviewed."
            : string.Join("\n", inspection.Warnings);
        var dialog = new Window
        {
            Title = $"Install {skill.Name}", Width = 650, SizeToContent = SizeToContent.Height,
            CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(20), Spacing = 12,
                Children =
                {
                    new TextBlock { Text = "SKILL INSTALL REVIEW", Classes = { "micro" }, Foreground = Avalonia.Media.Brush.Parse("#65C7D0") },
                    new TextBlock { Text = skill.Name, FontSize = 21, FontWeight = Avalonia.Media.FontWeight.SemiBold },
                    new TextBlock { Text = skill.Description, Classes = { "muted" }, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    new TextBlock { Text = $"{skill.Repository}  ·  {skill.SourceRevision[..Math.Min(10, skill.SourceRevision.Length)]}", Classes = { "mono" }, FontSize = 10 },
                    new Border { Height = 1, Background = Avalonia.Media.Brush.Parse("#29313C") },
                    new TextBlock { Text = $"{inspection.FileCount} files  ·  {FormatBytes(inspection.ByteLength)}  ·  {inspection.ScriptCount} scripts/executables" },
                    new TextBlock { Text = warnings, Foreground = Avalonia.Media.Brush.Parse(inspection.Warnings.Count == 0 ? "#8993A3" : "#E2A84A"), TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    new TextBlock { Text = "TARGET", Classes = { "micro" } }, target,
                    new TextBlock { Text = "SCOPE", Classes = { "micro" } }, scope,
                    acceptUnknown,
                    new TextBlock { Text = "Nothing has been downloaded. Confirming pins this exact revision, downloads it into Harness-owned storage, then copies it through the selected provider's standard setup path.", Classes = { "muted" }, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Spacing = 8, Children = { cancel, install } }
                }
            }
        };
        install.Click += (_, _) => dialog.Close(new SkillInstallChoice(
            (SkillInstallTarget)target.SelectedItem!,
            scope.SelectedIndex == 0 ? "WORKSPACE" : "USER"));
        cancel.Click += (_, _) => dialog.Close(null);
        return await dialog.ShowDialog<SkillInstallChoice?>(this);
    }

    private static string FormatBytes(long bytes) => bytes >= 1024 * 1024
        ? $"{bytes / 1024d / 1024d:0.0} MB"
        : $"{Math.Max(1, bytes / 1024d):0.#} KB";

    private async Task RunAsync(string status, Func<Task> action)
    {
        try
        {
            ViewModel.Status = status;
            await action();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception)
        {
            var error = exception.Message.Replace("\r", " ").Replace("\n", " ");
            ViewModel.Status = error;
            if (SettingsTabs.SelectedIndex == 5) ViewModel.SkillCatalogStatus = error;
            RecordActivity(
                "ERROR",
                status.Trim().TrimEnd('…'),
                error,
                "FAILED",
                false,
                "#E2A84A");
        }
    }

    private sealed record SkillInstallChoice(SkillInstallTarget Target, string Scope);
}

public sealed record SettingsActivityEventArgs(
    string Kind,
    string Title,
    string Detail,
    string Outcome,
    bool IsMilestone,
    string Color);

using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Harness.App.ViewModels;
using Harness.Core.Models;
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
    private readonly IReadOnlyList<SkillInstallTarget> _skillTargets;
    private readonly bool _openSkillsOnLaunch;
    private bool _loadingSkillCatalog;

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
        false)
    {
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
        bool openSkillsOnLaunch = false)
    {
        InitializeComponent();
        _save = save;
        _import = import;
        _importProject = importProject;
        _openProject = openProject;
        _repositoryChanged = repositoryChanged;
        _store = store;
        _skillTargets = skillTargets ?? [];
        _openSkillsOnLaunch = openSkillsOnLaunch;
        DataContext = new SettingsWindowViewModel(settings, workspacePath);
        ViewModel.SetCompatibilityTargets(compatibilityTargets ?? []);
        Opened += SettingsWindow_OnOpened;
        Closed += (_, _) => _lifetime.Cancel();
    }

    private SettingsWindowViewModel ViewModel => (SettingsWindowViewModel)DataContext!;

    public void ShowSkills()
    {
        SettingsTabs.SelectedIndex = 5;
        SkillSearchBox?.Focus();
    }

    private async void SettingsWindow_OnOpened(object? sender, EventArgs e)
    {
        if (_openSkillsOnLaunch) ShowSkills();
        await LoadSkillCatalogAsync();
        await RefreshGitHubAsync();
    }

    private async void Save_OnClick(object? sender, RoutedEventArgs e)
    {
        await RunAsync("Saving settings…", async () =>
        {
            await _save(ViewModel.ToSettings());
            ViewModel.Status = "Settings saved";
        });
    }

    private void Close_OnClick(object? sender, RoutedEventArgs e) => Close();

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
            var entries = await _store.SearchSkillCatalogAsync(
                ViewModel.SkillSearchText,
                ViewModel.SelectedSkillCategory,
                ViewModel.SelectedSkillSource,
                ViewModel.SelectedSkillCompatibility.IsAll ? null : ViewModel.SelectedSkillCompatibility.ProviderId,
                _lifetime.Token);
            var installed = await _store.ListInstalledSkillsAsync(_lifetime.Token);
            var sources = await _store.ListSkillSourcesAsync(_lifetime.Token);
            ViewModel.ReplaceSkills(entries, installed, sources);
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
            var candidates = await _github.DiscoverSkillSourceCandidatesAsync(18, _lifetime.Token);
            await _store.UpsertSkillInventoriesAsync(
                candidates.Select(source => new SkillRepositoryInventory(source, [])).ToArray(),
                _lifetime.Token);
            var sources = await _store.ListSkillSourcesAsync(_lifetime.Token);
            var completed = 0;
            foreach (var source in sources)
            {
                ViewModel.SkillCatalogStatus = $"Indexing every SKILL.md path in {source.Repository} · {completed}/{sources.Count} sources complete…";
                var inventory = await _github.IndexSkillRepositoryTreeAsync(source, _lifetime.Token);
                await _store.UpsertSkillInventoriesAsync([inventory], _lifetime.Token);
                if (inventory.Skills.Count == 0)
                    await _store.RemoveSkillSourceIfEmptyAsync(source.Repository, _lifetime.Token);
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
        await LoadSkillCatalogAsync();
    }

    private async void SkillFilter_OnChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _loadingSkillCatalog) return;
        await LoadSkillCatalogAsync();
    }

    private async void SkillTopic_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string category }) return;
        ViewModel.SelectedSkillCategory = category;
        await LoadSkillCatalogAsync();
    }

    private async Task SearchSkillsAsync()
    {
        if (_store is null) return;
        await RunAsync("Searching the local skill catalog…", async () =>
        {
            await LoadSkillCatalogAsync();
            ViewModel.SkillCatalogStatus = "Finding repositories, reading their total skill counts, and caching matching descriptions…";
            var inventories = await _github.DiscoverSkillRepositoriesAsync(
                ViewModel.SkillSearchText,
                ViewModel.SelectedSkillCategory,
                repository: ViewModel.SelectedSkillSource,
                maxRepositories: 3,
                skillsPerRepository: 18,
                cancellationToken: _lifetime.Token,
                hydrateMetadata: true);
            await _store.UpsertSkillInventoriesAsync(inventories, _lifetime.Token);
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
        await RunAsync($"Inspecting {selected.Name} without downloading it…", async () =>
        {
            var compatibleTargets = CompatibleTargets(selected.Entry).ToArray();
            if (compatibleTargets.Length == 0)
                throw new InvalidOperationException($"No connected provider can install a skill labeled {selected.Compatibility}.");
            var inspection = await _github.InspectSkillPackageAsync(selected.Entry, _lifetime.Token);
            var request = await ConfirmSkillInstallAsync(selected.Entry, inspection, compatibleTargets);
            if (request is null)
            {
                ViewModel.Status = "Skill installation canceled before download";
                return;
            }

            ViewModel.SkillCatalogStatus = $"Downloading the confirmed {selected.Name} package…";
            var package = await _github.DownloadSkillPackageAsync(
                selected.Entry,
                inspection,
                SkillPackageInstaller.DefaultPackageRoot,
                _lifetime.Token);
            if (!request.Target.ProviderId.Equals("openai-codex", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"The {request.Target.DisplayName} setup adapter is not implemented yet.");
            var installPath = await SkillPackageInstaller.InstallCodexAsync(
                package,
                selected.Entry,
                request.Scope,
                ViewModel.WorkspacePath,
                _lifetime.Token);
            var installed = new InstalledSkill(
                SkillPackageInstaller.CreateInstallId(
                    selected.Entry.Id,
                    request.Target.ProviderId,
                    request.Scope,
                    request.Scope == "WORKSPACE" ? ViewModel.WorkspacePath : null),
                selected.Entry.Id,
                selected.Entry.Name,
                selected.Entry.SourceRevision,
                package.PackagePath,
                installPath,
                request.Scope,
                request.Scope == "WORKSPACE" ? ViewModel.WorkspacePath : null,
                request.Target.ProviderId,
                request.Target.ModelId,
                package.ContentSha256,
                true,
                DateTimeOffset.UtcNow);
            await _store.SaveInstalledSkillAsync(installed, _lifetime.Token);
            await LoadSkillCatalogAsync();
            ViewModel.Status = $"Installed {selected.Name} for {request.Target.DisplayName}";
            ViewModel.SkillCatalogStatus = $"Installed at {installPath}. Codex detects skill changes automatically.";
        });
    }

    private IEnumerable<SkillInstallTarget> CompatibleTargets(SkillCatalogEntry skill)
    {
        var anthropicOnly = skill.Compatibility.Equals("Claude Code extension", StringComparison.OrdinalIgnoreCase);
        var openAiOnly = skill.Compatibility.Equals("Codex extension", StringComparison.OrdinalIgnoreCase);
        if (skill.Compatibility.Equals("Mixed provider extensions", StringComparison.OrdinalIgnoreCase)) return [];
        return _skillTargets.Where(target =>
            (!anthropicOnly || target.ProviderId.Contains("anthropic", StringComparison.OrdinalIgnoreCase))
            && (!openAiOnly || target.ProviderId.Equals("openai-codex", StringComparison.OrdinalIgnoreCase)));
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
        }
    }

    private sealed record SkillInstallChoice(SkillInstallTarget Target, string Scope);
}

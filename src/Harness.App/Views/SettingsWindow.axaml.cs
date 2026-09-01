using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Harness.App.ViewModels;
using Harness.Core.Models;
using Harness.Workspace;

namespace Harness.App.Views;

public sealed partial class SettingsWindow : Window
{
    private readonly Func<HarnessApplicationSettings, Task> _save;
    private readonly Func<ConversationImportPlan, Task> _import;
    private readonly Func<HarnessImportProject, Task> _importProject;
    private readonly Func<Task> _openProject;
    private readonly Func<Task> _repositoryChanged;
    private readonly GitWorkspaceClient _git;
    private readonly GitHubCliClient _github = new();
    private readonly CancellationTokenSource _lifetime = new();

    public SettingsWindow() : this(
        new HarnessApplicationSettings(),
        Environment.CurrentDirectory,
        new GitWorkspaceClient(),
        _ => Task.CompletedTask,
        _ => Task.CompletedTask,
        _ => Task.CompletedTask,
        () => Task.CompletedTask,
        () => Task.CompletedTask)
    {
    }

    public SettingsWindow(
        HarnessApplicationSettings settings,
        string workspacePath,
        GitWorkspaceClient git,
        Func<HarnessApplicationSettings, Task> save,
        Func<ConversationImportPlan, Task> import,
        Func<HarnessImportProject, Task> importProject,
        Func<Task> openProject,
        Func<Task> repositoryChanged)
    {
        InitializeComponent();
        _git = git;
        _save = save;
        _import = import;
        _importProject = importProject;
        _openProject = openProject;
        _repositoryChanged = repositoryChanged;
        DataContext = new SettingsWindowViewModel(settings, workspacePath);
        Opened += async (_, _) => await RefreshRepositoryAsync();
        Closed += (_, _) => _lifetime.Cancel();
    }

    private SettingsWindowViewModel ViewModel => (SettingsWindowViewModel)DataContext!;

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

    private async void RefreshRepository_OnClick(object? sender, RoutedEventArgs e) => await RefreshRepositoryAsync();
    private async void GitHubSignIn_OnClick(object? sender, RoutedEventArgs e) => await RunAsync("Opening GitHub device sign-in…", async () =>
    {
        await _github.SignInAsync(_lifetime.Token);
        await RefreshRepositoryAsync();
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
            ViewModel.Status = $"GitHub connected. Enter commit identity manually if needed: {exception.Message}";
        }
    });
    private async void InitializeGit_OnClick(object? sender, RoutedEventArgs e) => await RunGitActionAsync("Initializing repository…", () => _git.InitializeRepositoryAsync(ViewModel.WorkspacePath, _lifetime.Token));
    private async void AttachOrigin_OnClick(object? sender, RoutedEventArgs e) => await RunGitActionAsync("Attaching origin…", () => _git.SetOriginAsync(ViewModel.WorkspacePath, ViewModel.RemoteUrl, _lifetime.Token));
    private async void CreateGitHubRepository_OnClick(object? sender, RoutedEventArgs e) => await RunGitActionAsync("Creating initial commit and publishing repository…", PublishRepositoryAsync);
    private async void Commit_OnClick(object? sender, RoutedEventArgs e) => await RunGitActionAsync("Creating commit…", async () =>
    {
        await EnsureCommitIdentityAsync();
        await _git.CommitAsync(ViewModel.WorkspacePath, ViewModel.CommitMessage, _lifetime.Token);
    });
    private async void Fetch_OnClick(object? sender, RoutedEventArgs e) => await RunGitActionAsync("Fetching origin…", () => _git.FetchAsync(ViewModel.WorkspacePath, _lifetime.Token));
    private async void Pull_OnClick(object? sender, RoutedEventArgs e) => await RunGitActionAsync("Pulling…", () => _git.PullAsync(ViewModel.WorkspacePath, _lifetime.Token));
    private async void Push_OnClick(object? sender, RoutedEventArgs e) => await RunGitActionAsync("Pushing…", () => _git.PushAsync(ViewModel.WorkspacePath, _lifetime.Token));

    private async Task RunGitActionAsync(string status, Func<Task> action) =>
        await RunAsync(status, async () => { await action(); await RefreshRepositoryAsync(); await _repositoryChanged(); ViewModel.Status = "Repository updated"; });

    private async Task EnsureCommitIdentityAsync()
    {
        if (string.IsNullOrWhiteSpace(ViewModel.GitAuthorName)
            || string.IsNullOrWhiteSpace(ViewModel.GitAuthorEmail))
        {
            var profile = await _github.GetAuthenticatedUserAsync(_lifetime.Token);
            if (string.IsNullOrWhiteSpace(ViewModel.GitAuthorName)) ViewModel.GitAuthorName = profile.Name;
            if (string.IsNullOrWhiteSpace(ViewModel.GitAuthorEmail)) ViewModel.GitAuthorEmail = profile.Email;
        }
        await _git.ConfigureIdentityAsync(
            ViewModel.WorkspacePath,
            ViewModel.GitAuthorName,
            ViewModel.GitAuthorEmail,
            _lifetime.Token);
        await _save(ViewModel.ToSettings());
    }

    private async Task PublishRepositoryAsync()
    {
        var connection = await _github.GetConnectionStatusAsync(_lifetime.Token);
        if (!connection.IsAuthenticated)
            throw new InvalidOperationException("Sign in to GitHub before creating a repository.");
        await _git.InitializeRepositoryAsync(ViewModel.WorkspacePath, _lifetime.Token);
        await EnsureCommitIdentityAsync();
        await _git.PrepareForInitialPushAsync(
            ViewModel.WorkspacePath,
            "Initial commit",
            _lifetime.Token);
        var remote = await _git.GetRemoteUrlAsync(ViewModel.WorkspacePath, _lifetime.Token);
        if (string.IsNullOrWhiteSpace(remote))
        {
            var existing = await _github.GetRepositoryUrlAsync(ViewModel.RepositoryName, _lifetime.Token);
            if (existing is null)
            {
                await _github.CreateRepositoryAsync(
                    ViewModel.WorkspacePath,
                    ViewModel.RepositoryName,
                    ViewModel.PrivateRepository,
                    _lifetime.Token);
                return;
            }
            await _git.SetOriginAsync(ViewModel.WorkspacePath, existing, _lifetime.Token);
        }
        await _git.PushAsync(ViewModel.WorkspacePath, _lifetime.Token);
    }

    private async Task RefreshRepositoryAsync()
    {
        await RunAsync("Checking repository…", async () =>
        {
            ViewModel.GitHubStatus = await _github.ReadStatusAsync(_lifetime.Token);
            var snapshot = await _git.ReadStatusAsync(ViewModel.WorkspacePath, _lifetime.Token);
            ViewModel.RemoteUrl = snapshot.IsRepository
                ? await _git.GetRemoteUrlAsync(snapshot.RepositoryRoot!, _lifetime.Token) ?? ""
                : "";
            ViewModel.GitHubStatus += snapshot.IsRepository
                ? $" · {snapshot.Branch} · {snapshot.Files.Count} changed"
                : " · This folder is not a Git repository";
            ViewModel.Status = "Ready";
        });
    }

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
            ViewModel.Status = exception.Message.Replace("\r", " ").Replace("\n", " ");
        }
    }
}

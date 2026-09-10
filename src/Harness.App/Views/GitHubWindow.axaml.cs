using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using System.Diagnostics;
using Harness.App.ViewModels;
using Harness.Core.Models;
using Harness.Workspace;

namespace Harness.App.Views;

public sealed partial class GitHubWindow : Window
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly GitHubCliClient? _github;
    private readonly GitWorkspaceClient? _git;
    private readonly string? _workspacePath;
    private readonly GitHubModuleActions? _actions;
    private bool _actionRunning;
    private long _refreshVersion;

    public GitHubWindow()
    {
        InitializeComponent();
        DataContext = new GitHubWindowViewModel("Workspace", Environment.CurrentDirectory);
        Closed += (_, _) =>
        {
            _lifetime.Cancel();
            _lifetime.Dispose();
        };
    }

    public GitHubWindow(
        GitHubCliClient github,
        GitWorkspaceClient git,
        string workspacePath,
        string workspaceName,
        HarnessApplicationSettings settings,
        GitHubModuleActions actions) : this()
    {
        _github = github;
        _git = git;
        _workspacePath = Path.GetFullPath(workspacePath);
        _actions = actions;
        DataContext = new GitHubWindowViewModel(workspaceName, _workspacePath);
        BranchNameBox.Text = string.IsNullOrWhiteSpace(settings.DefaultGitBranch) ? "main" : settings.DefaultGitBranch;
        NewRepositoryNameBox.Text = workspaceName;
        AuthorNameBox.Text = settings.GitAuthorName;
        AuthorEmailBox.Text = settings.GitAuthorEmail;
        Opened += async (_, _) => await RefreshAsync();
    }

    public event EventHandler<GitHubModuleActivityEventArgs>? ActionCompleted;

    private GitHubWindowViewModel ViewModel => (GitHubWindowViewModel)DataContext!;

    private async Task RefreshAsync()
    {
        if (_github is null || _git is null || _workspacePath is null) return;
        var version = Interlocked.Increment(ref _refreshVersion);
        ViewModel.SetBusy(true, "Refreshing GitHub…");
        try
        {
            var connectionTask = Task.Run(() => _github.GetConnectionStatusAsync(_lifetime.Token), _lifetime.Token);
            var localTask = Task.Run(() => _git.ReadStatusAsync(_workspacePath, _lifetime.Token), _lifetime.Token);
            await Task.WhenAll(connectionTask, localTask);
            var connection = await connectionTask;
            var local = await localTask;
            var originTask = local.RepositoryRoot is { } root
                ? Task.Run(() => _git.GetRemoteUrlAsync(root, _lifetime.Token), _lifetime.Token)
                : Task.FromResult<string?>(null);
            var repositoriesTask = connection.IsAuthenticated
                ? Task.Run(() => _github.ListRepositoriesAsync(cancellationToken: _lifetime.Token), _lifetime.Token)
                : Task.FromResult<IReadOnlyList<GitHubRepository>>([]);
            await Task.WhenAll(originTask, repositoriesTask);
            if (version != Interlocked.Read(ref _refreshVersion)) return;
            var origin = await originTask;
            ViewModel.Apply(connection, local, origin, await repositoriesTask);
            OriginUrlBox.Text = origin ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(local.Branch)) BranchNameBox.Text = local.Branch;
            if (connection.IsAuthenticated
                && (string.IsNullOrWhiteSpace(AuthorNameBox.Text) || string.IsNullOrWhiteSpace(AuthorEmailBox.Text)))
            {
                try
                {
                    var profile = await Task.Run(
                        () => _github.GetAuthenticatedUserAsync(_lifetime.Token), _lifetime.Token);
                    if (version != Interlocked.Read(ref _refreshVersion)) return;
                    if (string.IsNullOrWhiteSpace(AuthorNameBox.Text)) AuthorNameBox.Text = profile.Name;
                    if (string.IsNullOrWhiteSpace(AuthorEmailBox.Text)) AuthorEmailBox.Text = profile.Email;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    ViewModel.Status = $"Repositories loaded · commit identity needs attention: {exception.Message}";
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ViewModel.Status = exception.Message;
        }
        finally
        {
            if (version == Interlocked.Read(ref _refreshVersion)) ViewModel.SetBusy(false);
        }
    }

    private void RepositorySearch_OnTextChanged(object? sender, TextChangedEventArgs e) => ApplyRepositoryFilter();
    private void RepositoryFilter_OnChanged(object? sender, SelectionChangedEventArgs e) => ApplyRepositoryFilter();

    private void ApplyRepositoryFilter()
    {
        if (DataContext is not GitHubWindowViewModel viewModel) return;
        var visibility = (RepositoryVisibilityFilter.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "all";
        viewModel.ApplyFilter(RepositorySearchBox.Text, visibility);
    }

    private async void Refresh_OnClick(object? sender, RoutedEventArgs e) => await RefreshAsync();

    private async void Initialize_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_actions is null) return;
        await RunActionAsync(
            token => _actions.InitializeAsync(BranchNameBox.Text ?? "main", token),
            "GIT", "Local repository initialized");
    }

    private async void ApplyBranch_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_actions is null) return;
        await RunActionAsync(
            token => _actions.ApplyBranchAsync(BranchNameBox.Text ?? string.Empty, token),
            "GIT", "Branch updated");
    }

    private async void AttachOrigin_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_actions is null) return;
        await RunActionAsync(
            token => _actions.AttachOriginAsync(
                OriginUrlBox.Text ?? string.Empty,
                BranchNameBox.Text ?? "main",
                token),
            "GITHUB", "Origin remote attached");
    }

    private void UseSelectedOrigin_OnClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedRepository is { } repository)
            OriginUrlBox.Text = repository.Repository.Url;
    }

    private async void Publish_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_actions is null) return;
        await RunActionAsync(
            token => _actions.PublishAsync(
                NewRepositoryNameBox.Text ?? string.Empty,
                PrivateRepositoryCheckBox.IsChecked == true,
                BranchNameBox.Text ?? "main",
                new GitIdentity(AuthorNameBox.Text ?? string.Empty, AuthorEmailBox.Text ?? string.Empty),
                token),
            "GITHUB", "Workspace published to GitHub");
    }

    private async void CloneRepository_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_actions is null || ViewModel.SelectedRepository is not { } selected) return;
        await RunActionAsync(
            token => _actions.CloneAndOpenAsync(selected.Repository, token),
            "GITHUB", $"Opened repository · {selected.NameWithOwner}", refreshAfter: false);
    }

    private void OpenWorkingTree_OnClick(object? sender, RoutedEventArgs e) => _actions?.OpenWorkingTree();

    private void GetGit_OnClick(object? sender, RoutedEventArgs e) => OpenExternal("https://git-scm.com/download/win");
    private void GetGitHubCli_OnClick(object? sender, RoutedEventArgs e) => OpenExternal("https://cli.github.com/");

    private static void OpenExternal(string url)
    {
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private async Task RunActionAsync(
        Func<CancellationToken, Task<string>> action,
        string kind,
        string title,
        bool refreshAfter = true)
    {
        if (_actionRunning) return;
        _actionRunning = true;
        ViewModel.SetBusy(true, $"{title}…");
        try
        {
            var detail = await action(_lifetime.Token);
            if (!IsVisible) return;
            ViewModel.Status = detail;
            ActionCompleted?.Invoke(this, new GitHubModuleActivityEventArgs(
                kind, title, detail, "COMPLETED", "#65C7D0"));
            if (refreshAfter) await RefreshAsync();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (!IsVisible) return;
            ViewModel.Status = exception.Message;
            ActionCompleted?.Invoke(this, new GitHubModuleActivityEventArgs(
                kind, $"Failed · {title}", exception.Message, "FAILED", "#E2A84A"));
        }
        finally
        {
            _actionRunning = false;
            ViewModel.SetBusy(false);
        }
    }

    private void TitleBar_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.ClickCount == 2)
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        else
            BeginMoveDrag(e);
    }

    private void Minimize_OnClick(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_OnClick(object? sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_OnClick(object? sender, RoutedEventArgs e) => Close();
}

public sealed record GitHubModuleActions(
    Func<string, CancellationToken, Task<string>> InitializeAsync,
    Func<string, CancellationToken, Task<string>> ApplyBranchAsync,
    Func<string, string, CancellationToken, Task<string>> AttachOriginAsync,
    Func<string, bool, string, GitIdentity, CancellationToken, Task<string>> PublishAsync,
    Func<GitHubRepository, CancellationToken, Task<string>> CloneAndOpenAsync,
    Action OpenWorkingTree);

public sealed record GitHubModuleActivityEventArgs(
    string Kind,
    string Title,
    string Detail,
    string Outcome,
    string Color);

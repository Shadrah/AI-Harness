using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Harness.App.ViewModels;
using Harness.Workspace;

namespace Harness.App.Views;

public sealed partial class WorkingTreeWindow : Window
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly GitWorkspaceClient? _git;
    private readonly string? _workspacePath;
    private readonly Func<string, string, CancellationToken, Task<string>>? _renameBranch;
    private bool _actionRunning;
    private long _diffLoadVersion;

    public WorkingTreeWindow()
    {
        InitializeComponent();
        DataContext = new WorkingTreeWindowViewModel();
        Closed += (_, _) =>
        {
            _lifetime.Cancel();
            _lifetime.Dispose();
        };
    }

    public WorkingTreeWindow(
        GitWorkspaceClient git,
        string workspacePath,
        Func<string, string, CancellationToken, Task<string>>? renameBranch = null) : this()
    {
        _git = git;
        _workspacePath = Path.GetFullPath(workspacePath);
        _renameBranch = renameBranch;
        Opened += WorkingTreeWindow_OnOpened;
    }

    public event EventHandler? WorkingTreeChanged;
    public event EventHandler<WorkingTreeActionEventArgs>? ActionCompleted;

    private WorkingTreeWindowViewModel ViewModel =>
        (WorkingTreeWindowViewModel)DataContext!;

    private async void WorkingTreeWindow_OnOpened(object? sender, EventArgs e) =>
        await RefreshAsync(loadSelectedDiff: true);

    private async void Refresh_OnClick(object? sender, RoutedEventArgs e) =>
        await RefreshAsync(loadSelectedDiff: true);

    private async void RenameBranch_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_git is null || ViewModel.RepositoryRoot is not { } root) return;
        var input = new TextBox
        {
            Text = ViewModel.Branch is "NO COMMITS" or "UNKNOWN" ? "main" : ViewModel.Branch,
            MinWidth = 340,
            Watermark = "main"
        };
        var rename = new Button { Content = "RENAME BRANCH", Classes = { "primary" } };
        var cancel = new Button { Content = "CANCEL", Classes = { "ghost" } };
        var dialog = new Window
        {
            Title = "Rename current branch",
            Width = 440,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(18),
                Spacing = 11,
                Children =
                {
                    new TextBlock { Text = "Current branch name", FontSize = 18, FontWeight = FontWeight.SemiBold },
                    new TextBlock { Text = "Harness publishes a renamed branch. If it was GitHub's default, Harness moves the default and then removes the old remote branch.", Classes = { "muted" }, TextWrapping = TextWrapping.Wrap },
                    input,
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Spacing = 7,
                        Children = { cancel, rename }
                    }
                }
            }
        };
        rename.Click += (_, _) => dialog.Close(input.Text?.Trim());
        cancel.Click += (_, _) => dialog.Close(null);
        var branch = await dialog.ShowDialog<string?>(this);
        if (string.IsNullOrWhiteSpace(branch)) return;
        var result = $"Renamed local branch to {branch}";
        await RunActionAsync(
            async repository =>
            {
                result = _renameBranch is null
                    ? await RenameLocalBranchAsync(repository, branch)
                    : await _renameBranch(repository, branch, _lifetime.Token);
            },
            () => result);
    }

    private async Task<string> RenameLocalBranchAsync(string repository, string branch)
    {
        await _git!.RenameCurrentBranchAsync(repository, branch, _lifetime.Token);
        return $"Renamed local branch to {branch}";
    }

    private async Task RefreshAsync(bool loadSelectedDiff, bool reportStatus = true)
    {
        if (_git is null || _workspacePath is null)
        {
            return;
        }
        try
        {
            var preferredPath = ViewModel.SelectedFile?.RelativePath;
            var snapshot = await _git.ReadStatusAsync(_workspacePath, _lifetime.Token);
            ViewModel.Apply(snapshot, preferredPath);
            if (reportStatus)
            {
                ViewModel.Activity = snapshot.IsRepository
                    ? $"{snapshot.Files.Count} changed file(s)"
                    : snapshot.Error ?? "Not a Git repository";
            }
            if (loadSelectedDiff && ViewModel.SelectedFile is not null)
            {
                await LoadSelectedDiffAsync();
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ViewModel.Activity = exception.Message;
        }
    }

    private async void FileSelection_OnChanged(object? sender, SelectionChangedEventArgs e) =>
        await LoadSelectedDiffAsync();

    private async Task LoadSelectedDiffAsync()
    {
        if (_git is null
            || ViewModel.RepositoryRoot is not { } root
            || ViewModel.SelectedFile is not { } file)
        {
            return;
        }
        var version = Interlocked.Increment(ref _diffLoadVersion);
        ViewModel.BeginDiffLoad();
        try
        {
            var diff = await _git.GetDiffAsync(root, file.Source, _lifetime.Token);
            if (version == Interlocked.Read(ref _diffLoadVersion)
                && string.Equals(ViewModel.SelectedFile?.RelativePath, file.RelativePath, StringComparison.OrdinalIgnoreCase))
            {
                ViewModel.DiffText = diff;
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (version == Interlocked.Read(ref _diffLoadVersion)) ViewModel.DiffText = exception.Message;
        }
    }

    private async void Stage_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: WorkingTreeFileItem file })
        {
            await RunActionAsync(
                root => _git!.StageAsync(root, file.RelativePath, _lifetime.Token),
                $"Staged {file.RelativePath}");
        }
    }

    private async void Unstage_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: WorkingTreeFileItem file })
        {
            await RunActionAsync(
                root => _git!.UnstageAsync(root, file.RelativePath, _lifetime.Token),
                $"Unstaged {file.RelativePath}");
        }
    }

    private async void Revert_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_git is null
            || sender is not Button { Tag: WorkingTreeFileItem file }
            || ViewModel.RepositoryRoot is not { } root
            || !await ConfirmRevertAsync(file.RelativePath))
        {
            return;
        }
        try
        {
            if (_actionRunning) return;
            _actionRunning = true;
            var recovery = await _git.RevertWorkTreeAsync(
                root,
                file.Source,
                _lifetime.Token);
            ViewModel.Activity = $"Reverted {file.RelativePath} · Recovery: {recovery.RecoveryPath}";
            ActionCompleted?.Invoke(this, new WorkingTreeActionEventArgs(
                "GIT", $"Reverted · {file.RelativePath}", $"Recovery copy: {recovery.RecoveryPath}", "COMPLETED", "#E2A84A"));
            WorkingTreeChanged?.Invoke(this, EventArgs.Empty);
            await RefreshAsync(loadSelectedDiff: true, reportStatus: false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ViewModel.Activity = exception.Message;
            ActionCompleted?.Invoke(this, new WorkingTreeActionEventArgs(
                "ERROR", $"Could not revert · {file.RelativePath}", exception.Message, "FAILED", "#E2A84A"));
        }
        finally { _actionRunning = false; }
    }

    private async void StageHunk_OnClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedFile is not { } file || ViewModel.SelectedHunk is not { } hunk) return;
        await RunActionAsync(
            root => _git!.StageHunkAsync(root, file.RelativePath, hunk.Source, _lifetime.Token),
            $"Staged hunk {hunk.Source.Index} from {file.RelativePath}");
    }

    private async void UnstageHunk_OnClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedFile is not { } file || ViewModel.SelectedHunk is not { } hunk) return;
        await RunActionAsync(
            root => _git!.UnstageHunkAsync(root, file.RelativePath, hunk.Source, _lifetime.Token),
            $"Unstaged hunk {hunk.Source.Index} from {file.RelativePath}");
    }

    private async void DiscardHunk_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_git is null
            || ViewModel.RepositoryRoot is not { } root
            || ViewModel.SelectedFile is not { } file
            || ViewModel.SelectedHunk is not { } hunk
            || !await ConfirmDiscardHunkAsync(file.RelativePath, hunk.DisplayName)
            || _actionRunning)
        {
            return;
        }

        try
        {
            _actionRunning = true;
            var recovery = await _git.RevertHunkAsync(root, file.Source, hunk.Source, _lifetime.Token);
            ViewModel.Activity = $"Discarded hunk {hunk.Source.Index} from {file.RelativePath} · Recovery: {recovery.RecoveryPath}";
            ActionCompleted?.Invoke(this, new WorkingTreeActionEventArgs(
                "GIT", $"Discarded hunk · {file.RelativePath}", $"Recovery copy: {recovery.RecoveryPath}", "COMPLETED", "#E2A84A"));
            WorkingTreeChanged?.Invoke(this, EventArgs.Empty);
            await RefreshAsync(loadSelectedDiff: true, reportStatus: false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception)
        {
            ViewModel.Activity = exception.Message;
            ActionCompleted?.Invoke(this, new WorkingTreeActionEventArgs(
                "ERROR", $"Could not discard hunk · {file.RelativePath}", exception.Message, "FAILED", "#E2A84A"));
        }
        finally { _actionRunning = false; }
    }

    private Task RunActionAsync(Func<string, Task> action, string success) =>
        RunActionAsync(action, () => success);

    private async Task RunActionAsync(Func<string, Task> action, Func<string> success)
    {
        if (_git is null || ViewModel.RepositoryRoot is not { } root || _actionRunning)
        {
            return;
        }
        try
        {
            _actionRunning = true;
            await action(root);
            var message = success();
            ViewModel.Activity = message;
            ActionCompleted?.Invoke(this, new WorkingTreeActionEventArgs(
                "GIT", message, string.Empty, "COMPLETED", "#65C7D0"));
            WorkingTreeChanged?.Invoke(this, EventArgs.Empty);
            await RefreshAsync(loadSelectedDiff: true, reportStatus: false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ViewModel.Activity = exception.Message;
            ActionCompleted?.Invoke(this, new WorkingTreeActionEventArgs(
                "ERROR", "Working-tree action failed", exception.Message, "FAILED", "#E2A84A"));
        }
        finally { _actionRunning = false; }
    }

    private async Task<bool> ConfirmDiscardHunkAsync(string relativePath, string hunk)
    {
        var discard = new Button { Content = "CREATE RECOVERY COPY + DISCARD", Classes = { "primary" } };
        var cancel = new Button { Content = "CANCEL" };
        var dialog = new Window
        {
            Title = "Discard selected hunk",
            Width = 560,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(18),
                Spacing = 14,
                Children =
                {
                    new TextBlock { Text = "DISCARD ONE HUNK", Classes = { "micro" }, Foreground = Brushes.Orange },
                    new TextBlock
                    {
                        Text = $"Discard {hunk} from {relativePath}? Harness will preserve the complete current file before applying the reverse patch.",
                        TextWrapping = TextWrapping.Wrap
                    },
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { cancel, discard }
                    }
                }
            }
        };
        discard.Click += (_, _) => dialog.Close(true);
        cancel.Click += (_, _) => dialog.Close(false);
        return await dialog.ShowDialog<bool>(this);
    }

    private async Task<bool> ConfirmRevertAsync(string relativePath)
    {
        var revert = new Button
        {
            Content = "CREATE RECOVERY COPY + REVERT",
            Classes = { "primary" }
        };
        var cancel = new Button { Content = "CANCEL" };
        var dialog = new Window
        {
            Title = "Revert working-tree changes",
            Width = 540,
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
                        Text = $"Revert working-tree changes in {relativePath}? Harness will preserve the current file in its recovery directory first.",
                        TextWrapping = TextWrapping.Wrap
                    },
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { cancel, revert }
                    }
                }
            }
        };
        revert.Click += (_, _) => dialog.Close(true);
        cancel.Click += (_, _) => dialog.Close(false);
        return await dialog.ShowDialog<bool>(this);
    }

    private void TitleBar_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            if (e.ClickCount == 2)
            {
                WindowState = WindowState == WindowState.Maximized
                    ? WindowState.Normal
                    : WindowState.Maximized;
            }
            else
            {
                BeginMoveDrag(e);
            }
        }
    }

    private void Minimize_OnClick(object? sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void Maximize_OnClick(object? sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_OnClick(object? sender, RoutedEventArgs e) => Close();
}

public sealed record WorkingTreeActionEventArgs(
    string Kind,
    string Title,
    string Detail,
    string Outcome,
    string Color);

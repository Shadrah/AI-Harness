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

    public WorkingTreeWindow(GitWorkspaceClient git, string workspacePath) : this()
    {
        _git = git;
        _workspacePath = Path.GetFullPath(workspacePath);
        Opened += WorkingTreeWindow_OnOpened;
    }

    public event EventHandler? WorkingTreeChanged;

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
                    new TextBlock { Text = "This changes the local branch name. The next push updates its upstream branch on the remote.", Classes = { "muted" }, TextWrapping = TextWrapping.Wrap },
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
        await RunActionAsync(
            repository => _git.RenameCurrentBranchAsync(repository, branch, _lifetime.Token),
            $"Renamed current branch to {branch}");
    }

    private async Task RefreshAsync(bool loadSelectedDiff)
    {
        if (_git is null || _workspacePath is null)
        {
            return;
        }
        try
        {
            var snapshot = await _git.ReadStatusAsync(_workspacePath, _lifetime.Token);
            ViewModel.Apply(snapshot);
            ViewModel.Activity = snapshot.IsRepository
                ? $"{snapshot.Files.Count} changed file(s)"
                : snapshot.Error ?? "Not a Git repository";
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
        try
        {
            ViewModel.DiffText = await _git.GetDiffAsync(root, file.Source, _lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ViewModel.DiffText = exception.Message;
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
            var recovery = await _git.RevertWorkTreeAsync(
                root,
                file.Source,
                _lifetime.Token);
            ViewModel.Activity = $"Reverted {file.RelativePath} · Recovery: {recovery.RecoveryPath}";
            WorkingTreeChanged?.Invoke(this, EventArgs.Empty);
            await RefreshAsync(loadSelectedDiff: true);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ViewModel.Activity = exception.Message;
        }
    }

    private async Task RunActionAsync(Func<string, Task> action, string success)
    {
        if (_git is null || ViewModel.RepositoryRoot is not { } root)
        {
            return;
        }
        try
        {
            await action(root);
            ViewModel.Activity = success;
            WorkingTreeChanged?.Invoke(this, EventArgs.Empty);
            await RefreshAsync(loadSelectedDiff: true);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ViewModel.Activity = exception.Message;
        }
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

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Harness.App.Services;
using Harness.App.ViewModels;
using Harness.Core.Models;
using Harness.Storage;

namespace Harness.App.Views;

public sealed partial class SessionLibraryWindow : Window
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly HarnessStore? _store;
    private readonly StoredProject? _project;
    private readonly SessionLibraryActions? _actions;
    private readonly Func<string?>? _activeSessionId;
    private CancellationTokenSource? _searchDelay;
    private long _refreshVersion;

    public SessionLibraryWindow()
    {
        InitializeComponent();
        DataContext = new SessionLibraryViewModel();
        Closed += (_, _) =>
        {
            _searchDelay?.Cancel();
            _searchDelay?.Dispose();
            _lifetime.Cancel();
            _lifetime.Dispose();
        };
    }

    public SessionLibraryWindow(
        HarnessStore store,
        StoredProject project,
        Func<string?> activeSessionId,
        SessionLibraryActions actions) : this()
    {
        _store = store;
        _project = project;
        _activeSessionId = activeSessionId;
        _actions = actions;
        WorkspaceLabel.Text = $"{project.Name}  ·  {project.RootPath}";
        Opened += async (_, _) => await RefreshAsync();
    }

    public event EventHandler<SessionLibraryActivityEventArgs>? ActionCompleted;

    private SessionLibraryViewModel ViewModel => (SessionLibraryViewModel)DataContext!;

    private async Task RefreshAsync()
    {
        if (_store is null || _project is null) return;
        var version = Interlocked.Increment(ref _refreshVersion);
        var preferred = ViewModel.SelectedSession?.SessionId;
        ViewModel.SetBusy(true);
        try
        {
            var archived = (StatusFilter.SelectedItem as ComboBoxItem)?.Tag?.ToString() switch
            {
                "active" => false,
                "archived" => true,
                _ => (bool?)null
            };
            var sessions = await _store.SearchSessionsAsync(
                _project.Id,
                SearchBox.Text,
                archived,
                cancellationToken: _lifetime.Token);
            if (version != Interlocked.Read(ref _refreshVersion)) return;
            ViewModel.Apply(sessions, preferred, _activeSessionId?.Invoke() ?? string.Empty);
            UpdateTitleEditor();
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

    private async void Search_OnTextChanged(object? sender, TextChangedEventArgs e)
    {
        _searchDelay?.Cancel();
        _searchDelay?.Dispose();
        _searchDelay = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        try
        {
            await Task.Delay(180, _searchDelay.Token);
            await RefreshAsync();
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async void Filter_OnSelectionChanged(object? sender, SelectionChangedEventArgs e) =>
        await RefreshAsync();

    private async void Refresh_OnClick(object? sender, RoutedEventArgs e) => await RefreshAsync();

    private void SessionSelection_OnChanged(object? sender, SelectionChangedEventArgs e) => UpdateTitleEditor();

    private void UpdateTitleEditor() => TitleEditor.Text = ViewModel.SelectedSession?.Title ?? string.Empty;

    private async void Open_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_actions is null || ViewModel.SelectedSession is not { IsArchived: false } session) return;
        await RunActionAsync(
            token => _actions.OpenAsync(session.SessionId, token),
            "TASK", $"Opened · {session.Title}", closeAfter: true);
    }

    private async void Rename_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_actions is null || ViewModel.SelectedSession is not { } session) return;
        var title = TitleEditor.Text?.Trim();
        if (string.IsNullOrWhiteSpace(title) || title == session.Title) return;
        await RunActionAsync(
            token => _actions.RenameAsync(session.SessionId, title, token),
            "TASK", $"Renamed · {title}");
    }

    private async void Archive_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_actions is null || ViewModel.SelectedSession is not { } session) return;
        var archive = !session.IsArchived;
        await RunActionAsync(
            token => _actions.SetArchivedAsync(session.SessionId, archive, token),
            "TASK",
            $"{(archive ? "Archived" : "Restored")} · {session.Title}");
    }

    private async void Delete_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_actions is null
            || ViewModel.SelectedSession is not { } session
            || !await ConfirmDeleteAsync(session.Title)) return;
        await RunActionAsync(
            token => _actions.DeleteAsync(session.SessionId, token),
            "TASK", $"Deleted · {session.Title}");
    }

    private async void Export_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_store is null || ViewModel.SelectedSession is not { } selected) return;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export Harness task",
            SuggestedFileName = SanitizeFileName(selected.Title) + ".md",
            DefaultExtension = "md",
            FileTypeChoices =
            [
                new FilePickerFileType("Markdown conversation") { Patterns = ["*.md"] },
                new FilePickerFileType("Harness JSON") { Patterns = ["*.json"] }
            ]
        });
        var path = file?.TryGetLocalPath();
        if (path is null) return;
        try
        {
            ViewModel.SetBusy(true);
            ViewModel.Status = "Exporting task…";
            var loaded = await _store.LoadSessionAsync(selected.SessionId, _lifetime.Token);
            await new SessionExportService().ExportAsync(
                path, loaded.Session, loaded.Messages, loaded.Attachments, _lifetime.Token);
            ViewModel.Status = $"Exported {Path.GetFileName(path)}";
            ActionCompleted?.Invoke(this, new SessionLibraryActivityEventArgs(
                "EXPORT", $"Exported task · {selected.Title}", path, "COMPLETED", "#65C7D0"));
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
            ViewModel.SetBusy(false);
        }
    }

    private async Task RunActionAsync(
        Func<CancellationToken, Task> action,
        string kind,
        string title,
        bool closeAfter = false)
    {
        if (ViewModel.IsBusy) return;
        try
        {
            ViewModel.SetBusy(true);
            ViewModel.Status = "Applying change…";
            await action(_lifetime.Token);
            ActionCompleted?.Invoke(this, new SessionLibraryActivityEventArgs(
                kind, title, string.Empty, "COMPLETED", "#65C7D0"));
            if (closeAfter)
            {
                Close();
                return;
            }
            await RefreshAsync();
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
            ViewModel.SetBusy(false);
        }
    }

    private async Task<bool> ConfirmDeleteAsync(string title)
    {
        var delete = new Button { Content = "DELETE", Classes = { "primary" } };
        var cancel = new Button { Content = "CANCEL", Classes = { "ghost" } };
        var dialog = new Window
        {
            Title = "Delete task",
            Width = 460,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(18), Spacing = 14,
                Children =
                {
                    new TextBlock { Text = $"Permanently delete ‘{title}’ and its locally stored history?", TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Spacing = 8, Children = { cancel, delete }
                    }
                }
            }
        };
        delete.Click += (_, _) => dialog.Close(true);
        cancel.Click += (_, _) => dialog.Close(false);
        return await dialog.ShowDialog<bool>(this);
    }

    private static string SanitizeFileName(string title)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var sanitized = new string(title.Select(character => invalid.Contains(character) ? '-' : character).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "Harness task" : sanitized;
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

public sealed record SessionLibraryActions(
    Func<string, CancellationToken, Task> OpenAsync,
    Func<string, string, CancellationToken, Task> RenameAsync,
    Func<string, bool, CancellationToken, Task> SetArchivedAsync,
    Func<string, CancellationToken, Task> DeleteAsync);

public sealed record SessionLibraryActivityEventArgs(
    string Kind,
    string Title,
    string Detail,
    string Outcome,
    string Color);

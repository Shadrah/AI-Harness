using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Harness.App.Services;
using Harness.Core.Desktop;

namespace Harness.App.Views;

public sealed partial class DesktopWindow : Window
{
    private readonly WindowsDesktopAutomation _automation;
    private readonly Func<bool> _getAccess;
    private readonly CancellationTokenSource _closed = new();
    private IReadOnlyList<DesktopTargetItem> _allTargets = [];
    private bool _refreshing;

    public DesktopWindow() : this(string.Empty, "Computer use", new WindowsDesktopAutomation(), () => false) { }
    public DesktopWindow(string sessionId, string title)
        : this(sessionId, title, new WindowsDesktopAutomation(), () => false) { }

    public DesktopWindow(string sessionId, string title, WindowsDesktopAutomation automation,
        Func<bool> getAccess)
    {
        _automation = automation;
        _getAccess = getAccess;
        SessionId = sessionId;
        InitializeComponent();
        SessionLabel.Text = title;
        SetAccessState(_getAccess());
        Opened += async (_, _) => await RefreshTargetsAsync();
        Closing += (_, _) => _closed.Cancel();
    }

    public string SessionId { get; }
    public bool AccessAllowed => _getAccess();
    public CancellationToken ClosedToken => _closed.Token;
    public DesktopTargetItem? ConnectedTarget => _automation.CurrentTarget;

    public async Task<DesktopToolResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        if (!AccessAllowed) throw new OperationCanceledException("Computer Use is off. Enable it in Settings.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closed.Token);
        linked.CancelAfter(TimeSpan.FromSeconds(50));
        var action = arguments.TryGetProperty("action", out var value) ? value.GetString() ?? "desktop" : "desktop";
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            DesktopStatus.Foreground = Brush.Parse("#65C7D0");
            DesktopStatus.Text = "Agent · " + action;
        });
        var result = await _automation.ExecuteAsync(arguments, linked.Token);
        linked.Token.ThrowIfCancellationRequested();
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            DesktopStatus.Text = $"Completed · {action} · {DateTime.Now:t}";
            RefreshConnectedSummary();
        });
        return result;
    }

    public void SetAccessState(bool enabled)
    {
        RefreshConnectedSummary();
    }

    private async Task RefreshTargetsAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        RefreshButton.IsEnabled = false;
        var selected = (TargetList.SelectedItem as DesktopTargetItem)?.Id;
        DesktopStatus.Text = "Discovering running windows and installed applications…";
        try
        {
            _allTargets = await _automation.ListTargetsAsync(_closed.Token);
            ApplyFilter();
            if (selected is not null)
                TargetList.SelectedItem = (TargetList.ItemsSource as IEnumerable<DesktopTargetItem>)?
                    .FirstOrDefault(item => item.Id == selected);
            DesktopStatus.Text = $"Ready · {_allTargets.Count:N0} application targets discovered · Computer Use {(_getAccess() ? "on" : "off")}.";
            DesktopStatus.Foreground = Brush.Parse("#65C7D0");
        }
        catch (OperationCanceledException) when (_closed.IsCancellationRequested) { }
        catch (Exception exception)
        {
            DesktopStatus.Text = "Application discovery failed · " + exception.Message;
            DesktopStatus.Foreground = Brush.Parse("#E2A84A");
        }
        finally
        {
            _refreshing = false;
            RefreshButton.IsEnabled = true;
        }
    }

    private void ApplyFilter()
    {
        var query = SearchBox.Text?.Trim();
        var filtered = string.IsNullOrWhiteSpace(query)
            ? _allTargets
            : _allTargets.Where(item => item.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                                        || item.WindowTitle.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToArray();
        var visible = filtered as IReadOnlyList<DesktopTargetItem> ?? filtered.ToArray();
        // Replacing one source is a bounded UI update and avoids hundreds of
        // per-item collection notifications and layout passes while filtering.
        TargetList.ItemsSource = visible;
        TargetCount.Text = visible.Count.ToString("N0");
    }

    private void RefreshConnectedSummary()
    {
        var target = _automation.CurrentTarget;
        if (target is null)
        {
            ConnectionDetail.Text = _getAccess() ? "COMPUTER USE ON · CODEX CHOOSES THE TARGET" : "COMPUTER USE OFF";
            ConnectionDetail.Foreground = Brush.Parse(_getAccess() ? "#65C7D0" : "#E2A84A");
            return;
        }
        ConnectionDetail.Text = (_getAccess() ? "COMPUTER USE ON" : "COMPUTER USE OFF") + " · CURRENT " + target.DisplayName.ToUpperInvariant();
        ConnectionDetail.Foreground = Brush.Parse(_getAccess() ? "#65C7D0" : "#E2A84A");
    }

    private async void Connect_OnClick(object? sender, RoutedEventArgs e)
    {
        if (TargetList.SelectedItem is not DesktopTargetItem selected || !selected.IsAllowed) return;
        ConnectButton.IsEnabled = false;
        try
        {
            _automation.SelectTarget(selected);
            if (!selected.IsRunning)
            {
                DesktopStatus.Text = "Opening " + selected.DisplayName + "…";
                var arguments = JsonSerializer.SerializeToElement(new { action = "launch", targetId = selected.Id });
                await _automation.ExecuteAsync(arguments, _closed.Token);
                await RefreshTargetsAsync();
            }
            RefreshConnectedSummary();
            DesktopStatus.Text = $"Starting target set to {_automation.CurrentTarget?.DisplayName}. Codex may switch when Computer Use is on.";
            DesktopStatus.Foreground = Brush.Parse("#65C7D0");
        }
        catch (OperationCanceledException) when (_closed.IsCancellationRequested) { }
        catch (Exception exception)
        {
            RefreshConnectedSummary();
            DesktopStatus.Text = "Could not connect · " + exception.Message;
            DesktopStatus.Foreground = Brush.Parse("#E2A84A");
        }
        finally
        {
            var currentSelection = TargetList.SelectedItem as DesktopTargetItem;
            ConnectButton.Content = currentSelection?.IsRunning == true ? "SET STARTING APP" : "OPEN APP";
            ConnectButton.IsEnabled = currentSelection?.IsAllowed == true && !_refreshing;
        }
    }

    private void TargetSelection_OnChanged(object? sender, SelectionChangedEventArgs e)
    {
        var selected = TargetList.SelectedItem as DesktopTargetItem;
        SelectedName.Text = selected?.DisplayName ?? "No application selected";
        SelectedDetail.Text = selected?.Detail ?? "Choose an application from the list.";
        ConnectButton.Content = selected?.IsRunning == true ? "SET STARTING APP" : "OPEN APP";
        ConnectButton.IsEnabled = selected?.IsAllowed == true && !_refreshing;
        if (selected?.IsAllowed == false)
        {
            ConnectionDetail.Text = "BLOCKED BY HARNESS";
            ConnectionDetail.Foreground = Brush.Parse("#E2A84A");
        }
        else RefreshConnectedSummary();
    }

    private void Search_OnTextChanged(object? sender, TextChangedEventArgs e) => ApplyFilter();
    private async void Refresh_OnClick(object? sender, RoutedEventArgs e) => await RefreshTargetsAsync();
    private async void Browse_OnClick(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a Windows application",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Windows applications") { Patterns = ["*.exe"] }]
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            var target = _automation.CreateLaunchTarget(path);
            _allTargets = [target, .. _allTargets.Where(item => item.Id != target.Id)];
            SearchBox.Text = string.Empty;
            ApplyFilter();
            TargetList.SelectedItem = target;
            DesktopStatus.Text = target.IsAllowed
                ? $"Selected {target.DisplayName}. Open and connect when ready."
                : target.UnavailableReason ?? "That application is unavailable.";
            DesktopStatus.Foreground = Brush.Parse(target.IsAllowed ? "#65C7D0" : "#E2A84A");
        }
        catch (Exception exception)
        {
            DesktopStatus.Text = "Could not add application · " + exception.Message;
            DesktopStatus.Foreground = Brush.Parse("#E2A84A");
        }
    }
    private void TitleBar_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || e.Source is Button) return;
        if (e.ClickCount == 2) ToggleMaximize(); else BeginMoveDrag(e);
    }
    private void ToggleMaximize() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Minimize_OnClick(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_OnClick(object? sender, RoutedEventArgs e) => ToggleMaximize();
    private void Close_OnClick(object? sender, RoutedEventArgs e) => Close();
}

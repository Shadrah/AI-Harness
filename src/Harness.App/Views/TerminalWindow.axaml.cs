using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Harness.App.Services;
using Harness.App.ViewModels;

namespace Harness.App.Views;

public sealed partial class TerminalWindow : Window
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _sessionGate = new(1, 1);
    private readonly DispatcherTimer _outputTimer;
    private readonly TerminalWindowViewModel _viewModel;
    private TerminalSession? _session;
    private bool _closing;

    public TerminalWindow(string workspacePath)
    {
        InitializeComponent();
        _viewModel = new TerminalWindowViewModel(workspacePath);
        DataContext = _viewModel;
        _outputTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _outputTimer.Tick += OutputTimer_OnTick;
        Opened += TerminalWindow_OnOpened;
        Closed += TerminalWindow_OnClosed;
    }

    public TerminalWindow() : this(Environment.CurrentDirectory)
    {
    }

    private TerminalWindowViewModel ViewModel => _viewModel;

    private async void TerminalWindow_OnOpened(object? sender, EventArgs e)
    {
        _outputTimer.Start();
        await StartSessionAsync();
        TerminalCommandBox.Focus();
    }

    private async void TerminalWindow_OnClosed(object? sender, EventArgs e)
    {
        _closing = true;
        _outputTimer.Stop();
        _lifetime.Cancel();
        await _sessionGate.WaitAsync();
        try { await DisposeSessionAsync(); }
        finally
        {
            _sessionGate.Release();
            _sessionGate.Dispose();
            _lifetime.Dispose();
        }
    }

    private async Task StartSessionAsync()
    {
        await _sessionGate.WaitAsync(_lifetime.Token);
        try
        {
            await DisposeSessionAsync();
            if (_closing) return;
            ViewModel.SetState(false, "STARTING");
            var session = await TerminalSession.StartAsync(ViewModel.WorkspacePath, _lifetime.Token);
            if (_closing)
            {
                await session.DisposeAsync();
                return;
            }
            _session = session;
            session.OutputReceived += Session_OnOutputReceived;
            session.StateChanged += Session_OnStateChanged;
            ViewModel.SetSession(session);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception)
        {
            ViewModel.SetState(false, "NEEDS ATTENTION");
            ViewModel.EnqueueSystem($"[Harness could not start the shell: {exception.Message}]\n");
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    private async Task DisposeSessionAsync()
    {
        var session = _session;
        _session = null;
        if (session is null) return;
        session.OutputReceived -= Session_OnOutputReceived;
        session.StateChanged -= Session_OnStateChanged;
        await session.DisposeAsync();
    }

    private void Session_OnOutputReceived(object? sender, TerminalOutputEventArgs e) => ViewModel.Enqueue(e);

    private void Session_OnStateChanged(object? sender, TerminalStateEventArgs e) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (!_closing && ReferenceEquals(sender, _session)) ViewModel.SetState(e.IsRunning, e.Status);
        });

    private void OutputTimer_OnTick(object? sender, EventArgs e)
    {
        var scroll = TerminalOutputBox.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        var followOutput = scroll is null || scroll.Offset.Y >= Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height - 24);
        if (!ViewModel.FlushPendingOutput() || !followOutput) return;
        Dispatcher.UIThread.Post(() =>
        {
            TerminalOutputBox.UpdateLayout();
            TerminalOutputBox.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault()?.ScrollToEnd();
        }, DispatcherPriority.Background);
    }

    private async Task SendCommandAsync()
    {
        var session = _session;
        ViewModel.CommandText = TerminalCommandBox.Text ?? "";
        if (session is null || !session.IsRunning || !ViewModel.IsRunning)
        {
            ViewModel.EnqueueSystem(session is null
                ? "[Shell is still starting. Your command is preserved; press Enter again when the status shows READY.]\n"
                : "[Shell is closed. Your command is preserved; restart the shell, then press Enter again.]\n");
            ViewModel.SetState(session?.IsRunning == true, session?.IsRunning == true ? "READY" : session is null ? "STARTING" : "SHELL CLOSED");
            TerminalCommandBox.Focus();
            return;
        }
        var command = ViewModel.TakeCommand();
        if (string.IsNullOrEmpty(command)) return;
        try
        {
            await session.SendAsync(command, _lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception)
        {
            ViewModel.EnqueueSystem($"[Harness could not send the command: {exception.Message}]\n");
            ViewModel.SetState(session.IsRunning, session.IsRunning ? "READY" : "SHELL CLOSED");
        }
        TerminalCommandBox.Focus();
    }

    private async void Run_OnClick(object? sender, RoutedEventArgs e) => await SendCommandAsync();

    private async void Restart_OnClick(object? sender, RoutedEventArgs e)
    {
        ViewModel.EnqueueSystem("\n[Restarting shell]\n");
        await StartSessionAsync();
        TerminalCommandBox.Focus();
    }

    private void Clear_OnClick(object? sender, RoutedEventArgs e) => ViewModel.Clear();

    private async void CommandBox_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None)
        {
            e.Handled = true;
            await SendCommandAsync();
        }
        else if (e.Key == Key.Up && e.KeyModifiers == KeyModifiers.None)
        {
            e.Handled = true;
            ViewModel.NavigateHistory(-1);
            TerminalCommandBox.CaretIndex = TerminalCommandBox.Text?.Length ?? 0;
        }
        else if (e.Key == Key.Down && e.KeyModifiers == KeyModifiers.None)
        {
            e.Handled = true;
            ViewModel.NavigateHistory(1);
            TerminalCommandBox.CaretIndex = TerminalCommandBox.Text?.Length ?? 0;
        }
    }

    private void Window_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.L && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            e.Handled = true;
            ViewModel.Clear();
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

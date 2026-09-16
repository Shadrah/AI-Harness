using System.Text.Json;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Harness.App.Services;
using Harness.Core.Browser;
using Harness.Core.Desktop;
using Harness.Providers.Codex;

namespace Harness.App.Views;

public sealed partial class MainWindow
{
    private DesktopWindow? _desktopWindow;
    private readonly WindowsDesktopAutomation _desktopAutomation = new();
    private readonly SemaphoreSlim _desktopGate = new(1, 1);
    private CancellationTokenSource _computerUseAccess = new();
    private bool _computerUseEnabled;
    private bool _openingDesktop;

    private bool SelectedModelSupportsDesktop => OperatingSystem.IsWindows()
        && string.Equals(ViewModel.SelectedModel?.ProviderId, SubscriptionProviderIds.OpenAiCodex, StringComparison.Ordinal)
        && ViewModel.SelectedModel?.Capabilities.Contains("TOOLS") == true
        && ViewModel.SelectedModel.Capabilities.Contains("VISION");

    private string BuildToolInstructions() => _applicationSettings.PersonalInstructions + "\n\n"
        + (OperatingSystem.IsWindows() ? BrowserTools.Instructions : "Harness browser tools are unavailable on this platform.")
        + (SelectedModelSupportsDesktop ? "\n\n" + DesktopTools.Instructions : string.Empty);

    private async void OpenDesktop_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_openingDesktop || _activeSession is null) return;
        _openingDesktop = true;
        var sessionId = _activeSession.Id;
        try
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Desktop control currently requires Windows.");
            if (!SelectedModelSupportsDesktop)
            {
                ViewModel.AddActivity("DESKTOP", "Select a connected model with verified vision and tool support before using agent desktop control.", "#E2A84A");
                return;
            }

            // A resumed Codex thread cannot acquire a new dynamic tool. Preserve the local
            // transcript and create a bounded provider continuation only after consent.
            if (_threadId is not null
                && ViewModel.SelectedModel?.ProviderId.StartsWith("api-", StringComparison.Ordinal) != true
                && string.Equals(ViewModel.SelectedModel?.ProviderId, SubscriptionProviderIds.OpenAiCodex, StringComparison.Ordinal)
                && _store is not null)
            {
                var threadId = _threadId;
                var marker = await _store.GetLatestProviderEventPayloadAsync(sessionId, "harness/desktopTools/v4", _lifetime.Token);
                if (_activeSession?.Id != sessionId || _threadId != threadId) return;
                var registered = marker is not null
                                 && JsonSerializer.Deserialize<JsonElement>(marker).TryGetProperty("threadId", out var stored)
                                 && stored.GetString() == threadId;
                if (!registered)
                {
                    if (ViewModel.IsRunning) throw new InvalidOperationException("Finish or stop this turn before connecting desktop tools to an older chat.");
                    var accepted = await ApproveApiToolAsync("Upgrade Computer Use for this chat?",
                        "This provider thread predates the current Computer Use controller. Harness will retain the visible transcript and start a new provider continuation on the next send using a bounded continuity brief. The old provider history remains stored.\n\nApprove to upgrade Computer Use, or decline to leave this chat unchanged.", _lifetime.Token);
                    if (!accepted || _activeSession?.Id != sessionId || _threadId != threadId || ViewModel.IsRunning) return;
                    var modelId = ViewModel.SelectedModel?.ModelName ?? throw new InvalidOperationException("Select a connected model first.");
                    await _store.AppendProviderEventAsync(sessionId, "harness/desktopContinuation", JsonSerializer.Serialize(new { previousThreadId = threadId }), _lifetime.Token);
                    await _store.UpdateSessionConnectionAsync(sessionId, _codex!.Id, null, modelId,
                        ViewModel.SelectedReasoningLevel?.Id, ViewModel.SelectedServiceTier?.Id);
                    if (_activeSession?.Id != sessionId) return;
                    _activeSession = _activeSession with { ProviderThreadId = null };
                    _threadId = null;
                    ViewModel.AddActivity("COMPUTER USE", "Controller upgraded. Next send starts a provider continuation; visible history is retained.", "#65C7D0");
                }
            }
            if (_activeSession?.Id == sessionId) OpenDesktopForSession(sessionId);
        }
        catch (Exception exception) { ViewModel.AddActivity("DESKTOP", CleanError(exception), "#E2A84A"); }
        finally { _openingDesktop = false; }
    }

    private DesktopWindow OpenDesktopForSession(string sessionId)
    {
        if (_desktopWindow?.SessionId != sessionId)
        {
            _desktopWindow?.Close();
            _desktopWindow = null;
        }
        if (_desktopWindow is null)
        {
            var window = new DesktopWindow(sessionId, ViewModel.WorkspaceName + " / " + ViewModel.CurrentSessionTitle,
                _desktopAutomation, () => _computerUseEnabled);
            _desktopWindow = window;
            window.Closed += (_, _) => { if (ReferenceEquals(_desktopWindow, window)) _desktopWindow = null; };
            window.Show(this);
        }
        _desktopWindow.Activate();
        return _desktopWindow;
    }

    private void SetComputerUseAccess(bool enabled)
    {
        if (_computerUseEnabled == enabled) return;
        _computerUseEnabled = enabled;
        if (enabled)
        {
            _computerUseAccess.Dispose();
            _computerUseAccess = new CancellationTokenSource();
            ViewModel.AddActivity("COMPUTER USE", "Enabled · Codex can discover and switch between ordinary applications", "#65C7D0");
        }
        else
        {
            _computerUseAccess.Cancel();
            ViewModel.AddActivity("COMPUTER USE", "Disabled · active desktop control revoked", "#E2A84A");
        }
        DesktopAccessIndicator.Opacity = enabled ? 1 : 0.25;
        if (_desktopWindow is not null) _desktopWindow.SetAccessState(enabled);
    }

    private async Task<DesktopToolResult> ExecuteDesktopAsync(
        JsonElement arguments,
        string sessionId,
        bool vision,
        CancellationToken cancellationToken)
    {
        await _desktopGate.WaitAsync(cancellationToken);
        try
        {
            var context = await Dispatcher.UIThread.InvokeAsync(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_activeSession?.Id != sessionId || !ViewModel.IsRunning)
                    throw new OperationCanceledException("This desktop request no longer belongs to the active turn.");
                if (!vision) throw new InvalidOperationException("Desktop control requires verified model vision support.");
                if (!_computerUseEnabled)
                    throw new InvalidOperationException("Computer Use is off. Enable it in Settings; Codex can then use the Windows desktop.");
                return (Target: _desktopAutomation.CurrentTarget, Permission: ViewModel.SelectedPermissionMode.Id,
                    AccessToken: _computerUseAccess.Token);
            });

            var action = arguments.TryGetProperty("action", out var actionValue) ? actionValue.GetString() : null;
            if (action is not ("list_apps" or "screenshot" or "observe" or "activate" or "launch" or "click" or "double_click" or "right_click" or "drag" or "move" or "scroll" or "type" or "keypress" or "key" or "wait"))
                throw new InvalidOperationException("Unknown desktop action.");
            var needsApproval = action is not ("list_apps" or "screenshot" or "observe" or "wait") && (context.Permission == "ask"
                || context.Permission == "auto" && RequiresAutomaticDesktopApproval(action, arguments, context.Target));
            if (needsApproval)
            {
                var detail = $"Application: {context.Target?.DisplayName ?? "selected by Codex"}\nWindow: {context.Target?.WindowTitle ?? "not active yet"}\n\n{arguments.GetRawText()}\n\n"
                             + "This input can change application state. Approve only if it matches the current task.";
                if (!await ApproveApiToolAsync("Approve desktop action", detail, cancellationToken))
                    throw new InvalidOperationException("User declined this desktop action. Do not retry it.");
                cancellationToken.ThrowIfCancellationRequested();
                var current = _desktopAutomation.CurrentTarget;
                if (context.Target is not null && current?.Id != context.Target.Id)
                    throw new InvalidOperationException("The selected desktop target changed during approval. Observe again.");
            }

            using var access = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, context.AccessToken);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_activeSession?.Id != sessionId || !ViewModel.IsRunning) throw new OperationCanceledException();
                ViewModel.SetTurnActivity("USING DESKTOP");
            });
            var itemId = "desktop-" + Guid.NewGuid().ToString("N");
            await Dispatcher.UIThread.InvokeAsync(() =>
                ViewModel.StartExecutionItem(itemId, "DESKTOP", action,
                    $"{context.Target?.DisplayName ?? "Windows"} · {action}", "#65C7D0"));
            try
            {
                var result = await _desktopAutomation.ExecuteAsync(arguments, access.Token);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (_activeSession?.Id != sessionId || !ViewModel.IsRunning) throw new OperationCanceledException();
                    var target = _desktopAutomation.CurrentTarget?.DisplayName ?? "Windows";
                    ViewModel.CompleteExecutionItem(itemId, "COMPLETED", action == "list_apps"
                        ? "Application inventory delivered to Codex"
                        : $"{action} completed in {target} · fresh observation delivered");
                });
                return result;
            }
            catch (Exception exception)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (_activeSession?.Id == sessionId && ViewModel.IsRunning)
                        ViewModel.CompleteExecutionItem(itemId, "FAILED", CleanError(exception));
                });
                throw;
            }
            finally
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (_activeSession?.Id == sessionId && ViewModel.IsRunning) ViewModel.SetTurnActivity("WORKING");
                });
            }
        }
        finally { _desktopGate.Release(); }
    }

    private static bool RequiresAutomaticDesktopApproval(string action, JsonElement arguments, DesktopTargetItem? target)
    {
        if (action == "launch") return true;
        if (action is "list_apps" or "screenshot" or "observe" or "activate" or "move" or "scroll" or "wait") return false;
        if (action is "key" or "keypress" && arguments.TryGetProperty("key", out var key))
        {
            var chord = key.GetString() ?? string.Empty;
            if (chord.Contains("Alt+F4", StringComparison.OrdinalIgnoreCase)
                || chord.Contains("Ctrl+W", StringComparison.OrdinalIgnoreCase)
                || chord.Contains("Ctrl+Q", StringComparison.OrdinalIgnoreCase)
                || chord.Contains("Shift+Delete", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private async Task HandleDesktopToolAsync(CodexAppServerClient client, CodexServerRequest request, CancellationToken cancellationToken)
    {
        object response;
        try
        {
            var parameters = request.Parameters;
            var context = await Dispatcher.UIThread.InvokeAsync(() => (
                Session: _activeSession?.Id,
                Thread: _threadId,
                Running: ViewModel.IsRunning,
                Turn: _browserCodexTurnId,
                Token: _browserTurnCancellation?.Token ?? cancellationToken,
                Vision: SelectedModelSupportsDesktop,
                Api: ViewModel.SelectedModel?.ProviderId.StartsWith("api-", StringComparison.Ordinal) == true));
            if (parameters.GetProperty("tool").GetString() != DesktopTools.Name || context.Session is null || context.Api
                || !context.Running || context.Thread != parameters.GetProperty("threadId").GetString()
                || context.Turn != parameters.GetProperty("turnId").GetString())
                throw new InvalidOperationException("Unknown tool or inactive provider session; desktop access denied.");
            var result = await ExecuteDesktopAsync(parameters.GetProperty("arguments").Clone(), context.Session, context.Vision, context.Token);
            var items = new List<object> { new { type = "inputText", text = result.Text } };
            if (result.ImageDataUrl is not null) items.Add(new { type = "inputImage", imageUrl = result.ImageDataUrl });
            response = new { success = true, contentItems = items };
        }
        catch (Exception exception)
        {
            response = new
            {
                success = false,
                contentItems = new[] { new { type = "inputText", text = "Desktop tool failed: " + CleanError(exception) } }
            };
        }
        await Task.Run(() => client.RespondToServerRequestAsync(request, response, cancellationToken), cancellationToken);
    }
}

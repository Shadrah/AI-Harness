using System.Text.Json;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Harness.Core.Browser;
using Harness.Providers.Codex;

namespace Harness.App.Views;

public sealed partial class MainWindow
{
    private BrowserWindow? _browserWindow;
    private readonly SemaphoreSlim _browserGate = new(1, 1);
    private bool _openingBrowser;
    private CancellationTokenSource? _browserTurnCancellation;
    private string? _browserCodexTurnId;

    private string BuildBrowserInstructions() => _applicationSettings.PersonalInstructions + "\n\n"
        + (OperatingSystem.IsWindows() ? BrowserTools.Instructions : "The Harness reference browser is unavailable on this platform.");

    private async void OpenBrowser_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_openingBrowser || _activeSession is null) return;
        _openingBrowser = true;
        var sessionId = _activeSession.Id;
        try
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The in-app browser currently requires Windows and WebView2.");
            // Old native threads do not accept new dynamic tools on resume in our supported runtime.
            // Ask before creating a continuation; never silently discard the existing provider thread.
            if (_threadId is not null && ViewModel.SelectedModel?.ProviderId.StartsWith("api-", StringComparison.Ordinal) != true && _store is not null)
            {
                var threadId = _threadId;
                var marker = await _store.GetLatestProviderEventPayloadAsync(sessionId, "harness/browserTools/v1", _lifetime.Token);
                if (_activeSession?.Id != sessionId || _threadId != threadId) return;
                var registered = marker is not null && JsonSerializer.Deserialize<JsonElement>(marker).GetProperty("threadId").GetString() == threadId;
                if (!registered)
                {
                    if (ViewModel.IsRunning) throw new InvalidOperationException("Finish or stop this turn before connecting browser tools to this older chat.");
                    var accepted = await ApproveApiToolAsync("Connect browser tools to this chat?",
                        "This older provider session was created without browser tools. Harness will keep your visible messages and create a new provider session on your next send, using a bounded continuity brief. The old provider history remains stored, but not every detail fits in that brief. New chats already include browser tools.\n\nApprove to connect this chat, or decline to leave it unchanged.", _lifetime.Token);
                    if (!accepted || _activeSession?.Id != sessionId || _threadId != threadId || ViewModel.IsRunning) return;
                    // The durable connection change is awaited before changing in-memory state.
                    var modelId = ViewModel.SelectedModel?.ModelName ?? throw new InvalidOperationException("Select a connected model first.");
                    await _store.AppendProviderEventAsync(sessionId, "harness/browserContinuation", JsonSerializer.Serialize(new { previousThreadId = threadId }), _lifetime.Token);
                    await _store.UpdateSessionConnectionAsync(sessionId, _codex!.Id, null, modelId,
                        ViewModel.SelectedReasoningLevel?.Id, ViewModel.SelectedServiceTier?.Id);
                    if (_activeSession?.Id != sessionId) return;
                    _activeSession = _activeSession with { ProviderThreadId = null };
                    _threadId = null;
                    ViewModel.AddActivity("BROWSER", "Browser tools connected. Next send starts a provider continuation; visible history is retained.", "#65C7D0");
                }
            }
            if (_activeSession?.Id == sessionId) OpenBrowserForSession(sessionId);
        }
        catch (Exception exception) { ViewModel.AddActivity("BROWSER", CleanError(exception), "#E2A84A"); }
        finally { _openingBrowser = false; }
    }

    private BrowserWindow OpenBrowserForSession(string sessionId)
    {
        if (_browserWindow?.SessionId != sessionId) { _browserWindow?.Close(); _browserWindow = null; }
        if (_browserWindow is null)
        {
            var window = new BrowserWindow(sessionId, ViewModel.WorkspaceName + " / " + ViewModel.CurrentSessionTitle);
            _browserWindow = window;
            window.Closed += (_, _) => { if (ReferenceEquals(_browserWindow, window)) _browserWindow = null; };
            window.Show(this);
        }
        _browserWindow.Activate();
        return _browserWindow;
    }

    private async Task<BrowserResult> ExecuteBrowserAsync(JsonElement args, string sessionId, bool vision, CancellationToken cancellationToken)
    {
        await _browserGate.WaitAsync(cancellationToken);
        try
        {
            return await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                void CheckSession()
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (_activeSession?.Id != sessionId || !ViewModel.IsRunning)
                        throw new OperationCanceledException("This browser request no longer belongs to the active turn.");
                }
                CheckSession();
                if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Browser tools currently require Windows.");
                var action = args.GetProperty("action").GetString();
                if (action is not ("navigate" or "inspect" or "screenshot" or "click" or "type" or "scroll" or "video"))
                    throw new InvalidOperationException("Unknown browser action.");
                if (action == "navigate") BrowserTools.ValidateUrl(args.GetProperty("url").GetString());
                if (args.GetRawText().Length > 16000) throw new InvalidOperationException("Browser arguments exceed the size limit.");
                var initialConsent = false;
                if (_browserWindow?.SessionId != sessionId || !_browserWindow.AccessAllowed)
                {
                    var accepted = await ApproveApiToolAsync("Allow browser access for this chat?",
                        "The agent will be able to read page text and send screenshots from Harness's visible browser to your selected model provider. This uses a separate Harness profile, not your personal browser tabs.\n\nTurn off Agent access or close the browser to revoke permission. Downloads, popups, microphone and camera access are disabled. Browser clicks and typing require separate approval unless you selected Full access.\n\nRequested action:\n" + args.GetRawText(), cancellationToken);
                    CheckSession();
                    if (!accepted) throw new InvalidOperationException("User declined browser access.");
                    OpenBrowserForSession(sessionId).AccessAllowed = true;
                    initialConsent = true;
                }
                var browser = _browserWindow!;
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, browser.ClosedToken);
                // Automatic command review does not cover custom browser actions. Ask honestly.
                if ((action is "click" or "type" or "navigate") && ViewModel.SelectedPermissionMode.Id != "full"
                    && !(initialConsent && action == "navigate"))
                {
                    var currentUrl = browser.CurrentUrl;
                    if (!await ApproveApiToolAsync("Approve browser action", $"Page: {currentUrl}\n\n{args.GetRawText()}\n\nClicks or typing may submit forms or change account data. Approve only if this fits your task.", linked.Token))
                        throw new InvalidOperationException("User declined browser action.");
                    CheckSession();
                    if (action != "navigate" && currentUrl != browser.CurrentUrl) throw new InvalidOperationException("The page changed during approval. Inspect again.");
                }
                CheckSession();
                ViewModel.SetTurnActivity("USING BROWSER");
                var id = "browser-" + Guid.NewGuid().ToString("N");
                ViewModel.StartExecutionItem(id, "BROWSER", action ?? "Browser", args.GetRawText(), "#65C7D0");
                try
                {
                    var result = await browser.ExecuteAsync(args, vision, linked.Token);
                    CheckSession();
                    ViewModel.CompleteExecutionItem(id, "COMPLETED", $"{action} completed · {browser.CurrentUrl}" + (result.ImageDataUrl is null ? "" : " · frame captured"));
                    return result;
                }
                catch (Exception exception)
                {
                    ViewModel.CompleteExecutionItem(id, "FAILED", exception.Message);
                    if (exception is TimeoutException or OperationCanceledException) browser.Close();
                    throw;
                }
                finally { if (_activeSession?.Id == sessionId && ViewModel.IsRunning) ViewModel.SetTurnActivity("WORKING"); }
            });
        }
        finally { _browserGate.Release(); }
    }

    private async Task HandleBrowserToolAsync(CodexAppServerClient client, CodexServerRequest request, CancellationToken cancellationToken)
    {
        object response;
        try
        {
            var p = request.Parameters;
            var context = await Dispatcher.UIThread.InvokeAsync(() => (
                Session: _activeSession?.Id, Thread: _threadId, Running: ViewModel.IsRunning,
                Turn: _browserCodexTurnId, Token: _browserTurnCancellation?.Token ?? cancellationToken,
                Vision: ViewModel.SelectedModel?.Capabilities.Contains("VISION") == true,
                Api: ViewModel.SelectedModel?.ProviderId.StartsWith("api-", StringComparison.Ordinal) == true));
            if (p.GetProperty("tool").GetString() != BrowserTools.Name || context.Session is null || context.Api
                || !context.Running || context.Thread != p.GetProperty("threadId").GetString()
                || context.Turn != p.GetProperty("turnId").GetString())
                throw new InvalidOperationException("Unknown tool or inactive provider session; browser access denied.");
            var result = await ExecuteBrowserAsync(p.GetProperty("arguments").Clone(), context.Session, context.Vision, context.Token);
            var items = new List<object> { new { type = "inputText", text = result.Text } };
            if (result.ImageDataUrl is not null) items.Add(new { type = "inputImage", imageUrl = result.ImageDataUrl });
            response = new { success = true, contentItems = items };
        }
        catch (Exception exception)
        {
            response = new { success = false, contentItems = new[] { new { type = "inputText", text = "Browser tool failed: " + exception.Message } } };
        }
        // Encoding screenshot results and writing JSON must not occupy Avalonia's UI dispatcher.
        await Task.Run(() => client.RespondToServerRequestAsync(request, response, cancellationToken), cancellationToken);
    }
}

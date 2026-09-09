using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Harness.Core.Models;
using Harness.Core.Providers;

namespace Harness.Providers.Claude;

public sealed class ClaudeCodeClient : IModelProvider
{
    private const int MaximumInlineTextCharacters = 512 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private ClaudeCodeRuntimeInfo _runtime;
    private IReadOnlyList<ModelDescriptor> _models = [];
    private readonly string? _configurationDirectory;

    public ClaudeCodeClient(ClaudeCodeRuntimeInfo? runtime = null, string? configurationDirectory = null)
    {
        _runtime = runtime ?? ClaudeCodeRuntimeResolver.Resolve();
        _configurationDirectory = string.IsNullOrWhiteSpace(configurationDirectory)
            ? null
            : Path.GetFullPath(configurationDirectory);
    }

    public string Id => "anthropic-claude";
    public string DisplayName => "Claude Code";
    public ClaudeCodeRuntimeInfo Runtime => _runtime;

    public async IAsyncEnumerable<ModelDescriptor> GetModelsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (_models.Count == 0)
            _models = (await ProbeAsync(cancellationToken).ConfigureAwait(false)).Models;
        foreach (var model in _models)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return model;
        }
    }

    public Task<ClaudeCodeConnection> ProbeAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => ProbeCoreAsync(cancellationToken), cancellationToken);

    public Task<ClaudeCodeTurnResult> RunTurnAsync(
        ClaudeCodeTurnRequest request,
        Func<ClaudeCodeEvent, Task> onEvent,
        Func<ClaudeCodePermissionRequest, CancellationToken, Task<bool>> approve,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => RunTurnCoreAsync(request, onEvent, approve, cancellationToken), cancellationToken);

    public Task SignInAsync(CancellationToken cancellationToken = default) =>
        Task.Run(async () =>
        {
            var result = await RunCommandAsync(["auth", "login"], TimeSpan.FromMinutes(5), cancellationToken)
                .ConfigureAwait(false);
            if (result.ExitCode != 0)
                throw new InvalidOperationException(FirstUsefulLine(result.StandardError, result.StandardOutput, "Claude sign-in did not complete."));
        }, cancellationToken);

    public Task SignOutAsync(CancellationToken cancellationToken = default) =>
        Task.Run(async () =>
        {
            var result = await RunCommandAsync(["auth", "logout"], TimeSpan.FromSeconds(30), cancellationToken)
                .ConfigureAwait(false);
            if (result.ExitCode != 0)
                throw new InvalidOperationException(FirstUsefulLine(result.StandardError, result.StandardOutput, "Claude sign-out failed."));
            _models = [];
        }, cancellationToken);

    private async Task<ClaudeCodeConnection> ProbeCoreAsync(CancellationToken cancellationToken)
    {
        if (_configurationDirectory is not null) Directory.CreateDirectory(_configurationDirectory);
        _runtime = ClaudeCodeRuntimeResolver.Resolve();
        ProcessResult version;
        try
        {
            version = await RunCommandAsync(["--version"], TimeSpan.FromSeconds(10), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            return new(_runtime, null, new(false, null, null, null, null, null), [], null, null,
                "Claude Code is not installed. Install the official Claude Code runtime, then connect it here.");
        }

        var versionText = FirstUsefulLine(version.StandardOutput, version.StandardError, null);
        if (version.ExitCode != 0)
            return new(_runtime, versionText, new(false, null, null, null, null, null), [], null, null,
                FirstUsefulLine(version.StandardError, version.StandardOutput, "Claude Code could not start."));

        var auth = await ReadAuthStatusAsync(cancellationToken).ConfigureAwait(false);
        if (!auth.IsAuthenticated)
            return new(_runtime, versionText, auth, [], null, null, "Sign in with your Claude Pro, Max, Team, Enterprise, or Console account.");

        try
        {
            var initialization = await ReadInitializationAsync(cancellationToken).ConfigureAwait(false);
            auth = initialization.Account.IsAuthenticated ? initialization.Account : auth;
            _models = initialization.Models;
            return new(_runtime, versionText, auth, _models, initialization.Usage,
                initialization.FastModeState, initialization.Diagnostic);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new(_runtime, versionText, auth, [], null, null,
                $"Claude is authenticated, but its model catalog could not be read: {CleanError(exception)}");
        }
    }

    private async Task<ClaudeCodeAccount> ReadAuthStatusAsync(CancellationToken cancellationToken)
    {
        var status = await RunCommandAsync(["auth", "status"], TimeSpan.FromSeconds(15), cancellationToken)
            .ConfigureAwait(false);
        if (status.ExitCode != 0 || string.IsNullOrWhiteSpace(status.StandardOutput))
            return new(false, null, null, null, null, null);
        try
        {
            using var document = JsonDocument.Parse(status.StandardOutput);
            var root = document.RootElement;
            return new(
                Boolean(root, "loggedIn") || Boolean(root, "isAuthenticated") || Boolean(root, "authenticated"),
                String(root, "email"),
                String(root, "organization") ?? String(root, "orgName"),
                String(root, "subscriptionType") ?? String(root, "subscription_type"),
                String(root, "authMethod") ?? String(root, "tokenSource"),
                String(root, "apiProvider"));
        }
        catch (JsonException)
        {
            // A successful process exit is not proof of an authenticated account.
            // Older or unexpected CLI output must remain honestly unknown rather
            // than surfacing a phantom connection in Settings.
            return new(false, null, null, null, null, null);
        }
    }

    private async Task<InitializationResult> ReadInitializationAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var process = new Process
        {
            StartInfo = ClaudeCodeRuntimeResolver.CreateStartInfo(_runtime,
                ["-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose",
                 "--permission-mode", "dontAsk", "--tools", ""],
                configurationDirectory: _configurationDirectory)
        };
        if (!process.Start()) throw new InvalidOperationException("Claude Code did not start.");
        var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
        var initId = "harness-init-" + Guid.NewGuid().ToString("N");
        await WriteLineAsync(process, new
        {
            type = "control_request",
            request_id = initId,
            request = new { subtype = "initialize" }
        }, timeout.Token).ConfigureAwait(false);

        JsonElement? init = null;
        ProviderUsageSnapshot? usage = null;
        var usageId = "harness-usage-" + Guid.NewGuid().ToString("N");
        while (await process.StandardOutput.ReadLineAsync(timeout.Token).ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (String(root, "type") != "control_response"
                || !root.TryGetProperty("response", out var envelope)) continue;
            var requestId = String(envelope, "request_id");
            var succeeded = String(envelope, "subtype") == "success";
            var response = envelope.TryGetProperty("response", out var value)
                ? value.Clone()
                : JsonSerializer.SerializeToElement(new { });
            if (requestId == initId)
            {
                if (!succeeded)
                    throw new InvalidOperationException(String(envelope, "error") ?? "Claude initialization failed.");
                init = response.Clone();
                await WriteLineAsync(process, new
                {
                    type = "control_request",
                    request_id = usageId,
                    request = new { subtype = "get_usage" }
                }, timeout.Token).ConfigureAwait(false);
            }
            else if (requestId == usageId)
            {
                if (init is not null && succeeded)
                    usage = ParseUsage(response, ParseAccount(init.Value).SubscriptionType);
                break;
            }
        }

        TryKill(process);
        try { await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
        var stderr = await IgnoreCancellationAsync(stderrTask).ConfigureAwait(false);
        if (init is null) throw new InvalidOperationException(FirstUsefulLine(stderr, null, "Claude did not return initialization data."));
        var parsedAccount = ParseAccount(init.Value);
        var models = ParseModels(init.Value);
        var fastState = String(init.Value, "fast_mode_state") ?? String(init.Value, "fastModeState");
        return new(parsedAccount, models, usage, fastState,
            models.Count == 0 ? "Claude authenticated but reported no selectable models for this account." : null);
    }

    private async Task<ClaudeCodeTurnResult> RunTurnCoreAsync(
        ClaudeCodeTurnRequest request,
        Func<ClaudeCodeEvent, Task> onEvent,
        Func<ClaudeCodePermissionRequest, CancellationToken, Task<bool>> approve,
        CancellationToken cancellationToken)
    {
        ValidateTurnRequest(request);
        var temporaryRoot = Path.Combine(Path.GetTempPath(), "harness-claude-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryRoot);
        try
        {
            var arguments = new List<string>
            {
                "-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose",
                "--include-partial-messages", "--include-hook-events",
                "--permission-prompt-tool", "stdio", "--model", request.Model,
                "--permission-mode", MapPermissionMode(request.PermissionMode)
            };
            if (request.PermissionMode == "full") arguments.Add("--dangerously-skip-permissions");
            if (!string.IsNullOrWhiteSpace(request.Effort))
            {
                ValidateCliValue(request.Effort, "effort");
                arguments.AddRange(["--effort", request.Effort]);
            }
            arguments.AddRange(request.Resume
                ? ["--resume", request.SessionId]
                : ["--session-id", request.SessionId]);

            var settingsPath = Path.Combine(temporaryRoot, "settings.json");
            await File.WriteAllTextAsync(settingsPath,
                JsonSerializer.Serialize(new { fastMode = request.FastMode }), cancellationToken).ConfigureAwait(false);
            arguments.AddRange(["--settings", settingsPath]);
            if (!string.IsNullOrWhiteSpace(request.AdditionalInstructions))
            {
                var instructionPath = Path.Combine(temporaryRoot, "instructions.md");
                await File.WriteAllTextAsync(instructionPath, request.AdditionalInstructions, cancellationToken)
                    .ConfigureAwait(false);
                arguments.AddRange(["--append-system-prompt-file", instructionPath]);
            }

            using var process = new Process
            {
                StartInfo = ClaudeCodeRuntimeResolver.CreateStartInfo(
                    _runtime, arguments, request.WorkingDirectory, _configurationDirectory)
            };
            if (!process.Start()) throw new InvalidOperationException("Claude Code did not start.");
            using var registration = cancellationToken.Register(() => TryKill(process));
            var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
            var initId = "harness-init-" + Guid.NewGuid().ToString("N");
            await WriteLineAsync(process, new
            {
                type = "control_request",
                request_id = initId,
                request = new { subtype = "initialize" }
            }, cancellationToken).ConfigureAwait(false);

            var promptSent = false;
            var sawTextDelta = false;
            var sessionId = request.SessionId;
            long? inputTokens = null, outputTokens = null, totalTokens = null;
            string? terminalError = null;
            var succeeded = false;
            while (await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                JsonElement root;
                try
                {
                    using var document = JsonDocument.Parse(line);
                    root = document.RootElement.Clone();
                }
                catch (JsonException) { continue; }

                if (root.TryGetProperty("type", out var typeElement)
                    && typeElement.GetString() == "control_request")
                {
                    await HandleControlRequestAsync(process, root, request.PermissionMode, approve, cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }
                if (IsSuccessfulControlResponse(root, out var responseId, out _)
                    && responseId == initId && !promptSent)
                {
                    promptSent = true;
                    await WriteLineAsync(process, await BuildUserMessageAsync(request, cancellationToken).ConfigureAwait(false),
                        cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var type = String(root, "type");
                if (type == "system" && String(root, "subtype") == "init")
                {
                    sessionId = String(root, "session_id") ?? sessionId;
                    await onEvent(Event(ClaudeCodeEventKind.Initialized, "claude-init", "Claude Code ready",
                        String(root, "model") ?? request.Model, root, sessionId: sessionId)).ConfigureAwait(false);
                    continue;
                }
                if (type == "stream_event" && root.TryGetProperty("event", out var streamEvent))
                {
                    var streamType = String(streamEvent, "type");
                    if (streamType == "content_block_delta" && streamEvent.TryGetProperty("delta", out var delta))
                    {
                        var deltaType = String(delta, "type");
                        var text = String(delta, "text") ?? String(delta, "thinking") ?? string.Empty;
                        if (text.Length > 0)
                        {
                            var isReasoning = deltaType?.Contains("thinking", StringComparison.OrdinalIgnoreCase) == true;
                            if (!isReasoning) sawTextDelta = true;
                            await onEvent(Event(isReasoning ? ClaudeCodeEventKind.ReasoningDelta : ClaudeCodeEventKind.TextDelta,
                                String(streamEvent, "index") ?? "claude-text", isReasoning ? "Reasoning" : "Response",
                                text, root, sessionId: sessionId)).ConfigureAwait(false);
                        }
                    }
                    continue;
                }
                if (type == "assistant")
                {
                    await EmitAssistantMessageAsync(root, sawTextDelta, onEvent, sessionId).ConfigureAwait(false);
                    continue;
                }
                if (type == "user")
                {
                    await EmitToolResultsAsync(root, onEvent, sessionId).ConfigureAwait(false);
                    continue;
                }
                if (type is "task_progress" or "tool_progress" or "hook_progress")
                {
                    await onEvent(Event(ClaudeCodeEventKind.Progress,
                        String(root, "task_id") ?? String(root, "tool_use_id") ?? Guid.NewGuid().ToString("N"),
                        "Working", String(root, "summary") ?? String(root, "message") ?? type, root,
                        sessionId: sessionId)).ConfigureAwait(false);
                    continue;
                }
                if (type == "rate_limit_event")
                {
                    await onEvent(Event(ClaudeCodeEventKind.RateLimit, "claude-rate-limit", "Usage updated",
                        String(root, "rate_limit_type") ?? "Claude rate limit update", root,
                        sessionId: sessionId)).ConfigureAwait(false);
                    continue;
                }
                if (type == "result")
                {
                    sessionId = String(root, "session_id") ?? sessionId;
                    succeeded = String(root, "subtype") == "success" && !Boolean(root, "is_error");
                    terminalError = succeeded ? null : String(root, "result") ?? String(root, "error");
                    if (root.TryGetProperty("usage", out var usage))
                    {
                        inputTokens = Integer(usage, "input_tokens");
                        outputTokens = Integer(usage, "output_tokens");
                        totalTokens = AddNullable(inputTokens, outputTokens,
                            Integer(usage, "cache_creation_input_tokens"), Integer(usage, "cache_read_input_tokens"));
                    }
                    await onEvent(Event(succeeded ? ClaudeCodeEventKind.Completed : ClaudeCodeEventKind.Error,
                        "claude-result", succeeded ? "Completed" : "Claude Code stopped",
                        terminalError ?? "Claude Code completed the turn.", root, inputTokens, outputTokens,
                        totalTokens, sessionId)).ConfigureAwait(false);
                    break;
                }
            }

            try { process.StandardInput.Close(); } catch { }
            if (!process.HasExited)
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            if (!succeeded && string.IsNullOrWhiteSpace(terminalError))
                terminalError = FirstUsefulLine(stderr, null, $"Claude Code exited with code {process.ExitCode}.");
            return new(sessionId, succeeded, terminalError, inputTokens, outputTokens, totalTokens);
        }
        finally
        {
            try { Directory.Delete(temporaryRoot, true); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    private static async Task HandleControlRequestAsync(
        Process process,
        JsonElement root,
        string permissionMode,
        Func<ClaudeCodePermissionRequest, CancellationToken, Task<bool>> approve,
        CancellationToken cancellationToken)
    {
        var requestId = String(root, "request_id") ?? throw new InvalidDataException("Claude omitted a control request id.");
        if (!root.TryGetProperty("request", out var request) || String(request, "subtype") != "can_use_tool")
        {
            await WriteLineAsync(process, new
            {
                type = "control_response",
                response = new { subtype = "error", request_id = requestId, error = "Unsupported Harness control request." }
            }, cancellationToken).ConfigureAwait(false);
            return;
        }

        var input = request.TryGetProperty("input", out var inputElement)
            ? inputElement.Clone()
            : JsonSerializer.SerializeToElement(new { });
        var permission = new ClaudeCodePermissionRequest(requestId,
            String(request, "tool_name") ?? "Tool", input, String(request, "title"),
            String(request, "description"), String(request, "tool_use_id"));
        // In auto mode Claude handles operations it classifies as safe. Any request that reaches
        // Harness crossed that boundary and still receives an explicit user decision.
        var allowed = permissionMode == "full"
            || await approve(permission, cancellationToken).ConfigureAwait(false);
        object decision = allowed
            ? new { behavior = "allow", updatedInput = input }
            : new { behavior = "deny", message = "The user declined this operation.", interrupt = false };
        await WriteLineAsync(process, new
        {
            type = "control_response",
            response = new { subtype = "success", request_id = requestId, response = decision }
        }, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<object> BuildUserMessageAsync(
        ClaudeCodeTurnRequest request,
        CancellationToken cancellationToken)
    {
        var blocks = new List<object> { new { type = "text", text = request.Prompt } };
        foreach (var file in request.TurnAttachments.Concat(request.ContextFiles))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.GetFullPath(file.Path);
            if (!File.Exists(path)) throw new FileNotFoundException("A Claude attachment is no longer available.", path);
            if (file.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true)
            {
                var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
                blocks.Add(new
                {
                    type = "image",
                    source = new { type = "base64", media_type = file.MediaType, data = Convert.ToBase64String(bytes) }
                });
                continue;
            }
            if (IsTextFile(file, path))
            {
                var text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
                if (text.Length > MaximumInlineTextCharacters) text = text[..MaximumInlineTextCharacters]
                    + "\n[Harness truncated this inline copy; Claude can read the complete file from the path below.]";
                blocks.Add(new { type = "text", text = $"Attached file {file.DisplayName} at {path}:\n{text}" });
            }
            else blocks.Add(new { type = "text", text = $"Attached file {file.DisplayName} is available at {path}. Inspect it with Claude Code's file tools when relevant." });
        }
        return new
        {
            type = "user",
            session_id = request.SessionId,
            parent_tool_use_id = (string?)null,
            message = new { role = "user", content = blocks }
        };
    }

    private static bool IsTextFile(FilePart file, string path) =>
        file.MediaType?.StartsWith("text/", StringComparison.OrdinalIgnoreCase) == true
        || new[] { ".md", ".txt", ".json", ".jsonl", ".cs", ".fs", ".js", ".ts", ".tsx", ".jsx",
            ".py", ".rs", ".go", ".java", ".cpp", ".c", ".h", ".xml", ".yaml", ".yml", ".toml",
            ".ini", ".css", ".html", ".sql", ".sh", ".ps1", ".cmd", ".bat", ".sln", ".csproj" }
        .Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private static async Task EmitAssistantMessageAsync(
        JsonElement root, bool sawTextDelta, Func<ClaudeCodeEvent, Task> onEvent, string sessionId)
    {
        if (!root.TryGetProperty("message", out var message)
            || !message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return;
        foreach (var block in content.EnumerateArray())
        {
            var type = String(block, "type");
            if (type == "text" && !sawTextDelta && String(block, "text") is { Length: > 0 } text)
                await onEvent(Event(ClaudeCodeEventKind.TextDelta, String(root, "uuid") ?? "claude-text",
                    "Response", text, root, sessionId: sessionId)).ConfigureAwait(false);
            else if (type == "thinking" && String(block, "thinking") is { Length: > 0 } thinking)
                await onEvent(Event(ClaudeCodeEventKind.ReasoningDelta, String(root, "uuid") ?? "claude-thinking",
                    "Reasoning", thinking, root, sessionId: sessionId)).ConfigureAwait(false);
            else if (type == "tool_use")
                await onEvent(Event(ClaudeCodeEventKind.ToolStarted, String(block, "id") ?? Guid.NewGuid().ToString("N"),
                    String(block, "name") ?? "Tool", block.TryGetProperty("input", out var input) ? input.GetRawText() : "",
                    root, sessionId: sessionId)).ConfigureAwait(false);
        }
    }

    private static async Task EmitToolResultsAsync(
        JsonElement root, Func<ClaudeCodeEvent, Task> onEvent, string sessionId)
    {
        if (!root.TryGetProperty("message", out var message)
            || !message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return;
        foreach (var block in content.EnumerateArray().Where(block => String(block, "type") == "tool_result"))
        {
            var text = block.TryGetProperty("content", out var value)
                ? value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.GetRawText()
                : "";
            await onEvent(Event(ClaudeCodeEventKind.ToolCompleted,
                String(block, "tool_use_id") ?? Guid.NewGuid().ToString("N"), "Tool completed", text, root,
                sessionId: sessionId)).ConfigureAwait(false);
        }
    }

    public static IReadOnlyList<ModelDescriptor> ParseModels(JsonElement initialization)
    {
        if (!initialization.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array) return [];
        var result = new List<ModelDescriptor>();
        foreach (var model in models.EnumerateArray())
        {
            var id = String(model, "value");
            if (string.IsNullOrWhiteSpace(id)) continue;
            var reportedLevels = model.TryGetProperty("supportedEffortLevels", out var effortLevels)
                                 && effortLevels.ValueKind == JsonValueKind.Array
                ? effortLevels.EnumerateArray().Select(item => item.GetString())
                    .Where(item => !string.IsNullOrWhiteSpace(item)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
                : [];
            var levels = reportedLevels.Length > 0
                ? new[] { new ReasoningLevelDescriptor("", "Provider default", IsDefault: true) }
                    .Concat(reportedLevels.Select(item => new ReasoningLevelDescriptor(item!, FormatEffort(item!))))
                    .ToArray()
                : [];
            var capabilities = ModelCapability.Text | ModelCapability.Vision | ModelCapability.ToolUse
                               | ModelCapability.PdfInput | ModelCapability.GeneratedArtifacts | ModelCapability.ContextManagement;
            if (Boolean(model, "supportsEffort") || Boolean(model, "supportsAdaptiveThinking") || levels.Length > 0)
                capabilities |= ModelCapability.Reasoning;
            var tiers = Boolean(model, "supportsFastMode")
                ? new[]
                {
                    new ServiceTierDescriptor(null, "Standard", "Claude Code standard mode", true),
                    new ServiceTierDescriptor("fast", "Fast", "Claude Code fast mode; may use separately billed extra usage")
                }
                : [];
            result.Add(new ModelDescriptor("anthropic-claude", id,
                String(model, "displayName") ?? id, capabilities,
                Int32(model, "contextWindow") ?? Int32(model, "contextWindowTokens"), levels, tiers,
                Boolean(model, "isDefault")));
        }
        return result;
    }

    public static ClaudeCodeAccount ParseAccount(JsonElement initialization)
    {
        var account = initialization.TryGetProperty("account", out var value) ? value : initialization;
        var email = String(account, "email");
        var subscription = String(account, "subscriptionType") ?? String(account, "subscription_type");
        var tokenSource = String(account, "tokenSource") ?? String(account, "token_source");
        var hasTokenSource = !string.IsNullOrWhiteSpace(tokenSource)
                             && !string.Equals(tokenSource, "none", StringComparison.OrdinalIgnoreCase)
                             && !string.Equals(tokenSource, "unknown", StringComparison.OrdinalIgnoreCase);
        return new(!string.IsNullOrWhiteSpace(email) || !string.IsNullOrWhiteSpace(subscription)
                   || hasTokenSource, email,
            String(account, "organization") ?? String(account, "organizationName"), subscription, tokenSource,
            String(account, "apiProvider") ?? String(account, "api_provider"));
    }

    public static ProviderUsageSnapshot? ParseUsage(JsonElement response, string? plan)
    {
        if (!Boolean(response, "rate_limits_available")
            || !response.TryGetProperty("rate_limits", out var limits) || limits.ValueKind != JsonValueKind.Object)
            return null;
        var windows = new List<UsageWindowSnapshot>();
        AddUsageWindow(windows, limits, "five_hour", "5 HOUR WINDOW", TimeSpan.FromHours(5));
        AddUsageWindow(windows, limits, "seven_day", "WEEKLY LIMIT", TimeSpan.FromDays(7));
        if (limits.TryGetProperty("model_scoped", out var scoped) && scoped.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in scoped.EnumerateArray())
            {
                var label = String(item, "display_name");
                if (label is null || Number(item, "utilization") is not { } used) continue;
                windows.Add(new("seven_day_" + Slug(label), "WEEKLY · " + label.ToUpperInvariant(),
                    ClampPercent(used), TimeSpan.FromDays(7), Timestamp(item, "resets_at")));
            }
        }
        return new("anthropic-claude", "claude-code",
            String(response, "subscription_type") ?? plan, windows, DateTimeOffset.UtcNow);
    }

    private static void AddUsageWindow(List<UsageWindowSnapshot> windows, JsonElement limits,
        string id, string label, TimeSpan duration)
    {
        if (!limits.TryGetProperty(id, out var window) || Number(window, "utilization") is not { } used) return;
        windows.Add(new(id, label, ClampPercent(used), duration, Timestamp(window, "resets_at")));
    }

    private async Task<ProcessResult> RunCommandAsync(
        IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(timeout);
        using var process = new Process
        {
            StartInfo = ClaudeCodeRuntimeResolver.CreateStartInfo(
                _runtime, arguments, configurationDirectory: _configurationDirectory)
        };
        if (!process.Start()) throw new InvalidOperationException("Claude Code did not start.");
        using var registration = linked.Token.Register(() => TryKill(process));
        var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        return new(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
    }

    private static bool IsSuccessfulControlResponse(JsonElement root, out string? requestId, out JsonElement response)
    {
        requestId = null;
        response = default;
        if (String(root, "type") != "control_response" || !root.TryGetProperty("response", out var envelope)
            || String(envelope, "subtype") != "success") return false;
        requestId = String(envelope, "request_id");
        response = envelope.TryGetProperty("response", out var value) ? value.Clone() : JsonSerializer.SerializeToElement(new { });
        return true;
    }

    private static Task WriteLineAsync(Process process, object value, CancellationToken cancellationToken) =>
        process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(value, JsonOptions).AsMemory(), cancellationToken);

    private static ClaudeCodeEvent Event(ClaudeCodeEventKind kind, string id, string title, string text,
        JsonElement native, long? input = null, long? output = null, long? total = null, string? sessionId = null) =>
        new(kind, id, title, text, native.Clone(), input, output, total, sessionId);

    private static string MapPermissionMode(string mode) => mode switch
    {
        "auto" => "auto",
        "full" => "bypassPermissions",
        _ => "manual"
    };

    private static void ValidateTurnRequest(ClaudeCodeTurnRequest request)
    {
        if (!Directory.Exists(request.WorkingDirectory)) throw new DirectoryNotFoundException(request.WorkingDirectory);
        if (!Guid.TryParse(request.SessionId, out _)) throw new ArgumentException("Claude session id must be a UUID.");
        ValidateCliValue(request.Model, "model");
    }

    private static void ValidateCliValue(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256
            || value.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':' or '/' or '@' or '[' or ']')))
            throw new ArgumentException($"Claude {name} contains unsupported command-line characters.");
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(true); }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    private static Task<string> IgnoreCancellationAsync(Task<string> task) => task.ContinueWith(
        completed => completed.Status == TaskStatus.RanToCompletion ? completed.Result : string.Empty,
        CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    private static string? FirstUsefulLine(string? primary, string? secondary, string? fallback) =>
        new[] { primary, secondary }.Where(value => !string.IsNullOrWhiteSpace(value))
            .SelectMany(value => value!.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            .Select(value => value.Trim()).FirstOrDefault(value => value.Length > 0) ?? fallback;
    private static string CleanError(Exception exception) => exception.Message.Replace("\r", " ").Replace("\n", " ").Trim();
    private static string? String(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static bool Boolean(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
        && value.ValueKind is JsonValueKind.True;
    private static long? Integer(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
        && value.TryGetInt64(out var number) ? number : null;
    private static int? Int32(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
        && value.TryGetInt32(out var number) ? number : null;
    private static double? Number(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
        && value.TryGetDouble(out var number) ? number : null;
    private static DateTimeOffset? Timestamp(JsonElement element, string name) =>
        String(element, name) is { } value && DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;
    private static double ClampPercent(double value) => Math.Clamp(value, 0, 100);
    private static string Slug(string value) => string.Concat(value.ToLowerInvariant().Select(character =>
        char.IsAsciiLetterOrDigit(character) ? character : '_')).Trim('_');
    private static long? AddNullable(params long?[] values) => values.All(value => value is null) ? null : values.Sum(value => value ?? 0);
    private static string FormatEffort(string effort) => effort.Equals("xhigh", StringComparison.OrdinalIgnoreCase)
        ? "XHigh" : char.ToUpperInvariant(effort[0]) + effort[1..];

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
    private sealed record InitializationResult(ClaudeCodeAccount Account, IReadOnlyList<ModelDescriptor> Models,
        ProviderUsageSnapshot? Usage, string? FastModeState, string? Diagnostic);
}

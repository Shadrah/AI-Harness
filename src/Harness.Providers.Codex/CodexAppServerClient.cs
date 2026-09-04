using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Harness.Core.Models;
using Harness.Core.Providers;

namespace Harness.Providers.Codex;

public sealed class CodexAppServerClient : IModelProvider, IProviderTelemetry, IAsyncDisposable
{
    private const int MaximumInlineTextCharacters = 512 * 1024;
    private static readonly HashSet<string> TextFileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".cs", ".fs", ".fsx", ".vb", ".js", ".mjs", ".cjs",
        ".ts", ".tsx", ".jsx", ".json", ".jsonl", ".xml", ".yaml", ".yml", ".toml", ".ini",
        ".cfg", ".conf", ".py", ".rs", ".go", ".java", ".kt", ".kts", ".cpp", ".cc", ".c",
        ".h", ".hpp", ".css", ".scss", ".sass", ".less", ".html", ".htm", ".sql", ".sh",
        ".bash", ".zsh", ".ps1", ".psm1", ".cmd", ".bat", ".gradle", ".properties", ".env",
        ".gitignore", ".gitattributes", ".editorconfig", ".sln", ".csproj", ".fsproj", ".vbproj"
    };

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly Process _process;
    private readonly StreamWriter _input;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly Channel<CodexNotification> _notifications =
        Channel.CreateUnbounded<CodexNotification>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = true
        });
    private readonly Channel<CodexServerRequest> _serverRequests =
        Channel.CreateUnbounded<CodexServerRequest>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true
        });
    private readonly Task<string> _standardErrorTask;
    private Task? _readTask;
    private long _nextRequestId;

    private CodexAppServerClient(Process process, CodexRuntimeInfo runtime)
    {
        _process = process;
        Runtime = runtime;
        _input = process.StandardInput;
        _input.AutoFlush = true;
        _standardErrorTask = process.StandardError.ReadToEndAsync();
    }

    public string Id => "openai-codex";
    public string DisplayName => "OpenAI Codex";
    public CodexRuntimeInfo Runtime { get; }

    public IAsyncEnumerable<CodexNotification> Notifications(
        CancellationToken cancellationToken = default) =>
        _notifications.Reader.ReadAllAsync(cancellationToken);

    public IAsyncEnumerable<CodexServerRequest> ServerRequests(
        CancellationToken cancellationToken = default) =>
        _serverRequests.Reader.ReadAllAsync(cancellationToken);

    public Task RespondToServerRequestAsync(
        CodexServerRequest request,
        object result,
        CancellationToken cancellationToken = default) =>
        WriteAsync(
            new { jsonrpc = "2.0", id = request.Id, result },
            cancellationToken);

    public Task RejectServerRequestAsync(
        CodexServerRequest request,
        string message,
        CancellationToken cancellationToken = default) =>
        WriteAsync(
            new
            {
                jsonrpc = "2.0",
                id = request.Id,
                error = new { code = -32601, message }
            },
            cancellationToken);

    public static Task<CodexAppServerClient> StartAsync(
        CancellationToken cancellationToken = default) =>
        // Resolution, process startup and the JSON read pump must never capture the UI context.
        Task.Run(() => StartCoreAsync(cancellationToken), cancellationToken);

    private static async Task<CodexAppServerClient> StartCoreAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var runtime = CodexRuntimeResolver.Resolve();
        var process = new Process { StartInfo = CreateStartInfo(runtime) };
        if (!process.Start())
        {
            throw new InvalidOperationException("Codex app-server did not start.");
        }

        var client = new CodexAppServerClient(process, runtime);
        client._readTask = client.ReadLoopAsync(client._shutdown.Token);

        try
        {
            await client.CallAsync<JsonElement>(
                "initialize",
                new
                {
                    clientInfo = new { name = "harness", title = "Harness", version = "0.1.0" },
                    capabilities = new { experimentalApi = true }
                },
                cancellationToken);
            await client.NotifyAsync("initialized", new { }, cancellationToken);
            return client;
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
    }

    public async IAsyncEnumerable<ModelDescriptor> GetModelsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await CallAsync<ModelListResponse>(
            "model/list",
            new { limit = 100 },
            cancellationToken);

        foreach (var model in response.Data.Where(item => !item.Hidden))
        {
            var capabilities = ModelCapability.Text | ModelCapability.ToolUse;
            if (model.InputModalities.Contains("image", StringComparer.OrdinalIgnoreCase))
            {
                capabilities |= ModelCapability.Vision;
            }
            if (model.SupportedReasoningEfforts.Count > 0)
            {
                capabilities |= ModelCapability.Reasoning;
            }

            yield return new ModelDescriptor(
                Id,
                model.Model,
                model.DisplayName,
                capabilities,
                ReasoningLevels: model.SupportedReasoningEfforts
                    .Select(option => new ReasoningLevelDescriptor(
                        option.ReasoningEffort,
                        FormatEffort(option.ReasoningEffort),
                        option.Description,
                        string.Equals(
                            option.ReasoningEffort,
                            model.DefaultReasoningEffort,
                            StringComparison.OrdinalIgnoreCase)))
                    .ToArray(),
                ServiceTiers: BuildServiceTiers(model),
                IsDefault: model.IsDefault);
        }
    }

    public async Task<string> StartThreadAsync(
        string workingDirectory,
        string model,
        string permissionMode,
        string? developerInstructions,
        CancellationToken cancellationToken = default)
    {
        var runtimePolicy = ResolveRuntimePolicy(permissionMode);
        var response = await CallAsync<ThreadStartResponse>(
            "thread/start",
            new
            {
                cwd = Path.GetFullPath(workingDirectory),
                model,
                approvalPolicy = runtimePolicy.ApprovalPolicy,
                approvalsReviewer = runtimePolicy.ApprovalsReviewer,
                sandbox = runtimePolicy.Sandbox,
                developerInstructions = NullIfWhiteSpace(developerInstructions),
                ephemeral = false,
                dynamicTools = Harness.Core.Browser.BrowserTools.CodexDefinitions
            },
            cancellationToken);
        return response.Thread.Id;
    }

    public async Task<string> ResumeThreadAsync(
        string threadId,
        string workingDirectory,
        string? model = null,
        string permissionMode = "ask",
        string? developerInstructions = null,
        CancellationToken cancellationToken = default)
    {
        var runtimePolicy = ResolveRuntimePolicy(permissionMode);
        var response = await CallAsync<ThreadResumeResponse>(
            "thread/resume",
            new
            {
                threadId,
                cwd = Path.GetFullPath(workingDirectory),
                model,
                approvalPolicy = runtimePolicy.ApprovalPolicy,
                approvalsReviewer = runtimePolicy.ApprovalsReviewer,
                sandbox = runtimePolicy.Sandbox,
                developerInstructions = NullIfWhiteSpace(developerInstructions)
            },
            cancellationToken);
        return response.Thread.Id;
    }

    public async Task<string> StartTurnAsync(
        string threadId,
        string prompt,
        string model,
        string? reasoningEffort,
        string? serviceTier,
        string permissionMode,
        IReadOnlyList<FilePart>? turnAttachments = null,
        IReadOnlyList<FilePart>? contextFiles = null,
        CancellationToken cancellationToken = default)
    {
        var input = new List<object> { new { type = "text", text = prompt } };
        foreach (var attachment in turnAttachments ?? [])
        {
            await AppendFileInputAsync(input, attachment, "turn attachment", cancellationToken);
        }
        foreach (var contextFile in contextFiles ?? [])
        {
            await AppendFileInputAsync(input, contextFile, "persistent context", cancellationToken);
        }

        var runtimePolicy = ResolveRuntimePolicy(permissionMode);
        var response = await CallAsync<TurnStartResponse>(
            "turn/start",
            new
            {
                threadId,
                input,
                model,
                effort = reasoningEffort,
                serviceTier,
                approvalPolicy = runtimePolicy.ApprovalPolicy,
                approvalsReviewer = runtimePolicy.ApprovalsReviewer,
                sandboxPolicy = runtimePolicy.IsFullAccess
                    ? new { type = "dangerFullAccess" }
                    : (object)new
                    {
                        type = "workspaceWrite",
                        writableRoots = Array.Empty<string>(),
                        networkAccess = false
                    }
            },
            cancellationToken);
        return response.Turn.Id;
    }

    internal static async Task AppendFileInputAsync(
        List<object> input,
        FilePart file,
        string role,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(file.Path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"The attached {role} file is no longer available.", fullPath);
        }

        var displayName = NormalizeAttachmentName(file.DisplayName, fullPath);
        if (file.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true)
        {
            input.Add(new
            {
                type = "text",
                text = $"Harness attached {role} image: {displayName}"
            });
            input.Add(new
            {
                type = "localImage",
                path = fullPath,
                detail = "auto"
            });
            return;
        }

        if (file.MediaType?.StartsWith("video/", StringComparison.OrdinalIgnoreCase) == true)
        {
            throw new NotSupportedException(
                "The connected Codex app-server protocol does not define native video input.");
        }

        input.Add(new
        {
            type = "mention",
            name = displayName,
            path = fullPath
        });

        if (IsTextFile(file, fullPath))
        {
            var snapshot = await ReadTextSnapshotAsync(fullPath, cancellationToken);
            var omitted = snapshot.WasTruncated
                ? $"\n\n[Harness included the first {snapshot.Text.Length:N0} characters. "
                  + $"The complete snapshot remains available at {fullPath}.]"
                : string.Empty;
            input.Add(new
            {
                type = "text",
                text = $"""
                    # Harness attached {role}: {displayName}
                    The following is the file content from the Harness-owned snapshot at {fullPath}.
                    --- BEGIN ATTACHED FILE: {displayName} ---
                    {snapshot.Text}{omitted}
                    --- END ATTACHED FILE: {displayName} ---
                    """
            });
            return;
        }

        input.Add(new
        {
            type = "text",
            text = $"Harness attached the non-text {role} file {displayName} at {fullPath}. "
                 + "Inspect that exact file with the available workspace tools before answering when its contents are relevant."
        });
    }

    private static bool IsTextFile(FilePart file, string fullPath) =>
        file.MediaType?.StartsWith("text/", StringComparison.OrdinalIgnoreCase) == true
        || file.MediaType is "application/json" or "application/xml" or "application/yaml"
        || TextFileExtensions.Contains(Path.GetExtension(fullPath));

    private static string NormalizeAttachmentName(string? requestedName, string fullPath)
    {
        var name = string.IsNullOrWhiteSpace(requestedName)
            ? Path.GetFileName(fullPath)
            : Path.GetFileName(requestedName);
        var normalized = new string(name.Where(character => !char.IsControl(character)).ToArray()).Trim();
        return normalized.Length == 0 ? Path.GetFileName(fullPath) : normalized;
    }

    private static async Task<TextFileSnapshot> ReadTextSnapshotAsync(
        string fullPath,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, Encoding.UTF8, true, 64 * 1024, leaveOpen: false);
        var buffer = new char[MaximumInlineTextCharacters + 1];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(length, buffer.Length - length), cancellationToken);
            if (read == 0) break;
            length += read;
        }

        var wasTruncated = length > MaximumInlineTextCharacters;
        return new TextFileSnapshot(
            new string(buffer, 0, Math.Min(length, MaximumInlineTextCharacters)),
            wasTruncated);
    }

    private sealed record TextFileSnapshot(string Text, bool WasTruncated);

    private static CodexRuntimePolicy ResolveRuntimePolicy(string? permissionMode) =>
        permissionMode?.Trim().ToLowerInvariant() switch
        {
            "auto" => new("on-request", "auto_review", "workspace-write", false),
            "full" => new("never", "user", "danger-full-access", true),
            _ => new("on-request", "user", "workspace-write", false)
        };

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public Task InterruptTurnAsync(
        string threadId,
        CancellationToken cancellationToken = default) =>
        CallAsync<JsonElement>(
            "turn/interrupt",
            new { threadId },
            cancellationToken);

    public Task CompactThreadAsync(
        string threadId,
        CancellationToken cancellationToken = default) =>
        CallAsync<JsonElement>(
            "thread/compact/start",
            new { threadId },
            cancellationToken);

    public async Task<ProviderUsageSnapshot?> GetUsageAsync(
        CancellationToken cancellationToken = default)
    {
        var response = await CallAsync<AccountRateLimitsResponse>(
            "account/rateLimits/read",
            new { },
            cancellationToken);

        var limits = response.RateLimitsByLimitId is not null
            && response.RateLimitsByLimitId.TryGetValue("codex", out var codexLimits)
                ? codexLimits
                : response.RateLimits;

        var windows = new List<UsageWindowSnapshot>(2);
        AddWindow(windows, "primary", limits.Primary);
        AddWindow(windows, "secondary", limits.Secondary);

        return new ProviderUsageSnapshot(
            Id,
            "codex-app-server",
            limits.PlanType,
            windows,
            DateTimeOffset.UtcNow,
            CreditBalance: limits.Credits?.Balance);
    }

    public Task<CodexDeviceCodeLoginStart> StartChatGptDeviceCodeLoginAsync(
        CancellationToken cancellationToken = default) =>
        CallAsync<CodexDeviceCodeLoginStart>(
            "account/login/start",
            new { type = "chatgptDeviceCode" },
            cancellationToken);

    public async Task<CodexAccountInfo> GetAccountAsync(
        bool refreshToken = false,
        CancellationToken cancellationToken = default)
    {
        var response = await CallAsync<GetAccountResponse>(
            "account/read",
            new { refreshToken },
            cancellationToken);
        return new CodexAccountInfo(
            response.Account is not null,
            response.RequiresOpenaiAuth,
            response.Account?.Type,
            response.Account?.Email,
            response.Account?.PlanType);
    }

    public async Task SignOutAsync(CancellationToken cancellationToken = default) =>
        _ = await CallAsync<JsonElement>("account/logout", null!, cancellationToken);

    public async IAsyncEnumerable<ProviderUsageSnapshot> WatchUsageAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            var snapshot = await GetUsageAsync(cancellationToken);
            if (snapshot is not null)
            {
                yield return snapshot;
            }
        }
        while (await timer.WaitForNextTickAsync(cancellationToken));
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        _notifications.Writer.TryComplete();
        _serverRequests.Writer.TryComplete();
        _input.Close();

        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
        }

        if (_readTask is not null)
        {
            try
            {
                await _readTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        try
        {
            await _standardErrorTask;
        }
        catch (OperationCanceledException)
        {
        }

        _process.Dispose();
        _shutdown.Dispose();
        _writeLock.Dispose();
    }

    private async Task<T> CallAsync<T>(
        string method,
        object parameters,
        CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _nextRequestId);
        var completion = new TaskCompletionSource<JsonElement>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;

        try
        {
            await WriteAsync(new { jsonrpc = "2.0", id, method, @params = parameters }, cancellationToken);
            var result = await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return result.Deserialize<T>(JsonOptions)
                ?? throw new InvalidOperationException($"Codex returned no result for {method}.");
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private Task NotifyAsync(string method, object parameters, CancellationToken cancellationToken) =>
        WriteAsync(new { jsonrpc = "2.0", method, @params = parameters }, cancellationToken);

    private async Task WriteAsync(object message, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(message, JsonOptions);
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await _input.WriteLineAsync(json.AsMemory(), cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await _process.StandardOutput.ReadLineAsync(cancellationToken) is { } line)
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.TryGetProperty("method", out var methodElement))
                {
                    var method = methodElement.GetString() ?? "unknown";
                    var parameters = root.TryGetProperty("params", out var paramsElement)
                        ? paramsElement.Clone()
                        : default;

                    if (root.TryGetProperty("id", out var requestId))
                    {
                        _serverRequests.Writer.TryWrite(new CodexServerRequest(
                            requestId.Clone(),
                            method,
                            parameters));
                    }
                    else
                    {
                        _notifications.Writer.TryWrite(new CodexNotification(method, SummarizeBrowserNotification(method, parameters)));
                    }

                    continue;
                }

                if (!root.TryGetProperty("id", out var idElement)
                    || !idElement.TryGetInt64(out var id)
                    || !_pending.TryRemove(id, out var completion))
                {
                    continue;
                }

                if (root.TryGetProperty("error", out var error))
                {
                    completion.TrySetException(new InvalidOperationException(
                        $"Codex RPC failed: {error.GetRawText()}"));
                    continue;
                }

                completion.TrySetResult(root.GetProperty("result").Clone());
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            _notifications.Writer.TryComplete();
            _serverRequests.Writer.TryComplete();
            var failure = new InvalidOperationException("Codex app-server stopped.");
            foreach (var completion in _pending.Values)
            {
                completion.TrySetException(failure);
            }
        }
    }

    public static JsonElement SummarizeBrowserNotification(string method, JsonElement parameters)
    {
        if (method != "item/completed" || !parameters.TryGetProperty("item", out var item)
            || !item.TryGetProperty("tool", out var tool) || tool.GetString() != Harness.Core.Browser.BrowserTools.Name)
            return parameters;
        // Images already went to the model through the tool response. Never serialize them a
        // second time into UI activity or SQLite event logs (which also causes UI stalls).
        string? Value(JsonElement element, string key) => element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        return JsonSerializer.SerializeToElement(new
        {
            threadId = Value(parameters, "threadId"), turnId = Value(parameters, "turnId"),
            item = new
            {
                id = Value(item, "id"), type = "dynamicToolCall", tool = Harness.Core.Browser.BrowserTools.Name,
                status = Value(item, "status"),
                result = item.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.True
                    ? "Browser observation delivered to model." : "Browser action did not succeed. See the browser activity entry."
            }
        });
    }

    private static ProcessStartInfo CreateStartInfo(CodexRuntimeInfo runtime)
    {
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        var start = new ProcessStartInfo
        {
            FileName = runtime.ExecutablePath,
            Arguments = "app-server --stdio",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = utf8,
            StandardOutputEncoding = utf8,
            StandardErrorEncoding = utf8,
            CreateNoWindow = true
        };
        // A Harness process launched from another agent can inherit that agent's
        // private pipes, thread IDs, sandbox flags, and helper paths. Passing those
        // through makes our standalone runtime accidentally depend on its parent.
        foreach (var key in start.Environment.Keys
                     .Where(key => key.StartsWith("CODEX_", StringComparison.OrdinalIgnoreCase)
                                   && !string.Equals(key, "CODEX_HOME", StringComparison.OrdinalIgnoreCase))
                     .ToArray())
        {
            start.Environment.Remove(key);
        }

        if (runtime.HarnessOwned && File.Exists(runtime.ExecutablePath))
        {
            var bin = Path.GetDirectoryName(Path.GetFullPath(runtime.ExecutablePath))!;
            var packageRoot = Directory.GetParent(bin)?.FullName;
            var resources = packageRoot is null ? null : Path.Combine(packageRoot, "codex-resources");
            var additions = resources is not null && Directory.Exists(resources)
                ? $"{bin}{Path.PathSeparator}{resources}"
                : bin;
            start.Environment.TryGetValue("PATH", out var inheritedPath);
            start.Environment["PATH"] = $"{additions}{Path.PathSeparator}{inheritedPath ?? string.Empty}";
            start.Environment["HARNESS_RUNTIME_ROOT"] = packageRoot ?? bin;
        }
        return start;
    }

    private static void AddWindow(
        ICollection<UsageWindowSnapshot> target,
        string id,
        RateLimitWindow? source)
    {
        if (source is null)
        {
            return;
        }

        var duration = source.WindowDurationMins is { } minutes
            ? TimeSpan.FromMinutes(minutes)
            : (TimeSpan?)null;
        var displayName = duration switch
        {
            { TotalMinutes: 300 } => "5 HOUR WINDOW",
            { TotalDays: 7 } => "WEEKLY LIMIT",
            { TotalHours: >= 1 } value => $"{value.TotalHours:0} HOUR WINDOW",
            _ => id.ToUpperInvariant()
        };

        target.Add(new UsageWindowSnapshot(
            id,
            displayName,
            source.UsedPercent,
            duration,
            source.ResetsAt is { } reset ? DateTimeOffset.FromUnixTimeSeconds(reset) : null));
    }

    private static IReadOnlyList<ServiceTierDescriptor> BuildServiceTiers(CodexModel model)
    {
        var advertised = model.ServiceTiers ?? [];
        if (advertised.Count == 0)
        {
            return [];
        }

        var tiers = new List<ServiceTierDescriptor>(advertised.Count + 1);
        if (string.IsNullOrWhiteSpace(model.DefaultServiceTier))
        {
            tiers.Add(new ServiceTierDescriptor(
                null,
                "Standard",
                "Provider default service tier",
                IsDefault: true));
        }

        tiers.AddRange(advertised.Select(tier => new ServiceTierDescriptor(
            tier.Id,
            tier.Name,
            tier.Description,
            string.Equals(
                tier.Id,
                model.DefaultServiceTier,
                StringComparison.OrdinalIgnoreCase))));
        return tiers;
    }

    private static string FormatEffort(string effort) => effort.ToLowerInvariant() switch
    {
        "xhigh" => "XHigh",
        _ => char.ToUpperInvariant(effort[0]) + effort[1..]
    };

    private sealed record ModelListResponse(IReadOnlyList<CodexModel> Data);
    private sealed record CodexModel(
        string Model,
        string DisplayName,
        bool Hidden,
        bool IsDefault,
        IReadOnlyList<string> InputModalities,
        string DefaultReasoningEffort,
        IReadOnlyList<CodexReasoningEffort> SupportedReasoningEfforts,
        string? DefaultServiceTier,
        IReadOnlyList<CodexServiceTier>? ServiceTiers);
    private sealed record CodexReasoningEffort(string ReasoningEffort, string Description);
    private sealed record CodexServiceTier(string Id, string Name, string Description);
    private sealed record AccountRateLimitsResponse(
        RateLimitSnapshot RateLimits,
        IReadOnlyDictionary<string, RateLimitSnapshot>? RateLimitsByLimitId);
    private sealed record RateLimitSnapshot(
        string? PlanType,
        RateLimitWindow? Primary,
        RateLimitWindow? Secondary,
        CreditsSnapshot? Credits);
    private sealed record RateLimitWindow(
        int UsedPercent,
        long? WindowDurationMins,
        long? ResetsAt);
    private sealed record CreditsSnapshot(string? Balance, bool HasCredits, bool Unlimited);
    private sealed record GetAccountResponse(CodexAccount? Account, bool RequiresOpenaiAuth);
    private sealed record CodexAccount(string Type, string? Email, string? PlanType);
    private sealed record ThreadStartResponse(ThreadReference Thread);
    private sealed record ThreadResumeResponse(ThreadReference Thread);
    private sealed record TurnStartResponse(TurnReference Turn);
    private sealed record ThreadReference(string Id);
    private sealed record TurnReference(string Id);
    private sealed record CodexRuntimePolicy(
        string ApprovalPolicy,
        string ApprovalsReviewer,
        string Sandbox,
        bool IsFullAccess);
}

public sealed record CodexNotification(string Method, JsonElement Parameters);
public sealed record CodexServerRequest(
    JsonElement Id,
    string Method,
    JsonElement Parameters);
public sealed record CodexDeviceCodeLoginStart(
    string Type,
    string VerificationUrl,
    string UserCode,
    string LoginId);
public sealed record CodexAccountInfo(
    bool IsAuthenticated,
    bool RequiresOpenAiAuth,
    string? AccountType,
    string? Email,
    string? PlanType);

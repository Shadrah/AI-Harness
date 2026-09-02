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

    public static async Task<CodexAppServerClient> StartAsync(
        CancellationToken cancellationToken = default)
    {
        var runtime = CodexRuntimeResolver.Resolve();
        var process = new Process { StartInfo = CreateStartInfo(runtime) };
        if (!process.Start())
        {
            throw new InvalidOperationException("Codex app-server did not start.");
        }

        var client = new CodexAppServerClient(process, runtime);
        client._readTask = client.ReadLoopAsync(client._shutdown.Token);

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
                ephemeral = false
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
            if (attachment.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true)
            {
                input.Add(new
                {
                    type = "localImage",
                    path = Path.GetFullPath(attachment.Path),
                    detail = "auto"
                });
            }
            else if (attachment.MediaType?.StartsWith("video/", StringComparison.OrdinalIgnoreCase) == true)
            {
                throw new NotSupportedException(
                    "The connected Codex app-server protocol does not define native video input.");
            }
            else
            {
                input.Add(new
                {
                    type = "mention",
                    name = Path.GetFileName(attachment.Path),
                    path = Path.GetFullPath(attachment.Path)
                });
            }
        }
        foreach (var contextFile in contextFiles ?? [])
        {
            input.Add(new
            {
                type = "mention",
                name = Path.GetFileName(contextFile.Path),
                path = Path.GetFullPath(contextFile.Path)
            });
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
            var result = await completion.Task.WaitAsync(cancellationToken);
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
                        _notifications.Writer.TryWrite(new CodexNotification(method, parameters));
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

using System.Text.Json;
using Harness.Core.Models;

namespace Harness.Providers.Claude;

public sealed record ClaudeCodeAccount(
    bool IsAuthenticated,
    string? Email,
    string? Organization,
    string? SubscriptionType,
    string? TokenSource,
    string? ApiProvider);

public sealed record ClaudeCodeConnection(
    ClaudeCodeRuntimeInfo Runtime,
    string? Version,
    ClaudeCodeAccount Account,
    IReadOnlyList<ModelDescriptor> Models,
    ProviderUsageSnapshot? Usage,
    string? FastModeState,
    string? Diagnostic);

public enum ClaudeCodeEventKind
{
    Initialized,
    TextDelta,
    ReasoningDelta,
    ToolStarted,
    ToolCompleted,
    Progress,
    Usage,
    RateLimit,
    Completed,
    Error
}

public sealed record ClaudeCodeEvent(
    ClaudeCodeEventKind Kind,
    string Id,
    string Title,
    string Text,
    JsonElement Native,
    long? InputTokens = null,
    long? OutputTokens = null,
    long? CumulativeTokens = null,
    string? SessionId = null);

public sealed record ClaudeCodeTurnRequest(
    string WorkingDirectory,
    string SessionId,
    bool Resume,
    string Model,
    string? Effort,
    bool FastMode,
    string PermissionMode,
    string Prompt,
    string? AdditionalInstructions,
    IReadOnlyList<FilePart> TurnAttachments,
    IReadOnlyList<FilePart> ContextFiles);

public sealed record ClaudeCodeTurnResult(
    string SessionId,
    bool Succeeded,
    string? Error,
    long? InputTokens,
    long? OutputTokens,
    long? TotalTokens);

public sealed record ClaudeCodePermissionRequest(
    string RequestId,
    string ToolName,
    JsonElement Input,
    string? Title,
    string? Description,
    string? ToolUseId);

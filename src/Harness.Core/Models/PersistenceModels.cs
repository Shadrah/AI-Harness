namespace Harness.Core.Models;

public sealed record StoredProject(
    string Id,
    string Name,
    string RootPath,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastOpenedAt);

public sealed record StoredSession(
    string Id,
    string ProjectId,
    string Title,
    string? ProviderId,
    string? ProviderThreadId,
    string? ModelId,
    string? ReasoningEffort,
    string? ServiceTier,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record StoredMessage(
    string Id,
    string SessionId,
    long Sequence,
    string Role,
    string Title,
    string Text,
    string Status,
    string Color,
    bool Monospace,
    DateTimeOffset CreatedAt,
    string? ProviderEventJson = null);

public sealed record StoredAttachment(
    string Id,
    string SessionId,
    string DisplayName,
    string OriginalPath,
    string StoredPath,
    string? MediaType,
    string Sha256,
    long ByteLength,
    DateTimeOffset CreatedAt);

public sealed record WorkspaceSessionSnapshot(
    StoredProject Project,
    IReadOnlyList<StoredSession> Sessions,
    StoredSession ActiveSession,
    IReadOnlyList<StoredMessage> Messages,
    IReadOnlyList<StoredAttachment> Attachments);

public sealed record HarnessApplicationSettings(
    bool RestoreLastWorkspace = true,
    bool ShowActivityTrace = true,
    bool ShowUsageInspector = true,
    bool ShowContextInspector = true,
    bool ShowTurnDiffInspector = true,
    string PersonalInstructions = "",
    string? LastWorkspacePath = null,
    string GitAuthorName = "",
    string GitAuthorEmail = "");

public sealed record ImportMessage(
    string Role,
    string Text,
    DateTimeOffset? CreatedAt = null);

public sealed record ConversationImportPlan(
    string SourceKind,
    string SourcePath,
    string SuggestedTitle,
    IReadOnlyList<ImportMessage> Messages,
    IReadOnlyList<string> Warnings,
    string? SourceConversationId = null,
    string? SourceWorkspacePath = null,
    DateTimeOffset? SourceUpdatedAt = null);

public sealed record HarnessImportCandidate(
    string SourceHarness,
    string SourcePath,
    string ConversationId,
    string Title,
    string? WorkspacePath,
    DateTimeOffset UpdatedAt,
    int MessageCount,
    IReadOnlyList<string> Warnings,
    ConversationImportPlan Plan,
    bool IsPrimaryContinuation = false)
{
    public string DisplayLabel =>
        $"{(IsPrimaryContinuation ? "LATEST CONTINUATION  ·  " : string.Empty)}{Title}  |  {MessageCount} messages  |  {UpdatedAt.LocalDateTime:g}";
    public override string ToString() => DisplayLabel;
}

public sealed record HarnessImportProject(
    string SourceHarness,
    string? WorkspacePath,
    string ProjectName,
    IReadOnlyList<HarnessImportCandidate> Conversations,
    IReadOnlyList<string> ContextFiles,
    DateTimeOffset UpdatedAt)
{
    public int MessageCount => Conversations.Sum(conversation => conversation.MessageCount);
    public HarnessImportCandidate PrimaryConversation =>
        Conversations.First(conversation => conversation.IsPrimaryContinuation);
    public string DisplayLabel =>
        $"{SourceHarness}  ·  {ProjectName}  ·  {Conversations.Count} chat{(Conversations.Count == 1 ? string.Empty : "s")}";
    public override string ToString() => DisplayLabel;
}

public sealed record HarnessImportInventory(
    IReadOnlyList<HarnessImportProject> Projects,
    IReadOnlyList<string> Diagnostics)
{
    public IReadOnlyList<HarnessImportCandidate> Conversations =>
        Projects.SelectMany(project => project.Conversations).ToArray();
}

public sealed record ConversationImportResult(
    StoredSession Session,
    int MessageCount,
    string StoredSourcePath);

public sealed record StoredImportSource(
    string Id,
    string SessionId,
    string SourceKind,
    string OriginalPath,
    string StoredPath,
    string Sha256,
    DateTimeOffset ImportedAt);

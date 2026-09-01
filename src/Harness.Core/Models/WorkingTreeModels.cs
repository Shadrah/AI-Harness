namespace Harness.Core.Models;

public sealed record WorkingTreeFile(
    string RelativePath,
    char IndexStatus,
    char WorkTreeStatus,
    bool IsUntracked)
{
    public bool IsStaged => !IsUntracked && IndexStatus != ' ';
    public bool HasWorkTreeChanges => IsUntracked || WorkTreeStatus != ' ';
    public string StatusCode => IsUntracked ? "??" : $"{IndexStatus}{WorkTreeStatus}";
}

public sealed record WorkingTreeSnapshot(
    bool IsRepository,
    string? RepositoryRoot,
    string? Branch,
    IReadOnlyList<WorkingTreeFile> Files,
    string? Error = null);

public sealed record WorkspaceRecoveryResult(
    string RelativePath,
    string RecoveryPath,
    bool OriginalWasMoved);

public enum DiffLineKind
{
    Context,
    Added,
    Removed,
    Hunk,
    Metadata
}

public sealed record DiffLine(
    DiffLineKind Kind,
    int? OldLineNumber,
    int? NewLineNumber,
    string Marker,
    string Text);

public sealed record DiffDocument(
    IReadOnlyList<DiffLine> Lines,
    int AddedLines,
    int RemovedLines);

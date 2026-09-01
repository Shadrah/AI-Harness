namespace Harness.Core.Models;

public enum ActivityKind
{
    Model,
    Tool,
    Approval,
    FileChange,
    Context
}

public sealed record ActivityEvent(
    ActivityKind Kind,
    string Title,
    string Detail,
    DateTimeOffset Timestamp,
    bool IsActive = false);

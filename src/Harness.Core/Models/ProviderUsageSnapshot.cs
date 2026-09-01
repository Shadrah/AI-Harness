namespace Harness.Core.Models;

public sealed record ProviderUsageSnapshot(
    string ProviderId,
    string ConnectionId,
    string? PlanName,
    IReadOnlyList<UsageWindowSnapshot> Windows,
    DateTimeOffset CapturedAt,
    long? LifetimeTokens = null,
    string? CreditBalance = null);

public sealed record UsageWindowSnapshot(
    string Id,
    string DisplayName,
    double UsedPercent,
    TimeSpan? Duration = null,
    DateTimeOffset? ResetsAt = null)
{
    public double RemainingPercent => Math.Clamp(100 - UsedPercent, 0, 100);
}

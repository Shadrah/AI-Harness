namespace Harness.App.Services;

public static class SubscriptionHandoffSelector
{
    public static SubscriptionIdentity? Select(
        string providerId,
        string originIdentityId,
        string selectedModelId,
        double thresholdPercent,
        IEnumerable<SubscriptionIdentity> identities) =>
        identities
            .Where(identity => identity.ProviderId == providerId
                && identity.Id != originIdentityId
                && identity.AutomaticHandoffEnabled
                && string.Equals(identity.ConnectionState, "CONNECTED", StringComparison.Ordinal)
                && !identity.BillingMode.Contains("API", StringComparison.OrdinalIgnoreCase)
                && identity.LastFiveHourRemainingPercent > thresholdPercent
                && (identity.LastWeeklyRemainingPercent is null
                    || identity.LastWeeklyRemainingPercent > thresholdPercent)
                && identity.LastModelIds?.Contains(selectedModelId, StringComparer.Ordinal) == true)
            .OrderByDescending(identity => Math.Min(
                identity.LastFiveHourRemainingPercent ?? -1,
                identity.LastWeeklyRemainingPercent ?? 100))
            .ThenBy(identity => identity.FiveHourResetsAt)
            .FirstOrDefault();
}

using System.Text.Json;

namespace Harness.App.Services;

/// <summary>
/// Stores credential-free identity metadata. Provider credentials remain inside each
/// provider-owned profile directory and are intentionally excluded from Harness backups.
/// </summary>
public sealed class SubscriptionIdentityStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _metadataPath;
    private readonly string _managedProfilesRoot;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SubscriptionIdentityStore(string? applicationRoot = null)
    {
        var root = Path.GetFullPath(applicationRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Harness"));
        _metadataPath = Path.Combine(root, "subscription-identities.json");
        _managedProfilesRoot = Path.Combine(root, "provider-profiles");
    }

    public async Task<IReadOnlyList<SubscriptionIdentity>> LoadAsync(
        CancellationToken cancellationToken = default) =>
        await LoadAsync(SubscriptionProviderIds.OpenAiCodex, cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<SubscriptionIdentity>> LoadAsync(
        string providerId,
        CancellationToken cancellationToken = default)
    {
        SubscriptionProviderIds.Validate(providerId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var identities = await ReadCoreAsync(cancellationToken).ConfigureAwait(false);
            var providerIdentities = identities
                .Where(identity => identity.ProviderId == providerId)
                .ToArray();
            if (providerIdentities.Length > 0) return providerIdentities;
            var primary = CreatePrimaryIdentity(providerId);
            identities.Add(primary);
            await WriteCoreAsync(identities, cancellationToken).ConfigureAwait(false);
            return [primary];
        }
        finally { _gate.Release(); }
    }

    public async Task<SubscriptionIdentity> AddAsync(
        string? displayName,
        CancellationToken cancellationToken = default) =>
        await AddAsync(SubscriptionProviderIds.OpenAiCodex, displayName, cancellationToken).ConfigureAwait(false);

    public async Task<SubscriptionIdentity> AddAsync(
        string providerId,
        string? displayName,
        CancellationToken cancellationToken = default)
    {
        SubscriptionProviderIds.Validate(providerId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var identities = await ReadCoreAsync(cancellationToken).ConfigureAwait(false);
            var providerCount = identities.Count(identity => identity.ProviderId == providerId);
            if (providerCount == 0)
            {
                identities.Add(CreatePrimaryIdentity(providerId));
                providerCount = 1;
            }
            var id = (providerId == SubscriptionProviderIds.OpenAiCodex ? "openai-" : "claude-")
                     + Guid.NewGuid().ToString("N");
            var providerName = providerId == SubscriptionProviderIds.OpenAiCodex ? "OpenAI" : "Claude";
            var name = string.IsNullOrWhiteSpace(displayName)
                ? $"{providerName} {providerCount + 1}"
                : displayName.Trim();
            if (name.Length > 80) name = name[..80];
            var identity = new SubscriptionIdentity(
                id,
                name,
                Path.Combine(_managedProfilesRoot, providerId, id),
                providerId,
                null,
                null,
                false,
                DateTimeOffset.UtcNow,
                null);
            identities.Add(identity);
            await WriteCoreAsync(identities, cancellationToken).ConfigureAwait(false);
            return identity;
        }
        finally { _gate.Release(); }
    }

    public async Task UpdateAsync(
        SubscriptionIdentity identity,
        CancellationToken cancellationToken = default)
    {
        Validate(identity);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var identities = await ReadCoreAsync(cancellationToken).ConfigureAwait(false);
            var index = identities.FindIndex(item => string.Equals(item.Id, identity.Id, StringComparison.Ordinal));
            if (index < 0) throw new InvalidOperationException("The subscription identity no longer exists.");
            identities[index] = identity;
            await WriteCoreAsync(identities, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task RemoveAsync(string identityId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var identities = await ReadCoreAsync(cancellationToken).ConfigureAwait(false);
            var identity = identities.FirstOrDefault(item => string.Equals(item.Id, identityId, StringComparison.Ordinal))
                ?? throw new InvalidOperationException("The subscription identity no longer exists.");
            if (identities.Count(item => item.ProviderId == identity.ProviderId) <= 1)
                throw new InvalidOperationException($"Keep at least one {SubscriptionProviderIds.DisplayName(identity.ProviderId)} subscription profile.");
            var removed = identities.RemoveAll(item => string.Equals(item.Id, identityId, StringComparison.Ordinal));
            if (removed == 0) throw new InvalidOperationException("The subscription identity no longer exists.");
            await WriteCoreAsync(identities, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private async Task<List<SubscriptionIdentity>> ReadCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_metadataPath)) return [];
        await using var stream = new FileStream(
            _metadataPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var identities = await JsonSerializer.DeserializeAsync<List<SubscriptionIdentity>>(
            stream, JsonOptions, cancellationToken).ConfigureAwait(false) ?? [];
        foreach (var identity in identities) Validate(identity);
        return identities
            .GroupBy(identity => identity.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(identity => identity.CreatedAt)
            .ToList();
    }

    private async Task WriteCoreAsync(
        IReadOnlyList<SubscriptionIdentity> identities,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_metadataPath)!);
        var temporary = _metadataPath + ".tmp";
        await using (var stream = new FileStream(
            temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(stream, identities, JsonOptions, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        File.Move(temporary, _metadataPath, overwrite: true);
    }

    private SubscriptionIdentity CreatePrimaryIdentity(string providerId)
    {
        var isCodex = providerId == SubscriptionProviderIds.OpenAiCodex;
        var inherited = isCodex ? Environment.GetEnvironmentVariable("CODEX_HOME") : null;
        var profileRoot = !string.IsNullOrWhiteSpace(inherited)
            ? Path.GetFullPath(inherited)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), isCodex ? ".codex" : ".claude");
        return new SubscriptionIdentity(
            isCodex ? "openai-primary" : "claude-primary",
            isCodex ? "OpenAI 1" : "Claude 1",
            profileRoot,
            providerId,
            null,
            null,
            true,
            DateTimeOffset.UtcNow,
            null);
    }

    private void Validate(SubscriptionIdentity identity)
    {
        if (string.IsNullOrWhiteSpace(identity.Id)
            || identity.Id.Length > 80
            || identity.Id.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
            throw new InvalidDataException("A subscription identity has an invalid identifier.");
        SubscriptionProviderIds.Validate(identity.ProviderId);
        if (string.IsNullOrWhiteSpace(identity.DisplayName) || identity.DisplayName.Length > 80)
            throw new InvalidDataException("A subscription identity has an invalid display name.");

        var profile = Path.GetFullPath(identity.ProfileRoot);
        if (identity.IsPrimary) return;
        var managedRoot = Path.GetFullPath(Path.Combine(_managedProfilesRoot, identity.ProviderId))
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!profile.StartsWith(managedRoot, comparison))
            throw new InvalidDataException("A managed subscription profile escapes Harness storage.");
    }
}

public sealed record SubscriptionIdentity(
    string Id,
    string DisplayName,
    string ProfileRoot,
    string ProviderId,
    string? Email,
    string? Plan,
    bool IsPrimary,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastConnectedAt,
    double? LastFiveHourRemainingPercent = null,
    DateTimeOffset? LastUsageAt = null,
    double? LastWeeklyRemainingPercent = null,
    DateTimeOffset? FiveHourResetsAt = null,
    DateTimeOffset? WeeklyResetsAt = null,
    IReadOnlyList<string>? LastModelIds = null,
    bool AutomaticHandoffEnabled = true,
    string ConnectionState = "UNKNOWN",
    string BillingMode = "NOT REPORTED")
{
    public string AccountLabel => !string.IsNullOrWhiteSpace(Email)
        ? $"{Email} · {(string.IsNullOrWhiteSpace(Plan) ? "PLAN NOT REPORTED" : Plan.ToUpperInvariant())}"
        : string.Equals(ConnectionState, "CONNECTED", StringComparison.Ordinal)
            ? $"Connected account · {(string.IsNullOrWhiteSpace(Plan) ? "PLAN NOT REPORTED" : Plan.ToUpperInvariant())}"
            : "Not signed in";

    public string UsageLabel => LastFiveHourRemainingPercent is { } remaining
        ? $"5-hour window · {remaining:0.#}% left · checked {LastUsageAt?.ToLocalTime():g}"
        : "Usage not checked yet";

    public double FiveHourRemainingValue => LastFiveHourRemainingPercent ?? 0;
    public double WeeklyRemainingValue => LastWeeklyRemainingPercent ?? 0;
    public string FiveHourUsageLabel => LastFiveHourRemainingPercent is { } remaining
        ? $"{remaining:0.#}% left" + (FiveHourResetsAt is { } reset ? $" · resets {reset.ToLocalTime():g}" : string.Empty)
        : "Not reported";
    public string WeeklyUsageLabel => LastWeeklyRemainingPercent is { } remaining
        ? $"{remaining:0.#}% left" + (WeeklyResetsAt is { } reset ? $" · resets {reset.ToLocalTime():g}" : string.Empty)
        : "Not reported";
    public string ProviderDisplayName => SubscriptionProviderIds.DisplayName(ProviderId);
    public string ConnectionSummary => $"{ConnectionState} · {BillingMode}";
    public string SchedulerLabel => AutomaticHandoffEnabled ? "AUTO ELIGIBLE" : "MANUAL ONLY";
    public string FreshnessLabel => LastUsageAt is { } checkedAt
        ? $"Updated {checkedAt.ToLocalTime():g}"
        : "Usage not checked";
}

public sealed record SubscriptionIdentityCatalogSnapshot(
    IReadOnlyList<SubscriptionIdentity> Identities,
    string ActiveIdentityId,
    string ProviderId = SubscriptionProviderIds.OpenAiCodex);

public sealed record SubscriptionIdentityActions(
    string ProviderId,
    Func<CancellationToken, Task<SubscriptionIdentityCatalogSnapshot>> Read,
    Func<string?, CancellationToken, Task<SubscriptionIdentity>> Add,
    Func<string, CancellationToken, Task> Activate,
    Func<string, CancellationToken, Task> Remove,
    Func<string, bool, CancellationToken, Task>? SetAutomaticHandoff = null);

public static class SubscriptionProviderIds
{
    public const string OpenAiCodex = "openai-codex";
    public const string AnthropicClaude = "anthropic-claude";

    public static string DisplayName(string providerId) => providerId switch
    {
        OpenAiCodex => "OpenAI",
        AnthropicClaude => "Claude",
        _ => providerId
    };

    public static void Validate(string providerId)
    {
        if (providerId is not (OpenAiCodex or AnthropicClaude))
            throw new InvalidDataException("A subscription identity uses an unsupported provider.");
    }
}

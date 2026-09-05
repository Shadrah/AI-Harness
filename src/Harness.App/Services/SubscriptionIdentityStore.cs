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
        _managedProfilesRoot = Path.Combine(root, "provider-profiles", "openai-codex");
    }

    public async Task<IReadOnlyList<SubscriptionIdentity>> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var identities = await ReadCoreAsync(cancellationToken).ConfigureAwait(false);
            if (identities.Count > 0) return identities;
            var primary = CreatePrimaryIdentity();
            await WriteCoreAsync([primary], cancellationToken).ConfigureAwait(false);
            return [primary];
        }
        finally { _gate.Release(); }
    }

    public async Task<SubscriptionIdentity> AddAsync(
        string? displayName,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var identities = await ReadCoreAsync(cancellationToken).ConfigureAwait(false);
            if (identities.Count == 0) identities.Add(CreatePrimaryIdentity());
            var id = "openai-" + Guid.NewGuid().ToString("N");
            var name = string.IsNullOrWhiteSpace(displayName)
                ? $"OpenAI {identities.Count + 1}"
                : displayName.Trim();
            if (name.Length > 80) name = name[..80];
            var identity = new SubscriptionIdentity(
                id,
                name,
                Path.Combine(_managedProfilesRoot, id),
                "openai-codex",
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
            if (identities.Count <= 1)
                throw new InvalidOperationException("Keep at least one OpenAI subscription profile.");
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

    private SubscriptionIdentity CreatePrimaryIdentity()
    {
        var inherited = Environment.GetEnvironmentVariable("CODEX_HOME");
        var profileRoot = string.IsNullOrWhiteSpace(inherited)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex")
            : Path.GetFullPath(inherited);
        return new SubscriptionIdentity(
            "openai-primary",
            "OpenAI 1",
            profileRoot,
            "openai-codex",
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
        if (!string.Equals(identity.ProviderId, "openai-codex", StringComparison.Ordinal))
            throw new InvalidDataException("A subscription identity uses an unsupported provider.");
        if (string.IsNullOrWhiteSpace(identity.DisplayName) || identity.DisplayName.Length > 80)
            throw new InvalidDataException("A subscription identity has an invalid display name.");

        var profile = Path.GetFullPath(identity.ProfileRoot);
        if (identity.IsPrimary) return;
        var managedRoot = Path.GetFullPath(_managedProfilesRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!profile.StartsWith(managedRoot, OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal))
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
    DateTimeOffset? LastUsageAt = null)
{
    public string AccountLabel => string.IsNullOrWhiteSpace(Email)
        ? "Not signed in"
        : $"{Email} · {(string.IsNullOrWhiteSpace(Plan) ? "PLAN NOT REPORTED" : Plan.ToUpperInvariant())}";

    public string UsageLabel => LastFiveHourRemainingPercent is { } remaining
        ? $"5-hour window · {remaining:0.#}% left · checked {LastUsageAt?.ToLocalTime():g}"
        : "Usage not checked yet";
}

public sealed record SubscriptionIdentityCatalogSnapshot(
    IReadOnlyList<SubscriptionIdentity> Identities,
    string ActiveIdentityId);

public sealed record SubscriptionIdentityActions(
    Func<CancellationToken, Task<SubscriptionIdentityCatalogSnapshot>> Read,
    Func<string?, CancellationToken, Task<SubscriptionIdentity>> Add,
    Func<string, CancellationToken, Task> Activate,
    Func<string, CancellationToken, Task> Remove);

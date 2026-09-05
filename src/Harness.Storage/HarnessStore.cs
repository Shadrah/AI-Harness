using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Harness.Core.Models;
using Microsoft.Data.Sqlite;

namespace Harness.Storage;

public sealed class HarnessStore : IAsyncDisposable
{
    private const int SchemaVersion = 5;
    public const int CurrentSchemaVersion = SchemaVersion;
    private readonly string _connectionString;
    // SQLite async calls still execute synchronously. Gate awaits force-yield
    // without capturing the caller's context so all database work stays off the UI.
    private readonly SemaphoreSlim _gate = new(1, 1);

    public HarnessStore(string databasePath)
    {
        DatabasePath = Path.GetFullPath(databasePath);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true,
            Pooling = false
        }.ToString();
    }

    public string DatabasePath { get; }

    public static string DefaultDatabasePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Harness",
        "data",
        "harness.db");

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA journal_mode = WAL;
                PRAGMA synchronous = NORMAL;
                PRAGMA foreign_keys = ON;

                CREATE TABLE IF NOT EXISTS schema_info (
                    version INTEGER NOT NULL
                );

                INSERT INTO schema_info(version)
                SELECT 1
                WHERE NOT EXISTS (SELECT 1 FROM schema_info);

                CREATE TABLE IF NOT EXISTS projects (
                    id TEXT PRIMARY KEY,
                    name TEXT NOT NULL,
                    root_path TEXT NOT NULL,
                    normalized_root_path TEXT NOT NULL UNIQUE,
                    created_at TEXT NOT NULL,
                    last_opened_at TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS sessions (
                    id TEXT PRIMARY KEY,
                    project_id TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
                    title TEXT NOT NULL,
                    provider_id TEXT NULL,
                    provider_thread_id TEXT NULL,
                    model_id TEXT NULL,
                    reasoning_effort TEXT NULL,
                    service_tier TEXT NULL,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                );

                CREATE INDEX IF NOT EXISTS ix_sessions_project_updated
                    ON sessions(project_id, updated_at DESC);

                CREATE TABLE IF NOT EXISTS messages (
                    id TEXT PRIMARY KEY,
                    session_id TEXT NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
                    sequence INTEGER NOT NULL,
                    role TEXT NOT NULL,
                    title TEXT NOT NULL,
                    text TEXT NOT NULL,
                    status TEXT NOT NULL,
                    color TEXT NOT NULL,
                    monospace INTEGER NOT NULL,
                    created_at TEXT NOT NULL,
                    provider_event_json TEXT NULL,
                    UNIQUE(session_id, sequence)
                );

                CREATE TABLE IF NOT EXISTS attachments (
                    id TEXT PRIMARY KEY,
                    session_id TEXT NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
                    message_id TEXT NULL REFERENCES messages(id) ON DELETE SET NULL,
                    original_path TEXT NOT NULL,
                    stored_path TEXT NOT NULL,
                    media_type TEXT NULL,
                    sha256 TEXT NOT NULL,
                    byte_length INTEGER NOT NULL,
                    created_at TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS provider_events (
                    id TEXT PRIMARY KEY,
                    session_id TEXT NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
                    method TEXT NOT NULL,
                    payload_json TEXT NOT NULL,
                    created_at TEXT NOT NULL
                );

                CREATE INDEX IF NOT EXISTS ix_provider_events_session_method_created
                    ON provider_events(session_id, method, created_at DESC);

                CREATE TABLE IF NOT EXISTS activity_events (
                    id TEXT PRIMARY KEY,
                    project_id TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
                    session_id TEXT NULL REFERENCES sessions(id) ON DELETE SET NULL,
                    kind TEXT NOT NULL,
                    title TEXT NOT NULL,
                    detail TEXT NOT NULL,
                    outcome TEXT NOT NULL,
                    color TEXT NOT NULL,
                    is_milestone INTEGER NOT NULL,
                    created_at TEXT NOT NULL
                );

                CREATE INDEX IF NOT EXISTS ix_activity_events_project_created
                    ON activity_events(project_id, created_at DESC);

                CREATE TABLE IF NOT EXISTS app_settings (
                    key TEXT PRIMARY KEY,
                    value_json TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS import_sources (
                    id TEXT PRIMARY KEY,
                    session_id TEXT NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
                    source_kind TEXT NOT NULL,
                    original_path TEXT NOT NULL,
                    stored_path TEXT NOT NULL,
                    sha256 TEXT NOT NULL,
                    imported_at TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS skill_catalog (
                    id TEXT PRIMARY KEY,
                    name TEXT NOT NULL,
                    description TEXT NOT NULL,
                    category TEXT NOT NULL,
                    repository TEXT NOT NULL,
                    skill_path TEXT NOT NULL,
                    source_revision TEXT NOT NULL,
                    source_url TEXT NOT NULL,
                    compatibility TEXT NOT NULL,
                    trust_state TEXT NOT NULL,
                    discovered_at TEXT NOT NULL,
                    refreshed_at TEXT NOT NULL,
                    raw_metadata_json TEXT NOT NULL
                );

                CREATE INDEX IF NOT EXISTS ix_skill_catalog_name
                    ON skill_catalog(name COLLATE NOCASE);
                CREATE INDEX IF NOT EXISTS ix_skill_catalog_category
                    ON skill_catalog(category COLLATE NOCASE);

                CREATE TABLE IF NOT EXISTS skill_sources (
                    repository TEXT PRIMARY KEY,
                    owner TEXT NOT NULL,
                    source_url TEXT NOT NULL,
                    reported_skill_count INTEGER NOT NULL,
                    indexed_skill_count INTEGER NOT NULL,
                    source_revision TEXT NOT NULL,
                    index_state TEXT NOT NULL,
                    refreshed_at TEXT NOT NULL,
                    diagnostic TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS installed_skills (
                    id TEXT PRIMARY KEY,
                    catalog_id TEXT NOT NULL REFERENCES skill_catalog(id) ON DELETE RESTRICT,
                    name TEXT NOT NULL,
                    source_revision TEXT NOT NULL,
                    package_path TEXT NOT NULL,
                    install_path TEXT NOT NULL,
                    scope TEXT NOT NULL,
                    workspace_path TEXT NULL,
                    provider_id TEXT NOT NULL,
                    model_id TEXT NULL,
                    content_sha256 TEXT NOT NULL,
                    enabled INTEGER NOT NULL,
                    installed_at TEXT NOT NULL
                );

                CREATE INDEX IF NOT EXISTS ix_installed_skills_catalog
                    ON installed_skills(catalog_id, provider_id, scope);

                UPDATE schema_info SET version = 5 WHERE version < 5;
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
            await ClassifyLegacyInternalCodexImportsAsync(connection, cancellationToken);

            await using var versionCommand = connection.CreateCommand();
            versionCommand.CommandText = "SELECT MAX(version) FROM schema_info;";
            var version = Convert.ToInt32(
                await versionCommand.ExecuteScalarAsync(cancellationToken),
                CultureInfo.InvariantCulture);
            if (version != SchemaVersion)
            {
                throw new InvalidOperationException(
                    $"Unsupported Harness database schema {version}; expected {SchemaVersion}.");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<HarnessApplicationSettings> LoadApplicationSettingsAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT value_json FROM app_settings WHERE key = 'application';";
            var json = await command.ExecuteScalarAsync(cancellationToken) as string;
            if (string.IsNullOrWhiteSpace(json)) return new HarnessApplicationSettings();
            var settings = JsonSerializer.Deserialize<HarnessApplicationSettings>(json)
                ?? new HarnessApplicationSettings();
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty(nameof(HarnessApplicationSettings.PromptForSubscriptionHandoff), out _))
                settings = settings with { PromptForSubscriptionHandoff = true };
            if (!root.TryGetProperty(nameof(HarnessApplicationSettings.SubscriptionHandoffThresholdPercent), out _)
                || settings.SubscriptionHandoffThresholdPercent is < 1 or > 25)
                settings = settings with { SubscriptionHandoffThresholdPercent = 5 };
            return settings;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<StoredProject>> ListProjectsAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id, name, root_path, created_at, last_opened_at
                FROM projects ORDER BY created_at ASC, name COLLATE NOCASE ASC;
                """;
            var projects = new List<StoredProject>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) projects.Add(ReadProject(reader));
            return projects;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RelocateProjectAsync(
        string projectId,
        string rootPath,
        CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(rootPath);
        if (!Directory.Exists(fullPath)) throw new DirectoryNotFoundException("Choose an existing project folder.");
        var normalized = NormalizePath(fullPath);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE projects
                SET name = $name, root_path = $rootPath, normalized_root_path = $normalized, last_opened_at = $openedAt
                WHERE id = $id
                  AND NOT EXISTS(SELECT 1 FROM projects WHERE normalized_root_path = $normalized AND id <> $id);
                """;
            command.Parameters.AddWithValue("$name", new DirectoryInfo(fullPath).Name);
            command.Parameters.AddWithValue("$rootPath", fullPath);
            command.Parameters.AddWithValue("$normalized", normalized);
            command.Parameters.AddWithValue("$openedAt", FormatTimestamp(DateTimeOffset.UtcNow));
            command.Parameters.AddWithValue("$id", projectId);
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new InvalidOperationException("That folder already belongs to another Harness workspace, or the restored workspace no longer exists.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveApplicationSettingsAsync(
        HarnessApplicationSettings settings,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO app_settings(key, value_json, updated_at)
                VALUES('application', $json, $updatedAt)
                ON CONFLICT(key) DO UPDATE SET
                    value_json = excluded.value_json,
                    updated_at = excluded.updated_at;
                """;
            command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(settings));
            command.Parameters.AddWithValue("$updatedAt", FormatTimestamp(DateTimeOffset.UtcNow));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task CreatePortableSnapshotAsync(
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        var destination = Path.GetFullPath(destinationPath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            if (File.Exists(destination)) File.Delete(destination);
            await using var source = await OpenConnectionAsync(cancellationToken);
            await using var snapshot = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = destination,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false,
                ForeignKeys = true
            }.ToString());
            await snapshot.OpenAsync(cancellationToken);
            source.BackupDatabase(snapshot);
            await MakeSnapshotPortableAsync(snapshot, Path.GetDirectoryName(DatabasePath)!, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public static Task RebasePortableSnapshotAsync(
        string databasePath,
        string restoredDataDirectory,
        string? portableContentDirectory = null,
        CancellationToken cancellationToken = default) => Task.Run(async () =>
        {
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path.GetFullPath(databasePath),
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false,
                ForeignKeys = true
            }.ToString());
            await connection.OpenAsync(cancellationToken);
            await RewritePortablePathsAsync(
                connection,
                restoredDataDirectory,
                makePortable: false,
                cancellationToken,
                portableContentDirectory);
            await using var integrity = connection.CreateCommand();
            integrity.CommandText = "PRAGMA quick_check;";
            if (!string.Equals(await integrity.ExecuteScalarAsync(cancellationToken) as string, "ok", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The restored Harness database failed its integrity check.");
            await using var schema = connection.CreateCommand();
            schema.CommandText = "SELECT MAX(version) FROM schema_info;";
            var version = Convert.ToInt32(await schema.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
            if (version > CurrentSchemaVersion)
                throw new InvalidDataException($"The restored database schema {version} requires a newer Harness release.");
        }, cancellationToken);

    private static async Task MakeSnapshotPortableAsync(
        SqliteConnection connection,
        string sourceDataDirectory,
        CancellationToken cancellationToken)
    {
        await RewritePortablePathsAsync(connection, sourceDataDirectory, makePortable: true, cancellationToken);
        await using var removeExternalInstalls = connection.CreateCommand();
        removeExternalInstalls.CommandText = "DELETE FROM installed_skills; PRAGMA journal_mode = DELETE;";
        await removeExternalInstalls.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task RewritePortablePathsAsync(
        SqliteConnection connection,
        string dataDirectory,
        bool makePortable,
        CancellationToken cancellationToken,
        string? portableContentDirectory = null)
    {
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        foreach (var (table, root) in new[] { ("attachments", "attachments"), ("import_sources", "imports") })
        {
            var paths = new List<(string Id, string Path)>();
            await using (var read = connection.CreateCommand())
            {
                read.Transaction = transaction;
                read.CommandText = $"SELECT id, stored_path FROM {table};";
                await using var reader = await read.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken)) paths.Add((reader.GetString(0), reader.GetString(1)));
            }

            foreach (var item in paths)
            {
                var rewritten = makePortable
                    ? ToPortableDataPath(dataDirectory, root, item.Path)
                    : FromPortableDataPath(dataDirectory, root, item.Path, portableContentDirectory);
                await using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = $"UPDATE {table} SET stored_path = $path WHERE id = $id;";
                update.Parameters.AddWithValue("$path", rewritten);
                update.Parameters.AddWithValue("$id", item.Id);
                await update.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        await transaction.CommitAsync(cancellationToken);
    }

    private static string ToPortableDataPath(string dataDirectory, string requiredRoot, string storedPath)
    {
        var fullData = Path.GetFullPath(dataDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullStored = Path.GetFullPath(storedPath);
        if (!File.Exists(fullStored))
            throw new FileNotFoundException("Harness-owned content referenced by the database is missing.", fullStored);
        if (!fullStored.StartsWith(fullData, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Harness-owned content was found outside the data directory and cannot be exported safely.");
        var relative = Path.GetRelativePath(dataDirectory, fullStored).Replace('\\', '/');
        if (!relative.StartsWith(requiredRoot + "/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"A {requiredRoot} record points outside its managed archive area.");
        return relative;
    }

    private static string FromPortableDataPath(
        string dataDirectory,
        string requiredRoot,
        string storedPath,
        string? portableContentDirectory)
    {
        if (Path.IsPathRooted(storedPath))
            throw new InvalidOperationException("The backup database contains a non-portable managed-content path.");
        var normalized = storedPath.Replace('\\', '/');
        if (!normalized.StartsWith(requiredRoot + "/", StringComparison.OrdinalIgnoreCase)
            || normalized.Split('/').Any(part => part is "" or "." or ".."))
            throw new InvalidOperationException($"The backup database contains an invalid {requiredRoot} path.");
        var target = Path.GetFullPath(Path.Combine(dataDirectory, normalized.Replace('/', Path.DirectorySeparatorChar)));
        var root = Path.GetFullPath(Path.Combine(dataDirectory, requiredRoot)).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"The backup database contains a {requiredRoot} path outside its managed directory.");
        if (portableContentDirectory is not null)
        {
            var payload = Path.GetFullPath(Path.Combine(
                portableContentDirectory,
                normalized.Replace('/', Path.DirectorySeparatorChar)));
            var payloadRoot = Path.GetFullPath(Path.Combine(portableContentDirectory, requiredRoot))
                .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!payload.StartsWith(payloadRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(payload))
                throw new InvalidDataException($"The backup database references missing {requiredRoot} content.");
        }
        return target;
    }

    public async Task UpsertSkillCatalogAsync(
        IEnumerable<SkillCatalogEntry> entries,
        CancellationToken cancellationToken = default)
    {
        var materialized = entries.ToArray();
        if (materialized.Length == 0) return;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            foreach (var entry in materialized)
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO skill_catalog(
                        id, name, description, category, repository, skill_path,
                        source_revision, source_url, compatibility, trust_state,
                        discovered_at, refreshed_at, raw_metadata_json)
                    VALUES(
                        $id, $name, $description, $category, $repository, $skillPath,
                        $revision, $url, $compatibility, $trustState,
                        $discoveredAt, $refreshedAt, $rawMetadata)
                    ON CONFLICT(id) DO UPDATE SET
                        name = CASE WHEN excluded.description LIKE 'Description not cached yet · %' THEN skill_catalog.name ELSE excluded.name END,
                        description = CASE WHEN excluded.description LIKE 'Description not cached yet · %' THEN skill_catalog.description ELSE excluded.description END,
                        category = CASE WHEN excluded.description LIKE 'Description not cached yet · %' THEN skill_catalog.category ELSE excluded.category END,
                        repository = excluded.repository,
                        skill_path = excluded.skill_path,
                        source_revision = excluded.source_revision,
                        source_url = excluded.source_url,
                        compatibility = CASE WHEN excluded.description LIKE 'Description not cached yet · %' THEN skill_catalog.compatibility ELSE excluded.compatibility END,
                        trust_state = excluded.trust_state,
                        refreshed_at = excluded.refreshed_at,
                        raw_metadata_json = excluded.raw_metadata_json;
                    """;
                command.Parameters.AddWithValue("$id", entry.Id);
                command.Parameters.AddWithValue("$name", entry.Name);
                command.Parameters.AddWithValue("$description", entry.Description);
                command.Parameters.AddWithValue("$category", entry.Category);
                command.Parameters.AddWithValue("$repository", entry.Repository);
                command.Parameters.AddWithValue("$skillPath", entry.SkillPath);
                command.Parameters.AddWithValue("$revision", entry.SourceRevision);
                command.Parameters.AddWithValue("$url", entry.SourceUrl);
                command.Parameters.AddWithValue("$compatibility", entry.Compatibility);
                command.Parameters.AddWithValue("$trustState", entry.TrustState);
                command.Parameters.AddWithValue("$discoveredAt", FormatTimestamp(entry.DiscoveredAt));
                command.Parameters.AddWithValue("$refreshedAt", FormatTimestamp(entry.RefreshedAt));
                command.Parameters.AddWithValue("$rawMetadata", entry.RawMetadataJson);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpsertSkillInventoriesAsync(
        IEnumerable<SkillRepositoryInventory> inventories,
        CancellationToken cancellationToken = default)
    {
        var materialized = inventories.ToArray();
        if (materialized.Length == 0) return;

        const int batchSize = 1_000;
        foreach (var inventory in materialized)
        {
            var skills = inventory.Skills.ToArray();
            var isBatched = skills.Length > batchSize;
            await UpsertSkillSourceAsync(
                inventory.Source with
                {
                    IndexState = isBatched ? "INDEXING PATHS" : inventory.Source.IndexState,
                    Diagnostic = isBatched
                        ? $"Writing {skills.Length:N0} catalog paths in background batches."
                        : inventory.Source.Diagnostic
                },
                preserveExistingForDiscoveryOnly: string.IsNullOrWhiteSpace(inventory.Source.SourceRevision),
                cancellationToken);

            foreach (var batch in skills.Chunk(batchSize))
            {
                await _gate.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
                try
                {
                    await using var connection = await OpenConnectionAsync(cancellationToken);
                    await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
                    await using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = """
                    INSERT INTO skill_catalog(
                        id, name, description, category, repository, skill_path,
                        source_revision, source_url, compatibility, trust_state,
                        discovered_at, refreshed_at, raw_metadata_json)
                    VALUES(
                        $id, $name, $description, $category, $repository, $skillPath,
                        $revision, $url, $compatibility, $trustState,
                        $discoveredAt, $refreshedAt, $rawMetadata)
                    ON CONFLICT(id) DO UPDATE SET
                        name = CASE WHEN excluded.description LIKE 'Description not cached yet · %' THEN skill_catalog.name ELSE excluded.name END,
                        description = CASE WHEN excluded.description LIKE 'Description not cached yet · %' THEN skill_catalog.description ELSE excluded.description END,
                        category = CASE WHEN excluded.description LIKE 'Description not cached yet · %' THEN skill_catalog.category ELSE excluded.category END,
                        source_revision = excluded.source_revision,
                        source_url = excluded.source_url,
                        compatibility = CASE WHEN excluded.description LIKE 'Description not cached yet · %' THEN skill_catalog.compatibility ELSE excluded.compatibility END,
                        trust_state = excluded.trust_state,
                        refreshed_at = excluded.refreshed_at,
                        raw_metadata_json = excluded.raw_metadata_json;
                    """;
                    var id = command.Parameters.Add("$id", SqliteType.Text);
                    var name = command.Parameters.Add("$name", SqliteType.Text);
                    var description = command.Parameters.Add("$description", SqliteType.Text);
                    var category = command.Parameters.Add("$category", SqliteType.Text);
                    var repository = command.Parameters.Add("$repository", SqliteType.Text);
                    var skillPath = command.Parameters.Add("$skillPath", SqliteType.Text);
                    var revision = command.Parameters.Add("$revision", SqliteType.Text);
                    var url = command.Parameters.Add("$url", SqliteType.Text);
                    var compatibility = command.Parameters.Add("$compatibility", SqliteType.Text);
                    var trustState = command.Parameters.Add("$trustState", SqliteType.Text);
                    var discoveredAt = command.Parameters.Add("$discoveredAt", SqliteType.Text);
                    var refreshedAt = command.Parameters.Add("$refreshedAt", SqliteType.Text);
                    var rawMetadata = command.Parameters.Add("$rawMetadata", SqliteType.Text);
                    await command.PrepareAsync(cancellationToken);
                    foreach (var entry in batch)
                    {
                        id.Value = entry.Id;
                        name.Value = entry.Name;
                        description.Value = entry.Description;
                        category.Value = entry.Category;
                        repository.Value = entry.Repository;
                        skillPath.Value = entry.SkillPath;
                        revision.Value = entry.SourceRevision;
                        url.Value = entry.SourceUrl;
                        compatibility.Value = entry.Compatibility;
                        trustState.Value = entry.TrustState;
                        discoveredAt.Value = FormatTimestamp(entry.DiscoveredAt);
                        refreshedAt.Value = FormatTimestamp(entry.RefreshedAt);
                        rawMetadata.Value = entry.RawMetadataJson;
                        await command.ExecuteNonQueryAsync(cancellationToken);
                    }

                    await transaction.CommitAsync(cancellationToken);
                }
                finally
                {
                    _gate.Release();
                }
            }

            await UpsertSkillSourceAsync(
                inventory.Source,
                preserveExistingForDiscoveryOnly: string.IsNullOrWhiteSpace(inventory.Source.SourceRevision),
                cancellationToken);
            await RefreshSkillSourceCountAsync(inventory.Source.Repository, cancellationToken);
        }
    }

    private async Task UpsertSkillSourceAsync(
        SkillCatalogSource source,
        bool preserveExistingForDiscoveryOnly,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO skill_sources(
                    repository, owner, source_url, reported_skill_count,
                    indexed_skill_count, source_revision, index_state,
                    refreshed_at, diagnostic)
                VALUES(
                    $repository, $owner, $sourceUrl, $reported, $indexed,
                    $revision, $state, $refreshedAt, $diagnostic)
                ON CONFLICT(repository) DO UPDATE SET
                    owner = excluded.owner,
                    source_url = excluded.source_url,
                    reported_skill_count = CASE WHEN $preserve = 1 THEN skill_sources.reported_skill_count ELSE excluded.reported_skill_count END,
                    indexed_skill_count = CASE WHEN $preserve = 1 THEN skill_sources.indexed_skill_count ELSE MAX(skill_sources.indexed_skill_count, excluded.indexed_skill_count) END,
                    source_revision = CASE WHEN $preserve = 1 THEN skill_sources.source_revision ELSE excluded.source_revision END,
                    index_state = CASE WHEN $preserve = 1 THEN skill_sources.index_state ELSE excluded.index_state END,
                    refreshed_at = excluded.refreshed_at,
                    diagnostic = excluded.diagnostic;
                """;
            command.Parameters.AddWithValue("$repository", source.Repository);
            command.Parameters.AddWithValue("$owner", source.Owner);
            command.Parameters.AddWithValue("$sourceUrl", source.SourceUrl);
            command.Parameters.AddWithValue("$reported", source.ReportedSkillCount);
            command.Parameters.AddWithValue("$indexed", source.IndexedSkillCount);
            command.Parameters.AddWithValue("$revision", source.SourceRevision);
            command.Parameters.AddWithValue("$state", source.IndexState);
            command.Parameters.AddWithValue("$refreshedAt", FormatTimestamp(source.RefreshedAt));
            command.Parameters.AddWithValue("$diagnostic", source.Diagnostic);
            command.Parameters.AddWithValue("$preserve", preserveExistingForDiscoveryOnly ? 1 : 0);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task RefreshSkillSourceCountAsync(string repository, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE skill_sources
                SET indexed_skill_count = (
                    SELECT COUNT(*) FROM skill_catalog
                    WHERE repository = $repository COLLATE NOCASE),
                    index_state = CASE
                        WHEN reported_skill_count = 0 THEN index_state
                        WHEN (SELECT COUNT(*) FROM skill_catalog WHERE repository = $repository COLLATE NOCASE) = reported_skill_count THEN 'COMPLETE PATH INDEX'
                        WHEN (SELECT COUNT(*) FROM skill_catalog WHERE repository = $repository COLLATE NOCASE) < reported_skill_count THEN 'PARTIAL · SEARCH TO EXPAND'
                        ELSE 'STALE ENTRIES · REFRESH SOURCE'
                    END
                WHERE repository = $repository COLLATE NOCASE;
                """;
            command.Parameters.AddWithValue("$repository", repository);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<SkillCatalogSource>> ListSkillSourcesAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT repository, owner, source_url, reported_skill_count,
                       indexed_skill_count, source_revision, index_state,
                       refreshed_at, diagnostic,
                       (SELECT COUNT(*) FROM skill_catalog catalog
                        WHERE catalog.repository = skill_sources.repository COLLATE NOCASE
                          AND catalog.description NOT LIKE 'Description not cached yet · %') AS described_skill_count
                FROM skill_sources
                ORDER BY reported_skill_count DESC, repository COLLATE NOCASE ASC;
                """;
            var result = new List<SkillCatalogSource>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                result.Add(new SkillCatalogSource(
                    reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    reader.GetInt32(3), reader.GetInt32(4), reader.GetString(5),
                    reader.GetString(6), ParseTimestamp(reader.GetString(7)), reader.GetString(8), reader.GetInt32(9)));
            }
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RemoveSkillSourceIfEmptyAsync(
        string repository,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                DELETE FROM skill_sources
                WHERE repository = $repository COLLATE NOCASE
                  AND reported_skill_count = 0
                  AND NOT EXISTS (
                      SELECT 1 FROM skill_catalog
                      WHERE skill_catalog.repository = $repository COLLATE NOCASE);
                """;
            command.Parameters.AddWithValue("$repository", repository);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<SkillCatalogEntry>> SearchSkillCatalogAsync(
        string? query = null,
        string? category = null,
        string? repository = null,
        string? compatibilityProvider = null,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id, name, description, category, repository, skill_path,
                       source_revision, source_url, compatibility, trust_state,
                       discovered_at, refreshed_at, raw_metadata_json
                FROM skill_catalog
                WHERE ($query = '' OR name LIKE $pattern ESCAPE '\' COLLATE NOCASE
                    OR description LIKE $pattern ESCAPE '\' COLLATE NOCASE
                    OR repository LIKE $pattern ESCAPE '\' COLLATE NOCASE
                    OR skill_path LIKE $pattern ESCAPE '\' COLLATE NOCASE)
                  AND ($category = '' OR category = $category COLLATE NOCASE)
                  AND ($repository = '' OR repository = $repository COLLATE NOCASE)
                  AND ($provider = ''
                    OR compatibility = 'Portable Agent Skill' COLLATE NOCASE
                    OR ($provider = 'openai-codex' AND compatibility = 'Codex extension' COLLATE NOCASE)
                    OR ($provider = 'anthropic-claude' AND compatibility = 'Claude Code extension' COLLATE NOCASE))
                ORDER BY refreshed_at DESC, name COLLATE NOCASE ASC
                LIMIT 250;
                """;
            var normalizedQuery = query?.Trim() ?? string.Empty;
            var normalizedCategory = string.Equals(category, "All", StringComparison.OrdinalIgnoreCase)
                ? string.Empty
                : category?.Trim() ?? string.Empty;
            var escaped = normalizedQuery.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
            command.Parameters.AddWithValue("$query", normalizedQuery);
            command.Parameters.AddWithValue("$pattern", $"%{escaped}%");
            command.Parameters.AddWithValue("$category", normalizedCategory);
            command.Parameters.AddWithValue("$repository",
                string.Equals(repository, "All sources", StringComparison.OrdinalIgnoreCase)
                    ? string.Empty
                    : repository?.Trim() ?? string.Empty);
            command.Parameters.AddWithValue("$provider", compatibilityProvider?.Trim() ?? string.Empty);
            var result = new List<SkillCatalogEntry>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) result.Add(ReadSkillCatalogEntry(reader));
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveInstalledSkillAsync(
        InstalledSkill skill,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO installed_skills(
                    id, catalog_id, name, source_revision, package_path, install_path,
                    scope, workspace_path, provider_id, model_id, content_sha256,
                    enabled, installed_at)
                VALUES(
                    $id, $catalogId, $name, $revision, $packagePath, $installPath,
                    $scope, $workspacePath, $providerId, $modelId, $sha256,
                    $enabled, $installedAt)
                ON CONFLICT(id) DO UPDATE SET
                    catalog_id = excluded.catalog_id,
                    name = excluded.name,
                    source_revision = excluded.source_revision,
                    package_path = excluded.package_path,
                    install_path = excluded.install_path,
                    scope = excluded.scope,
                    workspace_path = excluded.workspace_path,
                    provider_id = excluded.provider_id,
                    model_id = excluded.model_id,
                    content_sha256 = excluded.content_sha256,
                    enabled = excluded.enabled,
                    installed_at = excluded.installed_at;
                """;
            command.Parameters.AddWithValue("$id", skill.Id);
            command.Parameters.AddWithValue("$catalogId", skill.CatalogId);
            command.Parameters.AddWithValue("$name", skill.Name);
            command.Parameters.AddWithValue("$revision", skill.SourceRevision);
            command.Parameters.AddWithValue("$packagePath", skill.PackagePath);
            command.Parameters.AddWithValue("$installPath", skill.InstallPath);
            command.Parameters.AddWithValue("$scope", skill.Scope);
            command.Parameters.AddWithValue("$workspacePath", (object?)skill.WorkspacePath ?? DBNull.Value);
            command.Parameters.AddWithValue("$providerId", skill.ProviderId);
            command.Parameters.AddWithValue("$modelId", (object?)skill.ModelId ?? DBNull.Value);
            command.Parameters.AddWithValue("$sha256", skill.ContentSha256);
            command.Parameters.AddWithValue("$enabled", skill.Enabled ? 1 : 0);
            command.Parameters.AddWithValue("$installedAt", FormatTimestamp(skill.InstalledAt));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<InstalledSkill>> ListInstalledSkillsAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id, catalog_id, name, source_revision, package_path, install_path,
                       scope, workspace_path, provider_id, model_id, content_sha256,
                       enabled, installed_at
                FROM installed_skills ORDER BY installed_at DESC;
                """;
            var result = new List<InstalledSkill>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                result.Add(new InstalledSkill(
                    reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    reader.GetString(3), reader.GetString(4), reader.GetString(5),
                    reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.GetString(8), reader.IsDBNull(9) ? null : reader.GetString(9),
                    reader.GetString(10), reader.GetInt64(11) != 0,
                    ParseTimestamp(reader.GetString(12))));
            }
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ConversationImportResult> ImportConversationAsync(
        string projectId,
        ConversationImportPlan plan,
        CancellationToken cancellationToken = default)
    {
        var source = new FileInfo(Path.GetFullPath(plan.SourcePath));
        if (!source.Exists) throw new FileNotFoundException("The import source no longer exists.", source.FullName);
        var importsRoot = Path.Combine(Path.GetDirectoryName(DatabasePath)!, "imports");
        Directory.CreateDirectory(importsRoot);
        var pendingSource = Path.Combine(importsRoot, $".pending-{Guid.NewGuid():N}");
        var snapshotLength = source.Length;
        string hash;
        try
        {
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using var input = new FileStream(
                source.FullName,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var output = new FileStream(
                pendingSource,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var buffer = new byte[128 * 1024];
            long remaining = snapshotLength;
            while (remaining > 0)
            {
                var read = await input.ReadAsync(
                    buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)),
                    cancellationToken);
                if (read == 0) throw new IOException("The source history changed while Harness was snapshotting it.");
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                hasher.AppendData(buffer, 0, read);
                remaining -= read;
            }
            hash = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
        }
        catch
        {
            if (File.Exists(pendingSource)) File.Delete(pendingSource);
            throw;
        }
        var importDirectory = Path.Combine(importsRoot, hash);
        Directory.CreateDirectory(importDirectory);
        var storedSource = Path.Combine(importDirectory, source.Name);
        if (File.Exists(storedSource)) File.Delete(pendingSource);
        else File.Move(pendingSource, storedSource);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            var now = DateTimeOffset.UtcNow;
            var session = NewSession(projectId, plan.SuggestedTitle, now);
            await InsertSessionAsync(connection, transaction, session, cancellationToken);
            for (var index = 0; index < plan.Messages.Count; index++)
            {
                var imported = plan.Messages[index];
                await using var message = connection.CreateCommand();
                message.Transaction = transaction;
                message.CommandText = """
                    INSERT INTO messages(id, session_id, sequence, role, title, text, status, color,
                        monospace, created_at, provider_event_json)
                    VALUES($id, $sessionId, $sequence, $role, $title, $text, 'IMPORTED', $color,
                        0, $createdAt, NULL);
                    """;
                message.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
                message.Parameters.AddWithValue("$sessionId", session.Id);
                message.Parameters.AddWithValue("$sequence", index);
                message.Parameters.AddWithValue("$role", imported.Role);
                message.Parameters.AddWithValue("$title", imported.Role == "YOU" ? "Imported prompt" : "Imported response");
                message.Parameters.AddWithValue("$text", imported.Text);
                message.Parameters.AddWithValue("$color", imported.Role == "YOU" ? "#8993A3" : "#65C7D0");
                message.Parameters.AddWithValue("$createdAt", FormatTimestamp(imported.CreatedAt ?? now.AddTicks(index)));
                await message.ExecuteNonQueryAsync(cancellationToken);
            }
            await using (var import = connection.CreateCommand())
            {
                import.Transaction = transaction;
                import.CommandText = """
                    INSERT INTO import_sources(id, session_id, source_kind, original_path, stored_path, sha256, imported_at)
                    VALUES($id, $sessionId, $kind, $originalPath, $storedPath, $sha256, $importedAt);
                    """;
                import.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
                import.Parameters.AddWithValue("$sessionId", session.Id);
                import.Parameters.AddWithValue("$kind", plan.SourceKind);
                import.Parameters.AddWithValue("$originalPath", source.FullName);
                import.Parameters.AddWithValue("$storedPath", storedSource);
                import.Parameters.AddWithValue("$sha256", hash);
                import.Parameters.AddWithValue("$importedAt", FormatTimestamp(now));
                await import.ExecuteNonQueryAsync(cancellationToken);
            }
            await using (var boundary = connection.CreateCommand())
            {
                boundary.Transaction = transaction;
                boundary.CommandText = """
                    INSERT INTO provider_events(id, session_id, method, payload_json, created_at)
                    VALUES($id, $sessionId, 'harness/importBoundary', $payload, $createdAt);
                    """;
                boundary.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
                boundary.Parameters.AddWithValue("$sessionId", session.Id);
                boundary.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(new
                {
                    sourceKind = plan.SourceKind,
                    sourceSha256 = hash,
                    messageCount = plan.Messages.Count,
                    warnings = plan.Warnings,
                    retainedSource = storedSource
                }));
                boundary.Parameters.AddWithValue("$createdAt", FormatTimestamp(now));
                await boundary.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
            return new ConversationImportResult(session, plan.Messages.Count, storedSource);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<StoredImportSource?> GetImportSourceAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id, session_id, source_kind, original_path, stored_path, sha256, imported_at
                FROM import_sources WHERE session_id = $sessionId ORDER BY imported_at DESC LIMIT 1;
                """;
            command.Parameters.AddWithValue("$sessionId", sessionId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return null;
            return new StoredImportSource(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4), reader.GetString(5),
                ParseTimestamp(reader.GetString(6)));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> HasImportedSourceAsync(
        string projectId,
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(sourcePath);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT EXISTS(
                    SELECT 1
                    FROM import_sources imported
                    JOIN sessions session ON session.id = imported.session_id
                    WHERE session.project_id = $projectId AND imported.original_path = $sourcePath
                );
                """;
            command.Parameters.AddWithValue("$projectId", projectId);
            command.Parameters.AddWithValue("$sourcePath", fullPath);
            return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) != 0;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> HasProviderEventAsync(
        string sessionId,
        string method,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT EXISTS(SELECT 1 FROM provider_events WHERE session_id = $sessionId AND method = $method);";
            command.Parameters.AddWithValue("$sessionId", sessionId);
            command.Parameters.AddWithValue("$method", method);
            return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) != 0;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string?> GetLatestProviderEventPayloadAsync(
        string sessionId,
        string method,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT payload_json FROM provider_events WHERE session_id = $sessionId AND method = $method ORDER BY created_at DESC LIMIT 1;";
            command.Parameters.AddWithValue("$sessionId", sessionId);
            command.Parameters.AddWithValue("$method", method);
            var value = await command.ExecuteScalarAsync(cancellationToken);
            return value is null or DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<WorkspaceSessionSnapshot> OpenWorkspaceAsync(
        string rootPath,
        CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(rootPath);
        var normalizedPath = NormalizePath(fullPath);
        var now = DateTimeOffset.UtcNow;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)
                await connection.BeginTransactionAsync(cancellationToken);
            var project = await ReadProjectByPathAsync(
                connection,
                transaction,
                normalizedPath,
                cancellationToken);
            if (project is null)
            {
                project = new StoredProject(
                    Guid.NewGuid().ToString("N"),
                    new DirectoryInfo(fullPath).Name,
                    fullPath,
                    now,
                    now);
                await InsertProjectAsync(connection, transaction, project, normalizedPath, cancellationToken);
            }
            else
            {
                project = project with { LastOpenedAt = now, RootPath = fullPath };
                await UpdateProjectOpenedAsync(connection, transaction, project, cancellationToken);
            }

            var sessions = await ReadSessionsAsync(connection, transaction, project.Id, cancellationToken);
            if (sessions.Count == 0)
            {
                var session = NewSession(project.Id, "New session", now);
                await InsertSessionAsync(connection, transaction, session, cancellationToken);
                sessions.Add(session);
            }

            var active = sessions[0];
            var messages = await ReadMessagesAsync(connection, transaction, active.Id, cancellationToken);
            var attachments = await ReadAttachmentsAsync(
                connection,
                transaction,
                active.Id,
                cancellationToken);
            var activityEvents = await ReadActivityEventsAsync(
                connection,
                transaction,
                project.Id,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new WorkspaceSessionSnapshot(project, sessions, active, messages, attachments, activityEvents);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<(
        StoredSession Session,
        IReadOnlyList<StoredMessage> Messages,
        IReadOnlyList<StoredAttachment> Attachments)> LoadSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            var session = await ReadSessionAsync(connection, null, sessionId, cancellationToken)
                ?? throw new InvalidOperationException($"Session {sessionId} was not found.");
            var messages = await ReadMessagesAsync(connection, null, sessionId, cancellationToken);
            var attachments = await ReadAttachmentsAsync(connection, null, sessionId, cancellationToken);
            return (session, messages, attachments);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<StoredSession> CreateSessionAsync(
        string projectId,
        string title,
        CancellationToken cancellationToken = default)
    {
        var session = NewSession(projectId, title, DateTimeOffset.UtcNow);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await InsertSessionAsync(connection, null, session, cancellationToken);
            return session;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RenameSessionAsync(
        string sessionId,
        string title,
        CancellationToken cancellationToken = default)
    {
        var normalizedTitle = title.Trim();
        if (normalizedTitle.Length == 0)
        {
            throw new ArgumentException("A session title is required.", nameof(title));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE sessions SET title = $title, updated_at = $updatedAt WHERE id = $id;
                """;
            command.Parameters.AddWithValue("$title", normalizedTitle);
            command.Parameters.AddWithValue("$updatedAt", FormatTimestamp(DateTimeOffset.UtcNow));
            command.Parameters.AddWithValue("$id", sessionId);
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new InvalidOperationException($"Session {sessionId} was not found.");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM sessions WHERE id = $id;";
            command.Parameters.AddWithValue("$id", sessionId);
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new InvalidOperationException($"Session {sessionId} was not found.");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<StoredAttachment> AddAttachmentAsync(
        string sessionId,
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        var fullSourcePath = Path.GetFullPath(sourcePath);
        var source = new FileInfo(fullSourcePath);
        if (!source.Exists)
        {
            throw new FileNotFoundException("The context file no longer exists.", fullSourcePath);
        }

        const long maximumAttachmentBytes = 25L * 1024 * 1024;
        if (source.Length > maximumAttachmentBytes)
        {
            throw new InvalidOperationException(
                $"Context files are currently limited to {maximumAttachmentBytes / 1024 / 1024} MB each.");
        }

        string hash;
        await using (var input = source.OpenRead())
        {
            hash = Convert.ToHexString(
                await SHA256.HashDataAsync(input, cancellationToken)).ToLowerInvariant();
        }

        var attachmentsDirectory = Path.Combine(
            Path.GetDirectoryName(DatabasePath)!,
            "attachments");
        Directory.CreateDirectory(attachmentsDirectory);
        var extension = source.Extension.Length is > 0 and <= 16
            ? source.Extension.ToLowerInvariant()
            : string.Empty;
        var storedPath = Path.Combine(attachmentsDirectory, $"{hash}{extension}");
        if (!File.Exists(storedPath))
        {
            var temporaryPath = $"{storedPath}.{Guid.NewGuid():N}.tmp";
            try
            {
                await using (var input = source.OpenRead())
                await using (var output = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    81920,
                    FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await input.CopyToAsync(output, cancellationToken);
                    await output.FlushAsync(cancellationToken);
                }
                File.Move(temporaryPath, storedPath, overwrite: false);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using (var existingCommand = connection.CreateCommand())
            {
                existingCommand.CommandText = """
                    SELECT id, session_id, original_path, stored_path, media_type,
                           sha256, byte_length, created_at
                    FROM attachments
                    WHERE session_id = $sessionId AND sha256 = $sha256
                    LIMIT 1;
                    """;
                existingCommand.Parameters.AddWithValue("$sessionId", sessionId);
                existingCommand.Parameters.AddWithValue("$sha256", hash);
                await using var reader = await existingCommand.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken))
                {
                    return ReadAttachment(reader);
                }
            }

            var attachment = new StoredAttachment(
                Guid.NewGuid().ToString("N"),
                sessionId,
                source.Name,
                fullSourcePath,
                storedPath,
                GetMediaType(source.Extension),
                hash,
                source.Length,
                DateTimeOffset.UtcNow);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO attachments(
                    id, session_id, message_id, original_path, stored_path,
                    media_type, sha256, byte_length, created_at)
                VALUES(
                    $id, $sessionId, NULL, $originalPath, $storedPath,
                    $mediaType, $sha256, $byteLength, $createdAt);
                """;
            command.Parameters.AddWithValue("$id", attachment.Id);
            command.Parameters.AddWithValue("$sessionId", attachment.SessionId);
            command.Parameters.AddWithValue("$originalPath", attachment.OriginalPath);
            command.Parameters.AddWithValue("$storedPath", attachment.StoredPath);
            command.Parameters.AddWithValue("$mediaType", (object?)attachment.MediaType ?? DBNull.Value);
            command.Parameters.AddWithValue("$sha256", attachment.Sha256);
            command.Parameters.AddWithValue("$byteLength", attachment.ByteLength);
            command.Parameters.AddWithValue("$createdAt", FormatTimestamp(attachment.CreatedAt));
            await command.ExecuteNonQueryAsync(cancellationToken);
            return attachment;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RemoveAttachmentAsync(
        string attachmentId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            string? storedPath;
            await using (var read = connection.CreateCommand())
            {
                read.CommandText = "SELECT stored_path FROM attachments WHERE id = $id;";
                read.Parameters.AddWithValue("$id", attachmentId);
                storedPath = await read.ExecuteScalarAsync(cancellationToken) as string;
            }
            if (storedPath is null)
            {
                return;
            }

            await using (var delete = connection.CreateCommand())
            {
                delete.CommandText = "DELETE FROM attachments WHERE id = $id;";
                delete.Parameters.AddWithValue("$id", attachmentId);
                await delete.ExecuteNonQueryAsync(cancellationToken);
            }

            await using var references = connection.CreateCommand();
            references.CommandText = "SELECT COUNT(*) FROM attachments WHERE stored_path = $storedPath;";
            references.Parameters.AddWithValue("$storedPath", storedPath);
            var count = Convert.ToInt64(
                await references.ExecuteScalarAsync(cancellationToken),
                CultureInfo.InvariantCulture);
            if (count == 0 && File.Exists(storedPath))
            {
                File.Delete(storedPath);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpdateSessionConnectionAsync(
        string sessionId,
        string providerId,
        string? providerThreadId,
        string modelId,
        string? reasoningEffort,
        string? serviceTier,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE sessions
                SET provider_id = $providerId,
                    provider_thread_id = $providerThreadId,
                    model_id = $modelId,
                    reasoning_effort = $reasoningEffort,
                    service_tier = $serviceTier,
                    updated_at = $updatedAt
                WHERE id = $sessionId;
                """;
            command.Parameters.AddWithValue("$providerId", providerId);
            command.Parameters.AddWithValue("$providerThreadId", (object?)providerThreadId ?? DBNull.Value);
            command.Parameters.AddWithValue("$modelId", modelId);
            command.Parameters.AddWithValue("$reasoningEffort", (object?)reasoningEffort ?? DBNull.Value);
            command.Parameters.AddWithValue("$serviceTier", (object?)serviceTier ?? DBNull.Value);
            command.Parameters.AddWithValue("$updatedAt", FormatTimestamp(DateTimeOffset.UtcNow));
            command.Parameters.AddWithValue("$sessionId", sessionId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpdateSessionModelSettingsAsync(
        string sessionId,
        string providerId,
        string modelId,
        string? reasoningEffort,
        string? serviceTier,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE sessions
                SET provider_thread_id = CASE
                        WHEN provider_id IS NULL OR provider_id = $providerId
                            THEN provider_thread_id
                        ELSE NULL
                    END,
                    provider_id = $providerId,
                    model_id = $modelId,
                    reasoning_effort = $reasoningEffort,
                    service_tier = $serviceTier,
                    updated_at = $updatedAt
                WHERE id = $sessionId;
                """;
            command.Parameters.AddWithValue("$providerId", providerId);
            command.Parameters.AddWithValue("$modelId", modelId);
            command.Parameters.AddWithValue("$reasoningEffort", (object?)reasoningEffort ?? DBNull.Value);
            command.Parameters.AddWithValue("$serviceTier", (object?)serviceTier ?? DBNull.Value);
            command.Parameters.AddWithValue("$updatedAt", FormatTimestamp(DateTimeOffset.UtcNow));
            command.Parameters.AddWithValue("$sessionId", sessionId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpsertMessageAsync(
        StoredMessage message,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO messages (
                    id, session_id, sequence, role, title, text, status, color,
                    monospace, created_at, provider_event_json)
                VALUES (
                    $id, $sessionId,
                    COALESCE((SELECT MAX(sequence) + 1 FROM messages WHERE session_id = $sessionId), 0),
                    $role, $title, $text, $status, $color, $monospace, $createdAt,
                    $providerEventJson)
                ON CONFLICT(id) DO UPDATE SET
                    text = excluded.text,
                    status = excluded.status,
                    provider_event_json = COALESCE(excluded.provider_event_json, messages.provider_event_json);

                UPDATE sessions SET updated_at = $updatedAt WHERE id = $sessionId;
                """;
            command.Parameters.AddWithValue("$id", message.Id);
            command.Parameters.AddWithValue("$sessionId", message.SessionId);
            command.Parameters.AddWithValue("$role", message.Role);
            command.Parameters.AddWithValue("$title", message.Title);
            command.Parameters.AddWithValue("$text", message.Text);
            command.Parameters.AddWithValue("$status", message.Status);
            command.Parameters.AddWithValue("$color", message.Color);
            command.Parameters.AddWithValue("$monospace", message.Monospace ? 1 : 0);
            command.Parameters.AddWithValue("$createdAt", FormatTimestamp(message.CreatedAt));
            command.Parameters.AddWithValue(
                "$providerEventJson",
                (object?)message.ProviderEventJson ?? DBNull.Value);
            command.Parameters.AddWithValue("$updatedAt", FormatTimestamp(DateTimeOffset.UtcNow));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task AppendProviderEventAsync(
        string sessionId,
        string method,
        string payloadJson,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO provider_events(id, session_id, method, payload_json, created_at)
                VALUES($id, $sessionId, $method, $payload, $createdAt);
                """;
            command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
            command.Parameters.AddWithValue("$sessionId", sessionId);
            command.Parameters.AddWithValue("$method", method);
            command.Parameters.AddWithValue("$payload", payloadJson);
            command.Parameters.AddWithValue("$createdAt", FormatTimestamp(DateTimeOffset.UtcNow));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task AppendActivityEventAsync(
        StoredActivityEvent activityEvent,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO activity_events(
                    id, project_id, session_id, kind, title, detail, outcome,
                    color, is_milestone, created_at)
                VALUES(
                    $id, $projectId, $sessionId, $kind, $title, $detail, $outcome,
                    $color, $isMilestone, $createdAt);
                """;
            command.Parameters.AddWithValue("$id", activityEvent.Id);
            command.Parameters.AddWithValue("$projectId", activityEvent.ProjectId);
            command.Parameters.AddWithValue("$sessionId", (object?)activityEvent.SessionId ?? DBNull.Value);
            command.Parameters.AddWithValue("$kind", activityEvent.Kind);
            command.Parameters.AddWithValue("$title", activityEvent.Title);
            command.Parameters.AddWithValue("$detail", activityEvent.Detail);
            command.Parameters.AddWithValue("$outcome", activityEvent.Outcome);
            command.Parameters.AddWithValue("$color", activityEvent.Color);
            command.Parameters.AddWithValue("$isMilestone", activityEvent.IsMilestone ? 1 : 0);
            command.Parameters.AddWithValue("$createdAt", FormatTimestamp(activityEvent.CreatedAt));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static StoredSession NewSession(string projectId, string title, DateTimeOffset now) =>
        new(
            Guid.NewGuid().ToString("N"),
            projectId,
            title,
            null,
            null,
            null,
            null,
            null,
            now,
            now);

    private static async Task<StoredProject?> ReadProjectByPathAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string normalizedPath,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, name, root_path, created_at, last_opened_at
            FROM projects WHERE normalized_root_path = $path;
            """;
        command.Parameters.AddWithValue("$path", normalizedPath);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadProject(reader) : null;
    }

    private static async Task InsertProjectAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        StoredProject project,
        string normalizedPath,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO projects(id, name, root_path, normalized_root_path, created_at, last_opened_at)
            VALUES($id, $name, $rootPath, $normalizedPath, $createdAt, $lastOpenedAt);
            """;
        command.Parameters.AddWithValue("$id", project.Id);
        command.Parameters.AddWithValue("$name", project.Name);
        command.Parameters.AddWithValue("$rootPath", project.RootPath);
        command.Parameters.AddWithValue("$normalizedPath", normalizedPath);
        command.Parameters.AddWithValue("$createdAt", FormatTimestamp(project.CreatedAt));
        command.Parameters.AddWithValue("$lastOpenedAt", FormatTimestamp(project.LastOpenedAt));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpdateProjectOpenedAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        StoredProject project,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE projects SET root_path = $rootPath, last_opened_at = $lastOpenedAt WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$rootPath", project.RootPath);
        command.Parameters.AddWithValue("$lastOpenedAt", FormatTimestamp(project.LastOpenedAt));
        command.Parameters.AddWithValue("$id", project.Id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<List<StoredSession>> ReadSessionsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string projectId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, project_id, title, provider_id, provider_thread_id, model_id,
                   reasoning_effort, service_tier, created_at, updated_at
            FROM sessions
            WHERE project_id = $projectId
              AND NOT EXISTS (
                  SELECT 1 FROM import_sources imported
                  WHERE imported.session_id = sessions.id
                    AND imported.source_kind LIKE 'Codex internal%'
              )
            ORDER BY updated_at DESC;
            """;
        command.Parameters.AddWithValue("$projectId", projectId);
        var result = new List<StoredSession>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadSession(reader));
        }
        return result;
    }

    private static async Task ClassifyLegacyInternalCodexImportsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var candidates = new List<(string Id, string StoredPath)>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT id, stored_path FROM import_sources
                WHERE source_kind = 'Codex history';
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                candidates.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        foreach (var candidate in candidates)
        {
            if (!await IsInternalCodexHistoryAsync(candidate.StoredPath, cancellationToken)) continue;
            await using var update = connection.CreateCommand();
            update.CommandText = """
                UPDATE import_sources SET source_kind = 'Codex internal session'
                WHERE id = $id;
                """;
            update.Parameters.AddWithValue("$id", candidate.Id);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task<bool> IsInternalCodexHistoryAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return false;
        using var reader = new StreamReader(path);
        for (var index = 0; index < 12 && await reader.ReadLineAsync(cancellationToken) is { } line; index++)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (!root.TryGetProperty("type", out var type)
                    || type.GetString() != "session_meta"
                    || !root.TryGetProperty("payload", out var payload)) continue;
                var isSubagent = payload.TryGetProperty("source", out var source)
                    && source.ValueKind == JsonValueKind.Object
                    && source.TryGetProperty("subagent", out _);
                var isHarnessOrigin = payload.TryGetProperty("originator", out var originator)
                    && originator.ValueKind == JsonValueKind.String
                    && string.Equals(originator.GetString(), "harness", StringComparison.OrdinalIgnoreCase);
                return isSubagent || isHarnessOrigin;
            }
            catch (JsonException)
            {
                return false;
            }
        }
        return false;
    }

    private static async Task<StoredSession?> ReadSessionAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sessionId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, project_id, title, provider_id, provider_thread_id, model_id,
                   reasoning_effort, service_tier, created_at, updated_at
            FROM sessions WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", sessionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadSession(reader) : null;
    }

    private static async Task InsertSessionAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        StoredSession session,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO sessions(
                id, project_id, title, provider_id, provider_thread_id, model_id,
                reasoning_effort, service_tier, created_at, updated_at)
            VALUES(
                $id, $projectId, $title, $providerId, $providerThreadId, $modelId,
                $reasoningEffort, $serviceTier, $createdAt, $updatedAt);
            """;
        command.Parameters.AddWithValue("$id", session.Id);
        command.Parameters.AddWithValue("$projectId", session.ProjectId);
        command.Parameters.AddWithValue("$title", session.Title);
        command.Parameters.AddWithValue("$providerId", (object?)session.ProviderId ?? DBNull.Value);
        command.Parameters.AddWithValue("$providerThreadId", (object?)session.ProviderThreadId ?? DBNull.Value);
        command.Parameters.AddWithValue("$modelId", (object?)session.ModelId ?? DBNull.Value);
        command.Parameters.AddWithValue("$reasoningEffort", (object?)session.ReasoningEffort ?? DBNull.Value);
        command.Parameters.AddWithValue("$serviceTier", (object?)session.ServiceTier ?? DBNull.Value);
        command.Parameters.AddWithValue("$createdAt", FormatTimestamp(session.CreatedAt));
        command.Parameters.AddWithValue("$updatedAt", FormatTimestamp(session.UpdatedAt));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<List<StoredMessage>> ReadMessagesAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sessionId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, session_id, sequence, role, title, text, status, color,
                   monospace, created_at, provider_event_json
            FROM messages WHERE session_id = $sessionId ORDER BY sequence;
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId);
        var result = new List<StoredMessage>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new StoredMessage(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt64(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetInt64(8) != 0,
                ParseTimestamp(reader.GetString(9)),
                reader.IsDBNull(10) ? null : reader.GetString(10)));
        }
        return result;
    }

    private static async Task<List<StoredAttachment>> ReadAttachmentsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sessionId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, session_id, original_path, stored_path, media_type,
                   sha256, byte_length, created_at
            FROM attachments WHERE session_id = $sessionId ORDER BY created_at;
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId);
        var result = new List<StoredAttachment>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadAttachment(reader));
        }
        return result;
    }

    private static async Task<List<StoredActivityEvent>> ReadActivityEventsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string projectId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, project_id, session_id, kind, title, detail, outcome,
                   color, is_milestone, created_at
            FROM activity_events
            WHERE project_id = $projectId
            ORDER BY created_at DESC
            LIMIT 500;
            """;
        command.Parameters.AddWithValue("$projectId", projectId);
        var result = new List<StoredActivityEvent>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new StoredActivityEvent(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetInt64(8) != 0,
                ParseTimestamp(reader.GetString(9))));
        }
        return result;
    }

    private static StoredAttachment ReadAttachment(SqliteDataReader reader)
    {
        var originalPath = reader.GetString(2);
        return new StoredAttachment(
            reader.GetString(0),
            reader.GetString(1),
            Path.GetFileName(originalPath),
            originalPath,
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetString(5),
            reader.GetInt64(6),
            ParseTimestamp(reader.GetString(7)));
    }

    private static StoredProject ReadProject(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        ParseTimestamp(reader.GetString(3)),
        ParseTimestamp(reader.GetString(4)));

    private static SkillCatalogEntry ReadSkillCatalogEntry(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.GetString(5),
        reader.GetString(6),
        reader.GetString(7),
        reader.GetString(8),
        reader.GetString(9),
        ParseTimestamp(reader.GetString(10)),
        ParseTimestamp(reader.GetString(11)),
        reader.GetString(12));

    private static StoredSession ReadSession(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.IsDBNull(3) ? null : reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetString(5),
        reader.IsDBNull(6) ? null : reader.GetString(6),
        reader.IsDBNull(7) ? null : reader.GetString(7),
        ParseTimestamp(reader.GetString(8)),
        ParseTimestamp(reader.GetString(9)));

    private static string NormalizePath(string path) =>
        OperatingSystem.IsWindows()
            ? Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant()
            : Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static string? GetMediaType(string extension) => extension.ToLowerInvariant() switch
    {
        ".txt" or ".md" or ".cs" or ".fs" or ".vb" or ".js" or ".ts" or ".tsx"
            or ".jsx" or ".json" or ".xml" or ".yaml" or ".yml" or ".toml" or ".ini"
            or ".py" or ".rs" or ".go" or ".java" or ".kt" or ".cpp" or ".c" or ".h"
            or ".hpp" or ".css" or ".html" or ".sql" or ".sh" or ".ps1" => "text/plain",
        ".pdf" => "application/pdf",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        _ => null
    };

    public ValueTask DisposeAsync()
    {
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }
}

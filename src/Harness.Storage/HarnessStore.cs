using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Harness.Core.Models;
using Microsoft.Data.Sqlite;

namespace Harness.Storage;

public sealed class HarnessStore : IAsyncDisposable
{
    private const int SchemaVersion = 2;
    private readonly string _connectionString;
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
        await _gate.WaitAsync(cancellationToken);
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

                UPDATE schema_info SET version = 2 WHERE version < 2;
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
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT value_json FROM app_settings WHERE key = 'application';";
            var json = await command.ExecuteScalarAsync(cancellationToken) as string;
            return string.IsNullOrWhiteSpace(json)
                ? new HarnessApplicationSettings()
                : JsonSerializer.Deserialize<HarnessApplicationSettings>(json)
                  ?? new HarnessApplicationSettings();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<StoredProject>> ListProjectsAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
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

    public async Task SaveApplicationSettingsAsync(
        HarnessApplicationSettings settings,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
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

        await _gate.WaitAsync(cancellationToken);
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
        await _gate.WaitAsync(cancellationToken);
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
        await _gate.WaitAsync(cancellationToken);
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
        await _gate.WaitAsync(cancellationToken);
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

    public async Task<WorkspaceSessionSnapshot> OpenWorkspaceAsync(
        string rootPath,
        CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(rootPath);
        var normalizedPath = NormalizePath(fullPath);
        var now = DateTimeOffset.UtcNow;

        await _gate.WaitAsync(cancellationToken);
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
            await transaction.CommitAsync(cancellationToken);
            return new WorkspaceSessionSnapshot(project, sessions, active, messages, attachments);
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
        await _gate.WaitAsync(cancellationToken);
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
        await _gate.WaitAsync(cancellationToken);
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

        await _gate.WaitAsync(cancellationToken);
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
        await _gate.WaitAsync(cancellationToken);
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

        await _gate.WaitAsync(cancellationToken);
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
        await _gate.WaitAsync(cancellationToken);
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
        string providerThreadId,
        string modelId,
        string? reasoningEffort,
        string? serviceTier,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
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
            command.Parameters.AddWithValue("$providerThreadId", providerThreadId);
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
        await _gate.WaitAsync(cancellationToken);
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
        await _gate.WaitAsync(cancellationToken);
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

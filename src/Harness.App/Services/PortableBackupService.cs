using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Harness.Core.Models;
using Harness.Storage;
using Harness.Workspace;

namespace Harness.App.Services;

public sealed class PortableBackupService
{
    private const string Format = "harness.portable-backup.v1";
    private const int MaximumEntries = 200_000;
    private const long MaximumExpandedBytes = 20L * 1024 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly HarnessStore? _store;
    private readonly string _databasePath;
    private readonly string _apiMetadataPath;
    private readonly string _restoreRoot;
    private readonly string _globalSkillRoot;

    public PortableBackupService(
        HarnessStore? store = null,
        string? applicationRoot = null,
        string? databasePath = null,
        string? apiMetadataPath = null,
        string? globalSkillRoot = null)
    {
        _store = store;
        _databasePath = Path.GetFullPath(databasePath ?? HarnessStore.DefaultDatabasePath);
        _apiMetadataPath = Path.GetFullPath(apiMetadataPath ?? ApiConnectionStore.MetadataPath);
        var root = Path.GetFullPath(applicationRoot ?? Path.GetDirectoryName(Path.GetDirectoryName(_databasePath)!)!);
        _restoreRoot = Path.Combine(root, "restore");
        _globalSkillRoot = Path.GetFullPath(globalSkillRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".agents", "skills"));
    }

    private string PendingArchivePath => Path.Combine(_restoreRoot, "pending.harness-backup");
    private string PendingMarkerPath => Path.Combine(_restoreRoot, "pending.json");

    public Task<PortableBackupSummary> CreateAsync(string destinationPath, CancellationToken cancellationToken = default) =>
        Task.Run(async () =>
        {
            var destination = Path.GetFullPath(destinationPath);
            if (!destination.EndsWith(".harness-backup", StringComparison.OrdinalIgnoreCase))
                destination += ".harness-backup";
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var work = Path.Combine(Path.GetTempPath(), "Harness.Backup", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            try
            {
                var store = _store ?? throw new InvalidOperationException("An open Harness store is required to create a backup.");
                var snapshot = Path.Combine(work, "harness.db");
                await store.CreatePortableSnapshotAsync(snapshot, cancellationToken);
                var payloads = new List<BackupSource> { new(snapshot, "data/harness.db") };
                var dataDirectory = Path.GetDirectoryName(store.DatabasePath)!;
                AddManagedTree(payloads, dataDirectory, "attachments", cancellationToken);
                AddManagedTree(payloads, dataDirectory, "imports", cancellationToken);

                var apiMetadata = _apiMetadataPath;
                if (!File.Exists(apiMetadata))
                {
                    apiMetadata = Path.Combine(work, "api-connections.json");
                    await File.WriteAllTextAsync(apiMetadata, "[]", cancellationToken);
                }
                payloads.Add(new BackupSource(apiMetadata, "api-connections.json"));

                var installations = new List<PortableSkillInstallation>();
                foreach (var installed in await store.ListInstalledSkillsAsync(cancellationToken))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!Directory.Exists(installed.InstallPath)) continue;
                    ValidateSkillIdentity(installed.Id, Path.GetFileName(installed.InstallPath));
                    AddTree(payloads, installed.InstallPath, $"skills/installed/{installed.Id}", cancellationToken);
                    installations.Add(new PortableSkillInstallation(
                        installed.Id,
                        installed.CatalogId,
                        installed.Name,
                        installed.SourceRevision,
                        Path.GetFileName(installed.InstallPath),
                        installed.Scope,
                        installed.WorkspacePath,
                        installed.ProviderId,
                        installed.ModelId,
                        installed.ContentSha256,
                        installed.Enabled,
                        installed.InstalledAt));
                }
                var installationsPath = Path.Combine(work, "installations.json");
                await File.WriteAllTextAsync(installationsPath, JsonSerializer.Serialize(installations, JsonOptions), cancellationToken);
                payloads.Add(new BackupSource(installationsPath, "skills/installations.json"));

                var staged = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    var summary = WriteArchive(staged, payloads, cancellationToken);
                    File.Move(staged, destination, true);
                    return summary with { ArchivePath = destination, ArchiveBytes = new FileInfo(destination).Length };
                }
                finally
                {
                    TryDeleteFile(staged);
                }
            }
            finally
            {
                TryDeleteDirectory(work);
            }
        }, cancellationToken);

    public Task<PortableBackupSummary> StageRestoreAsync(
        string archivePath,
        CancellationToken cancellationToken = default) => Task.Run(() =>
        {
            var source = Path.GetFullPath(archivePath);
            var inspectionRoot = Path.Combine(Path.GetTempPath(), "Harness.RestoreInspection", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(inspectionRoot);
            try
            {
                var summary = ExtractValidated(source, inspectionRoot, cancellationToken);
                Directory.CreateDirectory(_restoreRoot);
                var staging = PendingArchivePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    File.Copy(source, staging, true);
                    File.Move(staging, PendingArchivePath, true);
                    WriteAtomic(PendingMarkerPath, JsonSerializer.Serialize(new PendingRestore(
                        summary.CreatedAtUtc,
                        DateTimeOffset.UtcNow,
                        Path.GetFileName(PendingArchivePath)), JsonOptions));
                }
                finally
                {
                    TryDeleteFile(staging);
                }
                return summary with { ArchivePath = source, ArchiveBytes = new FileInfo(source).Length };
            }
            finally
            {
                TryDeleteDirectory(inspectionRoot);
            }
        }, cancellationToken);

    public Task<PortableRestoreResult?> ApplyPendingRestoreAsync(
        CancellationToken cancellationToken = default) => Task.Run(async () =>
        {
            if (!File.Exists(PendingMarkerPath)) return null;
            if (!File.Exists(PendingArchivePath))
                throw new IOException("A restore request exists, but its staged backup archive is missing.");

            var extractionRoot = Path.Combine(_restoreRoot, "apply-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(extractionRoot);
            try
            {
                var summary = ExtractValidated(PendingArchivePath, extractionRoot, cancellationToken);
                var restoredDatabase = Path.Combine(extractionRoot, "data", "harness.db");
                var targetDatabase = _databasePath;
                var targetData = Path.GetDirectoryName(targetDatabase)!;
                await HarnessStore.RebasePortableSnapshotAsync(
                    restoredDatabase,
                    targetData,
                    Path.Combine(extractionRoot, "data"),
                    cancellationToken);
                await RestoreSkillsAsync(restoredDatabase, extractionRoot, targetData, cancellationToken);

                CopyPayloadTree(Path.Combine(extractionRoot, "data", "attachments"), Path.Combine(targetData, "attachments"), cancellationToken);
                CopyPayloadTree(Path.Combine(extractionRoot, "data", "imports"), Path.Combine(targetData, "imports"), cancellationToken);
                Directory.CreateDirectory(targetData);
                if (File.Exists(targetDatabase)) File.Copy(targetDatabase, targetDatabase + ".before-restore", true);
                var apiMetadataExisted = File.Exists(_apiMetadataPath);
                var apiMetadataRollback = _apiMetadataPath + ".before-restore";
                if (apiMetadataExisted) File.Copy(_apiMetadataPath, apiMetadataRollback, true);
                var walPath = targetDatabase + "-wal";
                var shmPath = targetDatabase + "-shm";
                var walRollback = walPath + ".before-restore";
                var shmRollback = shmPath + ".before-restore";
                try
                {
                    MoveAside(walPath, walRollback);
                    MoveAside(shmPath, shmRollback);
                    ReplaceFile(Path.Combine(extractionRoot, "api-connections.json"), _apiMetadataPath);
                    ReplaceFile(restoredDatabase, targetDatabase);
                }
                catch
                {
                    if (apiMetadataExisted) ReplaceFile(apiMetadataRollback, _apiMetadataPath);
                    else TryDeleteFile(_apiMetadataPath);
                    RestoreAside(walRollback, walPath);
                    RestoreAside(shmRollback, shmPath);
                    throw;
                }

                TryDeleteFile(PendingMarkerPath);
                TryDeleteFile(PendingArchivePath);
                return new PortableRestoreResult(summary.CreatedAtUtc, summary.PayloadCount, summary.PayloadBytes);
            }
            finally
            {
                TryDeleteDirectory(extractionRoot);
            }
        }, cancellationToken);

    public bool HasPendingRestore => File.Exists(PendingMarkerPath);

    private static PortableBackupSummary WriteArchive(
        string archivePath,
        IReadOnlyList<BackupSource> sources,
        CancellationToken cancellationToken)
    {
        var duplicate = sources.GroupBy(source => source.ArchivePath, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (sources.Count > MaximumEntries - 1) throw new InvalidOperationException("Backup exceeds the supported file count.");
        if (duplicate is not null) throw new InvalidOperationException($"Backup payload path is duplicated: {duplicate.Key}");
        var records = new List<PortableBackupPayload>(sources.Count);
        long completedBytes = 0;
        using var output = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024);
        using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: false);
        foreach (var source in sources.OrderBy(source => source.ArchivePath, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = archive.CreateEntry(source.ArchivePath, CompressionLevel.Optimal);
            using var entryStream = entry.Open();
            using var input = new FileStream(source.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.SequentialScan);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[128 * 1024];
            long length = 0;
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                entryStream.Write(buffer, 0, read);
                hash.AppendData(buffer, 0, read);
                length += read;
                if (completedBytes + length > MaximumExpandedBytes)
                    throw new InvalidOperationException("Backup exceeds the 20 GiB expanded-size limit.");
            }
            records.Add(new PortableBackupPayload(source.ArchivePath, length, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()));
            completedBytes += length;
        }

        var manifest = new PortableBackupManifest(
            Format,
            DateTimeOffset.UtcNow,
            ProductVersion(),
            HarnessStore.CurrentSchemaVersion,
            records,
            ["API and provider credentials", "Subscription and GitHub sign-ins", "Browser cookies and profiles", "Workspace source trees", "Managed runtimes", "Diagnostics"]);
        var manifestEntry = archive.CreateEntry("manifest.json", CompressionLevel.Optimal);
        using var manifestStream = manifestEntry.Open();
        JsonSerializer.Serialize(manifestStream, manifest, JsonOptions);
        return new PortableBackupSummary("", manifest.CreatedAtUtc, records.Count, records.Sum(record => record.Bytes), 0);
    }

    private static PortableBackupSummary ExtractValidated(
        string archivePath,
        string destinationRoot,
        CancellationToken cancellationToken)
    {
        using var file = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.SequentialScan);
        using var archive = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: false);
        if (archive.Entries.Count is 0 or > MaximumEntries)
            throw new InvalidDataException("The backup archive has an invalid number of entries.");
        var manifestEntries = archive.Entries.Where(entry => entry.FullName.Equals("manifest.json", StringComparison.Ordinal)).ToArray();
        if (manifestEntries.Length != 1 || manifestEntries[0].Length > 1024 * 1024)
            throw new InvalidDataException("The backup archive has no valid manifest.");
        PortableBackupManifest manifest;
        using (var stream = manifestEntries[0].Open())
            manifest = JsonSerializer.Deserialize<PortableBackupManifest>(stream)
                       ?? throw new InvalidDataException("The backup manifest is invalid.");
        if (manifest.Format != Format)
            throw new InvalidDataException($"Unsupported backup format: {manifest.Format}");
        if (manifest.DatabaseSchema > HarnessStore.CurrentSchemaVersion)
            throw new InvalidDataException($"This backup requires a newer Harness database schema ({manifest.DatabaseSchema}).");
        if (manifest.Payloads is null
            || manifest.Payloads.Any(payload => string.IsNullOrWhiteSpace(payload.Path)
                                                || string.IsNullOrWhiteSpace(payload.Sha256)
                                                || payload.Sha256.Length != 64))
            throw new InvalidDataException("The backup manifest contains an invalid payload record.");
        if (manifest.Payloads.Count + 1 != archive.Entries.Count || manifest.Payloads.Count > MaximumEntries - 1)
            throw new InvalidDataException("The backup manifest does not match the archive contents.");
        var expected = manifest.Payloads.ToDictionary(payload => payload.Path, StringComparer.OrdinalIgnoreCase);
        if (!expected.ContainsKey("data/harness.db")
            || !expected.ContainsKey("api-connections.json")
            || !expected.ContainsKey("skills/installations.json"))
            throw new InvalidDataException("The backup is missing required Harness data.");
        if (manifest.Payloads.Any(payload => payload.Bytes < 0)
            || manifest.Payloads.Sum(payload => payload.Bytes) > MaximumExpandedBytes)
            throw new InvalidDataException("The backup exceeds the expanded-size safety limit.");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries.Where(entry => !entry.FullName.Equals("manifest.json", StringComparison.Ordinal)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateArchivePath(entry.FullName);
            if (!seen.Add(entry.FullName) || !expected.TryGetValue(entry.FullName, out var payload))
                throw new InvalidDataException("The backup contains an unexpected or duplicate payload.");
            if (entry.Length != payload.Bytes)
                throw new InvalidDataException($"Backup payload size does not match its manifest: {entry.FullName}");
            var target = Path.GetFullPath(Path.Combine(destinationRoot, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
            var root = Path.GetFullPath(destinationRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("A backup payload points outside the restore staging directory.");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var input = entry.Open();
            using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[128 * 1024];
            long length = 0;
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                output.Write(buffer, 0, read);
                hash.AppendData(buffer, 0, read);
                length += read;
                if (length > payload.Bytes) throw new InvalidDataException("A backup payload expanded past its declared size.");
            }
            var actualHash = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (length != payload.Bytes || !actualHash.Equals(payload.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Backup payload integrity check failed: {entry.FullName}");
        }
        if (seen.Count != expected.Count) throw new InvalidDataException("One or more backup payloads are missing.");
        var apiMetadata = Path.Combine(destinationRoot, "api-connections.json");
        if (new FileInfo(apiMetadata).Length > 8 * 1024 * 1024)
            throw new InvalidDataException("API connection metadata exceeds the 8 MiB safety limit.");
        try
        {
            using var apiDocument = JsonDocument.Parse(File.ReadAllText(apiMetadata));
            if (apiDocument.RootElement.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("API connection metadata is not a JSON array.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("API connection metadata is invalid JSON.", exception);
        }
        var skillMetadata = Path.Combine(destinationRoot, "skills", "installations.json");
        if (new FileInfo(skillMetadata).Length > 8 * 1024 * 1024)
            throw new InvalidDataException("Installed-skill metadata exceeds the 8 MiB safety limit.");
        try
        {
            _ = JsonSerializer.Deserialize<List<PortableSkillInstallation>>(File.ReadAllText(skillMetadata))
                ?? throw new InvalidDataException("Installed-skill metadata is invalid.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Installed-skill metadata is invalid JSON.", exception);
        }
        return new PortableBackupSummary(archivePath, manifest.CreatedAtUtc, manifest.Payloads.Count, manifest.Payloads.Sum(item => item.Bytes), new FileInfo(archivePath).Length);
    }

    private static void AddManagedTree(
        List<BackupSource> payloads,
        string dataDirectory,
        string name,
        CancellationToken cancellationToken)
    {
        var sourceRoot = Path.Combine(dataDirectory, name);
        if (!Directory.Exists(sourceRoot)) return;
        foreach (var source in EnumerateSafeFiles(sourceRoot, cancellationToken))
        {
            var relative = Path.GetRelativePath(dataDirectory, source).Replace('\\', '/');
            payloads.Add(new BackupSource(source, "data/" + relative));
        }
    }

    private static void AddTree(
        List<BackupSource> payloads,
        string sourceRoot,
        string archiveRoot,
        CancellationToken cancellationToken)
    {
        foreach (var source in EnumerateSafeFiles(sourceRoot, cancellationToken))
        {
            var relative = Path.GetRelativePath(sourceRoot, source).Replace('\\', '/');
            payloads.Add(new BackupSource(source, $"{archiveRoot}/{relative}"));
        }
    }

    private static IEnumerable<string> EnumerateSafeFiles(string sourceRoot, CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(sourceRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var pending = new Stack<string>();
        pending.Push(Path.GetFullPath(sourceRoot));
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fullPath = Path.GetFullPath(file);
                if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                    || (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Harness will not follow linked files while creating a backup.");
                yield return fullPath;
            }
            foreach (var child in Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly))
            {
                var fullPath = Path.GetFullPath(child);
                if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                    || (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Harness will not follow linked directories while creating a backup.");
                pending.Push(fullPath);
            }
        }
    }

    private static void ValidateArchivePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\\') || Path.IsPathRooted(path)
            || path.Split('/').Any(part => part is "" or "." or ".."))
            throw new InvalidDataException("The backup contains an unsafe payload path.");
        if (path.Equals("data/harness.db", StringComparison.OrdinalIgnoreCase)
            || path.Equals("api-connections.json", StringComparison.OrdinalIgnoreCase)
            || path.Equals("skills/installations.json", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("skills/installed/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("data/attachments/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("data/imports/", StringComparison.OrdinalIgnoreCase)) return;
        throw new InvalidDataException($"The backup contains an unsupported payload: {path}");
    }

    private static void CopyPayloadTree(string sourceRoot, string targetRoot, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(sourceRoot)) return;
        foreach (var source in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = Path.Combine(targetRoot, Path.GetRelativePath(sourceRoot, source));
            ReplaceFile(source, target);
        }
    }

    private async Task RestoreSkillsAsync(
        string restoredDatabase,
        string extractionRoot,
        string targetData,
        CancellationToken cancellationToken)
    {
        var metadataPath = Path.Combine(extractionRoot, "skills", "installations.json");
        var installations = JsonSerializer.Deserialize<List<PortableSkillInstallation>>(
            await File.ReadAllTextAsync(metadataPath, cancellationToken)) ?? [];
        var indexRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var restoredStore = new HarnessStore(restoredDatabase);
        await restoredStore.InitializeAsync(cancellationToken);
        foreach (var item in installations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateSkillIdentity(item.Id, item.FolderName);
            var source = Path.Combine(extractionRoot, "skills", "installed", item.Id);
            if (!Directory.Exists(source)) throw new InvalidDataException($"Installed skill payload is missing: {item.Id}");
            var packagePath = Path.Combine(Path.GetDirectoryName(targetData)!, "skills", "restored-packages", item.Id, item.FolderName);
            CopyPayloadTree(source, packagePath, cancellationToken);

            var isWorkspace = item.Scope.Equals("WORKSPACE", StringComparison.OrdinalIgnoreCase);
            // Never let paths embedded in an archive write into a workspace during startup.
            // Workspace skills stay managed and disabled until the user selects or relinks that project.
            var workspaceAvailable = !isWorkspace;
            var installRoot = !isWorkspace ? _globalSkillRoot : Path.Combine(targetData, "deferred-skills", item.Id);
            var installPath = Path.Combine(installRoot, item.FolderName);
            var enabled = item.Enabled && workspaceAvailable;
            if (enabled)
            {
                if (Directory.Exists(installPath) && !HasMatchingSkillMarker(installPath, item.CatalogId))
                    installPath = RestoredCollisionPath(installRoot, item.FolderName, item.Id);
                if (!Directory.Exists(installPath)) CopyPayloadTree(source, installPath, cancellationToken);
                indexRoots.Add(installRoot);
            }
            else
            {
                CopyPayloadTree(source, installPath, cancellationToken);
            }

            await restoredStore.SaveInstalledSkillAsync(new InstalledSkill(
                item.Id, item.CatalogId, item.Name, item.SourceRevision,
                packagePath, installPath, item.Scope, item.WorkspacePath,
                item.ProviderId, item.ModelId, item.ContentSha256, enabled, item.InstalledAt),
                cancellationToken);
        }
        foreach (var indexRoot in indexRoots) await SkillPackageInstaller.RebuildProviderIndexAsync(indexRoot, cancellationToken);
    }

    public async Task<int> ActivateDeferredWorkspaceSkillsAsync(
        HarnessStore store,
        string previousWorkspacePath,
        string currentWorkspacePath,
        CancellationToken cancellationToken = default)
    {
        var deferred = (await store.ListInstalledSkillsAsync(cancellationToken))
            .Where(item => !item.Enabled
                           && item.Scope.Equals("WORKSPACE", StringComparison.OrdinalIgnoreCase)
                           && string.Equals(item.WorkspacePath, previousWorkspacePath, StringComparison.OrdinalIgnoreCase)
                           && Directory.Exists(item.InstallPath))
            .ToArray();
        if (deferred.Length == 0) return 0;
        var installRoot = Path.Combine(Path.GetFullPath(currentWorkspacePath), ".agents", "skills");
        foreach (var item in deferred)
        {
            var folderName = Path.GetFileName(item.InstallPath);
            ValidateSkillIdentity(item.Id, folderName);
            var destination = Path.Combine(installRoot, folderName);
            if (Directory.Exists(destination) && !HasMatchingSkillMarker(destination, item.CatalogId))
                destination = RestoredCollisionPath(installRoot, folderName, item.Id);
            if (!Directory.Exists(destination)) CopyPayloadTree(item.InstallPath, destination, cancellationToken);
            await store.SaveInstalledSkillAsync(item with
            {
                InstallPath = destination,
                WorkspacePath = Path.GetFullPath(currentWorkspacePath),
                Enabled = true
            }, cancellationToken);
        }
        await SkillPackageInstaller.RebuildProviderIndexAsync(installRoot, cancellationToken);
        return deferred.Length;
    }

    private static void ValidateSkillIdentity(string id, string folderName)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 128 || id.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
            throw new InvalidDataException("Installed-skill metadata contains an unsafe identifier.");
        if (string.IsNullOrWhiteSpace(folderName) || folderName is "." or ".." || folderName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidDataException("Installed-skill metadata contains an unsafe folder name.");
    }

    private static bool HasMatchingSkillMarker(string directory, string catalogId)
    {
        try
        {
            var marker = Path.Combine(directory, ".harness-source.json");
            if (!File.Exists(marker) || new FileInfo(marker).Length > 1024 * 1024) return false;
            using var document = JsonDocument.Parse(File.ReadAllText(marker));
            return document.RootElement.TryGetProperty("catalogId", out var value)
                   && string.Equals(value.GetString(), catalogId, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static string RestoredCollisionPath(string root, string folderName, string id)
    {
        var suffix = "--restored-" + id[..Math.Min(8, id.Length)].ToLowerInvariant();
        var maximumName = Math.Max(8, 96 - suffix.Length);
        var baseName = folderName.Length > maximumName ? folderName[..maximumName] : folderName;
        return Path.Combine(root, baseName + suffix);
    }

    private static void ReplaceFile(string source, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var staging = destination + "." + Guid.NewGuid().ToString("N") + ".restore";
        try
        {
            File.Copy(source, staging, true);
            File.Move(staging, destination, true);
        }
        finally
        {
            TryDeleteFile(staging);
        }
    }

    private static void MoveAside(string source, string rollback)
    {
        TryDeleteFile(rollback);
        if (File.Exists(source)) File.Move(source, rollback, true);
    }

    private static void RestoreAside(string rollback, string destination)
    {
        if (File.Exists(rollback)) File.Move(rollback, destination, true);
    }

    private static void WriteAtomic(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var staging = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(staging, content);
            File.Move(staging, path, true);
        }
        finally
        {
            TryDeleteFile(staging);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); } catch { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { }
    }

    private static string ProductVersion() =>
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? Assembly.GetEntryAssembly()?.GetName().Version?.ToString()
        ?? "unknown";

    private sealed record BackupSource(string SourcePath, string ArchivePath);
    private sealed record PendingRestore(DateTimeOffset BackupCreatedAtUtc, DateTimeOffset RequestedAtUtc, string ArchiveFileName);
}

public sealed record PortableBackupManifest(
    string Format,
    DateTimeOffset CreatedAtUtc,
    string ProductVersion,
    int DatabaseSchema,
    IReadOnlyList<PortableBackupPayload> Payloads,
    IReadOnlyList<string> Excluded);

public sealed record PortableBackupPayload(string Path, long Bytes, string Sha256);

public sealed record PortableBackupSummary(
    string ArchivePath,
    DateTimeOffset CreatedAtUtc,
    int PayloadCount,
    long PayloadBytes,
    long ArchiveBytes)
{
    public string DisplaySize => FormatBytes(ArchiveBytes);
    public string PayloadDisplaySize => FormatBytes(PayloadBytes);

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / (1024d * 1024 * 1024):0.##} GiB",
        >= 1024L * 1024 => $"{bytes / (1024d * 1024):0.##} MiB",
        >= 1024L => $"{bytes / 1024d:0.##} KiB",
        _ => $"{bytes} B"
    };
}

public sealed record PortableRestoreResult(DateTimeOffset BackupCreatedAtUtc, int PayloadCount, long PayloadBytes);

public sealed record PortableSkillInstallation(
    string Id,
    string CatalogId,
    string Name,
    string SourceRevision,
    string FolderName,
    string Scope,
    string? WorkspacePath,
    string ProviderId,
    string? ModelId,
    string ContentSha256,
    bool Enabled,
    DateTimeOffset InstalledAt);

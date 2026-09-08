using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Harness.Core.Models;

namespace Harness.Workspace;

public static class SkillPackageInstaller
{
    public static string DefaultPackageRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Harness", "skills", "packages");

    public static string DefaultActiveRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Harness", "skills", "active");

    public static async Task<string> InstallCodexAsync(
        DownloadedSkillPackage package,
        SkillCatalogEntry skill,
        string scope,
        string workspacePath,
        CancellationToken cancellationToken = default)
    {
        var destinationRoot = scope.Equals("WORKSPACE", StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(Path.GetFullPath(workspacePath), ".agents", "skills")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".agents", "skills");
        return await InstallAsync(package, skill, destinationRoot, cancellationToken);
    }

    public static async Task<string> InstallHarnessApiAsync(
        DownloadedSkillPackage package,
        SkillCatalogEntry skill,
        string providerId,
        string scope,
        string workspacePath,
        string? modelId = null,
        CancellationToken cancellationToken = default)
    {
        var destinationRoot = GetHarnessApiDestinationRoot(providerId, scope, workspacePath, modelId: modelId);
        return await InstallAsync(package, skill, destinationRoot, cancellationToken);
    }

    public static string GetHarnessApiDestinationRoot(
        string providerId,
        string scope,
        string workspacePath,
        string? activeRoot = null,
        string? modelId = null)
    {
        if (string.IsNullOrWhiteSpace(providerId)) throw new ArgumentException("A provider connection is required.", nameof(providerId));
        var providerKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(providerId))).ToLowerInvariant()[..16];
        var scopeKey = scope.Equals("WORKSPACE", StringComparison.OrdinalIgnoreCase)
            ? "workspace-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(workspacePath)))).ToLowerInvariant()[..16]
            : "user";
        var modelKey = string.IsNullOrWhiteSpace(modelId)
            ? "all-models"
            : "model-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(modelId))).ToLowerInvariant()[..16];
        return Path.Combine(Path.GetFullPath(activeRoot ?? DefaultActiveRoot), providerKey, scopeKey, modelKey);
    }

    private static async Task<string> InstallAsync(
        DownloadedSkillPackage package,
        SkillCatalogEntry skill,
        string destinationRoot,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destinationRoot);
        var installedName = CreateInstalledSkillName(skill);
        var destination = Path.Combine(destinationRoot, installedName);
        if (Directory.Exists(destination))
            throw new InvalidOperationException($"A skill already exists at {destination}. Remove or update it explicitly before replacing it.");

        var pending = Path.Combine(destinationRoot, $".pending-{Guid.NewGuid():N}");
        var moved = false;
        try
        {
            await PrepareManagedCopyAsync(package, skill, installedName, pending, cancellationToken);
            Directory.Move(pending, destination);
            moved = true;
            await RebuildProviderIndexAsync(destinationRoot, cancellationToken);
            return destination;
        }
        catch
        {
            if (Directory.Exists(pending)) Directory.Delete(pending, recursive: true);
            if (moved && Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
            await TryRebuildProviderIndexAsync(destinationRoot, cancellationToken);
            throw;
        }
    }

    public static async Task<ManagedSkillIntegrity> InspectManagedCopyAsync(
        InstalledSkill installed,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var markerPath = Path.Combine(Path.GetFullPath(installed.InstallPath), ".harness-source.json");
            if (!File.Exists(markerPath) || new FileInfo(markerPath).Length > 1024 * 1024)
                return ManagedSkillIntegrity.Unknown;
            using var marker = JsonDocument.Parse(await File.ReadAllTextAsync(markerPath, cancellationToken));
            if (!marker.RootElement.TryGetProperty("installedContentSha256", out var expected)
                || string.IsNullOrWhiteSpace(expected.GetString()))
                return ManagedSkillIntegrity.Unknown;
            var actual = await ComputeManagedContentHashAsync(installed.InstallPath, cancellationToken);
            return string.Equals(actual, expected.GetString(), StringComparison.OrdinalIgnoreCase)
                ? ManagedSkillIntegrity.Unchanged
                : ManagedSkillIntegrity.Modified;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or JsonException)
        {
            return ManagedSkillIntegrity.Unknown;
        }
    }

    public static async Task<ManagedSkillUpdateResult> UpdateAsync(
        InstalledSkill installed,
        DownloadedSkillPackage package,
        SkillCatalogEntry skill,
        CancellationToken cancellationToken = default)
    {
        var destination = Path.GetFullPath(installed.InstallPath);
        ValidateManagedInstall(destination, installed.CatalogId);
        var parent = Path.GetDirectoryName(destination)!;
        var pending = Path.Combine(parent, $".pending-update-{Guid.NewGuid():N}");
        var backup = Path.Combine(parent, $".replaced-{Guid.NewGuid():N}");
        var installedName = Path.GetFileName(destination);
        var oldMoved = false;
        var newMoved = false;
        try
        {
            await PrepareManagedCopyAsync(package, skill, installedName, pending, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(destination, backup);
            oldMoved = true;
            Directory.Move(pending, destination);
            newMoved = true;
            if (installed.Enabled) await RebuildProviderIndexAsync(parent, cancellationToken);
            return new ManagedSkillUpdateResult(destination, backup);
        }
        catch
        {
            if (newMoved && Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
            if (oldMoved && Directory.Exists(backup) && !Directory.Exists(destination)) Directory.Move(backup, destination);
            if (Directory.Exists(pending)) Directory.Delete(pending, recursive: true);
            await TryRebuildProviderIndexAsync(parent, cancellationToken);
            throw;
        }
    }

    public static void CommitUpdate(ManagedSkillUpdateResult update)
    {
        try { if (Directory.Exists(update.BackupPath)) Directory.Delete(update.BackupPath, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public static async Task RollbackUpdateAsync(
        ManagedSkillUpdateResult update,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(update.BackupPath)) return;
        if (Directory.Exists(update.InstallPath)) Directory.Delete(update.InstallPath, recursive: true);
        Directory.Move(update.BackupPath, update.InstallPath);
        if (enabled) await RebuildProviderIndexAsync(Path.GetDirectoryName(update.InstallPath)!, cancellationToken);
    }

    public static async Task<InstalledSkill> SetEnabledAsync(
        InstalledSkill installed,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        if (installed.Enabled == enabled) return installed;
        var source = Path.GetFullPath(installed.InstallPath);
        ValidateManagedInstall(source, installed.CatalogId);
        var sourceRoot = Path.GetDirectoryName(source)!;
        string destinationRoot;
        if (enabled)
        {
            destinationRoot = ActiveDestinationRoot(installed);
        }
        else
        {
            destinationRoot = GetDisabledDestinationRoot(sourceRoot, installed.Id);
        }
        Directory.CreateDirectory(destinationRoot);
        var destination = Path.Combine(destinationRoot, Path.GetFileName(source));
        if (Directory.Exists(destination))
            throw new InvalidOperationException($"A managed skill already exists at {destination}; Harness did not replace it.");
        cancellationToken.ThrowIfCancellationRequested();
        Directory.Move(source, destination);
        try
        {
            if (installed.Enabled) await RebuildProviderIndexAsync(sourceRoot, cancellationToken);
            if (enabled) await RebuildProviderIndexAsync(destinationRoot, cancellationToken);
            return installed with { InstallPath = destination, Enabled = enabled };
        }
        catch
        {
            if (Directory.Exists(destination) && !Directory.Exists(source)) Directory.Move(destination, source);
            await TryRebuildProviderIndexAsync(sourceRoot, cancellationToken);
            if (enabled) await TryRebuildProviderIndexAsync(destinationRoot, cancellationToken);
            throw;
        }
    }

    public static string GetDisabledDestinationRoot(string activeRoot, string installationId) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(activeRoot))!, ".harness-disabled-skills", installationId);

    public static async Task<string> RemoveRecoverablyAsync(
        InstalledSkill installed,
        CancellationToken cancellationToken = default)
    {
        var source = Path.GetFullPath(installed.InstallPath);
        ValidateManagedInstall(source, installed.CatalogId);
        var sourceRoot = Path.GetDirectoryName(source)!;
        var recoveryRoot = Path.Combine(Path.GetDirectoryName(sourceRoot)!, ".harness-removed-skills",
            $"{installed.Id}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}");
        Directory.CreateDirectory(recoveryRoot);
        var destination = Path.Combine(recoveryRoot, Path.GetFileName(source));
        cancellationToken.ThrowIfCancellationRequested();
        Directory.Move(source, destination);
        try
        {
            if (installed.Enabled) await RebuildProviderIndexAsync(sourceRoot, cancellationToken);
            return destination;
        }
        catch
        {
            if (Directory.Exists(destination) && !Directory.Exists(source)) Directory.Move(destination, source);
            await TryRebuildProviderIndexAsync(sourceRoot, cancellationToken);
            throw;
        }
    }

    public static async Task RestoreRemovedAsync(
        string recoveryPath,
        InstalledSkill installed,
        CancellationToken cancellationToken = default)
    {
        ValidateManagedInstall(recoveryPath, installed.CatalogId);
        var destination = Path.GetFullPath(installed.InstallPath);
        if (Directory.Exists(destination))
            throw new InvalidOperationException("The removed skill could not be restored because its original path is occupied.");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        Directory.Move(recoveryPath, destination);
        if (installed.Enabled) await RebuildProviderIndexAsync(Path.GetDirectoryName(destination)!, cancellationToken);
    }

    private static string ActiveDestinationRoot(InstalledSkill installed)
    {
        if (installed.ProviderId.Equals("openai-codex", StringComparison.OrdinalIgnoreCase))
        {
            if (installed.Scope.Equals("WORKSPACE", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(installed.WorkspacePath))
                    throw new InvalidOperationException("This workspace skill is not linked to a workspace.");
                return Path.Combine(Path.GetFullPath(installed.WorkspacePath), ".agents", "skills");
            }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".agents", "skills");
        }
        return GetHarnessApiDestinationRoot(
            installed.ProviderId,
            installed.Scope,
            installed.WorkspacePath ?? Environment.CurrentDirectory,
            modelId: installed.ModelId);
    }

    private static async Task TryRebuildProviderIndexAsync(string root, CancellationToken cancellationToken)
    {
        try { if (Directory.Exists(root)) await RebuildProviderIndexAsync(root, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void ValidateManagedInstall(string directory, string catalogId)
    {
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("The provider-facing skill copy is missing.");
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new UnauthorizedAccessException("Harness will not manage a skill through a symbolic link or junction.");
        var markerPath = Path.Combine(directory, ".harness-source.json");
        if (!File.Exists(markerPath) || new FileInfo(markerPath).Length > 1024 * 1024)
            throw new InvalidOperationException("The selected folder is not a Harness-managed skill installation.");
        using var marker = JsonDocument.Parse(File.ReadAllText(markerPath));
        if (!marker.RootElement.TryGetProperty("catalogId", out var id)
            || !string.Equals(id.GetString(), catalogId, StringComparison.Ordinal))
            throw new InvalidOperationException("The installed skill provenance does not match its database record.");
    }

    private static async Task PrepareManagedCopyAsync(
        DownloadedSkillPackage package,
        SkillCatalogEntry skill,
        string installedName,
        string pending,
        CancellationToken cancellationToken)
    {
        await CopyDirectoryAsync(package.PackagePath, pending, cancellationToken);
        await AdaptManifestIdentityAsync(pending, skill, installedName, cancellationToken);
        var installedContentSha256 = await ComputeManagedContentHashAsync(pending, cancellationToken);
        var marker = JsonSerializer.Serialize(new
        {
            catalogId = skill.Id,
            originalName = skill.Name,
            description = skill.Description,
            installedName,
            skill.Repository,
            skill.SkillPath,
            skill.SourceRevision,
            package.ContentSha256,
            installedContentSha256,
            installedAt = DateTimeOffset.UtcNow
        });
        await File.WriteAllTextAsync(Path.Combine(pending, ".harness-source.json"), marker, Encoding.UTF8, cancellationToken);
    }

    private static async Task<string> ComputeManagedContentHashAsync(
        string directory,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        foreach (var file in EnumerateFilesSafely(root)
                     .Where(file => !Path.GetFileName(file).StartsWith(".harness-", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(file => Path.GetRelativePath(root, file), StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                throw new UnauthorizedAccessException("A managed skill contains a symbolic link.");
            var relative = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
            hash.AppendData(Encoding.UTF8.GetBytes(relative));
            hash.AppendData([0]);
            await using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read,
                81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var buffer = new byte[81920];
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0) hash.AppendData(buffer, 0, read);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    public static string CreateInstalledSkillName(SkillCatalogEntry skill)
    {
        var source = SkillManifestParser.Slug(skill.Repository.Replace('/', '-'));
        if (source.Length > 32) source = source[..32].TrimEnd('-');
        var safeId = SkillManifestParser.Slug(skill.Id);
        if (string.IsNullOrWhiteSpace(safeId))
            safeId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{skill.Repository}\0{skill.SkillPath}"))).ToLowerInvariant();
        var suffix = safeId[..Math.Min(8, safeId.Length)].ToLowerInvariant();
        var tail = $"--{source}-{suffix}";
        var name = SkillManifestParser.Slug(skill.Name);
        var maxNameLength = Math.Max(8, 64 - tail.Length);
        if (name.Length > maxNameLength) name = name[..maxNameLength].TrimEnd('-');
        return $"{name}{tail}";
    }

    public static string CreateInstallId(string catalogId, string providerId, string scope, string? workspacePath, string? modelId = null)
    {
        var identity = $"{catalogId}\0{providerId}\0{scope}\0{workspacePath ?? string.Empty}";
        if (!string.IsNullOrWhiteSpace(modelId)) identity += $"\0{modelId}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(
            identity));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static async Task CopyDirectoryAsync(string source, string destination, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in EnumerateFilesSafely(source))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Path.GetFileName(file).Equals(".harness-package.json", StringComparison.OrdinalIgnoreCase)) continue;
            var relative = Path.GetRelativePath(source, file);
            var target = Path.GetFullPath(Path.Combine(destination, relative));
            if (!target.StartsWith(Path.GetFullPath(destination) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The skill package contains a path outside its root.");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
            await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
            await input.CopyToAsync(output, cancellationToken);
        }
    }

    private static IReadOnlyList<string> EnumerateFilesSafely(string root)
    {
        var files = new List<string>();
        var pending = new Stack<string>();
        pending.Push(Path.GetFullPath(root));
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new UnauthorizedAccessException("Skill packages cannot contain symbolic links or junctions.");
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new UnauthorizedAccessException("Skill packages cannot contain symbolic links or junctions.");
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
                else files.Add(entry);
            }
        }
        return files;
    }

    private static async Task AdaptManifestIdentityAsync(
        string packagePath,
        SkillCatalogEntry skill,
        string installedName,
        CancellationToken cancellationToken)
    {
        var manifestPath = Path.Combine(packagePath, "SKILL.md");
        if (!File.Exists(manifestPath))
            throw new InvalidOperationException("The installed package no longer contains SKILL.md.");
        var markdown = await File.ReadAllTextAsync(manifestPath, cancellationToken);
        var lines = markdown.Replace("\r\n", "\n").Split('\n').ToList();
        if (lines.Count > 0 && lines[0].Trim() == "---")
        {
            var closing = lines.FindIndex(1, line => line.Trim() == "---");
            if (closing < 0) throw new InvalidOperationException("The skill has unterminated YAML frontmatter.");
            var nameLine = lines.FindIndex(1, closing - 1, line => line.TrimStart().StartsWith("name:", StringComparison.OrdinalIgnoreCase));
            if (nameLine >= 0) lines[nameLine] = $"name: {installedName}";
            else lines.Insert(1, $"name: {installedName}");
        }
        else
        {
            lines.InsertRange(0,
            [
                "---",
                $"name: {installedName}",
                $"description: {EscapeYamlScalar(skill.Description)}",
                "---",
                ""
            ]);
        }
        await File.WriteAllTextAsync(manifestPath, string.Join('\n', lines), new UTF8Encoding(false), cancellationToken);
    }

    public static async Task RebuildProviderIndexAsync(string destinationRoot, CancellationToken cancellationToken = default)
    {
        var records = new List<object>();
        foreach (var markerPath in Directory.EnumerateDirectories(destinationRoot)
                     .Select(directory => Path.Combine(directory, ".harness-source.json"))
                     .Where(File.Exists))
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(markerPath, cancellationToken));
            var root = document.RootElement;
            records.Add(new
            {
                installedName = root.TryGetProperty("installedName", out var installedName) ? installedName.GetString() : Path.GetFileName(Path.GetDirectoryName(markerPath)),
                originalName = root.TryGetProperty("originalName", out var originalName) ? originalName.GetString() : null,
                repository = root.GetProperty("Repository").GetString(),
                skillPath = root.GetProperty("SkillPath").GetString(),
                sourceRevision = root.GetProperty("SourceRevision").GetString(),
                directory = Path.GetFileName(Path.GetDirectoryName(markerPath))
            });
        }
        var indexPath = Path.Combine(destinationRoot, ".harness-skill-index.json");
        var pendingPath = Path.Combine(destinationRoot, $".harness-index-{Guid.NewGuid():N}.tmp");
        var json = JsonSerializer.Serialize(new
        {
            format = "harness.skill-index.v1",
            generatedAt = DateTimeOffset.UtcNow,
            skills = records
        }, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(pendingPath, json, new UTF8Encoding(false), cancellationToken);
        File.Move(pendingPath, indexPath, overwrite: true);
    }

    private static string EscapeYamlScalar(string value) =>
        $"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", " ").Replace("\n", " ")}\"";
}

public enum ManagedSkillIntegrity
{
    Unknown,
    Unchanged,
    Modified
}

public sealed record ManagedSkillUpdateResult(string InstallPath, string BackupPath);

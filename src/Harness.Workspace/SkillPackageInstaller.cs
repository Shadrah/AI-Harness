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
        Directory.CreateDirectory(destinationRoot);
        var installedName = CreateInstalledSkillName(skill);
        var destination = Path.Combine(destinationRoot, installedName);
        if (Directory.Exists(destination))
            throw new InvalidOperationException($"A skill already exists at {destination}. Remove or update it explicitly before replacing it.");

        var pending = Path.Combine(destinationRoot, $".pending-{Guid.NewGuid():N}");
        var moved = false;
        try
        {
            await CopyDirectoryAsync(package.PackagePath, pending, cancellationToken);
            await AdaptManifestIdentityAsync(pending, skill, installedName, cancellationToken);
            var marker = JsonSerializer.Serialize(new
            {
                catalogId = skill.Id,
                originalName = skill.Name,
                installedName,
                skill.Repository,
                skill.SkillPath,
                skill.SourceRevision,
                package.ContentSha256,
                installedAt = DateTimeOffset.UtcNow
            });
            await File.WriteAllTextAsync(Path.Combine(pending, ".harness-source.json"), marker, Encoding.UTF8, cancellationToken);
            Directory.Move(pending, destination);
            moved = true;
            await RebuildProviderIndexAsync(destinationRoot, cancellationToken);
            return destination;
        }
        catch
        {
            if (Directory.Exists(pending)) Directory.Delete(pending, recursive: true);
            if (moved && Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
            throw;
        }
    }

    public static string CreateInstalledSkillName(SkillCatalogEntry skill)
    {
        var source = SkillManifestParser.Slug(skill.Repository.Replace('/', '-'));
        if (source.Length > 32) source = source[..32].TrimEnd('-');
        var suffix = skill.Id[..Math.Min(8, skill.Id.Length)].ToLowerInvariant();
        var tail = $"--{source}-{suffix}";
        var name = SkillManifestParser.Slug(skill.Name);
        var maxNameLength = Math.Max(8, 64 - tail.Length);
        if (name.Length > maxNameLength) name = name[..maxNameLength].TrimEnd('-');
        return $"{name}{tail}";
    }

    public static string CreateInstallId(string catalogId, string providerId, string scope, string? workspacePath)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{catalogId}\0{providerId}\0{scope}\0{workspacePath ?? string.Empty}"));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static async Task CopyDirectoryAsync(string source, string destination, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
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

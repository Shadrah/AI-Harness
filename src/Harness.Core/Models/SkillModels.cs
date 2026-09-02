namespace Harness.Core.Models;

public sealed record SkillCatalogEntry(
    string Id,
    string Name,
    string Description,
    string Category,
    string Repository,
    string SkillPath,
    string SourceRevision,
    string SourceUrl,
    string Compatibility,
    string TrustState,
    DateTimeOffset DiscoveredAt,
    DateTimeOffset RefreshedAt,
    string RawMetadataJson = "");

public sealed record SkillCatalogSource(
    string Repository,
    string Owner,
    string SourceUrl,
    int ReportedSkillCount,
    int IndexedSkillCount,
    string SourceRevision,
    string IndexState,
    DateTimeOffset RefreshedAt,
    string Diagnostic = "",
    int DescribedSkillCount = 0);

public sealed record SkillRepositoryInventory(
    SkillCatalogSource Source,
    IReadOnlyList<SkillCatalogEntry> Skills);

public sealed record InstalledSkill(
    string Id,
    string CatalogId,
    string Name,
    string SourceRevision,
    string PackagePath,
    string InstallPath,
    string Scope,
    string? WorkspacePath,
    string ProviderId,
    string? ModelId,
    string ContentSha256,
    bool Enabled,
    DateTimeOffset InstalledAt);

public sealed record SkillInstallTarget(
    string ProviderId,
    string DisplayName,
    string? ModelId = null,
    string SetupKind = "filesystem")
{
    public override string ToString() => DisplayName;
}

public sealed record SkillPackageFile(
    string Path,
    string Sha,
    long ByteLength,
    bool IsExecutable);

public sealed record SkillPackageInspection(
    IReadOnlyList<SkillPackageFile> Files,
    long ByteLength,
    int ScriptCount,
    IReadOnlyList<string> Warnings)
{
    public int FileCount => Files.Count;
}

public sealed record DownloadedSkillPackage(
    string PackagePath,
    string ContentSha256,
    int FileCount,
    long ByteLength);

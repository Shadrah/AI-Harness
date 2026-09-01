using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Harness.Providers.Codex;

public sealed class CodexRuntimeInstaller(HttpClient? httpClient = null)
{
    private const string LatestReleaseEndpoint =
        "https://api.github.com/repos/openai/codex/releases/latest";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient = httpClient ?? CreateHttpClient();

    public async Task<CodexRuntimeInfo> InstallLatestAsync(
        IProgress<CodexRuntimeInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report(new("CHECKING", "Checking the official Codex release"));
        var selectedRelease = await CheckLatestAsync(cancellationToken);
        var asset = new GitHubAsset(
            selectedRelease.AssetName,
            selectedRelease.DownloadUrl,
            selectedRelease.Digest);
        var releaseTag = selectedRelease.Version;

        var managedRoot = CodexRuntimeResolver.GetManagedRoot();
        var version = SanitizeSegment(releaseTag);
        var versionDirectory = Path.Combine(managedRoot, version);
        var executableName = OperatingSystem.IsWindows() ? "codex.exe" : "codex";
        versionDirectory += "-package";
        var installedExecutable = Path.Combine(versionDirectory, "bin", executableName);
        if (CodexRuntimeResolver.HasRequiredTools(installedExecutable))
        {
            await ActivateAsync(
                managedRoot,
                installedExecutable,
                releaseTag,
                asset,
                cancellationToken);
            return new CodexRuntimeInfo(installedExecutable, "HARNESS MANAGED", true, true);
        }

        var stagingDirectory = Path.Combine(
            Path.GetTempPath(),
            $"harness-codex-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingDirectory);
        try
        {
            var downloadPath = Path.Combine(stagingDirectory, asset.Name);
            progress?.Report(new("DOWNLOADING", $"Downloading Codex {releaseTag}"));
            await DownloadAsync(asset.BrowserDownloadUrl, downloadPath, cancellationToken);
            await VerifyDigestAsync(downloadPath, asset.Digest![7..], cancellationToken);
            progress?.Report(new("INSTALLING", "Verified download; installing runtime"));

            Directory.CreateDirectory(managedRoot);
            var preparedDirectory = Path.Combine(stagingDirectory, "prepared");
            Directory.CreateDirectory(preparedDirectory);
            await ExtractPackageAsync(downloadPath, preparedDirectory, cancellationToken);
            var preparedExecutable = Path.Combine(preparedDirectory, "bin", executableName);
            if (!CodexRuntimeResolver.HasRequiredTools(preparedExecutable))
            {
                throw new InvalidDataException(
                    "The Codex package did not contain the CLI and code-mode host.");
            }
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    preparedExecutable,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                    | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }

            if (!Directory.Exists(versionDirectory))
            {
                Directory.Move(preparedDirectory, versionDirectory);
            }

            await ActivateAsync(
                managedRoot,
                installedExecutable,
                releaseTag,
                asset,
                cancellationToken);
            progress?.Report(new("READY", $"Codex {releaseTag} is ready"));
            return new CodexRuntimeInfo(installedExecutable, "HARNESS MANAGED", true, true);
        }
        finally
        {
            if (Directory.Exists(stagingDirectory))
            {
                Directory.Delete(stagingDirectory, recursive: true);
            }
        }
    }

    public async Task<CodexRuntimeRelease> CheckLatestAsync(
        CancellationToken cancellationToken = default)
    {
        using var releaseResponse = await _httpClient.GetAsync(
            LatestReleaseEndpoint,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        releaseResponse.EnsureSuccessStatusCode();
        await using var releaseStream = await releaseResponse.Content.ReadAsStreamAsync(cancellationToken);
        var release = await JsonSerializer.DeserializeAsync<GitHubRelease>(
            releaseStream,
            JsonOptions,
            cancellationToken)
            ?? throw new InvalidOperationException("GitHub returned no Codex release metadata.");

        var assetName = GetAssetName();
        var asset = release.Assets.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, assetName, StringComparison.OrdinalIgnoreCase))
            ?? throw new PlatformNotSupportedException(
                $"Codex release {release.TagName} does not contain {assetName}.");
        if (string.IsNullOrWhiteSpace(asset.Digest)
            || !asset.Digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Codex release asset {asset.Name} did not include a SHA-256 digest.");
        }

        return new CodexRuntimeRelease(
            release.TagName,
            asset.Name,
            asset.BrowserDownloadUrl,
            asset.Digest);
    }

    private async Task DownloadAsync(
        string url,
        string destination,
        CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(
            url,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            81920,
            useAsync: true);
        await input.CopyToAsync(output, cancellationToken);
    }

    private static async Task VerifyDigestAsync(
        string path,
        string expected,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var actual = Convert.ToHexString(
            await SHA256.HashDataAsync(stream, cancellationToken));
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Codex download checksum mismatch. Expected {expected}; received {actual}.");
        }
    }

    private static async Task ExtractPackageAsync(
        string downloadPath,
        string destinationDirectory,
        CancellationToken cancellationToken)
    {
        await using var file = File.OpenRead(downloadPath);
        await using var gzip = new GZipStream(file, CompressionMode.Decompress);
        TarFile.ExtractToDirectory(gzip, destinationDirectory, overwriteFiles: false);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static async Task ActivateAsync(
        string managedRoot,
        string executablePath,
        string version,
        GitHubAsset asset,
        CancellationToken cancellationToken)
    {
        var manifest = new ManagedRuntimeManifest(
            Path.GetFullPath(executablePath),
            version,
            asset.Name,
            asset.Digest,
            DateTimeOffset.UtcNow);
        var temporaryPath = Path.Combine(managedRoot, $"current-{Guid.NewGuid():N}.json");
        var currentPath = Path.Combine(managedRoot, "current.json");
        await File.WriteAllTextAsync(
            temporaryPath,
            JsonSerializer.Serialize(manifest, JsonOptions),
            cancellationToken);
        File.Move(temporaryPath, currentPath, overwrite: true);
    }

    private static string GetAssetName()
    {
        var architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture;
        var target = architecture switch
        {
            System.Runtime.InteropServices.Architecture.X64 => "x86_64",
            System.Runtime.InteropServices.Architecture.Arm64 => "aarch64",
            _ => throw new PlatformNotSupportedException(
                $"Codex runtime installation does not support {architecture}.")
        };

        if (OperatingSystem.IsWindows())
        {
            return $"codex-package-{target}-pc-windows-msvc.tar.gz";
        }

        if (OperatingSystem.IsMacOS())
        {
            return $"codex-package-{target}-apple-darwin.tar.gz";
        }

        if (OperatingSystem.IsLinux())
        {
            return $"codex-package-{target}-unknown-linux-musl.tar.gz";
        }

        throw new PlatformNotSupportedException("Unsupported operating system.");
    }

    private static string SanitizeSegment(string value) => string.Concat(
        value.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Harness/0.1");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    private sealed record GitHubRelease(
        [property: JsonPropertyName("tag_name")] string TagName,
        IReadOnlyList<GitHubAsset> Assets);
    private sealed record GitHubAsset(
        string Name,
        [property: JsonPropertyName("browser_download_url")]
        string BrowserDownloadUrl,
        string? Digest);
    private sealed record ManagedRuntimeManifest(
        string ExecutablePath,
        string Version,
        string AssetName,
        string? Digest,
        DateTimeOffset InstalledAt);
}

public sealed record CodexRuntimeInstallProgress(string State, string Detail);
public sealed record CodexRuntimeRelease(
    string Version,
    string AssetName,
    string DownloadUrl,
    string Digest);

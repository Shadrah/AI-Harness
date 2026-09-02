using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Harness.Core.Models;

namespace Harness.Workspace;

public sealed class GitHubCliClient
{
    public static string DefaultSkillIndexRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Harness", "skills", "indexes");

    public async Task<GitHubConnectionStatus> GetConnectionStatusAsync(CancellationToken cancellationToken = default)
    {
        var version = await RunAsync(Environment.CurrentDirectory, ["--version"], cancellationToken);
        if (version.ExitCode != 0)
            return new GitHubConnectionStatus(false, false, "GitHub CLI is not installed.");
        var auth = await RunAsync(
            Environment.CurrentDirectory,
            ["auth", "status", "--active", "--hostname", "github.com"],
            cancellationToken);
        if (auth.ExitCode != 0)
            return new GitHubConnectionStatus(true, false, "GitHub CLI is installed but not signed in.");
        var account = await RunAsync(
            Environment.CurrentDirectory,
            ["api", "user", "--jq", ".login"],
            cancellationToken);
        var label = account.ExitCode == 0 && !string.IsNullOrWhiteSpace(account.Output)
            ? $"Connected to GitHub as {account.Output.Trim()}"
            : "Connected to GitHub";
        return new GitHubConnectionStatus(true, true, label);
    }

    public async Task<string> ReadStatusAsync(CancellationToken cancellationToken = default)
    {
        return (await GetConnectionStatusAsync(cancellationToken)).Message;
    }

    public async Task SignInAsync(CancellationToken cancellationToken = default)
    {
        var start = CreateStartInfo(Environment.CurrentDirectory,
            ["auth", "login", "--hostname", "github.com", "--web", "--clipboard", "--git-protocol", "https"]);
        CliResult result;
        try
        {
            using var process = Process.Start(start)
                ?? throw new InvalidOperationException("GitHub CLI could not be started.");
            var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var error = process.StandardError.ReadToEndAsync(cancellationToken);

            // Some desktop environments do not honor gh's browser launch even
            // though device authentication is active. Open the stable device page
            // ourselves so the UI promise is always true.
            await Task.Delay(500, cancellationToken);
            if (!process.HasExited) OpenBrowser("https://github.com/login/device");

            await process.WaitForExitAsync(cancellationToken);
            result = new CliResult(process.ExitCode, await output, await error);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            result = new CliResult(-1, "", "GitHub CLI is not installed. Install gh, then reopen Harness.");
        }
        EnsureSuccess(result, "sign in to GitHub");
        EnsureSuccess(
            await RunAsync(
                Environment.CurrentDirectory,
                ["auth", "setup-git", "--hostname", "github.com"],
                cancellationToken),
            "configure Git to use the GitHub credentials");
    }

    public async Task<GitHubUserProfile> GetAuthenticatedUserAsync(CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(Environment.CurrentDirectory, ["api", "user"], cancellationToken);
        EnsureSuccess(result, "read the signed-in GitHub profile");
        using var document = JsonDocument.Parse(result.Output);
        var root = document.RootElement;
        var login = root.TryGetProperty("login", out var loginElement)
            ? loginElement.GetString() ?? string.Empty
            : string.Empty;
        var name = root.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
            ? nameElement.GetString() ?? string.Empty
            : string.Empty;
        var email = root.TryGetProperty("email", out var emailElement) && emailElement.ValueKind == JsonValueKind.String
            ? emailElement.GetString() ?? string.Empty
            : string.Empty;
        var id = root.TryGetProperty("id", out var idElement) && idElement.TryGetInt64(out var parsedId)
            ? parsedId
            : 0;
        if (string.IsNullOrWhiteSpace(login))
            throw new InvalidOperationException("GitHub did not report the signed-in account.");
        if (string.IsNullOrWhiteSpace(name)) name = login;
        if (string.IsNullOrWhiteSpace(email))
            email = id > 0
                ? $"{id}+{login}@users.noreply.github.com"
                : $"{login}@users.noreply.github.com";
        return new GitHubUserProfile(login, name, email);
    }

    public async Task CreateRepositoryAsync(
        string sourceDirectory,
        string name,
        bool isPrivate,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("A repository name is required.");
        var visibility = isPrivate ? "--private" : "--public";
        var result = await RunAsync(Path.GetFullPath(sourceDirectory),
            ["repo", "create", name.Trim(), "--source", ".", "--remote", "origin", visibility, "--push"], cancellationToken);
        EnsureSuccess(result, "create the GitHub repository");
    }

    public async Task<string?> GetRepositoryUrlAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var result = await RunAsync(
            Environment.CurrentDirectory,
            ["repo", "view", name.Trim(), "--json", "url", "--jq", ".url"],
            cancellationToken);
        return result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.Output)
            ? result.Output.Trim()
            : null;
    }

    public async Task SetDefaultBranchAsync(
        string sourceDirectory,
        string branchName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(branchName))
            throw new InvalidOperationException("A branch name is required.");
        var result = await RunAsync(
            Path.GetFullPath(sourceDirectory),
            ["repo", "edit", "--default-branch", branchName.Trim()],
            cancellationToken);
        EnsureSuccess(result, $"set the GitHub default branch to {branchName.Trim()}");
    }

    public async Task<string?> GetDefaultBranchAsync(
        string sourceDirectory,
        CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(
            Path.GetFullPath(sourceDirectory),
            ["repo", "view", "--json", "defaultBranchRef", "--jq", ".defaultBranchRef.name"],
            cancellationToken);
        EnsureSuccess(result, "read the GitHub default branch");
        return string.IsNullOrWhiteSpace(result.Output) ? null : result.Output.Trim();
    }

    public async Task<IReadOnlyList<SkillCatalogEntry>> SearchSkillsAsync(
        string? query,
        string? category,
        int limit = 12,
        bool hydrateMetadata = true,
        CancellationToken cancellationToken = default)
        => (await DiscoverSkillRepositoriesAsync(
                query,
                category,
                repository: null,
                maxRepositories: 3,
                skillsPerRepository: limit,
                hydrateMetadata: hydrateMetadata,
                cancellationToken: cancellationToken))
            .SelectMany(inventory => inventory.Skills)
            .ToArray();

    public async Task<IReadOnlyList<SkillRepositoryInventory>> DiscoverSkillRepositoriesAsync(
        string? query,
        string? category,
        string? repository = null,
        int maxRepositories = 3,
        int skillsPerRepository = 18,
        bool hydrateMetadata = true,
        CancellationToken cancellationToken = default)
    {
        var connection = await GetConnectionStatusAsync(cancellationToken);
        if (!connection.IsAuthenticated)
            throw new InvalidOperationException("Connect GitHub before searching its public skill catalog.");
        var filterTerms = BuildSkillSearchTerms(query, category);
        var discoveryTerms = new List<string> { "filename:SKILL.md" };
        if (!string.IsNullOrWhiteSpace(repository)
            && !repository.Equals("All sources", StringComparison.OrdinalIgnoreCase))
            discoveryTerms.Add($"repo:{repository.Trim()}");
        discoveryTerms.AddRange(filterTerms);
        var global = await SearchCodeAsync(
            string.Join(' ', discoveryTerms),
            25,
            cancellationToken);
        var repositories = global.Items
            .Select(item => item.Repository)
            .Where(repository => repository.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(Math.Clamp(maxRepositories, 1, 4))
            .ToArray();
        var inventories = new List<SkillRepositoryInventory>();
        foreach (var repositoryName in repositories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var completeSource = await SearchCodeAsync(
                $"repo:{repositoryName} filename:SKILL.md",
                Math.Clamp(skillsPerRepository, 1, 25),
                cancellationToken);
            var matches = filterTerms.Count == 0
                ? completeSource
                : await SearchCodeAsync(
                    string.Join(' ', new[] { $"repo:{repositoryName}", "filename:SKILL.md" }.Concat(filterTerms)),
                    Math.Clamp(skillsPerRepository, 1, 25),
                    cancellationToken);
            using var hydrationGate = new SemaphoreSlim(4, 4);
            var entryTasks = matches.Items.Take(skillsPerRepository).Select(async item =>
            {
                await hydrationGate.WaitAsync(cancellationToken);
                try
                {
                    return await CreateSkillEntryAsync(item, query, category, hydrateMetadata, cancellationToken);
                }
                finally
                {
                    hydrationGate.Release();
                }
            }).ToArray();
            var entries = (await Task.WhenAll(entryTasks)).OfType<SkillCatalogEntry>().ToList();
            var revision = entries.FirstOrDefault()?.SourceRevision
                ?? completeSource.Items.Select(item => TryExtractRevision(item.SourceUrl)).FirstOrDefault(value => value.Length > 0)
                ?? string.Empty;
            var now = DateTimeOffset.UtcNow;
            var source = new SkillCatalogSource(
                repositoryName,
                repositoryName.Split('/')[0],
                $"https://github.com/{repositoryName}",
                completeSource.TotalCount,
                entries.Count,
                revision,
                completeSource.TotalCount <= entries.Count ? "COMPLETE" : "PARTIAL · SEARCH TO EXPAND",
                now,
                completeSource.TotalCount > 1000
                    ? "GitHub reports the complete source total; descriptions are cached progressively from searches."
                    : string.Empty);
            inventories.Add(new SkillRepositoryInventory(source, entries));
        }
        return inventories;
    }

    public async Task<SkillRepositoryInventory> IndexSkillRepositoryTreeAsync(
        SkillCatalogSource source,
        CancellationToken cancellationToken = default)
    {
        ValidateRepositoryName(source.Repository);
        var cacheId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source.Repository)))[..16].ToLowerInvariant();
        var cacheRoot = Path.Combine(DefaultSkillIndexRoot, cacheId);
        Directory.CreateDirectory(DefaultSkillIndexRoot);
        string revision;
        if (!Directory.Exists(Path.Combine(cacheRoot, ".git")))
        {
            if (Directory.Exists(cacheRoot) && Directory.EnumerateFileSystemEntries(cacheRoot).Any())
                throw new InvalidOperationException($"The metadata index at {cacheRoot} is incomplete. Remove that one index directory and sync again.");
            var clone = await RunGitAsync(
                Environment.CurrentDirectory,
                ["clone", "--filter=blob:none", "--no-checkout", "--depth", "1", $"https://github.com/{source.Repository}.git", cacheRoot],
                cancellationToken);
            EnsureGitSuccess(clone, $"create the metadata-only index for {source.Repository}");
            var head = await RunGitAsync(cacheRoot, ["rev-parse", "HEAD"], cancellationToken);
            EnsureGitSuccess(head, $"resolve {source.Repository}'s indexed revision");
            revision = head.Output.Trim();
        }
        else
        {
            var fetch = await RunGitAsync(
                cacheRoot,
                ["fetch", "--filter=blob:none", "--depth", "1", "origin"],
                cancellationToken);
            EnsureGitSuccess(fetch, $"refresh the metadata-only index for {source.Repository}");
            var fetched = await RunGitAsync(cacheRoot, ["rev-parse", "FETCH_HEAD"], cancellationToken);
            EnsureGitSuccess(fetched, $"resolve {source.Repository}'s refreshed revision");
            revision = fetched.Output.Trim();
        }
        if (revision.Length < 7 || revision.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidOperationException($"Git did not return an immutable revision for {source.Repository}.");
        var tree = await RunGitAsync(cacheRoot, ["ls-tree", "-r", revision], cancellationToken);
        EnsureGitSuccess(tree, $"enumerate the complete skill tree for {source.Repository}");
        var now = DateTimeOffset.UtcNow;
        var entries = new List<SkillCatalogEntry>();
        foreach (var line in tree.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tab = line.IndexOf('\t');
            if (tab < 0) continue;
            var header = line[..tab].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var path = line[(tab + 1)..].Trim().Replace('\\', '/');
            if (header.Length < 3 || !Path.GetFileName(path).Equals("SKILL.md", StringComparison.OrdinalIgnoreCase)) continue;
            var blobSha = header[2];
            var fallback = Path.GetFileName(Path.GetDirectoryName(path.Replace('/', Path.DirectorySeparatorChar)))
                ?? source.Repository.Split('/').Last();
            var name = SkillManifestParser.Slug(fallback);
            entries.Add(new SkillCatalogEntry(
                CatalogId(source.Repository, path),
                name,
                $"Description not cached yet · {path}",
                SkillManifestParser.InferCategory(name, string.Empty, path),
                source.Repository,
                path,
                revision,
                $"https://github.com/{source.Repository}/blob/{revision}/{path}",
                "Unverified skill format",
                "UNREVIEWED GITHUB SOURCE",
                now,
                now,
                JsonSerializer.Serialize(new { blobSha, metadataHydrated = false, indexKind = "git-tree" })));
        }
        var indexedSource = source with
        {
            ReportedSkillCount = entries.Count,
            IndexedSkillCount = entries.Count,
            SourceRevision = revision,
            IndexState = entries.Count == 0 ? "NO SKILL.md FOUND" : "COMPLETE PATH INDEX",
            RefreshedAt = now,
            Diagnostic = entries.Count == 0
                ? "Repository tree verification found no SKILL.md files."
                : "Complete repository tree indexed without downloading skill package blobs; descriptions hydrate progressively."
        };
        return new SkillRepositoryInventory(indexedSource, entries);
    }

    public async Task<IReadOnlyList<SkillCatalogSource>> DiscoverSkillSourceCandidatesAsync(
        int limit = 12,
        CancellationToken cancellationToken = default)
    {
        var connection = await GetConnectionStatusAsync(cancellationToken);
        if (!connection.IsAuthenticated)
            throw new InvalidOperationException("Connect GitHub before synchronizing public skill sources.");
        var repositories = new Dictionary<string, SkillCatalogSource>(StringComparer.OrdinalIgnoreCase);
        var discoveryQueries = new[]
        {
            "topic:agent-skills",
            "topic:claude-skills",
            "topic:codex-skills",
            "SKILL.md agent in:readme"
        };
        var perQuery = Math.Clamp((limit + discoveryQueries.Length - 1) / discoveryQueries.Length, 1, 30);
        foreach (var query in discoveryQueries)
        {
            var sort = query.Contains("in:readme", StringComparison.OrdinalIgnoreCase) ? "stars" : "updated";
            var result = await RunAsync(
                Environment.CurrentDirectory,
                ["api", "-X", "GET", "search/repositories", "-f", $"q={query}", "-f", $"per_page={perQuery}", "-f", $"sort={sort}"],
                cancellationToken);
            EnsureSuccess(result, "discover GitHub skill repositories");
            using var document = JsonDocument.Parse(result.Output);
            if (!document.RootElement.TryGetProperty("items", out var items)) continue;
            foreach (var item in items.EnumerateArray())
            {
                var repository = item.GetProperty("full_name").GetString() ?? string.Empty;
                if (repository.Length == 0 || repositories.ContainsKey(repository)) continue;
                ValidateRepositoryName(repository);
                var now = DateTimeOffset.UtcNow;
                repositories[repository] = new SkillCatalogSource(
                    repository,
                    item.GetProperty("owner").GetProperty("login").GetString() ?? repository.Split('/')[0],
                    item.GetProperty("html_url").GetString() ?? $"https://github.com/{repository}",
                    0,
                    0,
                    string.Empty,
                    "DISCOVERED · PATH INDEX PENDING",
                    now,
                    "Discovered by a GitHub skill topic; repository tree verification is pending.");
            }
        }
        return repositories.Values.Take(limit).ToArray();
    }

    private async Task<SkillCatalogEntry?> CreateSkillEntryAsync(
        SkillSearchItem item,
        string? query,
        string? category,
        bool hydrateMetadata,
        CancellationToken cancellationToken)
    {
        if (item.Repository.Length == 0 || item.Path.Length == 0 || item.BlobSha.Length == 0) return null;
        var revision = ExtractRevision(item.SourceUrl);
        var fallback = Path.GetFileName(Path.GetDirectoryName(item.Path.Replace('/', Path.DirectorySeparatorChar)))
            ?? item.Repository.Split('/').Last();
        var markdown = string.Empty;
        if (hydrateMetadata)
        {
            try
            {
                markdown = await ReadBlobTextAsync(item.Repository, item.BlobSha, cancellationToken);
            }
            catch (InvalidOperationException)
            {
                // A single unavailable blob must not hide the rest of a source.
            }
        }
        var metadata = SkillManifestParser.Analyze(markdown, fallback);
        var now = DateTimeOffset.UtcNow;
        return new SkillCatalogEntry(
            CatalogId(item.Repository, item.Path),
            metadata.Name,
            string.IsNullOrWhiteSpace(metadata.Description)
                ? $"Description not cached yet · {item.Path}"
                : metadata.Description,
            SkillManifestParser.InferCategory(metadata.Name, metadata.Description, item.Path, category),
            item.Repository,
            item.Path.Replace('\\', '/'),
            revision,
            item.SourceUrl,
            metadata.Compatibility,
            "UNREVIEWED GITHUB SOURCE",
            now,
            now,
            JsonSerializer.Serialize(new { blobSha = item.BlobSha, query, category }));
    }

    private async Task<SkillSearchResult> SearchCodeAsync(
        string searchQuery,
        int limit,
        CancellationToken cancellationToken)
    {
        var result = await RunAsync(
            Environment.CurrentDirectory,
            ["api", "-X", "GET", "search/code", "-f", $"q={searchQuery}", "-f", $"per_page={Math.Clamp(limit, 1, 25)}"],
            cancellationToken);
        EnsureSuccess(result, "search GitHub skills");
        using var document = JsonDocument.Parse(result.Output);
        var total = document.RootElement.TryGetProperty("total_count", out var totalElement)
            && totalElement.TryGetInt32(out var parsedTotal)
            ? parsedTotal
            : 0;
        var items = new List<SkillSearchItem>();
        if (document.RootElement.TryGetProperty("items", out var itemElements))
        {
            foreach (var item in itemElements.EnumerateArray())
            {
                items.Add(new SkillSearchItem(
                    item.GetProperty("repository").GetProperty("full_name").GetString() ?? string.Empty,
                    item.GetProperty("path").GetString() ?? string.Empty,
                    item.GetProperty("sha").GetString() ?? string.Empty,
                    item.GetProperty("html_url").GetString() ?? string.Empty));
            }
        }
        return new SkillSearchResult(total, items);
    }

    private static IReadOnlyList<string> BuildSkillSearchTerms(string? query, string? category)
    {
        var terms = new List<string>();
        if (!string.IsNullOrWhiteSpace(query)) terms.Add(query.Trim());
        if (!string.IsNullOrWhiteSpace(category) && !category.Equals("All", StringComparison.OrdinalIgnoreCase))
            terms.Add(CategorySearchTerms(category));
        return terms;
    }

    public async Task<SkillPackageInspection> InspectSkillPackageAsync(
        SkillCatalogEntry skill,
        CancellationToken cancellationToken = default)
    {
        var skillRoot = GetSkillRoot(skill.SkillPath);
        var files = new List<SkillPackageFile>();
        var warnings = new List<string>();
        var pendingDirectories = new Queue<string>();
        pendingDirectories.Enqueue(skillRoot);
        while (pendingDirectories.Count > 0)
        {
            var directory = pendingDirectories.Dequeue();
            var endpoint = directory.Length == 0
                ? $"repos/{skill.Repository}/contents?ref={skill.SourceRevision}"
                : $"repos/{skill.Repository}/contents/{directory}?ref={skill.SourceRevision}";
            var result = await RunAsync(Environment.CurrentDirectory, ["api", endpoint], cancellationToken);
            EnsureSuccess(result, $"inspect {skill.Name}");
            using var document = JsonDocument.Parse(result.Output);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("GitHub did not return a complete directory listing for this skill.");
            foreach (var item in document.RootElement.EnumerateArray())
            {
                var path = item.GetProperty("path").GetString() ?? string.Empty;
                if (!IsUnderSkillRoot(path, skillRoot)) continue;
                var type = item.GetProperty("type").GetString() ?? string.Empty;
                if (type == "dir")
                {
                    pendingDirectories.Enqueue(path);
                    continue;
                }
                var relative = skillRoot.Length == 0 ? path : path[(skillRoot.Length + 1)..];
                ValidatePackagePath(relative);
                if (type is "symlink" or "submodule")
                    throw new InvalidOperationException($"The skill contains an unsupported {type} ({relative}).");
                if (type != "file") continue;
                var size = item.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var parsedSize)
                    ? parsedSize
                    : 0;
                if (size > 5L * 1024 * 1024)
                    throw new InvalidOperationException($"The skill contains a file larger than 5 MB ({relative}); Harness will not download it.");
                var sha = item.GetProperty("sha").GetString() ?? string.Empty;
                files.Add(new SkillPackageFile(relative, sha, size, IsScript(relative)));
                if (files.Count > 250)
                    throw new InvalidOperationException("This skill contains more than 250 files; Harness stopped inspecting it before any package content was downloaded.");
            }
        }
        if (!files.Any(file => file.Path.Equals("SKILL.md", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("The selected GitHub path does not contain a SKILL.md package root.");
        var bytes = files.Sum(file => file.ByteLength);
        if (bytes > 25L * 1024 * 1024)
            throw new InvalidOperationException($"This skill reports {FormatBytes(bytes)}; Harness currently limits skill packages to 25 MB.");
        var scripts = files.Count(file => file.IsExecutable);
        if (scripts > 0) warnings.Add($"Contains {scripts} script or executable file{(scripts == 1 ? string.Empty : "s")}; running them still requires normal Harness approval.");
        if (files.Any(file => IsCredentialLike(file.Path)))
            throw new InvalidOperationException("The package contains a credential-like file name. Harness will not download it.");
        return new SkillPackageInspection(files, bytes, scripts, warnings);
    }

    public async Task<DownloadedSkillPackage> DownloadSkillPackageAsync(
        SkillCatalogEntry skill,
        SkillPackageInspection inspection,
        string packageRoot,
        CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(packageRoot);
        Directory.CreateDirectory(root);
        var destination = Path.Combine(root, SkillManifestParser.Slug(skill.Name), skill.SourceRevision);
        var markerPath = Path.Combine(destination, ".harness-package.json");
        if (File.Exists(markerPath))
        {
            using var existing = JsonDocument.Parse(await File.ReadAllTextAsync(markerPath, cancellationToken));
            return new DownloadedSkillPackage(
                destination,
                existing.RootElement.GetProperty("contentSha256").GetString() ?? string.Empty,
                existing.RootElement.GetProperty("fileCount").GetInt32(),
                existing.RootElement.GetProperty("byteLength").GetInt64());
        }

        var pending = Path.Combine(root, $".pending-{Guid.NewGuid():N}");
        Directory.CreateDirectory(pending);
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long totalBytes = 0;
            foreach (var file in inspection.Files.OrderBy(file => file.Path, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var bytes = await ReadBlobBytesAsync(skill.Repository, file.Sha, cancellationToken);
                VerifyGitBlob(file.Sha, bytes, file.Path);
                totalBytes += bytes.Length;
                if (totalBytes > 25L * 1024 * 1024)
                    throw new InvalidOperationException("The downloaded package exceeds Harness's 25 MB skill limit.");
                var target = Path.GetFullPath(Path.Combine(pending, file.Path.Replace('/', Path.DirectorySeparatorChar)));
                if (!target.StartsWith(pending + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The skill package contains a path outside its root.");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await File.WriteAllBytesAsync(target, bytes, cancellationToken);
                hash.AppendData(Encoding.UTF8.GetBytes(file.Path));
                hash.AppendData([0]);
                hash.AppendData(bytes);
            }
            var contentSha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            var marker = JsonSerializer.Serialize(new
            {
                skill.Id,
                skill.Repository,
                skill.SkillPath,
                skill.SourceRevision,
                contentSha256,
                fileCount = inspection.FileCount,
                byteLength = totalBytes
            });
            await File.WriteAllTextAsync(Path.Combine(pending, ".harness-package.json"), marker, Encoding.UTF8, cancellationToken);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            Directory.Move(pending, destination);
            return new DownloadedSkillPackage(destination, contentSha256, inspection.FileCount, totalBytes);
        }
        catch
        {
            if (Directory.Exists(pending)) Directory.Delete(pending, recursive: true);
            throw;
        }
    }

    private async Task<string> ReadBlobTextAsync(string repository, string sha, CancellationToken cancellationToken) =>
        Encoding.UTF8.GetString(await ReadBlobBytesAsync(repository, sha, cancellationToken));

    private async Task<byte[]> ReadBlobBytesAsync(string repository, string sha, CancellationToken cancellationToken)
    {
        var result = await RunAsync(Environment.CurrentDirectory, ["api", $"repos/{repository}/git/blobs/{sha}"], cancellationToken);
        EnsureSuccess(result, $"read GitHub package content from {repository}");
        using var document = JsonDocument.Parse(result.Output);
        var encoding = document.RootElement.GetProperty("encoding").GetString();
        if (!string.Equals(encoding, "base64", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("GitHub returned an unsupported skill content encoding.");
        var content = document.RootElement.GetProperty("content").GetString() ?? string.Empty;
        return Convert.FromBase64String(content.Replace("\n", string.Empty).Replace("\r", string.Empty));
    }

    private static string CatalogId(string repository, string path) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"github\0{repository}\0{path}"))).ToLowerInvariant();

    private static string ExtractRevision(string sourceUrl)
    {
        if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri))
            throw new InvalidOperationException("GitHub did not return a valid source URL for this skill.");
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var blob = Array.FindIndex(segments, segment => segment.Equals("blob", StringComparison.OrdinalIgnoreCase));
        if (blob < 0 || blob + 1 >= segments.Length)
            throw new InvalidOperationException("GitHub did not return a pinned source revision for this skill.");
        var revision = segments[blob + 1];
        if (revision.Length < 7 || revision.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidOperationException("GitHub did not return an immutable commit revision for this skill.");
        return revision;
    }

    private static string TryExtractRevision(string sourceUrl)
    {
        try { return ExtractRevision(sourceUrl); }
        catch (InvalidOperationException) { return string.Empty; }
    }

    private static string GetSkillRoot(string skillPath)
    {
        var normalized = skillPath.Replace('\\', '/').Trim('/');
        var separator = normalized.LastIndexOf('/');
        return separator < 0 ? string.Empty : normalized[..separator];
    }

    private static bool IsUnderSkillRoot(string path, string root) =>
        root.Length == 0 || path.StartsWith(root + "/", StringComparison.Ordinal);

    private static void ValidatePackagePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path)
            || path.Split('/').Any(part => part is "" or "." or ".."))
            throw new InvalidOperationException($"The skill contains an unsafe package path: {path}");
    }

    private static bool IsScript(string path)
    {
        var normalized = $"/{path.Replace('\\', '/').ToLowerInvariant()}";
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return normalized.Contains("/scripts/") || extension is ".ps1" or ".sh" or ".bat" or ".cmd" or ".py" or ".js" or ".exe";
    }

    private static bool IsCredentialLike(string path)
    {
        var name = Path.GetFileName(path).ToLowerInvariant();
        return name is ".env" or "id_rsa" or "id_ed25519"
            || name.EndsWith(".pem", StringComparison.Ordinal)
            || name.EndsWith(".pfx", StringComparison.Ordinal);
    }

    private static void VerifyGitBlob(string expectedSha, byte[] content, string path)
    {
        var header = Encoding.UTF8.GetBytes($"blob {content.Length}\0");
        var payload = new byte[header.Length + content.Length];
        Buffer.BlockCopy(header, 0, payload, 0, header.Length);
        Buffer.BlockCopy(content, 0, payload, header.Length, content.Length);
        var actual = expectedSha.Length == 64
            ? Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant()
            : Convert.ToHexString(SHA1.HashData(payload)).ToLowerInvariant();
        if (!actual.Equals(expectedSha, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"GitHub content verification failed for {path}.");
    }

    private static string CategorySearchTerms(string category) => category switch
    {
        "Game development" => "game",
        "Frontend" => "frontend",
        "Backend" => "backend",
        "DevOps" => "devops",
        "Testing" => "testing",
        "Security" => "security",
        "Data" => "data",
        "Documents" => "document",
        "Media" => "media",
        "Research" => "research",
        "Productivity" => "workflow",
        _ => category
    };

    private static string FormatBytes(long bytes) => bytes >= 1024 * 1024
        ? $"{bytes / 1024d / 1024d:0.0} MB"
        : $"{Math.Max(1, bytes / 1024d):0.#} KB";

    private static async Task<CliResult> RunAsync(string workingDirectory, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var start = CreateStartInfo(workingDirectory, arguments);
        try
        {
            using var process = Process.Start(start) ?? throw new InvalidOperationException("GitHub CLI could not be started.");
            var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var error = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            return new CliResult(process.ExitCode, await output, await error);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return new CliResult(-1, "", "GitHub CLI is not installed. Install gh, then reopen Harness.");
        }
    }

    private static async Task<CliResult> RunGitAsync(string workingDirectory, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        try
        {
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Git could not be started.");
            var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var error = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            return new CliResult(process.ExitCode, await output, await error);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return new CliResult(-1, "", "Git is not installed or is not available on PATH.");
        }
    }

    private static void ValidateRepositoryName(string repository)
    {
        var parts = repository.Split('/');
        if (parts.Length != 2 || parts.Any(part => part.Length == 0 || part.Any(character =>
                !char.IsLetterOrDigit(character) && character is not '-' and not '_' and not '.')))
            throw new InvalidOperationException($"GitHub returned an invalid repository name: {repository}");
    }

    private static void EnsureGitSuccess(CliResult result, string action)
    {
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"Could not {action}: {(string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error).Trim()}");
    }

    private static ProcessStartInfo CreateStartInfo(string workingDirectory, IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo
        {
            FileName = "gh",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return start;
    }

    private static void OpenBrowser(string url)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true
        });
    }

    private static void EnsureSuccess(CliResult result, string action)
    {
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"Could not {action}: {(string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error).Trim()}");
    }

    private sealed record SkillSearchItem(string Repository, string Path, string BlobSha, string SourceUrl);
    private sealed record SkillSearchResult(int TotalCount, IReadOnlyList<SkillSearchItem> Items);
    private sealed record CliResult(int ExitCode, string Output, string Error);
}

public sealed record GitHubConnectionStatus(bool IsCliInstalled, bool IsAuthenticated, string Message);
public sealed record GitHubUserProfile(string Login, string Name, string Email);

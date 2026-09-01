using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Harness.Core.Models;

namespace Harness.Workspace;

public sealed class GitWorkspaceClient
{
    private readonly string _recoveryRoot;

    public GitWorkspaceClient(string? recoveryRoot = null)
    {
        _recoveryRoot = recoveryRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Harness",
            "data",
            "recovery");
    }

    public async Task<WorkingTreeSnapshot> ReadStatusAsync(
        string workspacePath,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var rootResult = await RunGitAsync(
                workspacePath,
                ["rev-parse", "--show-toplevel"],
                cancellationToken);
            if (rootResult.ExitCode != 0)
            {
                return new WorkingTreeSnapshot(false, null, null, [], CleanError(rootResult));
            }

            var root = Path.GetFullPath(rootResult.StandardOutput.Trim());
            var branchResult = await RunGitAsync(
                root,
                ["branch", "--show-current"],
                cancellationToken);
            var branch = branchResult.ExitCode == 0
                ? branchResult.StandardOutput.Trim()
                : string.Empty;
            if (branch.Length == 0)
            {
                var head = await RunGitAsync(
                    root,
                    ["rev-parse", "--short", "HEAD"],
                    cancellationToken);
                branch = head.ExitCode == 0 ? $"DETACHED {head.StandardOutput.Trim()}" : "NO COMMITS";
            }

            var status = await RunGitAsync(
                root,
                ["status", "--porcelain=v1", "-z", "--untracked-files=all"],
                cancellationToken);
            EnsureSuccess(status, "read Git working-tree status");
            return new WorkingTreeSnapshot(
                true,
                root,
                branch,
                ParsePorcelain(status.StandardOutput));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new WorkingTreeSnapshot(false, null, null, [], exception.Message);
        }
    }

    public async Task<string> GetDiffAsync(
        string repositoryRoot,
        WorkingTreeFile file,
        CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(repositoryRoot);
        ValidateRelativePath(root, file.RelativePath);
        if (file.IsUntracked)
        {
            return await BuildUntrackedDiffAsync(root, file.RelativePath, cancellationToken);
        }

        var sections = new List<string>();
        if (file.IsStaged)
        {
            var staged = await RunGitAsync(
                root,
                ["diff", "--cached", "--no-ext-diff", "--", file.RelativePath],
                cancellationToken);
            EnsureSuccess(staged, $"read the staged diff for {file.RelativePath}");
            if (!string.IsNullOrWhiteSpace(staged.StandardOutput))
            {
                sections.Add("STAGED\n\n" + staged.StandardOutput);
            }
        }
        if (file.HasWorkTreeChanges)
        {
            var unstaged = await RunGitAsync(
                root,
                ["diff", "--no-ext-diff", "--", file.RelativePath],
                cancellationToken);
            EnsureSuccess(unstaged, $"read the working-tree diff for {file.RelativePath}");
            if (!string.IsNullOrWhiteSpace(unstaged.StandardOutput))
            {
                sections.Add("WORKING TREE\n\n" + unstaged.StandardOutput);
            }
        }
        return sections.Count == 0 ? "No textual diff was reported." : string.Join("\n\n", sections);
    }

    public async Task StageAsync(
        string repositoryRoot,
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(repositoryRoot);
        ValidateRelativePath(root, relativePath);
        var result = await RunGitAsync(root, ["add", "--", relativePath], cancellationToken);
        EnsureSuccess(result, $"stage {relativePath}");
    }

    public async Task InitializeRepositoryAsync(string workspacePath, CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(workspacePath);
        var result = await RunGitAsync(root, ["init"], cancellationToken);
        EnsureSuccess(result, "initialize the Git repository");
    }

    public async Task<string?> GetRemoteUrlAsync(string repositoryRoot, CancellationToken cancellationToken = default)
    {
        var result = await RunGitAsync(Path.GetFullPath(repositoryRoot), ["remote", "get-url", "origin"], cancellationToken);
        return result.ExitCode == 0 ? result.StandardOutput.Trim() : null;
    }

    public async Task SetOriginAsync(string repositoryRoot, string remoteUrl, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(remoteUrl.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme is not ("https" or "ssh"))
        {
            throw new InvalidOperationException("Enter a complete HTTPS or SSH repository URL.");
        }
        var root = Path.GetFullPath(repositoryRoot);
        var existing = await GetRemoteUrlAsync(root, cancellationToken);
        var arguments = existing is null
            ? new[] { "remote", "add", "origin", remoteUrl.Trim() }
            : new[] { "remote", "set-url", "origin", remoteUrl.Trim() };
        EnsureSuccess(await RunGitAsync(root, arguments, cancellationToken), "attach the origin remote");
    }

    public async Task CommitAsync(string repositoryRoot, string message, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(message)) throw new InvalidOperationException("A commit message is required.");
        EnsureSuccess(await RunGitAsync(Path.GetFullPath(repositoryRoot), ["commit", "-m", message.Trim()], cancellationToken), "create the commit");
    }

    public async Task CommitAllAsync(string repositoryRoot, string message, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(message)) throw new InvalidOperationException("A commit message is required.");
        var root = Path.GetFullPath(repositoryRoot);
        EnsureSuccess(await RunGitAsync(root, ["add", "-A"], cancellationToken), "stage workspace changes");
        EnsureSuccess(await RunGitAsync(root, ["commit", "-m", message.Trim()], cancellationToken), "create the commit");
    }

    public async Task<GitIdentity> ReadIdentityAsync(string repositoryRoot, CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(repositoryRoot);
        var name = await RunGitAsync(root, ["config", "--get", "user.name"], cancellationToken);
        var email = await RunGitAsync(root, ["config", "--get", "user.email"], cancellationToken);
        return new GitIdentity(
            name.ExitCode == 0 ? name.StandardOutput.Trim() : string.Empty,
            email.ExitCode == 0 ? email.StandardOutput.Trim() : string.Empty);
    }

    public async Task ConfigureIdentityAsync(
        string repositoryRoot,
        string name,
        string email,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("Enter the name to record on Git commits.");
        if (string.IsNullOrWhiteSpace(email)
            || !email.Contains('@', StringComparison.Ordinal)
            || email.StartsWith('@')
            || email.EndsWith('@'))
            throw new InvalidOperationException("Enter a valid email address for Git commits.");
        var root = Path.GetFullPath(repositoryRoot);
        EnsureSuccess(await RunGitAsync(root, ["config", "user.name", name.Trim()], cancellationToken), "set the repository commit name");
        EnsureSuccess(await RunGitAsync(root, ["config", "user.email", email.Trim()], cancellationToken), "set the repository commit email");
    }

    public async Task<bool> PrepareForInitialPushAsync(
        string repositoryRoot,
        string commitMessage,
        CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(repositoryRoot);
        EnsureSuccess(await RunGitAsync(root, ["add", "-A"], cancellationToken), "stage workspace files");
        var hasHead = (await RunGitAsync(root, ["rev-parse", "--verify", "HEAD"], cancellationToken)).ExitCode == 0;
        var staged = await RunGitAsync(root, ["diff", "--cached", "--quiet"], cancellationToken);
        var hasStagedChanges = staged.ExitCode == 1;
        if (staged.ExitCode is not (0 or 1))
            EnsureSuccess(staged, "inspect staged workspace files");
        if (hasHead && !hasStagedChanges) return false;
        var arguments = hasStagedChanges
            ? new[] { "commit", "-m", commitMessage }
            : new[] { "commit", "--allow-empty", "-m", commitMessage };
        EnsureSuccess(await RunGitAsync(root, arguments, cancellationToken), "create the initial commit");
        return true;
    }

    public async Task FetchAsync(string repositoryRoot, CancellationToken cancellationToken = default) =>
        EnsureSuccess(await RunGitAsync(Path.GetFullPath(repositoryRoot), ["fetch", "--prune", "origin"], cancellationToken), "fetch origin");

    public async Task PullAsync(string repositoryRoot, CancellationToken cancellationToken = default) =>
        EnsureSuccess(await RunGitAsync(Path.GetFullPath(repositoryRoot), ["pull", "--ff-only"], cancellationToken), "pull with fast-forward only");

    public async Task PushAsync(string repositoryRoot, CancellationToken cancellationToken = default) =>
        EnsureSuccess(await RunGitAsync(Path.GetFullPath(repositoryRoot), ["push", "-u", "origin", "HEAD"], cancellationToken), "push the current branch");

    public async Task UnstageAsync(
        string repositoryRoot,
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(repositoryRoot);
        ValidateRelativePath(root, relativePath);
        var result = await RunGitAsync(
            root,
            ["restore", "--staged", "--", relativePath],
            cancellationToken);
        EnsureSuccess(result, $"unstage {relativePath}");
    }

    public async Task<WorkspaceRecoveryResult> RevertWorkTreeAsync(
        string repositoryRoot,
        WorkingTreeFile file,
        CancellationToken cancellationToken = default)
    {
        if (!file.HasWorkTreeChanges)
        {
            throw new InvalidOperationException("This file has no working-tree changes to revert.");
        }

        var root = Path.GetFullPath(repositoryRoot);
        var fullPath = ValidateRelativePath(root, file.RelativePath);
        var recoveryDirectory = Path.Combine(
            _recoveryRoot,
            DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss"),
            Guid.NewGuid().ToString("N"));
        var recoveryPath = Path.Combine(
            recoveryDirectory,
            file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(recoveryPath)!);

        var moved = file.IsUntracked && File.Exists(fullPath);
        var manifest = new
        {
            repositoryRoot = root,
            file.RelativePath,
            file.StatusCode,
            capturedAt = DateTimeOffset.UtcNow,
            originalWasMoved = moved
        };
        await File.WriteAllTextAsync(
            Path.Combine(recoveryDirectory, "recovery.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }),
            cancellationToken);

        if (File.Exists(fullPath))
        {
            if (file.IsUntracked)
            {
                File.Move(fullPath, recoveryPath);
            }
            else
            {
                File.Copy(fullPath, recoveryPath, overwrite: false);
            }
        }

        if (!file.IsUntracked)
        {
            var result = await RunGitAsync(
                root,
                ["restore", "--worktree", "--", file.RelativePath],
                cancellationToken);
            EnsureSuccess(result, $"revert {file.RelativePath}");
        }

        return new WorkspaceRecoveryResult(file.RelativePath, recoveryDirectory, moved);
    }

    private static IReadOnlyList<WorkingTreeFile> ParsePorcelain(string output)
    {
        var files = new List<WorkingTreeFile>();
        var offset = 0;
        while (offset + 3 <= output.Length)
        {
            var indexStatus = output[offset];
            var workTreeStatus = output[offset + 1];
            offset += 3;
            var terminator = output.IndexOf('\0', offset);
            if (terminator < 0)
            {
                break;
            }

            var path = output[offset..terminator].Replace('\\', '/');
            offset = terminator + 1;
            if (indexStatus is 'R' or 'C')
            {
                var originalTerminator = output.IndexOf('\0', offset);
                if (originalTerminator < 0)
                {
                    break;
                }
                offset = originalTerminator + 1;
            }

            files.Add(new WorkingTreeFile(
                path,
                indexStatus,
                workTreeStatus,
                indexStatus == '?' && workTreeStatus == '?'));
        }
        return files;
    }

    private static async Task<string> BuildUntrackedDiffAsync(
        string root,
        string relativePath,
        CancellationToken cancellationToken)
    {
        var fullPath = ValidateRelativePath(root, relativePath);
        var info = new FileInfo(fullPath);
        if (!info.Exists)
        {
            return "The untracked file no longer exists.";
        }
        if (info.Length > 1024 * 1024)
        {
            return $"Untracked file · {info.Length:N0} bytes. Preview is limited to 1 MB.";
        }

        try
        {
            var bytes = await File.ReadAllBytesAsync(fullPath, cancellationToken);
            if (bytes.Contains((byte)0))
            {
                return $"Untracked binary file · {info.Length:N0} bytes.";
            }
            var text = new UTF8Encoding(false, true).GetString(bytes);
            var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
            var builder = new StringBuilder()
                .AppendLine("--- /dev/null")
                .AppendLine($"+++ b/{relativePath}")
                .AppendLine($"@@ -0,0 +1,{lines.Length} @@");
            foreach (var line in lines)
            {
                builder.Append('+').AppendLine(line);
            }
            return builder.ToString();
        }
        catch (DecoderFallbackException)
        {
            return $"Untracked binary file · {info.Length:N0} bytes.";
        }
    }

    private static string ValidateRelativePath(string root, string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
        {
            throw new InvalidOperationException("Git returned an unexpected absolute path.");
        }
        var fullPath = Path.GetFullPath(
            relativePath.Replace('/', Path.DirectorySeparatorChar),
            root);
        var rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(
                rootPrefix,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The Git path escapes the repository root.");
        }
        return fullPath;
    }

    private static async Task<GitResult> RunGitAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = Path.GetFullPath(workingDirectory),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Git could not be started.");
        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return new GitResult(
            process.ExitCode,
            await standardOutput,
            await standardError);
    }

    private static void EnsureSuccess(GitResult result, string operation)
    {
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"Could not {operation}: {CleanError(result)}");
        }
    }

    private static string CleanError(GitResult result) =>
        string.IsNullOrWhiteSpace(result.StandardError)
            ? result.StandardOutput.Trim()
            : result.StandardError.Trim();

    private sealed record GitResult(int ExitCode, string StandardOutput, string StandardError);
}

public sealed record GitIdentity(string Name, string Email)
{
    public bool IsComplete => !string.IsNullOrWhiteSpace(Name) && !string.IsNullOrWhiteSpace(Email);
}

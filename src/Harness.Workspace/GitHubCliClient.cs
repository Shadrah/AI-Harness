using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Harness.Workspace;

public sealed class GitHubCliClient
{
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

    private sealed record CliResult(int ExitCode, string Output, string Error);
}

public sealed record GitHubConnectionStatus(bool IsCliInstalled, bool IsAuthenticated, string Message);
public sealed record GitHubUserProfile(string Login, string Name, string Email);

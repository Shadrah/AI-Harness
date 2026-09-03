using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace Harness.Providers.Api;

/// <summary>File operations stay within the project and reject symlinks. Commands are NOT OS-sandboxed:
/// the host must obtain approval for every command unless the user explicitly enabled full access.</summary>
public sealed class ApiWorkspaceTools(string root, Func<string, string, CancellationToken, Task<bool>> approve)
{
    public static IReadOnlyList<ApiTool> Definitions { get; } =
    [
        Define("list_files", "List one project directory. Does not recurse. Paths are relative to the project.", ("path", "Directory path, or .")),
        Define("read_file", "Read a UTF-8 project text file (up to 1 MiB).", ("path", "Project-relative file path")),
        Define("write_file", "Create or replace a project text file. Requires approval; supply its complete content.", ("path", "Project-relative file path"), ("content", "Complete new UTF-8 file content")),
        Define("run_command", "Run a shell command in the project. Requires user approval. Commands are not sandboxed. Do not request destructive or external actions without user authorization.", ("command", "PowerShell command on Windows, sh command otherwise"))
    ];

    private static ApiTool Define(string name, string description, params (string Name, string Description)[] parameters)
    {
        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (var (key, detail) in parameters) { properties[key] = new JsonObject { ["type"] = "string", ["description"] = detail }; required.Add(key); }
        return new(name, description, new JsonObject { ["type"] = "object", ["properties"] = properties, ["required"] = required, ["additionalProperties"] = false });
    }

    public async Task<string> ExecuteAsync(ApiToolCall call, CancellationToken cancellationToken)
    {
        try
        {
            if (call.Arguments.Length > 2 * 1024 * 1024) return "Error: tool arguments exceed the safety limit.";
            var args = JsonNode.Parse(call.Arguments)?.AsObject() ?? throw new ArgumentException("Invalid tool arguments.");
            var name = call.Name;
            if (name == "run_command")
            {
                var command = args["command"]!.GetValue<string>();
                if (!await approve("Command (not sandboxed)", command, cancellationToken).ConfigureAwait(false)) return "User declined this command. Do not retry it.";
                return await RunCommandAsync(command, cancellationToken).ConfigureAwait(false);
            }
            if (name is not ("list_files" or "read_file" or "write_file")) return "Error: unknown tool.";
            var path = Resolve(args["path"]!.GetValue<string>());
            switch (name)
            {
                case "list_files":
                    return await Task.Run(() => string.Join('\n', Directory.EnumerateFileSystemEntries(path).Take(500)
                        .Select(entry => Path.GetFileName(entry) + (Directory.Exists(entry) ? "/" : ""))) + "\n[List limited to 500 entries.]", cancellationToken).ConfigureAwait(false);
                case "read_file":
                    if (new FileInfo(path).Length > 1024 * 1024) return "Error: file exceeds the 1 MiB text limit. Use a scoped command with approval.";
                    var text = await File.ReadAllTextAsync(path, new UTF8Encoding(false, true), cancellationToken).ConfigureAwait(false);
                    return text.Contains('\0') ? "Error: this is a binary file." : text;
                default:
                    var content = args["content"]!.GetValue<string>();
                    if (content.Length > 1024 * 1024) return "Error: replacement exceeds 1 MiB.";
                    if (!await approve("Write file", $"{Path.GetRelativePath(root, path)}\n\nComplete replacement ({content.Length:N0} characters):\n{content}", cancellationToken).ConfigureAwait(false))
                        return "User declined this file change. Do not retry it.";
                    Resolve(args["path"]!.GetValue<string>()); // Revalidate after the approval dialog.
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    var temporary = Path.Combine(Path.GetDirectoryName(path)!, $".harness-write-{Guid.NewGuid():N}.tmp");
                    try
                    {
                        await File.WriteAllTextAsync(temporary, content, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
                        cancellationToken.ThrowIfCancellationRequested();
                        Resolve(args["path"]!.GetValue<string>());
                        File.Move(temporary, path, overwrite: true);
                    }
                    finally { if (File.Exists(temporary)) File.Delete(temporary); }
                    return $"Wrote {Path.GetRelativePath(root, path)} ({content.Length:N0} characters).";
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or System.Text.Json.JsonException or InvalidOperationException)
        {
            return $"Tool failed: {exception.GetType().Name}. Check the relative path and arguments; no retry was performed.";
        }
    }

    private string Resolve(string relative)
    {
        if (Path.IsPathRooted(relative) || relative.Contains(':')) throw new UnauthorizedAccessException("Only project-relative paths are allowed.");
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var full = Path.GetFullPath(Path.Combine(fullRoot, relative));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!full.StartsWith(fullRoot + Path.DirectorySeparatorChar, comparison) && !full.Equals(fullRoot, comparison))
            throw new UnauthorizedAccessException("Path escapes the project.");
        var segments = Path.GetRelativePath(fullRoot, full).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (segments.Any(segment => segment.Equals(".git", comparison))) throw new UnauthorizedAccessException("Direct access to Git internals is blocked.");
        // Also inspect root ancestors; a project root itself can be a junction.
        for (var current = full; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new UnauthorizedAccessException("Symbolic links and junctions require an explicitly approved command.");
        return full;
    }

    private async Task<string> RunCommandAsync(string command, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        var start = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "powershell.exe" : "/bin/sh",
            WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        if (OperatingSystem.IsWindows())
        {
            start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive"); start.ArgumentList.Add("-Command");
            command = "[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new(); " + command;
        }
        else start.ArgumentList.Add("-c");
        start.ArgumentList.Add(command);
        using var process = Process.Start(start) ?? throw new IOException("Command did not start.");
        using var registration = timeout.Token.Register(() => { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { } });
        var stdout = ReadBoundedAsync(process.StandardOutput, timeout.Token);
        var stderr = ReadBoundedAsync(process.StandardError, timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return $"Exit {process.ExitCode}\n{await stdout.ConfigureAwait(false)}\n{await stderr.ConfigureAwait(false)}";
        }
        finally
        {
            try { await Task.WhenAll(stdout, stderr).ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var result = new StringBuilder(); var buffer = new char[4096]; var truncated = false;
        int read;
        while ((read = await reader.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            var take = Math.Min(read, Math.Max(0, 24000 - result.Length));
            result.Append(buffer, 0, take); truncated |= take < read;
        }
        return result + (truncated ? "\n[Output truncated at 24,000 characters.]" : "");
    }
}

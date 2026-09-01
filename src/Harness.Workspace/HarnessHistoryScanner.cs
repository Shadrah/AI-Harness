using System.Text.Json;
using Harness.Core.Models;

namespace Harness.Workspace;

public static class HarnessHistoryScanner
{
    // JSONL is parsed as a stream. Long-lived root conversations routinely grow
    // beyond 200 MB, so a small whole-file cap selects only tiny subagent logs.
    private const long MaximumSourceBytes = 1024L * 1024 * 1024;
    private static readonly string[] RootContextFiles =
    [
        "AGENTS.md", "CLAUDE.md", "GEMINI.md", ".cursorrules", ".clinerules",
        "CONTRIBUTING.md", "README.md"
    ];
    private static readonly string[] NestedContextFiles =
    [
        Path.Combine(".github", "copilot-instructions.md"),
        Path.Combine(".claude", "CLAUDE.md")
    ];

    public static async Task<HarnessImportInventory> ScanKnownSourcesAsync(
        CancellationToken cancellationToken = default)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return await ScanAsync(
            Path.Combine(home, ".codex", "sessions"),
            Path.Combine(home, ".claude", "projects"),
            cancellationToken);
    }

    public static async Task<HarnessImportInventory> ScanAsync(
        string codexRoot,
        string claudeRoot,
        CancellationToken cancellationToken = default)
    {
        var candidates = new List<HarnessImportCandidate>();
        var diagnostics = new List<string>();
        await ScanRootAsync("Codex", codexRoot, ParseCodexAsync, candidates, diagnostics, cancellationToken);
        await ScanRootAsync("Claude Code", claudeRoot, ParseClaudeAsync, candidates, diagnostics, cancellationToken);
        var projects = candidates
            .GroupBy(candidate => ProjectKey(candidate.SourceHarness, candidate.WorkspacePath), StringComparer.OrdinalIgnoreCase)
            .Select(group => CreateProject(group.ToArray()))
            .OrderByDescending(project => project.UpdatedAt)
            .ToArray();
        return new HarnessImportInventory(projects, diagnostics);
    }

    private static HarnessImportProject CreateProject(IReadOnlyList<HarnessImportCandidate> conversations)
    {
        var ordered = conversations.OrderByDescending(candidate => candidate.UpdatedAt).ToArray();
        var marked = ordered
            .Select((candidate, index) => candidate with { IsPrimaryContinuation = index == 0 })
            .ToArray();
        var newest = marked[0];
        var workspace = NormalizeWorkspacePath(newest.WorkspacePath) ?? newest.WorkspacePath;
        var projectName = ProjectName(workspace);
        return new HarnessImportProject(
            newest.SourceHarness,
            workspace,
            projectName,
            marked,
            DiscoverContextFiles(workspace),
            newest.UpdatedAt);
    }

    private static string ProjectKey(string sourceHarness, string? workspacePath) =>
        $"{sourceHarness}|{NormalizeWorkspacePath(workspacePath) ?? "unknown"}";

    private static string ProjectName(string? workspacePath)
    {
        try
        {
            return string.IsNullOrWhiteSpace(workspacePath)
                ? "Unidentified project"
                : new DirectoryInfo(workspacePath).Name;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return "Unidentified project";
        }
    }

    private static string? NormalizeWorkspacePath(string? workspacePath)
    {
        if (string.IsNullOrWhiteSpace(workspacePath)) return null;
        try
        {
            var fullPath = Path.GetFullPath(workspacePath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!Directory.Exists(fullPath)) return fullPath;
            for (var current = new DirectoryInfo(fullPath); current is not null; current = current.Parent)
            {
                if (Directory.Exists(Path.Combine(current.FullName, ".git"))
                    || Directory.EnumerateFiles(current.FullName, "*.sln", SearchOption.TopDirectoryOnly).Any()
                    || File.Exists(Path.Combine(current.FullName, "package.json"))
                    || File.Exists(Path.Combine(current.FullName, "pyproject.toml"))
                    || File.Exists(Path.Combine(current.FullName, "Cargo.toml"))
                    || File.Exists(Path.Combine(current.FullName, "project.godot")))
                {
                    return current.FullName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                }
            }
            return fullPath;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return workspacePath.Trim();
        }
    }

    private static IReadOnlyList<string> DiscoverContextFiles(string? workspacePath)
    {
        if (string.IsNullOrWhiteSpace(workspacePath) || !Directory.Exists(workspacePath)) return [];
        var paths = RootContextFiles
            .Concat(NestedContextFiles)
            .Select(relative => Path.Combine(workspacePath, relative))
            .Where(File.Exists)
            .Where(path => new FileInfo(path).Length <= 25L * 1024 * 1024)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return paths;
    }

    private static async Task ScanRootAsync(
        string sourceHarness,
        string root,
        Func<string, CancellationToken, Task<HarnessImportCandidate?>> parser,
        List<HarnessImportCandidate> candidates,
        List<string> diagnostics,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(root))
        {
            diagnostics.Add($"{sourceHarness}: no history directory found");
            return;
        }

        foreach (var path in Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var info = new FileInfo(path);
                if (info.Length == 0 || info.Length > MaximumSourceBytes) continue;
                var candidate = await parser(path, cancellationToken);
                if (candidate is not null) candidates.Add(candidate);
            }
            catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
            {
                diagnostics.Add($"{sourceHarness}: skipped {Path.GetFileName(path)} ({exception.Message})");
            }
        }
    }

    private static async Task<HarnessImportCandidate?> ParseCodexAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var messages = new List<ImportMessage>();
        string? id = null;
        string? workspace = null;
        DateTimeOffset? sessionTimestamp = null;

        await foreach (var element in ReadJsonLinesAsync(path, cancellationToken))
        {
            var timestamp = ReadTimestamp(element, "timestamp");
            var type = ReadString(element, "type");
            if (!element.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object) continue;
            if (type == "session_meta")
            {
                if (IsCodexSubagent(payload)
                    || string.Equals(ReadString(payload, "originator"), "harness", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }
                id = ReadString(payload, "id") ?? id;
                workspace = ReadString(payload, "cwd") ?? workspace;
                sessionTimestamp = ReadTimestamp(payload, "timestamp") ?? timestamp ?? sessionTimestamp;
                continue;
            }
            if (type == "event_msg" && ReadString(payload, "type") == "user_message")
            {
                AddMessage(messages, "YOU", ReadString(payload, "message"), timestamp);
                continue;
            }
            if (type == "response_item"
                && ReadString(payload, "type") == "message"
                && ReadString(payload, "role") == "assistant")
            {
                AddMessage(messages, "HARNESS", ReadContent(payload), timestamp);
            }
        }

        return CreateCandidate("Codex", path, id, workspace, sessionTimestamp, messages,
            "Reasoning internals, approvals, and provider-side state are retained only in the copied raw source and are not reconstructed as chat messages.");
    }

    private static async Task<HarnessImportCandidate?> ParseClaudeAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var messages = new List<ImportMessage>();
        string? id = null;
        string? workspace = null;
        DateTimeOffset? sessionTimestamp = null;
        await foreach (var element in ReadJsonLinesAsync(path, cancellationToken))
        {
            var type = ReadString(element, "type");
            if (type is not ("user" or "assistant")) continue;
            id = ReadString(element, "sessionId") ?? id;
            workspace = ReadString(element, "cwd") ?? workspace;
            var timestamp = ReadTimestamp(element, "timestamp");
            sessionTimestamp ??= timestamp;
            if (!element.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object) continue;
            AddMessage(messages, type == "user" ? "YOU" : "HARNESS", ReadContent(message), timestamp);
        }

        return CreateCandidate("Claude Code", path, id, workspace, sessionTimestamp, messages,
            "Tool calls, thinking blocks, checkpoints, and provider-side state remain in the copied raw source but are not reconstructed as chat messages.");
    }

    private static HarnessImportCandidate? CreateCandidate(
        string harness,
        string path,
        string? id,
        string? workspace,
        DateTimeOffset? sessionTimestamp,
        List<ImportMessage> messages,
        string warning)
    {
        if (messages.Count == 0) return null;
        var info = new FileInfo(path);
        var updated = messages.LastOrDefault()?.CreatedAt ?? sessionTimestamp ?? info.LastWriteTimeUtc;
        var firstPrompt = messages.FirstOrDefault(message => message.Role == "YOU")?.Text;
        var title = MakeTitle(firstPrompt, Path.GetFileNameWithoutExtension(path));
        var conversationId = id ?? Path.GetFileNameWithoutExtension(path);
        var warnings = new[]
        {
            warning,
            "Authentication, credentials, and hidden provider instructions are never imported."
        };
        var plan = new ConversationImportPlan(
            $"{harness} history",
            path,
            title,
            messages,
            warnings,
            conversationId,
            workspace,
            updated);
        return new HarnessImportCandidate(harness, path, conversationId, title, workspace, updated, messages.Count, warnings, plan);
    }

    private static async IAsyncEnumerable<JsonElement> ReadJsonLinesAsync(
        string path,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var document = JsonDocument.Parse(line);
            yield return document.RootElement.Clone();
        }
    }

    private static string? ReadContent(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var content)) return ReadString(message, "text");
        if (content.ValueKind == JsonValueKind.String) return content.GetString();
        if (content.ValueKind != JsonValueKind.Array) return null;
        var parts = new List<string>();
        foreach (var part in content.EnumerateArray())
        {
            if (part.ValueKind == JsonValueKind.String) parts.Add(part.GetString()!);
            else if (part.ValueKind == JsonValueKind.Object
                     && ReadString(part, "type") is "input_text" or "output_text" or "text"
                     && ReadString(part, "text") is { } text) parts.Add(text);
        }
        return string.Join("\n", parts);
    }

    private static void AddMessage(List<ImportMessage> messages, string role, string? text, DateTimeOffset? timestamp)
    {
        if (!string.IsNullOrWhiteSpace(text)) messages.Add(new ImportMessage(role, text.Trim(), timestamp));
    }

    private static string MakeTitle(string? prompt, string fallback)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return fallback;
        var line = prompt.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return line.Length <= 72 ? line : line[..69].TrimEnd() + "...";
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static DateTimeOffset? ReadTimestamp(JsonElement element, string name) =>
        ReadString(element, name) is { } value && DateTimeOffset.TryParse(value, out var timestamp) ? timestamp : null;

    private static bool IsCodexSubagent(JsonElement sessionMetadata) =>
        sessionMetadata.TryGetProperty("source", out var source)
        && source.ValueKind == JsonValueKind.Object
        && source.TryGetProperty("subagent", out _);
}

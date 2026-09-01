using System.Text.Json;
using System.Text.RegularExpressions;
using Harness.Core.Models;

namespace Harness.Workspace;

public static partial class ConversationImportScanner
{
    public static async Task<ConversationImportPlan> ScanAsync(
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        var path = Path.GetFullPath(sourcePath);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The conversation export no longer exists.", path);
        }

        var info = new FileInfo(path);
        if (info.Length > 50L * 1024 * 1024)
        {
            throw new InvalidOperationException("Conversation exports are limited to 50 MB.");
        }

        var text = await File.ReadAllTextAsync(path, cancellationToken);
        var extension = Path.GetExtension(path).ToLowerInvariant();
        var messages = extension switch
        {
            ".json" => ParseJson(text),
            ".jsonl" => ParseJsonLines(text),
            _ => ParseTranscript(text)
        };
        if (messages.Count == 0)
        {
            throw new InvalidOperationException(
                "No user or assistant messages were found. Use an exported Markdown, text, JSON, or JSONL conversation.");
        }

        var warnings = new List<string>
        {
            "Provider-hidden prompts, server-side memory, and unavailable tool state cannot be reconstructed."
        };
        return new ConversationImportPlan(
            extension is ".json" or ".jsonl" ? "structured export" : "chat transcript",
            path,
            Path.GetFileNameWithoutExtension(path),
            messages,
            warnings);
    }

    private static List<ImportMessage> ParseJson(string text)
    {
        using var document = JsonDocument.Parse(text);
        var result = new List<ImportMessage>();
        if (document.RootElement.ValueKind == JsonValueKind.Array)
        {
            ReadMessageArray(document.RootElement, result);
        }
        else if (document.RootElement.ValueKind == JsonValueKind.Object)
        {
            if (document.RootElement.TryGetProperty("messages", out var messages)
                && messages.ValueKind == JsonValueKind.Array)
            {
                ReadMessageArray(messages, result);
            }
            else if (document.RootElement.TryGetProperty("conversation", out var conversation)
                     && conversation.ValueKind == JsonValueKind.Array)
            {
                ReadMessageArray(conversation, result);
            }
        }
        return result;
    }

    private static List<ImportMessage> ParseJsonLines(string text)
    {
        var result = new List<ImportMessage>();
        foreach (var line in text.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var document = JsonDocument.Parse(line);
                TryReadMessage(document.RootElement, result);
            }
            catch (JsonException)
            {
                // A preview should skip non-message log records instead of inventing content.
            }
        }
        return result;
    }

    private static void ReadMessageArray(JsonElement array, List<ImportMessage> result)
    {
        foreach (var element in array.EnumerateArray()) TryReadMessage(element, result);
    }

    private static void TryReadMessage(JsonElement element, List<ImportMessage> result)
    {
        if (element.ValueKind != JsonValueKind.Object) return;
        var role = ReadString(element, "role") ?? ReadString(element, "author");
        if (element.TryGetProperty("author", out var author)
            && author.ValueKind == JsonValueKind.Object)
        {
            role ??= ReadString(author, "role");
        }
        var normalizedRole = NormalizeRole(role);
        if (normalizedRole is null) return;

        var content = ReadContent(element);
        if (!string.IsNullOrWhiteSpace(content))
        {
            result.Add(new ImportMessage(normalizedRole, content.Trim()));
        }
    }

    private static string? ReadContent(JsonElement element)
    {
        foreach (var property in new[] { "content", "text", "message" })
        {
            if (!element.TryGetProperty(property, out var value)) continue;
            if (value.ValueKind == JsonValueKind.String) return value.GetString();
            if (value.ValueKind == JsonValueKind.Object)
            {
                if (value.TryGetProperty("text", out var nestedText)
                    && nestedText.ValueKind == JsonValueKind.String) return nestedText.GetString();
                if (value.TryGetProperty("parts", out var parts)
                    && parts.ValueKind == JsonValueKind.Array)
                {
                    return string.Join("\n", parts.EnumerateArray()
                        .Where(part => part.ValueKind == JsonValueKind.String)
                        .Select(part => part.GetString()));
                }
            }
            if (value.ValueKind == JsonValueKind.Array)
            {
                return string.Join("\n", value.EnumerateArray().Select(part =>
                    part.ValueKind == JsonValueKind.String
                        ? part.GetString()
                        : part.TryGetProperty("text", out var partText) ? partText.GetString() : null)
                    .Where(part => !string.IsNullOrWhiteSpace(part)));
            }
        }
        return null;
    }

    private static List<ImportMessage> ParseTranscript(string text)
    {
        var result = new List<ImportMessage>();
        string? role = null;
        var buffer = new List<string>();
        void Flush()
        {
            var content = string.Join("\n", buffer).Trim();
            if (role is not null && content.Length > 0) result.Add(new ImportMessage(role, content));
            buffer.Clear();
        }

        foreach (var line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var match = RoleHeading().Match(line);
            if (!match.Success)
            {
                if (role is not null) buffer.Add(line);
                continue;
            }
            Flush();
            role = NormalizeRole(match.Groups[1].Value);
            var inline = match.Groups[2].Value.Trim();
            if (inline.Length > 0) buffer.Add(inline);
        }
        Flush();
        return result;
    }

    private static string? NormalizeRole(string? role) => role?.Trim().ToLowerInvariant() switch
    {
        "user" or "you" or "human" => "YOU",
        "assistant" or "harness" or "ai" or "claude" or "chatgpt" or "codex" => "HARNESS",
        _ => null
    };

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    [GeneratedRegex(@"^\s*(?:#{1,6}\s*)?(user|you|human|assistant|harness|ai|claude|chatgpt|codex)\s*:?[ \t]*(.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex RoleHeading();
}

using Harness.Core.Models;

namespace Harness.Workspace;

public static class SkillManifestParser
{
    public static (string Name, string Description) Parse(string markdown, string fallbackName)
    {
        var manifest = Analyze(markdown, fallbackName);
        return (manifest.Name, manifest.Description);
    }

    public static SkillManifestInfo Analyze(string markdown, string fallbackName)
    {
        var name = fallbackName;
        var description = string.Empty;
        var fields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var reader = new StringReader(markdown);
        if (!string.Equals(reader.ReadLine()?.Trim(), "---", StringComparison.Ordinal))
            return new SkillManifestInfo(NormalizeName(name), description, false, fields, "Unverified skill format");
        while (reader.ReadLine() is { } line)
        {
            var trimmed = line.Trim();
            if (trimmed == "---") break;
            var separator = trimmed.IndexOf(':');
            if (separator <= 0) continue;
            var key = trimmed[..separator].Trim();
            fields.Add(key);
            var value = Unquote(trimmed[(separator + 1)..].Trim());
            if (key.Equals("name", StringComparison.OrdinalIgnoreCase) && value.Length > 0) name = value;
            if (key.Equals("description", StringComparison.OrdinalIgnoreCase) && value.Length > 0) description = value;
        }
        var compatibility = ClassifyCompatibility(markdown, fields, description);
        return new SkillManifestInfo(NormalizeName(name), description, true, fields, compatibility);
    }

    public static string InferCompatibility(string repository, string path, string markdown)
    {
        var fallback = Path.GetFileName(Path.GetDirectoryName(path.Replace('/', Path.DirectorySeparatorChar)))
            ?? repository.Split('/').Last();
        return Analyze(markdown, fallback).Compatibility;
    }

    public static string InferCategory(string name, string description, string path, string? requestedCategory = null)
    {
        if (!string.IsNullOrWhiteSpace(requestedCategory)
            && !requestedCategory.Equals("All", StringComparison.OrdinalIgnoreCase))
            return requestedCategory;
        var evidence = $"{name} {description} {path}".ToLowerInvariant();
        foreach (var (category, words) in Categories)
        {
            if (words.Any(evidence.Contains)) return category;
        }
        return "Other";
    }

    public static string Slug(string value)
    {
        var chars = value.Trim().ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) ? character : '-')
            .ToArray();
        var slug = string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(slug) ? "skill" : slug;
    }

    private static string NormalizeName(string value) =>
        string.IsNullOrWhiteSpace(value) ? "Unnamed skill" : value.Trim();

    private static string Unquote(string value) =>
        value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\''))
            ? value[1..^1]
            : value;

    private static string ClassifyCompatibility(
        string markdown,
        IReadOnlySet<string> fields,
        string description)
    {
        var claudeFrontmatter = fields.Any(field => ClaudeOnlyFields.Contains(field));
        var claudeBody = markdown.Contains("$ARGUMENTS", StringComparison.Ordinal)
            || markdown.Contains("${CLAUDE_", StringComparison.Ordinal)
            || markdown.Split('\n').Any(line => line.TrimStart().StartsWith("!`", StringComparison.Ordinal));
        var codexExtension = markdown.Contains("agents/openai.yaml", StringComparison.OrdinalIgnoreCase)
            || markdown.Contains("openai.yaml", StringComparison.OrdinalIgnoreCase)
            || fields.Contains("codex-tools");
        if ((claudeFrontmatter || claudeBody) && codexExtension) return "Mixed provider extensions";
        if (claudeFrontmatter || claudeBody) return "Claude Code extension";
        if (codexExtension) return "Codex extension";
        if (fields.Any(field => !AgentSkillFields.Contains(field))) return "Unverified provider extension";
        return string.IsNullOrWhiteSpace(description)
            ? "Unverified skill format"
            : "Portable Agent Skill";
    }

    private static readonly HashSet<string> AgentSkillFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "name", "description", "license", "compatibility", "metadata", "allowed-tools"
    };

    private static readonly HashSet<string> ClaudeOnlyFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "argument-hint", "disable-model-invocation", "user-invocable", "model", "context", "agent", "hooks"
    };

    private static readonly IReadOnlyList<(string Category, string[] Words)> Categories =
    [
        ("Game development", ["game", "unity", "unreal", "godot", "bevy", "pygame", "phaser"]),
        ("Frontend", ["frontend", "react", "vue", "angular", "css", "design system", "web ui"]),
        ("Backend", ["backend", "server", "database", "api", "asp.net", "django", "spring"]),
        ("DevOps", ["devops", "deploy", "docker", "kubernetes", "ci/cd", "github actions"]),
        ("Testing", ["test", "qa", "playwright", "selenium", "verification"]),
        ("Security", ["security", "vulnerability", "threat", "audit", "pentest"]),
        ("Data", ["data", "spreadsheet", "sql", "analytics", "machine learning"]),
        ("Documents", ["document", "pdf", "word", "presentation", "slides"]),
        ("Media", ["image", "audio", "video", "animation", "remotion"]),
        ("Research", ["research", "paper", "citation", "literature"]),
        ("Productivity", ["productivity", "workflow", "planning", "todo", "project management"])
    ];
}

public sealed record SkillManifestInfo(
    string Name,
    string Description,
    bool HasFrontmatter,
    IReadOnlySet<string> Fields,
    string Compatibility);

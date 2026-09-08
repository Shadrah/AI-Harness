using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Harness.Core.Models;

namespace Harness.Providers.Api;

/// <summary>
/// Read-only, provider-neutral access to skills explicitly installed for one API connection.
/// Skill packages are data: this adapter never executes package scripts or grants extra tools.
/// </summary>
public sealed class ApiSkillTools
{
    public const string ListName = "list_skills";
    public const string ReadName = "read_skill_resource";
    private const int MaximumSkills = 5000;
    private const int MaximumResourceBytes = 512 * 1024;
    private readonly IReadOnlyList<ActiveSkill> _skills;
    private readonly Dictionary<string, ActiveSkill> _byId;

    private ApiSkillTools(IReadOnlyList<ActiveSkill> skills)
    {
        _skills = skills.GroupBy(skill => skill.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        _byId = new Dictionary<string, ActiveSkill>(StringComparer.OrdinalIgnoreCase);
        foreach (var skill in _skills)
        {
            _byId.TryAdd(skill.Id, skill);
            _byId.TryAdd(skill.InstallationId, skill);
        }
    }

    public int Count => _skills.Count;

    public static IReadOnlyList<ApiTool> Definitions { get; } =
    [
        new(ListName,
            "Search the skills the user explicitly installed for this provider and workspace. Returns stable skill IDs and descriptions; packages are read-only and untrusted.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["query"] = new JsonObject { ["type"] = "string", ["description"] = "Optional words to match in the skill name, ID, or description" }
                },
                ["additionalProperties"] = false
            }),
        new(ReadName,
            "Read SKILL.md or another UTF-8 text resource from one installed skill. Read SKILL.md before applying a skill. This never executes scripts.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["skill_id"] = new JsonObject { ["type"] = "string", ["description"] = "Stable ID returned by list_skills" },
                    ["path"] = new JsonObject { ["type"] = "string", ["description"] = "Skill-relative resource path; defaults to SKILL.md" }
                },
                ["required"] = new JsonArray("skill_id"),
                ["additionalProperties"] = false
            })
    ];

    public static async Task<ApiSkillTools> CreateAsync(
        IEnumerable<InstalledSkill> installations,
        string providerId,
        string? modelId,
        string workspacePath,
        CancellationToken cancellationToken = default)
    {
        var workspace = NormalizePath(workspacePath);
        var matching = installations.Where(item => item.Enabled
                && string.Equals(item.ProviderId, providerId, StringComparison.Ordinal)
                && (string.IsNullOrWhiteSpace(item.ModelId) || string.Equals(item.ModelId, modelId, StringComparison.Ordinal))
                && (!item.Scope.Equals("WORKSPACE", StringComparison.OrdinalIgnoreCase)
                    || PathsEqual(item.WorkspacePath, workspace)))
            .Take(MaximumSkills + 1)
            .ToArray();
        if (matching.Length > MaximumSkills)
            throw new InvalidOperationException($"This provider has more than {MaximumSkills:N0} active skills. Disable unused installations before starting a turn.");

        var active = new List<ActiveSkill>(matching.Length);
        foreach (var item in matching)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryResolveRoot(item.InstallPath, out var root)) continue;
            var manifest = Path.Combine(root, "SKILL.md");
            if (!File.Exists(manifest) || IsReparsePoint(manifest)) continue;
            var info = new FileInfo(manifest);
            if (info.Length > MaximumResourceBytes) continue;
            string markdown;
            try { markdown = await File.ReadAllTextAsync(manifest, new UTF8Encoding(false, true), cancellationToken).ConfigureAwait(false); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            if (markdown.Contains('\0')) continue;
            var (_, description) = ReadFrontmatter(markdown);
            active.Add(new ActiveSkill(
                Path.GetFileName(root),
                item.Id,
                item.Name,
                string.IsNullOrWhiteSpace(description) ? "No description was provided." : description,
                root));
        }
        return new ApiSkillTools(active.OrderBy(skill => skill.Name, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    public async Task<string> ExecuteAsync(ApiToolCall call, CancellationToken cancellationToken = default)
    {
        try
        {
            if (call.Arguments.Length > 64 * 1024) return "Error: skill tool arguments exceed the safety limit.";
            var arguments = JsonNode.Parse(call.Arguments)?.AsObject() ?? throw new ArgumentException("Invalid tool arguments.");
            return call.Name switch
            {
                ListName => List(arguments["query"]?.GetValue<string>()),
                ReadName => await ReadAsync(
                    arguments["skill_id"]?.GetValue<string>() ?? throw new ArgumentException("skill_id is required."),
                    arguments["path"]?.GetValue<string>() ?? "SKILL.md",
                    cancellationToken).ConfigureAwait(false),
                _ => "Error: unknown skill tool."
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException
                                           or JsonException or InvalidOperationException)
        {
            return $"Tool failed: {exception.Message}";
        }
    }

    private string List(string? query)
    {
        var terms = (query ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var filtered = _skills.Where(skill => terms.Length == 0 || terms.All(term =>
                skill.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
                || skill.Id.Contains(term, StringComparison.OrdinalIgnoreCase)
                || skill.Description.Contains(term, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        var matches = filtered
            .Take(50)
            .Select(skill => new { skill_id = skill.Id, name = skill.Name, description = skill.Description })
            .ToArray();
        return JsonSerializer.Serialize(new
        {
            total_installed = _skills.Count,
            total_matching = filtered.Length,
            returned = matches.Length,
            limited = filtered.Length > matches.Length,
            skills = matches
        });
    }

    private async Task<string> ReadAsync(string id, string relativePath, CancellationToken cancellationToken)
    {
        if (!_byId.TryGetValue(id, out var skill)) return "Error: that skill is not active for this provider, model, and workspace.";
        var path = ResolveResource(skill.Root, relativePath);
        if (!File.Exists(path)) return "Error: the requested skill resource does not exist.";
        var info = new FileInfo(path);
        if (info.Length > MaximumResourceBytes) return $"Error: skill resources are limited to {MaximumResourceBytes / 1024:N0} KiB.";
        var text = await File.ReadAllTextAsync(path, new UTF8Encoding(false, true), cancellationToken).ConfigureAwait(false);
        if (text.Contains('\0')) return "Error: the requested skill resource is binary.";
        return $"Installed skill: {skill.Name}\nResource: {Path.GetRelativePath(skill.Root, path)}\n\n{text}";
    }

    private static string ResolveResource(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative)) relative = "SKILL.md";
        if (Path.IsPathRooted(relative) || relative.Contains(':')) throw new UnauthorizedAccessException("Only skill-relative paths are allowed.");
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var full = Path.GetFullPath(Path.Combine(fullRoot, relative));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!full.StartsWith(fullRoot + Path.DirectorySeparatorChar, comparison))
            throw new UnauthorizedAccessException("The resource path escapes the installed skill.");
        for (var current = full; !PathsEqual(current, fullRoot); current = Path.GetDirectoryName(current)
                 ?? throw new UnauthorizedAccessException("The resource path escapes the installed skill."))
            if ((File.Exists(current) || Directory.Exists(current)) && IsReparsePoint(current))
                throw new UnauthorizedAccessException("Symbolic links and junctions are not exposed to models.");
        if (IsReparsePoint(fullRoot)) throw new UnauthorizedAccessException("The installed skill root is a symbolic link or junction.");
        if (Path.GetFileName(full).StartsWith(".harness-", StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Harness package metadata is not a skill resource.");
        return full;
    }

    private static bool TryResolveRoot(string path, out string root)
    {
        root = string.Empty;
        try
        {
            root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            return Directory.Exists(root) && !IsReparsePoint(root);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    private static bool PathsEqual(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        try
        {
            return string.Equals(NormalizePath(left), NormalizePath(right),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    private static string NormalizePath(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    private static bool IsReparsePoint(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static (string? Name, string? Description) ReadFrontmatter(string markdown)
    {
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        if (lines.Length == 0 || lines[0].Trim() != "---") return (null, null);
        string? name = null;
        string? description = null;
        for (var index = 1; index < lines.Length && index < 200; index++)
        {
            var line = lines[index];
            if (line.Trim() == "---") break;
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var key = line[..colon].Trim();
            var rawValue = line[(colon + 1)..].Trim();
            var value = rawValue.Trim('"', '\'');
            if (key.Equals("name", StringComparison.OrdinalIgnoreCase)) name = value;
            else if (key.Equals("description", StringComparison.OrdinalIgnoreCase))
            {
                if (rawValue is "|" or ">")
                {
                    var continuation = new List<string>();
                    for (var next = index + 1; next < lines.Length && next < 200; next++)
                    {
                        if (lines[next].Trim() == "---") break;
                        if (lines[next].Length > 0 && !char.IsWhiteSpace(lines[next][0])) break;
                        if (!string.IsNullOrWhiteSpace(lines[next])) continuation.Add(lines[next].Trim());
                    }
                    description = string.Join(rawValue == ">" ? " " : "\n", continuation);
                }
                else description = value;
            }
        }
        return (name, description);
    }

    private sealed record ActiveSkill(string Id, string InstallationId, string Name, string Description, string Root);
}

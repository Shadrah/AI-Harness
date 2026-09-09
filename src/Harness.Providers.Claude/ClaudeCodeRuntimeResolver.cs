using System.Diagnostics;

namespace Harness.Providers.Claude;

public static class ClaudeCodeRuntimeResolver
{
    public static ClaudeCodeRuntimeInfo Resolve()
    {
        var configured = Environment.GetEnvironmentVariable("HARNESS_CLAUDE_PATH");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            return new(Path.GetFullPath(configured), "CUSTOM RUNTIME", true);

        if (OperatingSystem.IsWindows())
        {
            var native = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local", "bin", "claude.exe");
            if (File.Exists(native)) return new(native, "CLAUDE NATIVE", true);

            var npm = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "npm", "claude.cmd");
            if (File.Exists(npm)) return new(npm, "SYSTEM CLAUDE CLI", true);
        }

        var resolved = ResolveFromPath();
        return resolved is null
            ? new("claude", "SYSTEM PATH", false)
            : new(resolved, "SYSTEM CLAUDE CLI", true);
    }

    private static string? ResolveFromPath()
    {
        var names = OperatingSystem.IsWindows()
            ? new[] { "claude.exe", "claude.cmd" }
            : new[] { "claude" };
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var name in names)
            {
                try
                {
                    var candidate = Path.Combine(directory, name);
                    if (File.Exists(candidate)) return Path.GetFullPath(candidate);
                }
                catch (Exception exception) when (exception is ArgumentException or NotSupportedException) { }
            }
        }
        return null;
    }

    internal static ProcessStartInfo CreateStartInfo(
        ClaudeCodeRuntimeInfo runtime,
        IEnumerable<string> arguments,
        string? workingDirectory = null,
        string? configurationDirectory = null)
    {
        var args = arguments.ToArray();
        var start = new ProcessStartInfo
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
                ? Environment.CurrentDirectory
                : Path.GetFullPath(workingDirectory)
        };
        start.StandardInputEncoding = new System.Text.UTF8Encoding(false);
        start.StandardOutputEncoding = new System.Text.UTF8Encoding(false);
        start.StandardErrorEncoding = new System.Text.UTF8Encoding(false);

        if (OperatingSystem.IsWindows()
            && (runtime.ExecutablePath.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
                || runtime.ExecutablePath.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)))
        {
            start.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            start.ArgumentList.Add("/d");
            start.ArgumentList.Add("/s");
            start.ArgumentList.Add("/c");
            start.ArgumentList.Add(BuildCommandLine(runtime.ExecutablePath, args));
        }
        else
        {
            start.FileName = runtime.ExecutablePath;
            foreach (var argument in args) start.ArgumentList.Add(argument);
        }

        // Discard only private Agent SDK transport state from a parent process. Provider-supported
        // ANTHROPIC_ variables remain available because Claude Code itself owns those contracts.
        foreach (var key in start.Environment.Keys
                     .Where(key => key.StartsWith("CLAUDE_AGENT_SDK_", StringComparison.OrdinalIgnoreCase))
                     .ToArray())
            start.Environment.Remove(key);
        start.Environment["CLAUDE_AGENT_SDK_CLIENT_APP"] = "harness/0.1";
        start.Environment["CLAUDE_CODE_AUTO_CONNECT_IDE"] = "0";
        start.Environment["CLAUDE_CODE_IDE_SKIP_AUTO_INSTALL"] = "1";
        if (!string.IsNullOrWhiteSpace(configurationDirectory))
            start.Environment["CLAUDE_CONFIG_DIR"] = Path.GetFullPath(configurationDirectory);
        return start;
    }

    private static string BuildCommandLine(string executable, IReadOnlyList<string> arguments)
    {
        static string Quote(string value)
        {
            if (value.Any(character => character is '\r' or '\n' or '\0'))
                throw new ArgumentException("Claude runtime arguments cannot contain control characters.");
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
        return string.Join(" ", new[] { Quote(executable) }.Concat(arguments.Select(Quote)));
    }
}

public sealed record ClaudeCodeRuntimeInfo(string ExecutablePath, string SourceLabel, bool IsResolved);

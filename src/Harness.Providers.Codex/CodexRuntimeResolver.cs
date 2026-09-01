namespace Harness.Providers.Codex;

public static class CodexRuntimeResolver
{
    public static CodexRuntimeInfo Resolve()
    {
        var configured = Environment.GetEnvironmentVariable("HARNESS_CODEX_PATH");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            var path = Path.GetFullPath(configured);
            return new CodexRuntimeInfo(path, "CUSTOM RUNTIME", true, HasRequiredTools(path));
        }

        var bundled = FindBundledRuntime();
        if (bundled is not null)
        {
            return new CodexRuntimeInfo(bundled, "HARNESS BUNDLED", true, HasRequiredTools(bundled));
        }

        var managed = FindManagedRuntime();
        if (managed is not null)
        {
            return new CodexRuntimeInfo(managed, "HARNESS MANAGED", true, HasRequiredTools(managed));
        }

        if (OperatingSystem.IsWindows())
        {
            var npmCommand = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "npm",
                "codex.cmd");
            if (File.Exists(npmCommand))
            {
                return new CodexRuntimeInfo(npmCommand, "SYSTEM CODEX CLI", false, true);
            }
        }

        return new CodexRuntimeInfo("codex", "SYSTEM PATH", false, true);
    }

    public static bool HasRequiredTools(string executablePath)
    {
        if (!File.Exists(executablePath)) return false;
        var directory = Path.GetDirectoryName(Path.GetFullPath(executablePath));
        if (directory is null) return false;
        var hostName = OperatingSystem.IsWindows() ? "codex-code-mode-host.exe" : "codex-code-mode-host";
        return File.Exists(Path.Combine(directory, hostName));
    }

    public static string GetManagedRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Harness",
        "runtimes",
        "codex");

    private static string? FindBundledRuntime()
    {
        var executableName = OperatingSystem.IsWindows() ? "codex.exe" : "codex";
        var runtimeIdentifier = GetRuntimeIdentifier();
        var candidate = Path.Combine(
            AppContext.BaseDirectory,
            "runtimes",
            "codex",
            runtimeIdentifier,
            executableName);
        return File.Exists(candidate) ? candidate : null;
    }

    private static string? FindManagedRuntime()
    {
        var root = GetManagedRoot();
        if (!Directory.Exists(root))
        {
            return null;
        }

        var currentPath = Path.Combine(root, "current.json");
        if (File.Exists(currentPath))
        {
            try
            {
                using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(currentPath));
                var executable = document.RootElement.GetProperty("executablePath").GetString();
                if (!string.IsNullOrWhiteSpace(executable) && File.Exists(executable))
                {
                    return executable;
                }
            }
            catch (System.Text.Json.JsonException)
            {
            }
        }

        var executableName = OperatingSystem.IsWindows() ? "codex.exe" : "codex";
        return Directory.EnumerateFiles(root, executableName, SearchOption.AllDirectories)
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .Select(file => file.FullName)
            .FirstOrDefault();
    }

    private static string GetRuntimeIdentifier()
    {
        var architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
            .ToString()
            .ToLowerInvariant();
        var platform = OperatingSystem.IsWindows()
            ? "win"
            : OperatingSystem.IsMacOS() ? "osx" : "linux";
        return $"{platform}-{architecture}";
    }
}

public sealed record CodexRuntimeInfo(
    string ExecutablePath,
    string SourceLabel,
    bool HarnessOwned,
    bool CodeToolsAvailable = false);

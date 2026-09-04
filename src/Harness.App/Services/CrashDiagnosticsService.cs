using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia.Threading;

namespace Harness.App.Services;

public sealed class CrashDiagnosticsService
{
    private const int RetainedReportCount = 20;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object _gate = new();
    private readonly string _pendingDirectory;
    private readonly string _reportDirectory;
    private string? _currentMarkerPath;
    private int _handlersAttached;
    private int _fatalReportWritten;

    public CrashDiagnosticsService(string? diagnosticsDirectory = null)
    {
        DiagnosticsDirectory = diagnosticsDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Harness",
            "Diagnostics");
        _pendingDirectory = Path.Combine(DiagnosticsDirectory, "pending");
        _reportDirectory = Path.Combine(DiagnosticsDirectory, "reports");
    }

    public static CrashDiagnosticsService Shared { get; } = new();
    public string DiagnosticsDirectory { get; }

    public void AttachProcessHandlers()
    {
        if (Interlocked.Exchange(ref _handlersAttached, 1) != 0) return;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception) WriteFatalReport(exception, "app-domain");
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            if (Volatile.Read(ref _fatalReportWritten) == 0) CompleteSession();
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            _ = WriteNonfatalReportAsync(args.Exception, "unobserved-task");
            args.SetObserved();
        };
        Dispatcher.UIThread.UnhandledException += (_, args) =>
            WriteFatalReport(args.Exception, "ui-dispatcher");
    }

    public Task<CrashRecoveryNotice?> StartSessionAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => StartSession(cancellationToken), cancellationToken);

    public Task CompleteSessionAsync() => Task.Run(CompleteSession);

    private void CompleteSession()
    {
        lock (_gate)
        {
            if (_currentMarkerPath is null) return;
            TryDelete(_currentMarkerPath);
            _currentMarkerPath = null;
        }
    }

    public Task<string?> WriteNonfatalReportAsync(Exception exception, string origin) =>
        Task.Run(() => WriteReport(exception, origin, false));

    private CrashRecoveryNotice? StartSession(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(_pendingDirectory);
            Directory.CreateDirectory(_reportDirectory);

            var recovered = new List<(PendingSession Marker, string ReportPath)>();
            foreach (var markerPath in Directory.EnumerateFiles(_pendingDirectory, "*.json"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var marker = TryReadMarker(markerPath);
                if (marker is null)
                {
                    TryDelete(markerPath);
                    continue;
                }
                if (IsProcessStillRunning(marker)) continue;
                var reportPath = WriteAbandonedSessionReport(marker);
                if (reportPath is not null) recovered.Add((marker, reportPath));
                TryDelete(markerPath);
            }

            var process = Process.GetCurrentProcess();
            var current = new PendingSession(
                Guid.NewGuid().ToString("N"),
                Environment.ProcessId,
                process.StartTime.ToUniversalTime(),
                DateTimeOffset.UtcNow,
                ProductVersion());
            _currentMarkerPath = Path.Combine(_pendingDirectory, current.SessionId + ".json");
            WriteAtomic(_currentMarkerPath, JsonSerializer.Serialize(current, JsonOptions));
            TrimOldReports();

            if (recovered.Count == 0) return null;
            var latest = recovered.OrderByDescending(item => item.Marker.StartedAtUtc).First();
            return new CrashRecoveryNotice(recovered.Count, latest.ReportPath, latest.Marker.StartedAtUtc);
        }
    }

    private void WriteFatalReport(Exception exception, string origin)
    {
        if (Interlocked.Exchange(ref _fatalReportWritten, 1) != 0) return;
        try { WriteReport(exception, origin, true); }
        catch { /* Diagnostics must never replace the original failure. */ }
    }

    private string? WriteReport(Exception exception, string origin, bool fatal)
    {
        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(_reportDirectory);
                var report = new SanitizedDiagnosticReport(
                    Guid.NewGuid().ToString("N"),
                    DateTimeOffset.UtcNow,
                    fatal ? "fatal-exception" : "nonfatal-exception",
                    origin,
                    ProductVersion(),
                    RuntimeInformation.FrameworkDescription,
                    RuntimeInformation.OSDescription,
                    RuntimeInformation.ProcessArchitecture.ToString(),
                    BuildExceptionChain(exception),
                    BuildFrames(exception));
                var path = ReportPath(report.Id, report.TimestampUtc);
                WriteAtomic(path, JsonSerializer.Serialize(report, JsonOptions));
                TrimOldReports();
                return path;
            }
        }
        catch
        {
            return null;
        }
    }

    private string? WriteAbandonedSessionReport(PendingSession marker)
    {
        try
        {
            var report = new SanitizedDiagnosticReport(
                Guid.NewGuid().ToString("N"),
                DateTimeOffset.UtcNow,
                "unclean-shutdown",
                "session-marker",
                marker.ProductVersion,
                RuntimeInformation.FrameworkDescription,
                RuntimeInformation.OSDescription,
                RuntimeInformation.ProcessArchitecture.ToString(),
                [],
                []);
            var path = ReportPath(report.Id, report.TimestampUtc);
            WriteAtomic(path, JsonSerializer.Serialize(report, JsonOptions));
            return path;
        }
        catch
        {
            return null;
        }
    }

    private string ReportPath(string id, DateTimeOffset timestamp) => Path.Combine(
        _reportDirectory,
        $"harness-{timestamp:yyyyMMdd-HHmmss}-{id[..8]}.json");

    private static IReadOnlyList<SanitizedException> BuildExceptionChain(Exception exception)
    {
        var result = new List<SanitizedException>();
        for (var current = exception; current is not null && result.Count < 8; current = current.InnerException)
            result.Add(new SanitizedException(current.GetType().FullName ?? current.GetType().Name, $"0x{current.HResult:X8}"));
        return result;
    }

    private static IReadOnlyList<string> BuildFrames(Exception exception)
    {
        var frames = new List<string>();
        foreach (var frame in new StackTrace(exception, false).GetFrames() ?? [])
        {
            var method = frame.GetMethod();
            if (method is null) continue;
            var owner = method.DeclaringType?.FullName ?? "unknown";
            frames.Add($"{owner}.{method.Name}() IL+{frame.GetILOffset():X}");
            if (frames.Count == 40) break;
        }
        return frames;
    }

    private PendingSession? TryReadMarker(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Length > 16 * 1024) return null;
            return JsonSerializer.Deserialize<PendingSession>(File.ReadAllText(path));
        }
        catch
        {
            return null;
        }
    }

    private static bool IsProcessStillRunning(PendingSession marker)
    {
        try
        {
            using var process = Process.GetProcessById(marker.ProcessId);
            return !process.HasExited
                   && Math.Abs((process.StartTime.ToUniversalTime() - marker.ProcessStartedUtc.UtcDateTime).TotalSeconds) < 2;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch
        {
            return true;
        }
    }

    private void TrimOldReports()
    {
        try
        {
            foreach (var path in Directory.EnumerateFiles(_reportDirectory, "harness-*.json")
                         .Select(path => new FileInfo(path))
                         .OrderByDescending(file => file.LastWriteTimeUtc)
                         .Skip(RetainedReportCount)
                         .Select(file => file.FullName))
                TryDelete(path);
        }
        catch { }
    }

    private static void WriteAtomic(string path, string content)
    {
        var staging = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(staging, content);
            File.Move(staging, path, true);
        }
        finally
        {
            TryDelete(staging);
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }

    private static string ProductVersion() =>
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? Assembly.GetEntryAssembly()?.GetName().Version?.ToString()
        ?? "unknown";

    private sealed record PendingSession(
        string SessionId,
        int ProcessId,
        DateTimeOffset ProcessStartedUtc,
        DateTimeOffset StartedAtUtc,
        string ProductVersion);

    private sealed record SanitizedDiagnosticReport(
        string Id,
        DateTimeOffset TimestampUtc,
        string Kind,
        string Origin,
        string ProductVersion,
        string Runtime,
        string OperatingSystem,
        string Architecture,
        IReadOnlyList<SanitizedException> Exceptions,
        IReadOnlyList<string> Frames);

    private sealed record SanitizedException(string Type, string HResult);
}

public sealed record CrashRecoveryNotice(int RecoveredSessionCount, string ReportPath, DateTimeOffset PreviousSessionStartedAtUtc);

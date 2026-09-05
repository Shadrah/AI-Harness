using System.Diagnostics;
using System.Text;

namespace Harness.App.Services;

/// <summary>
/// Owns one redirected user shell. Process creation, stream reads, and shutdown never run on
/// Avalonia's dispatcher; consumers receive small output chunks on worker threads.
/// </summary>
public sealed class TerminalSession : IAsyncDisposable
{
    private readonly Process _process;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _inputGate = new(1, 1);
    private readonly Task _standardOutputTask;
    private readonly Task _standardErrorTask;
    private int _disposed;

    private TerminalSession(Process process, string workspacePath, string shellName)
    {
        _process = process;
        WorkspacePath = workspacePath;
        ShellName = shellName;
        _standardOutputTask = ReadStreamAsync(process.StandardOutput, TerminalOutputKind.StandardOutput, _lifetime.Token);
        _standardErrorTask = ReadStreamAsync(process.StandardError, TerminalOutputKind.StandardError, _lifetime.Token);
        _ = ObserveExitAsync();
    }

    public string WorkspacePath { get; }
    public string ShellName { get; }
    public bool IsRunning => Volatile.Read(ref _disposed) == 0 && !_process.HasExited;

    public event EventHandler<TerminalOutputEventArgs>? OutputReceived;
    public event EventHandler<TerminalStateEventArgs>? StateChanged;

    public static Task<TerminalSession> StartAsync(
        string workspacePath,
        CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(workspacePath);
        return Task.Run(() => StartCore(fullPath, cancellationToken), cancellationToken);
    }

    public async Task SendAsync(string command, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!IsRunning) throw new InvalidOperationException("The shell is not running. Restart it before sending another command.");
        if (command.IndexOf('\0') >= 0) throw new InvalidOperationException("Terminal input cannot contain a null character.");
        if (command.Length > 64 * 1024) throw new InvalidOperationException("Terminal input is limited to 64 KiB per command.");

        await _inputGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _process.StandardInput.WriteLineAsync(command.AsMemory(), cancellationToken).ConfigureAwait(false);
            await _process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (IOException exception) when (!IsRunning)
        {
            throw new InvalidOperationException("The shell closed before it accepted the command.", exception);
        }
        finally
        {
            _inputGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel();

        try { _process.StandardInput.Close(); } catch { }
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(750));
            await _process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await Task.Run(() =>
            {
                try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch { }
            }).ConfigureAwait(false);
        }

        try { await Task.WhenAll(_standardOutputTask, _standardErrorTask).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        _process.Dispose();
        _inputGate.Dispose();
        _lifetime.Dispose();
    }

    private static TerminalSession StartCore(string workspacePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(workspacePath))
            throw new DirectoryNotFoundException($"The terminal workspace does not exist: {workspacePath}");

        var shell = ResolveShell();
        var start = new ProcessStartInfo
        {
            FileName = shell.FileName,
            WorkingDirectory = workspacePath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in shell.Arguments) start.ArgumentList.Add(argument);
        start.Environment["TERM"] = "xterm-256color";
        start.Environment["NO_COLOR"] = "1";

        var process = Process.Start(start)
            ?? throw new InvalidOperationException($"Could not start {shell.DisplayName}.");
        process.EnableRaisingEvents = true;
        return new TerminalSession(process, workspacePath, shell.DisplayName);
    }

    private static ShellDescriptor ResolveShell()
    {
        if (OperatingSystem.IsWindows())
        {
            var systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
            var windowsPowerShell = Path.Combine(
                Directory.GetParent(systemDirectory)?.FullName ?? Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
            if (File.Exists(windowsPowerShell))
                return new ShellDescriptor(windowsPowerShell, "Windows PowerShell", ["-NoLogo", "-NoProfile", "-Command", "-"]);

            var commandProcessor = Environment.GetEnvironmentVariable("ComSpec");
            if (!string.IsNullOrWhiteSpace(commandProcessor) && File.Exists(commandProcessor))
                return new ShellDescriptor(commandProcessor, "Command Prompt", ["/D", "/Q"]);
            throw new InvalidOperationException("Harness could not find a Windows command shell.");
        }

        var configuredShell = Environment.GetEnvironmentVariable("SHELL");
        var fileName = !string.IsNullOrWhiteSpace(configuredShell) && File.Exists(configuredShell)
            ? configuredShell
            : "/bin/sh";
        return new ShellDescriptor(fileName, Path.GetFileName(fileName), []);
    }

    private async Task ReadStreamAsync(
        StreamReader reader,
        TerminalOutputKind kind,
        CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (count == 0) break;
                PublishOutput(new string(buffer, 0, count), kind);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (Volatile.Read(ref _disposed) != 0) { }
        catch (IOException exception) when (Volatile.Read(ref _disposed) == 0)
        {
            PublishOutput($"\n[output stream closed: {exception.Message}]\n", TerminalOutputKind.System);
        }
    }

    private async Task ObserveExitAsync()
    {
        try
        {
            await _process.WaitForExitAsync(_lifetime.Token).ConfigureAwait(false);
            if (Volatile.Read(ref _disposed) == 0)
                PublishState(false, $"Shell exited with code {_process.ExitCode}");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (ObjectDisposedException) { }
    }

    private void PublishOutput(string text, TerminalOutputKind kind)
    {
        try { OutputReceived?.Invoke(this, new TerminalOutputEventArgs(text, kind)); }
        catch { /* A presentation subscriber must never stop the process readers. */ }
    }

    private void PublishState(bool isRunning, string status)
    {
        try { StateChanged?.Invoke(this, new TerminalStateEventArgs(isRunning, status)); }
        catch { }
    }

    private sealed record ShellDescriptor(string FileName, string DisplayName, IReadOnlyList<string> Arguments);
}

public enum TerminalOutputKind
{
    StandardOutput,
    StandardError,
    System
}

public sealed class TerminalOutputEventArgs(string text, TerminalOutputKind kind) : EventArgs
{
    public string Text { get; } = text;
    public TerminalOutputKind Kind { get; } = kind;
}

public sealed class TerminalStateEventArgs(bool isRunning, string status) : EventArgs
{
    public bool IsRunning { get; } = isRunning;
    public string Status { get; } = status;
}

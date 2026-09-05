using System.Text;
using System.Text.RegularExpressions;
using Harness.App.Services;

namespace Harness.App.ViewModels;

public sealed class TerminalWindowViewModel : ObservableObject
{
    private const int MaximumTranscriptCharacters = 180_000;
    private const int MaximumPendingCharacters = 96_000;
    private static readonly Regex AnsiSequence = new(
        "\\x1B(?:\\[[0-?]*[ -/]*[@-~]|[@-_])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly object _pendingLock = new();
    private readonly StringBuilder _pending = new();
    private readonly List<string> _history = [];
    private string _outputText = "";
    private string _commandText = "";
    private string _shellName = "STARTING SHELL";
    private string _status = "STARTING";
    private bool _isRunning;
    private int _historyIndex;
    private long _droppedCharacters;

    public TerminalWindowViewModel(string workspacePath)
    {
        WorkspacePath = Path.GetFullPath(workspacePath);
        WorkspaceName = new DirectoryInfo(WorkspacePath).Name;
    }

    public string WorkspaceName { get; }
    public string WorkspacePath { get; }
    public string ShellName { get => _shellName; private set => SetProperty(ref _shellName, value); }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string OutputText { get => _outputText; private set => SetProperty(ref _outputText, value); }
    public string CommandText
    {
        get => _commandText;
        set
        {
            if (SetProperty(ref _commandText, value)) RaisePropertyChanged(nameof(CanSend));
        }
    }
    public bool IsRunning { get => _isRunning; private set => SetProperty(ref _isRunning, value); }
    public bool CanSend => IsRunning && !string.IsNullOrWhiteSpace(CommandText);

    public void SetSession(TerminalSession session)
    {
        ShellName = session.ShellName.ToUpperInvariant();
        SetState(session.IsRunning, session.IsRunning ? "READY" : "SHELL CLOSED");
        EnqueueSystem($"Harness terminal · {session.ShellName}\nWorkspace · {WorkspacePath}\n\n");
    }

    public void SetState(bool running, string status)
    {
        IsRunning = running;
        Status = status.ToUpperInvariant();
        RaisePropertyChanged(nameof(CanSend));
    }

    public string TakeCommand()
    {
        // Keyboard input can arrive before the redirected shell has finished starting.
        // Never consume text unless a session is ready to accept it.
        if (!IsRunning) return "";
        var command = CommandText.TrimEnd('\r', '\n');
        if (string.IsNullOrWhiteSpace(command)) return "";
        CommandText = "";
        _history.RemoveAll(item => string.Equals(item, command, StringComparison.Ordinal));
        _history.Add(command);
        if (_history.Count > 100) _history.RemoveAt(0);
        _historyIndex = _history.Count;
        RaisePropertyChanged(nameof(CanSend));
        EnqueueSystem($"› {command}\n");
        return command;
    }

    public void NavigateHistory(int direction)
    {
        if (_history.Count == 0) return;
        _historyIndex = Math.Clamp(_historyIndex + direction, 0, _history.Count);
        CommandText = _historyIndex == _history.Count ? "" : _history[_historyIndex];
        RaisePropertyChanged(nameof(CanSend));
    }

    public void Enqueue(TerminalOutputEventArgs output) => Enqueue(output.Text);

    public void EnqueueSystem(string text) => Enqueue(text);

    public bool FlushPendingOutput()
    {
        string update;
        long dropped;
        lock (_pendingLock)
        {
            if (_pending.Length == 0 && _droppedCharacters == 0) return false;
            update = _pending.ToString();
            _pending.Clear();
            dropped = _droppedCharacters;
            _droppedCharacters = 0;
        }

        update = CleanTerminalText(update);
        if (dropped > 0)
            update = $"\n[Harness skipped {dropped:N0} characters of terminal output to keep the interface responsive.]\n" + update;

        var next = OutputText + update;
        if (next.Length > MaximumTranscriptCharacters)
        {
            var remove = next.Length - MaximumTranscriptCharacters;
            var lineBreak = next.IndexOf('\n', remove);
            next = lineBreak >= 0 ? next[(lineBreak + 1)..] : next[^MaximumTranscriptCharacters..];
            next = "[Earlier terminal output was trimmed.]\n" + next;
        }
        OutputText = next;
        return true;
    }

    public void Clear()
    {
        lock (_pendingLock)
        {
            _pending.Clear();
            _droppedCharacters = 0;
        }
        OutputText = "";
    }

    private void Enqueue(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        lock (_pendingLock)
        {
            _pending.Append(text);
            if (_pending.Length <= MaximumPendingCharacters) return;
            var overflow = _pending.Length - MaximumPendingCharacters;
            _pending.Remove(0, overflow);
            _droppedCharacters += overflow;
        }
    }

    private static string CleanTerminalText(string value)
    {
        value = AnsiSequence.Replace(value, "").Replace("\r\n", "\n", StringComparison.Ordinal);
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (character == '\b')
            {
                if (builder.Length > 0 && builder[^1] != '\n') builder.Length--;
                continue;
            }
            if (character == '\r')
            {
                builder.Append('\n');
                continue;
            }
            if (character is '\n' or '\t' || !char.IsControl(character)) builder.Append(character);
        }
        return builder.ToString();
    }
}

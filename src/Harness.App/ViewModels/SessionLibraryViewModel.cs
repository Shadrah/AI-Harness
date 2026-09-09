using System.Collections.ObjectModel;
using Harness.Core.Models;

namespace Harness.App.ViewModels;

public sealed class SessionLibraryViewModel : ObservableObject
{
    private SessionLibraryItem? _selectedSession;
    private string _status = "Loading task history…";
    private bool _isBusy;

    public ObservableCollection<SessionLibraryItem> Sessions { get; } = [];

    public SessionLibraryItem? SelectedSession
    {
        get => _selectedSession;
        set
        {
            if (!SetProperty(ref _selectedSession, value)) return;
            RaisePropertyChanged(nameof(HasSelection));
            RaisePropertyChanged(nameof(CanOpen));
            RaisePropertyChanged(nameof(ArchiveAction));
        }
    }

    public string Status { get => _status; set => SetProperty(ref _status, value); }
    public bool IsBusy { get => _isBusy; set => SetProperty(ref _isBusy, value); }
    public bool HasSelection => SelectedSession is not null && !IsBusy;
    public bool CanOpen => SelectedSession is { IsArchived: false } && !IsBusy;
    public string ArchiveAction => SelectedSession?.IsArchived == true ? "RESTORE" : "ARCHIVE";

    public void Apply(IReadOnlyList<StoredSession> sessions, string? preferredSessionId, string activeSessionId)
    {
        Sessions.Clear();
        foreach (var session in sessions)
            Sessions.Add(SessionLibraryItem.FromStored(session, session.Id == activeSessionId));
        SelectedSession = Sessions.FirstOrDefault(item => item.SessionId == preferredSessionId)
            ?? Sessions.FirstOrDefault(item => item.SessionId == activeSessionId)
            ?? Sessions.FirstOrDefault();
        Status = sessions.Count == 500
            ? "Showing the newest 500 matching tasks"
            : $"{sessions.Count} matching task{(sessions.Count == 1 ? string.Empty : "s")}";
    }

    public void SetBusy(bool busy)
    {
        IsBusy = busy;
        RaisePropertyChanged(nameof(HasSelection));
        RaisePropertyChanged(nameof(CanOpen));
    }
}

public sealed record SessionLibraryItem(
    string SessionId,
    string Title,
    string Provider,
    string Updated,
    string State,
    string StateColor,
    bool IsArchived,
    bool IsActive)
{
    public static SessionLibraryItem FromStored(StoredSession session, bool active)
    {
        var provider = string.IsNullOrWhiteSpace(session.ProviderId)
            ? "Provider not selected"
            : string.IsNullOrWhiteSpace(session.ModelId)
                ? session.ProviderId
                : $"{session.ProviderId} · {session.ModelId}";
        return new SessionLibraryItem(
            session.Id,
            session.Title,
            provider,
            $"Updated {session.UpdatedAt.ToLocalTime():g}",
            active ? "CURRENT" : session.ArchivedAt is null ? "ACTIVE" : "ARCHIVED",
            active ? "#65C7D0" : session.ArchivedAt is null ? "#8993A3" : "#E2A84A",
            session.ArchivedAt is not null,
            active);
    }
}

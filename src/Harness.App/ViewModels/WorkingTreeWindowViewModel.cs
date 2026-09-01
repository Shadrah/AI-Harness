using System.Collections.ObjectModel;
using Harness.Core.Models;
using Harness.Workspace;

namespace Harness.App.ViewModels;

public sealed class WorkingTreeWindowViewModel : ObservableObject
{
    private WorkingTreeFileItem? _selectedFile;
    private string _diffText = "Select a changed file to inspect its diff.";
    private string _status = "CHECKING";
    private string _branch = "GIT —";
    private string? _repositoryRoot;
    private string _activity = "Ready";

    public ObservableCollection<WorkingTreeFileItem> Files { get; } = [];
    public ObservableCollection<DiffLineItem> DiffLines { get; } = [];

    public WorkingTreeFileItem? SelectedFile
    {
        get => _selectedFile;
        set => SetProperty(ref _selectedFile, value);
    }

    public string DiffText
    {
        get => _diffText;
        set
        {
            if (SetProperty(ref _diffText, value))
            {
                ApplyDiff(UnifiedDiffParser.Parse(value));
            }
        }
    }

    public string DiffSummary { get; private set; } = "+0  −0";

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public string Branch
    {
        get => _branch;
        private set => SetProperty(ref _branch, value);
    }

    public string? RepositoryRoot
    {
        get => _repositoryRoot;
        private set => SetProperty(ref _repositoryRoot, value);
    }

    public string Activity
    {
        get => _activity;
        set => SetProperty(ref _activity, value);
    }

    public void Apply(WorkingTreeSnapshot snapshot)
    {
        Files.Clear();
        RepositoryRoot = snapshot.RepositoryRoot;
        if (!snapshot.IsRepository)
        {
            Status = "NOT A REPOSITORY";
            Branch = "GIT —";
            DiffText = snapshot.Error ?? "This workspace is not a Git repository.";
            return;
        }

        foreach (var file in snapshot.Files)
        {
            Files.Add(WorkingTreeFileItem.FromModel(file));
        }
        Status = snapshot.Files.Count == 0 ? "CLEAN" : $"{snapshot.Files.Count} CHANGED";
        Branch = snapshot.Branch ?? "UNKNOWN";
        if (snapshot.Files.Count == 0)
        {
            SelectedFile = null;
            DiffText = "Working tree clean.";
        }
        else
        {
            SelectedFile = Files[0];
        }
    }

    private void ApplyDiff(DiffDocument document)
    {
        DiffLines.Clear();
        foreach (var line in document.Lines)
        {
            DiffLines.Add(DiffLineItem.FromModel(line));
        }
        DiffSummary = $"+{document.AddedLines}  −{document.RemovedLines}";
        RaisePropertyChanged(nameof(DiffSummary));
    }
}

public sealed record DiffLineItem(
    string OldLine,
    string NewLine,
    string Marker,
    string Text,
    string Background,
    string Foreground)
{
    public static DiffLineItem FromModel(DiffLine line)
    {
        var colors = line.Kind switch
        {
            DiffLineKind.Added => ("#142A22", "#9BE3AC"),
            DiffLineKind.Removed => ("#321D22", "#F1A0AA"),
            DiffLineKind.Hunk => ("#182630", "#65C7D0"),
            DiffLineKind.Metadata => ("Transparent", "#8993A3"),
            _ => ("Transparent", "#D8DEE8")
        };
        return new DiffLineItem(
            line.OldLineNumber?.ToString() ?? string.Empty,
            line.NewLineNumber?.ToString() ?? string.Empty,
            line.Marker,
            line.Text,
            colors.Item1,
            colors.Item2);
    }
}

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
    private DiffHunkItem? _selectedHunk;

    public ObservableCollection<WorkingTreeFileItem> Files { get; } = [];
    public ObservableCollection<DiffLineItem> DiffLines { get; } = [];
    public ObservableCollection<DiffHunkItem> Hunks { get; } = [];

    public WorkingTreeFileItem? SelectedFile
    {
        get => _selectedFile;
        set
        {
            if (SetProperty(ref _selectedFile, value)) RaiseHunkState();
        }
    }

    public string DiffText
    {
        get => _diffText;
        set
        {
            if (SetProperty(ref _diffText, value))
            {
                ApplyHunks(value);
            }
        }
    }

    public DiffHunkItem? SelectedHunk
    {
        get => _selectedHunk;
        set
        {
            if (!SetProperty(ref _selectedHunk, value)) return;
            ApplyDiff(UnifiedDiffParser.Parse(value?.Source.Patch ?? _diffText));
            RaiseHunkState();
        }
    }

    public bool HasHunks => Hunks.Count > 0;
    public bool CanStageHunk => SelectedHunk?.Source.Source == DiffHunkSource.WorkingTree;
    public bool CanUnstageHunk => SelectedHunk?.Source.Source == DiffHunkSource.Staged;
    public bool CanDiscardHunk => CanStageHunk && SelectedFile?.Source.IsUntracked == false;
    public string HunkStatus => SelectedHunk is null
        ? "NO TEXT HUNKS"
        : $"HUNK {SelectedHunk.Source.Index} OF {Hunks.Count} · {SelectedHunk.SourceLabel}";

    public void BeginDiffLoad()
    {
        _diffText = "Loading diff…";
        RaisePropertyChanged(nameof(DiffText));
        Hunks.Clear();
        SelectedHunk = null;
        ApplyDiff(UnifiedDiffParser.Parse(_diffText));
        RaiseHunkState();
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

    public void Apply(WorkingTreeSnapshot snapshot, string? preferredPath = null)
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
            SelectedFile = Files.FirstOrDefault(file =>
                string.Equals(file.RelativePath, preferredPath, StringComparison.OrdinalIgnoreCase))
                ?? Files[0];
        }
    }

    private void ApplyHunks(string diff)
    {
        Hunks.Clear();
        foreach (var hunk in UnifiedDiffParser.ParseHunks(diff))
            Hunks.Add(DiffHunkItem.FromModel(hunk));
        SelectedHunk = Hunks.FirstOrDefault();
        if (SelectedHunk is null) ApplyDiff(UnifiedDiffParser.Parse(diff));
        RaiseHunkState();
    }

    private void RaiseHunkState()
    {
        RaisePropertyChanged(nameof(HasHunks));
        RaisePropertyChanged(nameof(CanStageHunk));
        RaisePropertyChanged(nameof(CanUnstageHunk));
        RaisePropertyChanged(nameof(CanDiscardHunk));
        RaisePropertyChanged(nameof(HunkStatus));
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

public sealed record DiffHunkItem(DiffHunk Source, string DisplayName, string SourceLabel)
{
    public static DiffHunkItem FromModel(DiffHunk hunk)
    {
        var source = hunk.Source == DiffHunkSource.Staged ? "STAGED" : "WORKING TREE";
        return new DiffHunkItem(
            hunk,
            $"{source} · Hunk {hunk.Index} · +{hunk.AddedLines} −{hunk.RemovedLines}",
            source);
    }

    public override string ToString() => DisplayName;
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

using System.Collections.ObjectModel;
using Harness.Workspace;

namespace Harness.App.ViewModels;

public sealed class DiffWindowViewModel
{
    public DiffWindowViewModel(string title, string diff)
    {
        Title = title;
        var document = UnifiedDiffParser.Parse(diff);
        foreach (var line in document.Lines) Lines.Add(DiffLineItem.FromModel(line));
        Summary = $"+{document.AddedLines}  −{document.RemovedLines}";
    }

    public string Title { get; }
    public string Summary { get; }
    public ObservableCollection<DiffLineItem> Lines { get; } = [];
}

using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace Harness.App.ViewModels;

public sealed class BatchObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> values)
    {
        var replacement = values.ToArray();
        if (this.SequenceEqual(replacement)) return;
        CheckReentrancy();
        Items.Clear();
        foreach (var value in replacement) Items.Add(value);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}

using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
namespace Stacker.Desktop.ViewModels;

/// <summary>Replaces a virtualized list with one notification instead of one per source row.</summary>
public sealed class BatchCollection<T>:ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> values)
    {
        CheckReentrancy();Items.Clear();foreach(var value in values)Items.Add(value);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}

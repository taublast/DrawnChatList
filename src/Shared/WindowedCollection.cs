using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace DrawnChatList;

/// <summary>
/// ObservableCollection with single-notification batch operations, used as a sliding window
/// over a larger virtual dataset:
/// - AddRange: forward load, one Add event at the tail index
/// - InsertRange(0,..): backward load, one Add event at index 0 (structure-preserving prepend)
/// - RemoveRange: window trim (memory cap), one Remove event for the whole block
/// - ReplaceRange: window rebase (jump), one Reset event
/// Kept as a minimal self-contained reference of the event contract DrawnUI's windowed
/// LoadMore expects; the sample itself uses AppoMobi.Specials.ObservableRangeCollection
/// (10.0.1+) which provides the same index-aware batch notifications.
/// </summary>
public class WindowedCollection<T> : ObservableCollection<T>
{
    public void AddRange(IList<T> items)
    {
        if (items == null || items.Count == 0)
            return;

        int start = Items.Count;
        foreach (var item in items)
            Items.Add(item);

        RaiseBatchChange(new NotifyCollectionChangedEventArgs(
            NotifyCollectionChangedAction.Add, items.ToList(), start));
    }

    public void InsertRange(int index, IList<T> items)
    {
        if (items == null || items.Count == 0)
            return;

        for (int i = 0; i < items.Count; i++)
            Items.Insert(index + i, items[i]);

        RaiseBatchChange(new NotifyCollectionChangedEventArgs(
            NotifyCollectionChangedAction.Add, items.ToList(), index));
    }

    public void RemoveRange(int index, int count)
    {
        if (count <= 0)
            return;

        var removed = new List<T>(count);
        for (int i = 0; i < count; i++)
        {
            removed.Add(Items[index]);
            Items.RemoveAt(index);
        }

        RaiseBatchChange(new NotifyCollectionChangedEventArgs(
            NotifyCollectionChangedAction.Remove, removed, index));
    }

    /// <summary>
    /// Same as ReplaceRange here: full window rebase raising one Reset. Name kept in parity with
    /// AppoMobi.Specials.ObservableRangeCollection (where plain ReplaceRange raises Replace instead,
    /// which preserves scroll — wrong for a window rebase) so call sites read identically.
    /// </summary>
    public void ReplaceRangeReset(IList<T> items) => ReplaceRange(items);

    public void ReplaceRange(IList<T> items)
    {
        Items.Clear();
        if (items != null)
        {
            foreach (var item in items)
                Items.Add(item);
        }

        RaiseBatchChange(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    private void RaiseBatchChange(NotifyCollectionChangedEventArgs args)
    {
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(args);
    }
}

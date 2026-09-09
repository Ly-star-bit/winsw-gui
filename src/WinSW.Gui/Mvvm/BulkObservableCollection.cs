using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace WinSW.Gui.Mvvm
{
    /// <summary>
    /// An <see cref="ObservableCollection{T}"/> that can be refilled in one notification.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Clearing and re-adding raises one event per item, and a bound list answers each of them
    /// — measuring, laying out, and for a virtualizing panel deciding again which containers it
    /// needs. Refilling five thousand lines that way is five thousand rounds of that, and it is
    /// what the log viewer did on every keystroke in the filter box.
    /// </para>
    /// <para>
    /// Only for wholesale replacement. Appending is still one event per line on purpose: a
    /// Reset tells the list it knows nothing, which costs the scroll position, and a handful of
    /// appended lines is cheap to report properly.
    /// </para>
    /// </remarks>
    public sealed class BulkObservableCollection<T> : ObservableCollection<T>
    {
        private static readonly PropertyChangedEventArgs CountChanged = new(nameof(Count));

        /// <summary>Indexer notification, spelled the way WPF's own collections spell it.</summary>
        private static readonly PropertyChangedEventArgs IndexerChanged = new("Item[]");

        private static readonly NotifyCollectionChangedEventArgs Reset =
            new(NotifyCollectionChangedAction.Reset);

        /// <summary>Replaces the contents, announcing it once.</summary>
        public void ReplaceAll(IEnumerable<T> items)
        {
            this.Items.Clear();
            foreach (var item in items)
            {
                this.Items.Add(item);
            }

            this.OnPropertyChanged(CountChanged);
            this.OnPropertyChanged(IndexerChanged);
            this.OnCollectionChanged(Reset);
        }
    }
}

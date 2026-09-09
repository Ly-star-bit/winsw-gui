using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using WinSW.Gui.Mvvm;
using Xunit;

namespace WinSW.Gui.Tests
{
    public class BulkObservableCollectionTests
    {
        [Fact]
        public void ReplacingAnnouncesItselfExactlyOnce()
        {
            var collection = new BulkObservableCollection<string> { "old" };

            var events = new List<NotifyCollectionChangedAction>();
            collection.CollectionChanged += (_, e) => events.Add(e.Action);

            collection.ReplaceAll(new[] { "a", "b", "c", "d", "e" });

            Assert.Equal(new[] { NotifyCollectionChangedAction.Reset }, events);
            Assert.Equal(new[] { "a", "b", "c", "d", "e" }, collection);
        }

        /// <summary>
        /// A bound list watches Count and the indexer as well as the collection itself; a
        /// replacement that changed them silently would leave a stale count on screen.
        /// </summary>
        [Fact]
        public void ReplacingRaisesCountAndTheIndexer()
        {
            var collection = new BulkObservableCollection<string> { "old" };

            var properties = new List<string?>();
            ((INotifyPropertyChanged)collection).PropertyChanged += (_, e) => properties.Add(e.PropertyName);

            collection.ReplaceAll(new[] { "a", "b" });

            Assert.Contains("Count", properties);
            Assert.Contains("Item[]", properties);
        }

        [Fact]
        public void ReplacingWithNothingEmptiesIt()
        {
            var collection = new BulkObservableCollection<string> { "a", "b" };

            collection.ReplaceAll(System.Array.Empty<string>());

            Assert.Empty(collection);
        }

        /// <summary>
        /// Appending is still reported per item on purpose: a Reset tells the list it knows
        /// nothing, which costs the scroll position the log viewer is trying to keep.
        /// </summary>
        [Fact]
        public void AddingStillReportsAnAdd()
        {
            var collection = new BulkObservableCollection<string>();

            var events = new List<NotifyCollectionChangedAction>();
            collection.CollectionChanged += (_, e) => events.Add(e.Action);

            collection.Add("a");

            Assert.Equal(new[] { NotifyCollectionChangedAction.Add }, events);
        }
    }
}

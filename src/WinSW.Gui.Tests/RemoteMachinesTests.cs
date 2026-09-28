using System.Collections.ObjectModel;
using System.Linq;
using WinSW.Gui.ViewModels;
using Xunit;

namespace WinSW.Gui.Tests
{
    /// <summary>
    /// The machines the Remote page offers in its drop-down: the last few it connected to, most
    /// recent first, each once.
    /// </summary>
    public class RemoteMachinesTests
    {
        [Fact]
        public void ANewMachineGoesFirst()
        {
            var recent = new ObservableCollection<string> { "web01", "db01" };

            Assert.True(MachineHistory.Remember(recent, "app01"));

            Assert.Equal(new[] { "app01", "web01", "db01" }, recent);
        }

        [Fact]
        public void AMachineConnectedToAgainMovesUpInsteadOfRepeating()
        {
            var recent = new ObservableCollection<string> { "web01", "db01", "app01" };

            Assert.True(MachineHistory.Remember(recent, "app01"));

            Assert.Equal(new[] { "app01", "web01", "db01" }, recent);
        }

        /// <summary>Windows does not tell WEB01 from web01; the spelling used last is kept.</summary>
        [Fact]
        public void NamesAreComparedWithoutCaseAndKeepTheLatestSpelling()
        {
            var recent = new ObservableCollection<string> { "web01", "db01" };

            Assert.True(MachineHistory.Remember(recent, "DB01"));

            Assert.Equal(new[] { "DB01", "web01" }, recent);
        }

        [Fact]
        public void TheMachineAlreadyFirstChangesNothing()
        {
            var recent = new ObservableCollection<string> { "web01", "db01" };

            Assert.False(MachineHistory.Remember(recent, "  web01 "));

            Assert.Equal(new[] { "web01", "db01" }, recent);
        }

        [Fact]
        public void ABlankNameIsNotRemembered()
        {
            var recent = new ObservableCollection<string> { "web01" };

            Assert.False(MachineHistory.Remember(recent, "   "));

            Assert.Equal(new[] { "web01" }, recent);
        }

        [Fact]
        public void OnlyTheLastFewAreKept()
        {
            var recent = new ObservableCollection<string>(Enumerable.Range(1, MachineHistory.Limit).Select(i => "m" + i));

            Assert.True(MachineHistory.Remember(recent, "new"));

            Assert.Equal(MachineHistory.Limit, recent.Count);
            Assert.Equal("new", recent[0]);
            Assert.DoesNotContain("m" + MachineHistory.Limit, recent);
        }

        /// <summary>
        /// The drop-down's selected item is moved, not removed and put back: a removal is what
        /// makes an editable ComboBox let go of its text.
        /// </summary>
        [Fact]
        public void AnEarlierMachineIsMovedNotRemovedAndInserted()
        {
            var recent = new ObservableCollection<string> { "web01", "db01", "app01" };
            var actions = new System.Collections.Generic.List<System.Collections.Specialized.NotifyCollectionChangedAction>();
            recent.CollectionChanged += (_, e) => actions.Add(e.Action);

            MachineHistory.Remember(recent, "db01");

            Assert.Equal(new[] { System.Collections.Specialized.NotifyCollectionChangedAction.Move }, actions);
        }

        [Fact]
        public void TheSavedListIsCleanedUpOnLoad()
        {
            var saved = new[] { " web01 ", null, string.Empty, "WEB01", "db01" }
                .Concat(Enumerable.Range(1, MachineHistory.Limit).Select(i => "m" + i));

            var loaded = MachineHistory.Load(saved);

            Assert.Equal(MachineHistory.Limit, loaded.Count);
            Assert.Equal(new[] { "web01", "db01", "m1" }, loaded.Take(3));
        }

        [Fact]
        public void NothingSavedLoadsAsAnEmptyList()
        {
            Assert.Empty(MachineHistory.Load(null));
        }
    }
}

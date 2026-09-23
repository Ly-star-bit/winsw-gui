using System.Linq;
using WinSW.Gui.Services;
using Xunit;

namespace WinSW.Gui.Tests
{
    /// <summary>The comparison the History window shows before a version is restored.</summary>
    public class TextDiffTests
    {
        [Fact]
        public void TheSameTextHasNothingToShow()
        {
            Assert.Empty(TextDiff.Compare("<service>\n  <id>a</id>\n</service>\n", "<service>\r\n  <id>a</id>\r\n</service>"));
        }

        [Fact]
        public void AChangedLineShowsItsOldFormThenItsNew()
        {
            var lines = TextDiff.Compare("<service>\n  <id>a</id>\n</service>", "<service>\n  <id>b</id>\n</service>");

            Assert.Equal(
                new[] { (DiffKind.Same, "<service>"), (DiffKind.Removed, "  <id>a</id>"), (DiffKind.Added, "  <id>b</id>"), (DiffKind.Same, "</service>") },
                lines.Select(l => (l.Kind, l.Text)));
            Assert.Equal(2, lines[1].OldNumber);
            Assert.Null(lines[1].NewNumber);
            Assert.Equal(2, lines[2].NewNumber);
        }

        [Fact]
        public void InsertionsAndRemovalsKeepTheirLineNumbers()
        {
            var lines = TextDiff.Full(new[] { "a", "b", "c", "d" }, new[] { "a", "c", "x", "d" });

            Assert.Equal(
                new[] { "  a", "− b", "  c", "+ x", "  d" },
                lines.Select(l => l.Marker + " " + l.Text));
            Assert.Equal(new int?[] { 1, 2, 3, null, 4 }, lines.Select(l => l.OldNumber));
            Assert.Equal(new int?[] { 1, null, 2, 3, 4 }, lines.Select(l => l.NewNumber));
        }

        /// <summary>Long unchanged stretches are folded, with the context kept around each change.</summary>
        [Fact]
        public void UnchangedStretchesAreFolded()
        {
            string old = string.Join("\n", Enumerable.Range(1, 20).Select(i => "line " + i));
            string changed = old.Replace("line 10", "line ten");

            var lines = TextDiff.Compare(old, changed);

            Assert.Equal(DiffKind.Gap, lines[0].Kind);
            Assert.Equal(6, lines[0].Count);
            Assert.Equal("line 7", lines[1].Text);
            Assert.Equal(DiffKind.Gap, lines[^1].Kind);
            Assert.Equal(7, lines[^1].Count);
            Assert.Equal(1, lines.Count(l => l.Kind == DiffKind.Removed));
            Assert.Equal(1, lines.Count(l => l.Kind == DiffKind.Added));
        }

        [Fact]
        public void AnEmptySideIsAllAddedOrAllRemoved()
        {
            Assert.All(TextDiff.Compare(string.Empty, "a\nb"), l => Assert.Equal(DiffKind.Added, l.Kind));
            Assert.All(TextDiff.Compare("a\nb", string.Empty), l => Assert.Equal(DiffKind.Removed, l.Kind));
        }
    }
}

using System;
using System.Linq;
using WinSW.Gui.Services;
using WinSW.Gui.ViewModels;
using Xunit;

namespace WinSW.Gui.Tests
{
    /// <summary>
    /// The log viewer's lines are items of their own, not their text: a restart loop writes the
    /// same lines every cycle, and a list of strings scrolled to the first cycle's copy. The
    /// error count follows the lines shown, down as well as up.
    /// </summary>
    public class LogLineBufferTests
    {
        /// <summary>
        /// A list finds an item by comparing it with its items. Two lines that read the same
        /// must still be two items, or scrolling to the second lands on the first.
        /// </summary>
        [Fact]
        public void LinesThatReadTheSameAreDifferentItems()
        {
            var buffer = new LogLineBuffer(100, _ => true);

            buffer.Append(new LogLine("INFO:     Application startup failed. Exiting."));
            buffer.Append(new LogLine("INFO:     Application startup failed. Exiting."));

            Assert.False(buffer.Visible[0].Equals(buffer.Visible[1]));
            Assert.Equal(1, buffer.Visible.IndexOf(buffer.Visible[1]));
        }

        /// <summary>
        /// uvicorn's lines carry no timestamp, so every cycle of a restart loop is the same
        /// lines, blank ones included. The newest copy is found where it is: at the end.
        /// </summary>
        [Fact]
        public void ARestartLoopsNewestLinesAreFoundAtTheEnd()
        {
            var buffer = new LogLineBuffer(100, _ => true);
            string[] cycle = { "INFO:     Started server process [4242]", "ERROR:    [Errno 10048] address already in use", string.Empty };

            for (int i = 0; i < 5; i++)
            {
                foreach (string text in cycle)
                {
                    buffer.Append(new LogLine(text));
                }
            }

            int last = buffer.Visible.Count - 1;
            Assert.Equal(last, buffer.Visible.IndexOf(buffer.Visible[last]));
            Assert.Equal(last - 1, buffer.Visible.IndexOf(buffer.Visible[last - 1]));
            Assert.Equal(5, buffer.ErrorCount);
        }

        /// <summary>The count only went up, and still counted errors the buffer had let go of.</summary>
        [Fact]
        public void AnErrorLeavingTheBufferIsCountedOff()
        {
            var buffer = new LogLineBuffer(3, _ => true);
            buffer.Append(new LogLine("ERROR first"));
            buffer.Append(new LogLine("b"));
            buffer.Append(new LogLine("c"));
            Assert.Equal(1, buffer.ErrorCount);

            bool shifted = buffer.Append(new LogLine("d"));

            Assert.True(shifted);
            Assert.Equal(0, buffer.ErrorCount);
            Assert.Equal(new[] { "b", "c", "d" }, buffer.Visible.Select(l => l.Text));
            Assert.Equal(3, buffer.Count);
        }

        /// <summary>
        /// A line the filter hides was never shown or counted, so its leaving takes nothing off
        /// the screen or the count.
        /// </summary>
        [Fact]
        public void AHiddenLineLeavingTheBufferChangesNothingShown()
        {
            var buffer = new LogLineBuffer(3, line => !line.Text.Contains("noise", StringComparison.Ordinal));
            buffer.Append(new LogLine("ERROR noise"));
            buffer.Append(new LogLine("ERROR real"));
            buffer.Append(new LogLine("b"));
            Assert.Equal(1, buffer.ErrorCount);

            bool shifted = buffer.Append(new LogLine("c"));

            Assert.False(shifted);
            Assert.Equal(1, buffer.ErrorCount);
            Assert.Equal(new[] { "ERROR real", "b", "c" }, buffer.Visible.Select(l => l.Text));
        }

        /// <summary>
        /// Hours of a restart loop through a full buffer: the count is always the errors
        /// actually on screen, and the screen is always the buffer's last lines.
        /// </summary>
        [Fact]
        public void TheCountStaysTheErrorsOnScreenThroughALongLoop()
        {
            const int capacity = 5000;
            var buffer = new LogLineBuffer(capacity, line => line.Text.Length > 0);
            string[] cycle =
            {
                "INFO:     Started server process [4242]",
                "INFO:     Waiting for application startup.",
                "ERROR:    Traceback (most recent call last):",
                "  File \"main.py\", line 3, in <module>",
                "ModuleNotFoundError: No module named 'app'",
                string.Empty,
                "WARNING:  retrying",
            };

            const int total = 12000;
            for (int i = 0; i < total; i++)
            {
                buffer.Append(new LogLine(cycle[i % cycle.Length]));
                if (i % 500 == 0)
                {
                    Assert.Equal(buffer.Visible.Count(l => l.IsError), buffer.ErrorCount);
                }
            }

            var expected = Enumerable.Range(total - capacity, capacity)
                .Select(i => cycle[i % cycle.Length])
                .Where(text => text.Length > 0);

            Assert.Equal(capacity, buffer.Count);
            Assert.Equal(expected, buffer.Visible.Select(l => l.Text));
            Assert.Equal(buffer.Visible.Count(l => l.IsError), buffer.ErrorCount);
        }

        /// <summary>
        /// A filter can let one copy of a line through and not another — a regular expression
        /// that ran out of time on one of them. The hidden copy's leaving must not take the
        /// shown one, which reads the same, off the screen.
        /// </summary>
        [Fact]
        public void AHiddenCopyLeavingDoesNotTakeTheShownCopy()
        {
            var hidden = new LogLine("ERROR boom");
            var buffer = new LogLineBuffer(3, line => !ReferenceEquals(line, hidden));
            buffer.Append(hidden);
            var shown = new LogLine("ERROR boom");
            buffer.Append(shown);
            buffer.Append(new LogLine("b"));

            bool shifted = buffer.Append(new LogLine("c"));

            Assert.False(shifted);
            Assert.Same(shown, buffer.Visible[0]);
            Assert.Equal(1, buffer.ErrorCount);
            Assert.Equal(new[] { "ERROR boom", "b", "c" }, buffer.Visible.Select(l => l.Text));
        }

        /// <summary>A new filter shows, and counts, only what it lets through.</summary>
        [Fact]
        public void RebuildingCountsWhatTheFilterLetsThrough()
        {
            string needle = string.Empty;
            var buffer = new LogLineBuffer(100, line => line.Text.Contains(needle, StringComparison.OrdinalIgnoreCase));
            buffer.Append(new LogLine("ERROR db timeout"));
            buffer.Append(new LogLine("ERROR port in use"));
            buffer.Append(new LogLine("INFO db ready"));
            Assert.Equal(2, buffer.ErrorCount);

            needle = "db";
            buffer.Rebuild();

            Assert.Equal(new[] { "ERROR db timeout", "INFO db ready" }, buffer.Visible.Select(l => l.Text));
            Assert.Equal(1, buffer.ErrorCount);
            Assert.Equal(3, buffer.Count);

            needle = string.Empty;
            buffer.Rebuild();

            Assert.Equal(3, buffer.Visible.Count);
            Assert.Equal(2, buffer.ErrorCount);
        }

        /// <summary>A batch bigger than the buffer keeps its last lines, and counts only those.</summary>
        [Fact]
        public void ReplacingKeepsTheLastLinesAndCountsThem()
        {
            var buffer = new LogLineBuffer(3, _ => true);
            buffer.Append(new LogLine("ERROR old"));

            buffer.ReplaceAll(new[] { new LogLine("ERROR 1"), new LogLine("ERROR 2"), new LogLine("3"), new LogLine("4") });

            Assert.Equal(new[] { "ERROR 2", "3", "4" }, buffer.Visible.Select(l => l.Text));
            Assert.Equal(1, buffer.ErrorCount);
            Assert.Equal(3, buffer.Count);
        }

        [Fact]
        public void ClearingForgetsTheLinesAndTheCount()
        {
            var buffer = new LogLineBuffer(10, _ => true);
            buffer.Append(new LogLine("ERROR a"));
            buffer.Append(new LogLine("b"));

            buffer.Clear();

            Assert.Empty(buffer.Visible);
            Assert.Equal(0, buffer.Count);
            Assert.Equal(0, buffer.ErrorCount);
        }

        /// <summary>
        /// At most one colour applies: a warning that mentions an error is an error, which is
        /// the order the old converter tried them in.
        /// </summary>
        [Theory]
        [InlineData("ERROR:    Exception in ASGI application", true, false)]
        [InlineData("WARNING:  error reading config, using defaults", true, false)]
        [InlineData("WARNING:  StatReload detected changes", false, true)]
        [InlineData("INFO:     Uvicorn running on http://0.0.0.0:8000", false, false)]
        [InlineData("", false, false)]
        public void ALineIsAnErrorAWarningOrNeither(string text, bool isError, bool isWarning)
        {
            var line = new LogLine(text);

            Assert.Equal(isError, line.IsError);
            Assert.Equal(isWarning, line.IsWarning);
            Assert.Equal(text, line.ToString());
        }
    }
}

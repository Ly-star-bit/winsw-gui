using System;
using System.Collections.Generic;
using WinSW.Gui.Mvvm;
using WinSW.Gui.Services;

namespace WinSW.Gui.ViewModels
{
    /// <summary>
    /// The last lines read from a log, the ones among them a filter lets through, and how many
    /// of those look like errors.
    /// </summary>
    /// <remarks>
    /// Kept apart from the log viewer so that it can be tested on its own: which line leaves
    /// the screen when the buffer is full, and what that does to the error count, is where it
    /// went wrong. The count only ever went up, and still counted errors the buffer had long
    /// let go of.
    /// </remarks>
    internal sealed class LogLineBuffer
    {
        private readonly LinkedList<LogLine> history = new();
        private readonly int capacity;
        private readonly Func<LogLine, bool> isVisible;

        /// <param name="capacity">How many lines are held, shown or not.</param>
        /// <param name="isVisible">
        /// The filter. Asked once per line as it arrives, and for every line held again by
        /// <see cref="Rebuild"/>, which is to be called whenever the filter changes.
        /// </param>
        public LogLineBuffer(int capacity, Func<LogLine, bool> isVisible)
        {
            this.capacity = capacity;
            this.isVisible = isVisible;
        }

        /// <summary>
        /// The lines shown, which is the buffer with the filter applied. Bulk so that
        /// re-filtering announces itself once rather than once per line.
        /// </summary>
        public BulkObservableCollection<LogLine> Visible { get; } = new();

        /// <summary>How many lines are held, shown or not.</summary>
        public int Count => this.history.Count;

        /// <summary>Error-looking lines among <see cref="Visible"/>.</summary>
        public int ErrorCount { get; private set; }

        /// <summary>Adds a line at the end, letting go of the oldest when the buffer is full.</summary>
        /// <returns>
        /// Whether a line left the top of <see cref="Visible"/>, which moves every line below
        /// it up by one.
        /// </returns>
        public bool Append(LogLine line)
        {
            bool shifted = false;

            this.history.AddLast(line);
            if (this.history.Count > this.capacity)
            {
                var dropped = this.history.First!.Value;
                this.history.RemoveFirst();

                // Taken off the screen only when it is the line there, and counted off only
                // then: a line the filter hides was never shown or counted. Compared as the
                // same line rather than the same text, which a restart loop repeats.
                if (this.Visible.Count > 0 && ReferenceEquals(this.Visible[0], dropped))
                {
                    this.Visible.RemoveAt(0);
                    shifted = true;
                    if (dropped.IsError)
                    {
                        this.ErrorCount--;
                    }
                }
            }

            if (this.isVisible(line))
            {
                this.Visible.Add(line);
                if (line.IsError)
                {
                    this.ErrorCount++;
                }
            }

            return shifted;
        }

        /// <summary>
        /// Replaces everything held with <paramref name="lines"/>, or with the last of them
        /// when there are more than the buffer holds, and shows them in one announcement.
        /// </summary>
        public void ReplaceAll(IReadOnlyList<LogLine> lines)
        {
            this.history.Clear();
            for (int i = Math.Max(0, lines.Count - this.capacity); i < lines.Count; i++)
            {
                this.history.AddLast(lines[i]);
            }

            this.Rebuild();
        }

        /// <summary>Applies the filter again to every line held, and counts again.</summary>
        public void Rebuild()
        {
            // Built to one side and handed over whole. This runs on every keystroke in the
            // filter box, against a buffer of up to five thousand lines.
            int errors = 0;
            var visible = new List<LogLine>(this.history.Count);
            foreach (var line in this.history)
            {
                if (this.isVisible(line))
                {
                    visible.Add(line);
                    if (line.IsError)
                    {
                        errors++;
                    }
                }
            }

            this.Visible.ReplaceAll(visible);
            this.ErrorCount = errors;
        }

        public void Clear()
        {
            this.history.Clear();
            this.Visible.Clear();
            this.ErrorCount = 0;
        }
    }
}

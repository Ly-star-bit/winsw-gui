using System.Collections.Generic;
using WinSW.Gui.Services;

namespace WinSW.Gui.ViewModels
{
    /// <summary>Which error line "next error" (F3) and "previous error" (Shift+F3) go to.</summary>
    /// <remarks>
    /// Kept apart from the log viewer so that it can be tested on its own. "Next error" used to
    /// start at the oldest of up to five thousand lines and go only forward, while what matters
    /// after a crash is the latest failure: at the bottom, a press per error away.
    /// </remarks>
    internal static class LogErrorNavigation
    {
        /// <summary>The error line to go to, among the lines shown.</summary>
        /// <param name="lines">The lines shown, oldest first.</param>
        /// <param name="from">
        /// The line the last jump landed on, or -1 when there is none to go on from: nothing
        /// has been jumped to since the lines were refilled or Follow was last turned on.
        /// Next then starts at the top, and previous at the bottom.
        /// </param>
        /// <param name="forward">Towards the newest line (F3) rather than the oldest (Shift+F3).</param>
        /// <param name="following">
        /// Follow is on. The eye is on the newest lines, so either key goes to the newest error
        /// rather than to the top of the buffer; from there the keys go on either way.
        /// </param>
        /// <returns>
        /// The index of the line, or -1 when none of the lines is an error. Past either end the
        /// search goes round to the other, as a search in an editor does.
        /// </returns>
        public static int Find(IReadOnlyList<LogLine> lines, int from, bool forward, bool following)
        {
            int count = lines.Count;
            if (count == 0)
            {
                return -1;
            }

            if (following)
            {
                // Looking up from just below the last line finds the newest error.
                from = count;
                forward = false;
            }
            else if (from < 0 || from >= count)
            {
                // Nothing to go on from. Just above the first line for next, and just below the
                // last for previous: starting previous at -1 would go round to the line above
                // the last and pass over the last line itself.
                from = forward ? -1 : count;
            }

            for (int step = 1; step <= count; step++)
            {
                int index = forward ? (from + step) % count : (from - step + count) % count;
                if (lines[index].IsError)
                {
                    return index;
                }
            }

            return -1;
        }
    }
}

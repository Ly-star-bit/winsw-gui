using System.Collections.Generic;
using System.Linq;
using WinSW.Gui.Services;
using WinSW.Gui.ViewModels;
using Xunit;

namespace WinSW.Gui.Tests
{
    /// <summary>
    /// "Next error" started at the oldest of up to five thousand lines and went only forward,
    /// while what matters after a crash is the latest failure. F3 and Shift+F3 go either way,
    /// and with Follow on the first press goes to the newest error.
    /// </summary>
    public class LogErrorNavigationTests
    {
        // Errors at 1, 3 and 6 of eight lines.
        private static readonly IReadOnlyList<LogLine> Lines = Make(
            "INFO:     Started server process [4242]",
            "ERROR:    [Errno 10048] address already in use",
            "INFO:     Waiting for application startup.",
            "Traceback: ModuleNotFoundError: No module named 'app'",
            "INFO:     Application startup complete.",
            string.Empty,
            "FATAL: the database is gone",
            "INFO:     Shutting down");

        [Fact]
        public void WithFollowOnEitherKeyGoesToTheNewestError()
        {
            Assert.Equal(6, LogErrorNavigation.Find(Lines, -1, forward: true, following: true));
            Assert.Equal(6, LogErrorNavigation.Find(Lines, -1, forward: false, following: true));
        }

        /// <summary>
        /// A place jumped to before Follow was turned on again is not where the eye is: Follow
        /// is at the bottom.
        /// </summary>
        [Fact]
        public void WithFollowOnAnEarlierPlaceIsNotGoneOnFrom()
        {
            Assert.Equal(6, LogErrorNavigation.Find(Lines, 1, forward: true, following: true));
        }

        [Fact]
        public void PreviousFromTheNewestGoesToTheOneBefore()
        {
            Assert.Equal(3, LogErrorNavigation.Find(Lines, 6, forward: false, following: false));
            Assert.Equal(1, LogErrorNavigation.Find(Lines, 3, forward: false, following: false));
        }

        [Fact]
        public void NextGoesToTheNextNewer()
        {
            Assert.Equal(3, LogErrorNavigation.Find(Lines, 1, forward: true, following: false));
            Assert.Equal(6, LogErrorNavigation.Find(Lines, 3, forward: true, following: false));
        }

        [Fact]
        public void NextAfterTheNewestGoesRoundToTheOldest()
        {
            Assert.Equal(1, LogErrorNavigation.Find(Lines, 6, forward: true, following: false));
        }

        [Fact]
        public void PreviousBeforeTheOldestGoesRoundToTheNewest()
        {
            Assert.Equal(6, LogErrorNavigation.Find(Lines, 1, forward: false, following: false));
        }

        /// <summary>With Follow off and nothing jumped to yet, next starts at the top, as it always did.</summary>
        [Fact]
        public void WithNothingToGoOnFromNextStartsAtTheTop()
        {
            Assert.Equal(1, LogErrorNavigation.Find(Lines, -1, forward: true, following: false));
        }

        /// <summary>Previous starts at the bottom, and does not pass over the last line on the way round.</summary>
        [Fact]
        public void WithNothingToGoOnFromPreviousStartsAtTheBottom()
        {
            var lines = Make("a", "ERROR one", "b", "ERROR last");

            Assert.Equal(3, LogErrorNavigation.Find(lines, -1, forward: false, following: false));
        }

        /// <summary>A place past the end — the lines were shortened under it — counts as none.</summary>
        [Fact]
        public void APlaceNoLongerAmongTheLinesCountsAsNone()
        {
            Assert.Equal(1, LogErrorNavigation.Find(Lines, 42, forward: true, following: false));
            Assert.Equal(6, LogErrorNavigation.Find(Lines, 42, forward: false, following: false));
        }

        [Fact]
        public void ASingleErrorIsFoundAgainFromItself()
        {
            var lines = Make("a", "ERROR only", "b");

            Assert.Equal(1, LogErrorNavigation.Find(lines, 1, forward: true, following: false));
            Assert.Equal(1, LogErrorNavigation.Find(lines, 1, forward: false, following: false));
        }

        [Fact]
        public void NoErrorsIsNowhereToGo()
        {
            var lines = Make("a", "WARNING: slow", "b");

            Assert.Equal(-1, LogErrorNavigation.Find(lines, -1, forward: true, following: true));
            Assert.Equal(-1, LogErrorNavigation.Find(lines, 0, forward: true, following: false));
            Assert.Equal(-1, LogErrorNavigation.Find(lines, 0, forward: false, following: false));
            Assert.Equal(-1, LogErrorNavigation.Find(new List<LogLine>(), -1, forward: true, following: true));
        }

        /// <summary>
        /// A restart loop writes the same error every cycle. The newest copy is the one gone to,
        /// not the first line that reads the same.
        /// </summary>
        [Fact]
        public void TheNewestOfARepeatedErrorIsTheOneGoneTo()
        {
            var lines = Make("ERROR:    [Errno 10048] address already in use", "INFO", "ERROR:    [Errno 10048] address already in use", "INFO");

            Assert.Equal(2, LogErrorNavigation.Find(lines, -1, forward: true, following: true));
        }

        private static IReadOnlyList<LogLine> Make(params string[] texts) => texts.Select(t => new LogLine(t)).ToList();
    }
}

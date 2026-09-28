using System;
using System.IO;
using System.Linq;
using WinSW.Gui.Services;
using Xunit;

namespace WinSW.Gui.Tests
{
    /// <summary>
    /// What the unattended alert's run posts — the line saying why included, as the console's own
    /// alert has it — where it may keep its log and state, and which builds can run it at all.
    /// Reading the event back and the folder's owner need Windows and are not tested here.
    /// </summary>
    public class UnattendedAlertRunTests
    {
        private const string CauseFormat = "Last line of {0}: {1}";

        private static readonly ScmFailure Crash = new(ScmFailureKind.Crashed, "My App", 3, "3");

        /// <summary>The console leaves crashes to the run; they used to reach the chat without why.</summary>
        [Fact]
        public void TheLineSayingWhyGoesUnderTheMessage()
        {
            string text = UnattendedAlertRun.Message(Crash, "HOST", "myapp", 0, new ErrorLine("myapp.err.log", "ModuleNotFoundError: No module named 'main'"), Format, CauseFormat, ErrorText);

            Assert.Equal("M.Alert.Crashed|HOST|myapp|3\nLast line of myapp.err.log: ModuleNotFoundError: No module named 'main'", text);
        }

        /// <summary>The count of what was held back stays with the message it belongs to, above the cause.</summary>
        [Fact]
        public void TheCountOfWhatWasHeldBackComesBeforeTheCause()
        {
            string text = UnattendedAlertRun.Message(Crash, "HOST", "myapp", 4, new ErrorLine("myapp.err.log", "boom"), Format, CauseFormat, ErrorText);

            Assert.Equal("M.Alert.Crashed|HOST|myapp|3 M.Alert.Held|4\nLast line of myapp.err.log: boom", text);
        }

        [Fact]
        public void WithoutACauseTheMessageIsAsBefore()
        {
            Assert.Equal("M.Alert.Crashed|HOST|myapp|3", UnattendedAlertRun.Message(Crash, "HOST", "myapp", 0, null, Format, CauseFormat, ErrorText));
            Assert.Equal(
                "M.Alert.StartFailed|HOST|myapp|error 1069",
                UnattendedAlertRun.Message(new ScmFailure(ScmFailureKind.StartFailed, "My App", 1069, "%%1069"), "HOST", "myapp", 0, null, Format, CauseFormat, ErrorText));
        }

        /// <summary>
        /// Only a service that ran and ended is given its program's last line. One that could not
        /// be started, or did not reach the service control manager in time, may have written no
        /// start of its own, and the line would be an earlier run's.
        /// </summary>
        [Theory]
        [InlineData(ScmFailureKind.Crashed, true)]
        [InlineData(ScmFailureKind.EndedWithError, true)]
        [InlineData(ScmFailureKind.EndedWithCode, true)]
        [InlineData(ScmFailureKind.StartFailed, false)]
        [InlineData(ScmFailureKind.StartTimedOut, false)]
        public void OnlyAServiceThatRanIsGivenItsLastLine(ScmFailureKind kind, bool quoted)
        {
            Assert.Equal(quoted, UnattendedAlertRun.QuotesCause(kind));
        }

        /// <summary>
        /// The run never makes the machine folder: made by it, the folder would take ProgramData's
        /// permissions, under which any user may create files in it.
        /// </summary>
        [Fact]
        public void AMissingFolderIsNeitherUsedNorMade()
        {
            string folder = Path.Combine(Path.GetTempPath(), "winsw-gui-tests", Guid.NewGuid().ToString("N"));

            Assert.False(UnattendedAlertRun.MayUse(folder));
            Assert.False(Directory.Exists(folder));
        }

        private static string Format(string key, object?[] args) => string.Join("|", new object?[] { key }.Concat(args));

        private static string ErrorText(int code) => "error " + code;
    }
}

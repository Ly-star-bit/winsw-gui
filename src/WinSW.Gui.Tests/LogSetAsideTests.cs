using System;
using System.IO;
using WinSW.Gui.Services;
using Xunit;

namespace WinSW.Gui.Tests
{
    /// <summary>
    /// Roll mode's .old files are in the viewer's list, and the .old file is what the wrapper
    /// replaces the log onto at every start. On Windows before POSIX rename semantics a file
    /// held open cannot be replaced, so a viewer left on the run before a crash failed the next
    /// start. A reader holds such a file only while it reads it, and says when it was replaced.
    /// </summary>
    public sealed class LogSetAsideTests : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "winsw-gui-old-" + Guid.NewGuid().ToString("n"));
        private readonly string log;
        private readonly string old;

        public LogSetAsideTests()
        {
            Directory.CreateDirectory(this.directory);
            this.log = Path.Combine(this.directory, "svc.err.log");
            this.old = this.log + ".old";
        }

        public void Dispose() => Directory.Delete(this.directory, recursive: true);

        [Theory]
        [InlineData("svc.err.log.old", true)]
        [InlineData(@"D:\logs\SVC.OUT.LOG.OLD", true)]
        [InlineData("svc.err.log", false)]
        [InlineData("svc.0.err.log", false)]
        [InlineData("svc.wrapper.log", false)]
        public void OnlyTheOldFilesAreSetAside(string path, bool setAside)
        {
            Assert.Equal(setAside, LogTailReader.IsSetAside(path));
        }

        /// <summary>The file on screen is let go of after every read; the log being written stays open.</summary>
        [Fact]
        public void ASetAsideFileIsHeldOnlyWhileItIsRead()
        {
            File.WriteAllText(this.old, "one\ntwo\n");
            File.WriteAllText(this.log, "three\n");
            using var setAside = new LogTailReader(this.old);
            using var followed = new LogTailReader(this.log);

            Assert.Equal(new[] { "one", "two" }, setAside.ReadNewLines());
            Assert.False(setAside.IsHolding);

            Assert.Empty(setAside.ReadNewLines());
            Assert.Equal(LogRollover.None, setAside.Rollover);
            Assert.False(setAside.Restarted);
            Assert.False(setAside.IsHolding);

            Assert.Equal(new[] { "three" }, followed.ReadNewLines());
            Assert.True(followed.IsHolding);
        }

        /// <summary>
        /// The service starts again while its last run is on screen: the wrapper moves the log
        /// onto the .old file, which the viewer does not stand in the way of. The old file had
        /// been read to its end, its last line without a newline included, and the new one is
        /// read from its first byte — longer than the old, so not taken for growth of it.
        /// </summary>
        [Fact]
        public void TheNextStartReplacesTheFileOnScreenAndItIsReadFromItsStart()
        {
            File.WriteAllText(this.old, "run one\nlast words");
            using var reader = new LogTailReader(this.old);
            Assert.Equal(new[] { "run one" }, reader.ReadNewLines());

            File.WriteAllText(this.log, "run two started\nrun two crashed\n");
            File.Move(this.log, this.old, overwrite: true);

            Assert.Equal(new[] { "last words" }, reader.ReadNewLines());
            Assert.Equal(LogRollover.ReadToEnd, reader.Rollover);
            Assert.False(reader.Restarted);
            Assert.False(reader.IsHolding);

            Assert.Equal(new[] { "run two started", "run two crashed" }, reader.ReadNewLines());
            Assert.Equal(LogRollover.None, reader.Rollover);

            Assert.Empty(reader.ReadNewLines());
            Assert.Equal(LogRollover.None, reader.Rollover);
            Assert.False(reader.IsHolding);
        }

        /// <summary>A shorter run in its place is another file too, not the old one reset.</summary>
        [Fact]
        public void AShorterFileInItsPlaceIsAnotherFileNotAReset()
        {
            File.WriteAllText(this.old, "a long first run\nthat went on\n");
            using var reader = new LogTailReader(this.old);
            reader.ReadNewLines();

            File.WriteAllText(this.log, "short\n");
            File.Move(this.log, this.old, overwrite: true);

            Assert.Empty(reader.ReadNewLines());
            Assert.Equal(LogRollover.ReadToEnd, reader.Rollover);
            Assert.False(reader.Restarted);

            Assert.Equal(new[] { "short" }, reader.ReadNewLines());
        }

        /// <summary>The viewer lets go of the file when its page is left; for a set-aside file there is nothing to let go of.</summary>
        [Fact]
        public void LettingGoBetweenReadsChangesNothing()
        {
            File.WriteAllText(this.old, "one\n");
            using var reader = new LogTailReader(this.old);
            reader.ReadNewLines();

            reader.Release();

            Assert.Empty(reader.ReadNewLines());
            Assert.Equal(LogRollover.None, reader.Rollover);
            Assert.False(reader.Restarted);
        }

        /// <summary>A file that is gone for the moment is read once it is back, from its start.</summary>
        [Fact]
        public void AFileThatIsGoneForTheMomentIsReadOnceItIsBack()
        {
            File.WriteAllText(this.old, "one\n");
            using var reader = new LogTailReader(this.old);
            reader.ReadNewLines();

            File.Delete(this.old);
            Assert.Empty(reader.ReadNewLines());

            File.WriteAllText(this.old, "two\nthree\n");
            Assert.Empty(reader.ReadNewLines());
            Assert.Equal(LogRollover.ReadToEnd, reader.Rollover);

            Assert.Equal(new[] { "two", "three" }, reader.ReadNewLines());
        }
    }
}

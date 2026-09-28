using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using WinSW.Gui.Services;
using Xunit;

namespace WinSW.Gui.Tests
{
    /// <summary>
    /// The tail follows the path, not the file, through the wrapper's rolls: the old file is
    /// read to its end and let go, and the file that takes over the name is read from its
    /// first byte. Real files, renamed the way the size roller renames them.
    /// </summary>
    public sealed class LogRollTests : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "winsw-gui-roll-" + Guid.NewGuid().ToString("n"));
        private readonly string path;
        private readonly string rolled;

        public LogRollTests()
        {
            Directory.CreateDirectory(this.directory);
            this.path = Path.Combine(this.directory, "svc.out.log");
            this.rolled = Path.Combine(this.directory, "svc.0.out.log");
        }

        public void Dispose() => Directory.Delete(this.directory, recursive: true);

        /// <summary>write, rename, recreate, write: nothing is lost, nothing is read twice.</summary>
        [Fact]
        public void TheOldFileIsReadToItsEndAndTheNewOneFromItsStart()
        {
            File.WriteAllText(this.path, "one\ntwo\n");
            using var reader = new LogTailReader(this.path);
            Assert.Equal(new[] { "one", "two" }, reader.ReadNewLines());

            // The last line the wrapper writes before it rolls, then the roll itself.
            File.AppendAllText(this.path, "three\n");
            this.Roll();
            File.WriteAllText(this.path, "four\n");

            // A file still growing is not asked about; the one after is.
            Assert.Equal(new[] { "three" }, reader.ReadNewLines());
            Assert.Equal(LogRollover.None, reader.Rollover);

            Assert.Empty(reader.ReadNewLines());
            Assert.Equal(LogRollover.ReadToEnd, reader.Rollover);
            Assert.False(reader.Restarted);

            Assert.Equal(new[] { "four" }, reader.ReadNewLines());
            Assert.Equal(LogRollover.None, reader.Rollover);

            File.AppendAllText(this.path, "five\n");
            Assert.Equal(new[] { "five" }, reader.ReadNewLines());
        }

        /// <summary>A roll that lands between two reads is seen at the next one.</summary>
        [Fact]
        public void ARollBetweenReadsIsSeenAtTheNextRead()
        {
            File.WriteAllText(this.path, "one\ntwo\n");
            using var reader = new LogTailReader(this.path);
            reader.ReadNewLines();

            this.Roll();
            File.WriteAllText(this.path, "x\n");

            Assert.Empty(reader.ReadNewLines());
            Assert.Equal(LogRollover.ReadToEnd, reader.Rollover);
            Assert.Equal(new[] { "x" }, reader.ReadNewLines());
        }

        /// <summary>The old file's last line never gets its newline, so it is handed over as it is.</summary>
        [Fact]
        public void ALastLineWithoutItsNewlineIsHandedOverAtTheRoll()
        {
            File.WriteAllText(this.path, "one\npart");
            using var reader = new LogTailReader(this.path);
            Assert.Equal(new[] { "one" }, reader.ReadNewLines());

            this.Roll();
            File.WriteAllText(this.path, "x\n");

            Assert.Equal(new[] { "part" }, reader.ReadNewLines());
            Assert.Equal(LogRollover.ReadToEnd, reader.Rollover);
            Assert.Equal(new[] { "x" }, reader.ReadNewLines());
        }

        /// <summary>
        /// Roll mode renames the log at every start, and the new run can outgrow the old one
        /// before the viewer looks: a longer file under the name is a roll too, not growth.
        /// </summary>
        [Fact]
        public void ANewFileLongerThanTheOldOneIsStillARoll()
        {
            File.WriteAllText(this.path, "a\n");
            using var reader = new LogTailReader(this.path);
            reader.ReadNewLines();

            this.Roll();
            var lines = Enumerable.Range(0, 100).Select(i => "line " + i).ToList();
            File.WriteAllText(this.path, string.Join("\n", lines) + "\n");

            Assert.Empty(reader.ReadNewLines());
            Assert.Equal(LogRollover.ReadToEnd, reader.Rollover);
            Assert.Equal(lines, reader.ReadNewLines());
        }

        /// <summary>
        /// A new file exactly as long as the old one: Windows tells them apart by file number
        /// at once; by length alone they are told apart at the new file's next write. Either
        /// way the new file is read whole, from its first byte.
        /// </summary>
        [Fact]
        public void ANewFileAsLongAsTheOldOneIsReadWhole()
        {
            File.WriteAllText(this.path, "one\n");
            using var reader = new LogTailReader(this.path);
            reader.ReadNewLines();

            this.Roll();
            File.WriteAllText(this.path, "two\n");
            var lines = new List<string>(reader.ReadNewLines());
            bool rolledOver = reader.Rollover == LogRollover.ReadToEnd;

            File.AppendAllText(this.path, "three\n");
            for (int i = 0; i < 2; i++)
            {
                lines.AddRange(reader.ReadNewLines());
                rolledOver |= reader.Rollover == LogRollover.ReadToEnd;
            }

            Assert.True(rolledOver);
            Assert.Equal(new[] { "two", "three" }, lines);
        }

        /// <summary>An empty log rolled away at a start, and a new one written straight after.</summary>
        [Fact]
        public void AnEmptyFileRolledAwayIsFollowed()
        {
            File.WriteAllText(this.path, string.Empty);
            using var reader = new LogTailReader(this.path);
            Assert.Empty(reader.ReadNewLines());

            this.Roll();
            File.WriteAllText(this.path, "started\n");

            Assert.Empty(reader.ReadNewLines());
            Assert.Equal(LogRollover.ReadToEnd, reader.Rollover);
            Assert.Equal(new[] { "started" }, reader.ReadNewLines());
        }

        /// <summary>
        /// Between the wrapper's rename and its new file the name is missing. That proves
        /// nothing yet, so the old file stays open and whatever still lands in it is read.
        /// </summary>
        [Fact]
        public void AMissingNameIsNoVerdict()
        {
            File.WriteAllText(this.path, "one\n");
            using var reader = new LogTailReader(this.path);
            reader.ReadNewLines();

            this.Roll();
            Assert.Empty(reader.ReadNewLines());
            Assert.Equal(LogRollover.None, reader.Rollover);
            Assert.False(reader.Restarted);

            File.AppendAllText(this.rolled, "two\n");
            Assert.Equal(new[] { "two" }, reader.ReadNewLines());

            File.WriteAllText(this.path, "three\n");
            Assert.Empty(reader.ReadNewLines());
            Assert.Equal(LogRollover.ReadToEnd, reader.Rollover);
            Assert.Equal(new[] { "three" }, reader.ReadNewLines());
        }

        /// <summary>
        /// Once the roll is seen the old file is let go of, so the wrapper's next roll can
        /// delete it from the end of the chain and move another file onto its name. On Windows
        /// before POSIX delete semantics, a file still held open stays in the way of that move.
        /// </summary>
        [Fact]
        public void TheRolledFileIsLetGo()
        {
            File.WriteAllText(this.path, "one\n");
            using var reader = new LogTailReader(this.path);
            reader.ReadNewLines();

            this.Roll();
            File.WriteAllText(this.path, "second\n");
            reader.ReadNewLines();
            Assert.Equal(LogRollover.ReadToEnd, reader.Rollover);

            File.Delete(this.rolled);
            File.Move(this.path, this.rolled);
            Assert.Equal("second\n", File.ReadAllText(this.rolled));
        }

        /// <summary>A reader let go of carries on at its place, partial line included, not at the tail again.</summary>
        [Fact]
        public void AReleasedReaderCarriesOnWhereItWas()
        {
            File.WriteAllText(this.path, "one\npar");
            using var reader = new LogTailReader(this.path);
            Assert.Equal(new[] { "one" }, reader.ReadNewLines());

            reader.Release();
            File.AppendAllText(this.path, "t\nnext\n");

            Assert.Equal(new[] { "part", "next" }, reader.ReadNewLines());
            Assert.Equal(LogRollover.None, reader.Rollover);
            Assert.False(reader.Restarted);
        }

        /// <summary>
        /// While let go of, the reader holds nothing: the file can be rolled away and its name
        /// taken over. Where the file system numbers its files — Windows — that is said to be a
        /// roll; elsewhere a new file shorter than the place looks like a reset. Either way the
        /// change is said and the new file is read from its first byte.
        /// </summary>
        [Fact]
        public void AFileRolledWhileReleasedStartsOverAndSaysSo()
        {
            File.WriteAllText(this.path, "first line\n");
            using var reader = new LogTailReader(this.path);
            reader.ReadNewLines();

            reader.Release();
            this.Roll();
            File.WriteAllText(this.path, "x\n");

            var lines = new List<string>(reader.ReadNewLines());
            bool said = reader.Restarted || reader.Rollover == LogRollover.WhileReleased;
            if (reader.Rollover == LogRollover.WhileReleased)
            {
                Assert.Empty(lines);
                lines.AddRange(reader.ReadNewLines());
            }

            Assert.True(said);
            Assert.Equal(new[] { "x" }, lines);
        }

        /// <summary>A file reset in place while let go of is still a reset, not a roll.</summary>
        [Fact]
        public void AFileResetWhileReleasedIsARestart()
        {
            File.WriteAllText(this.path, "a\nb\nc\n");
            using var reader = new LogTailReader(this.path);
            reader.ReadNewLines();

            reader.Release();
            File.WriteAllText(this.path, "x\n");

            Assert.Equal(new[] { "x" }, reader.ReadNewLines());
            Assert.True(reader.Restarted);
            Assert.Equal(LogRollover.None, reader.Rollover);
        }

        /// <summary>A quiet file that is still the one under its name stays open and quiet.</summary>
        [Fact]
        public void AQuietFileIsNotARoll()
        {
            File.WriteAllText(this.path, "one\n");
            using var reader = new LogTailReader(this.path);
            reader.ReadNewLines();

            for (int i = 0; i < 3; i++)
            {
                Assert.Empty(reader.ReadNewLines());
                Assert.Equal(LogRollover.None, reader.Rollover);
                Assert.False(reader.Restarted);
            }

            File.AppendAllText(this.path, "two\n");
            Assert.Equal(new[] { "two" }, reader.ReadNewLines());
        }

        /// <summary>What the size roller does: the live file becomes .0 and a new one takes its name.</summary>
        private void Roll() => File.Move(this.path, this.rolled);
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using static System.IO.File;

namespace WinSW.Tests
{
    public class LogAppenderTests
    {
        private const byte CR = 0x0d;
        private const byte LF = 0x0a;

        [Fact]
        public void DefaultLogAppender()
        {
            byte[] stdout = { 0x4e, 0x65, 0x78, 0x74 };
            byte[] stderr = { 0x54, 0x75, 0x72, 0x6e };

            using var data = TestData.Create();

            string baseName = data.name;
            string outFileExt = ".out.log";
            string errFileExt = ".err.log";
            string outFileName = baseName + outFileExt;
            string errFileName = baseName + errFileExt;
            string outFilePath = Path.Combine(data.path, outFileName);
            string errFilePath = Path.Combine(data.path, errFileName);

            WriteAllBytes(outFilePath, stdout);
            WriteAllBytes(errFilePath, stderr);

            var appender = new DefaultLogAppender(data.path, data.name, false, false, outFileExt, errFileExt);
            appender.Log(new(new MemoryStream(stdout)), new(new MemoryStream(stderr)));

            Assert.True(Exists(outFilePath));
            Assert.True(Exists(errFilePath));

            Assert.Equal(stdout.Concat(stdout), ReadAllBytes(outFilePath));
            Assert.Equal(stderr.Concat(stderr), ReadAllBytes(errFilePath));
        }

        [Fact]
        public void ResetLogAppender()
        {
            byte[] stdout = { 0x4e, 0x65, 0x78, 0x74 };
            byte[] stderr = { 0x54, 0x75, 0x72, 0x6e };

            using var data = TestData.Create();

            string baseName = data.name;
            string outFileExt = ".out.log";
            string errFileExt = ".err.log";
            string outFileName = baseName + outFileExt;
            string errFileName = baseName + errFileExt;
            string outFilePath = Path.Combine(data.path, outFileName);
            string errFilePath = Path.Combine(data.path, errFileName);

            WriteAllBytes(outFilePath, stderr);
            WriteAllBytes(errFilePath, stdout);

            var appender = new ResetLogAppender(data.path, data.name, false, false, outFileExt, errFileExt);
            appender.Log(new(new MemoryStream(stdout)), new(new MemoryStream(stderr)));

            Assert.True(Exists(outFilePath));
            Assert.True(Exists(errFilePath));

            Assert.Equal(stdout, ReadAllBytes(outFilePath));
            Assert.Equal(stderr, ReadAllBytes(errFilePath));
        }

        [Fact]
        public void IgnoreLogAppender()
        {
            byte[] stdout = { 0x4e, 0x65, 0x78, 0x74 };
            byte[] stderr = { 0x54, 0x75, 0x72, 0x6e };

            using var data = TestData.Create();

            string baseName = data.name;
            string outFileExt = ".out.log";
            string errFileExt = ".err.log";
            string outFileName = baseName + outFileExt;
            string errFileName = baseName + errFileExt;
            string outFilePath = Path.Combine(data.path, outFileName);
            string errFilePath = Path.Combine(data.path, errFileName);

            var appender = new IgnoreLogAppender();
            appender.Log(new(new MemoryStream(stdout)), new(new MemoryStream(stderr)));

            Assert.False(Exists(outFilePath));
            Assert.False(Exists(errFilePath));
        }

        [Fact]
        public void SizeBasedRollingLogAppender()
        {
            byte[] stdout = { 0x4e, 0x65, CR, LF, 0x78, 0x74 };
            byte[] stderr = { 0x54, 0x75, CR, LF, 0x72, 0x6e };

            using var data = TestData.Create();

            string baseName = data.name;
            string outFileExt = ".out.log";
            string errFileExt = ".err.log";

            var appender = new SizeBasedRollingLogAppender(data.path, data.name, false, false, outFileExt, errFileExt, 3, 2);
            appender.Log(new(new MemoryStream(stdout)), new(new MemoryStream(stderr)));

            Assert.Equal(stdout.Take(4), ReadAllBytes(Path.Combine(data.path, baseName + ".0" + outFileExt)));
            Assert.Equal(stdout.Skip(4), ReadAllBytes(Path.Combine(data.path, baseName + outFileExt)));
            Assert.Equal(stderr.Take(4), ReadAllBytes(Path.Combine(data.path, baseName + ".0" + errFileExt)));
            Assert.Equal(stderr.Skip(4), ReadAllBytes(Path.Combine(data.path, baseName + errFileExt)));
        }

        [Fact]
        public void SizeBasedRollingLogAppender_RollsTheOthers_WhenARolledFileCannotBeDeleted()
        {
            byte[] stdout = { 0x4e, 0x65, CR, LF, 0x78, 0x74 };

            using var data = TestData.Create();

            string outFileExt = ".out.log";
            string outFilePath = Path.Combine(data.path, data.name + outFileExt);
            string firstFilePath = Path.Combine(data.path, data.name + ".0" + outFileExt);
            string secondFilePath = Path.Combine(data.path, data.name + ".1" + outFileExt);
            string lastFilePath = Path.Combine(data.path, data.name + ".2" + outFileExt);

            // Deleting a read-only file is denied, as is deleting a file that is already marked for
            // deletion but still held open by a reader, which is what the roll runs into on older
            // versions of Windows when a log viewer has followed the file down to the last name.
            WriteAllBytes(firstFilePath, new byte[] { 0x61 });
            WriteAllBytes(secondFilePath, new byte[] { 0x62 });
            WriteAllBytes(lastFilePath, new byte[] { 0x63 });
            SetAttributes(lastFilePath, FileAttributes.ReadOnly);
            try
            {
                var events = new EventRecorder();
                var appender = new SizeBasedRollingLogAppender(data.path, data.name, false, true, outFileExt, ".err.log", 3, 3)
                {
                    EventLogger = events,
                };
                appender.Log(new(new MemoryStream(stdout)), StreamReader.Null);

                // The failure is reported, and the other files roll: the one that would have moved
                // onto the last name is deleted instead, and nothing of the live file is lost.
                Assert.StartsWith("Failed to roll log: ", Assert.Single(events.Entries).Message);
                Assert.Equal(stdout.Skip(4), ReadAllBytes(outFilePath));
                Assert.Equal(stdout.Take(4), ReadAllBytes(firstFilePath));
                Assert.Equal(new byte[] { 0x61 }, ReadAllBytes(secondFilePath));
                Assert.Equal(new byte[] { 0x63 }, ReadAllBytes(lastFilePath));
            }
            finally
            {
                // Checked, so that a file the test expected to stay but did not shows as the failure.
                if (Exists(lastFilePath))
                {
                    SetAttributes(lastFilePath, FileAttributes.Normal);
                }
            }
        }

        [Fact]
        public void SizeBasedRollingLogAppender_KeepsTheOutput_AndWaitsAnotherThreshold_WhenTheRollFails()
        {
            byte[] line = { 0x4e, 0x65, CR, LF };
            byte[] stdout = Enumerable.Repeat(line, 12).SelectMany(bytes => bytes).ToArray();

            using var data = TestData.Create();

            string outFileExt = ".out.log";
            string outFilePath = Path.Combine(data.path, data.name + outFileExt);
            WriteAllBytes(outFilePath, Array.Empty<byte>());

            var events = new EventRecorder();
            var appender = new SizeBasedRollingLogAppender(data.path, data.name, false, true, outFileExt, ".err.log", 10, 2)
            {
                EventLogger = events,
            };

            // A reader that does not share delete access, as many log viewers open files, keeps the
            // file from being renamed.
            using (new FileStream(outFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                appender.Log(new(new MemoryStream(stdout)), StreamReader.Null);
            }

            // Twelve 4-byte lines with a 10-byte threshold: the roll is tried after the 3rd, 6th, 9th
            // and 12th lines, not on each of the ten lines from the 3rd on, and nothing is lost.
            Assert.Equal(4, events.Entries.Count);
            Assert.All(events.Entries, entry => Assert.StartsWith("Failed to roll log: ", entry.Message));
            Assert.Equal(stdout, ReadAllBytes(outFilePath));
            Assert.False(Exists(Path.Combine(data.path, data.name + ".0" + outFileExt)));
        }

        [Fact]
        public void RollingLogAppender_StillLogs_WhenTheOldFileCannotBeReplaced()
        {
            byte[] earlier = { 0x4e, 0x65, 0x78, 0x74 };
            byte[] stdout = { 0x54, 0x75, 0x72, 0x6e };

            using var data = TestData.Create();

            string outFilePath = Path.Combine(data.path, data.name + ".out.log");
            string oldFilePath = outFilePath + ".old";

            // Replacing a read-only file is denied, as is replacing one that a reader still holds
            // open on older versions of Windows.
            WriteAllBytes(outFilePath, earlier);
            WriteAllBytes(oldFilePath, earlier);
            SetAttributes(oldFilePath, FileAttributes.ReadOnly);
            try
            {
                var events = new EventRecorder();
                var appender = new RollingLogAppender(data.path, data.name, false, true, ".out.log", ".err.log")
                {
                    EventLogger = events,
                };
                appender.Log(new(new MemoryStream(stdout)), StreamReader.Null);

                Assert.StartsWith("Failed to move :", Assert.Single(events.Entries).Message);
                Assert.Equal(earlier.Concat(stdout), ReadAllBytes(outFilePath));
            }
            finally
            {
                // Checked, so that a file the test expected to stay but did not shows as the failure.
                if (Exists(oldFilePath))
                {
                    SetAttributes(oldFilePath, FileAttributes.Normal);
                }
            }
        }

        [Fact]
        public void RollingSizeTimeLogAppender_RollsOnSize()
        {
            byte[] stdout = { 0x4e, 0x65, CR, LF, 0x78, 0x74 };
            byte[] stderr = { 0x54, 0x75, CR, LF, 0x72, 0x6e };

            using var data = TestData.Create();

            string baseName = data.name;
            string outFileExt = ".out.log";
            string errFileExt = ".err.log";

            var events = new EventRecorder();
            var appender = new RollingSizeTimeLogAppender(data.path, data.name, false, false, outFileExt, errFileExt, 3, "yyyyMMdd", null, null, "yyyyMM")
            {
                EventLogger = events,
            };
            appender.Log(new(new MemoryStream(stdout)), new(new MemoryStream(stderr)));

            // The rolled file is named after today's date, which is left to a wildcard in case the
            // test runs across midnight.
            Assert.Empty(events.Entries);
            Assert.Equal(stdout.Take(4), ReadAllBytes(Assert.Single(Directory.GetFiles(data.path, baseName + ".*.#0001" + outFileExt))));
            Assert.Equal(stdout.Skip(4), ReadAllBytes(Path.Combine(data.path, baseName + outFileExt)));
            Assert.Equal(stderr.Take(4), ReadAllBytes(Assert.Single(Directory.GetFiles(data.path, baseName + ".*.#0001" + errFileExt))));
            Assert.Equal(stderr.Skip(4), ReadAllBytes(Path.Combine(data.path, baseName + errFileExt)));
        }

        [Fact]
        public void RollingSizeTimeLogAppender_KeepsTheOutput_AndWaitsAnotherThreshold_WhenTheRollFails()
        {
            byte[] line = { 0x4e, 0x65, CR, LF };
            byte[] stdout = Enumerable.Repeat(line, 12).SelectMany(bytes => bytes).ToArray();

            using var data = TestData.Create();

            string outFileExt = ".out.log";
            string outFilePath = Path.Combine(data.path, data.name + outFileExt);
            WriteAllBytes(outFilePath, Array.Empty<byte>());

            var events = new EventRecorder();
            var appender = new RollingSizeTimeLogAppender(data.path, data.name, false, true, outFileExt, ".err.log", 10, "yyyyMMdd", null, null, "yyyyMM")
            {
                EventLogger = events,
            };

            // A reader that does not share delete access, as many log viewers open files, keeps the
            // file from being renamed.
            using (new FileStream(outFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                appender.Log(new(new MemoryStream(stdout)), StreamReader.Null);
            }

            // Twelve 4-byte lines with a 10-byte threshold: the roll is tried after the 3rd, 6th, 9th
            // and 12th lines, not on each of the ten lines from the 3rd on, and nothing is lost.
            Assert.Equal(4, events.Entries.Count);
            Assert.All(events.Entries, entry => Assert.StartsWith("Failed to roll size time log: ", entry.Message));
            Assert.Equal(stdout, ReadAllBytes(outFilePath));
            Assert.Empty(Directory.GetFiles(data.path, data.name + ".*.#*" + outFileExt));
        }

        [Theory]
        [InlineData("roll-by-size", "Failed to roll log: ")]
        [InlineData("roll-by-size-time", "Failed to roll size time log: ")]
        public void RollingLogAppenders_GoOnCopying_WhenTheFileCannotBeOpenedAgain(string mode, string rollFailure)
        {
            byte[] first = { 0x4e, 0x65, CR, LF };
            byte[] second = { 0x78, 0x74, CR, LF };
            byte[] third = { 0x61, 0x62 };

            using var data = TestData.Create(nameof(RollingLogAppenders_GoOnCopying_WhenTheFileCannotBeOpenedAgain) + "_" + mode);

            string outFileExt = ".out.log";
            string outFilePath = Path.Combine(data.path, data.name + outFileExt);
            WriteAllBytes(outFilePath, Array.Empty<byte>());

            var events = new EventRecorder();
            AbstractFileLogAppender appender = mode == "roll-by-size"
                ? new SizeBasedRollingLogAppender(data.path, data.name, false, true, outFileExt, ".err.log", 3, 2)
                : new RollingSizeTimeLogAppender(data.path, data.name, false, true, outFileExt, ".err.log", 3, "yyyyMMdd", null, null, "yyyyMM");
            appender.EventLogger = events;

            // The reader keeps the file from being rolled. While the file is read-only, it cannot be
            // opened again for writing either, which the copy's own handle, opened before, still can.
            var source = new ChunkStream(
                (first, () => SetAttributes(outFilePath, FileAttributes.ReadOnly)),
                (second, () => SetAttributes(outFilePath, FileAttributes.Normal)),
                (third, null));
            try
            {
                using (new FileStream(outFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    appender.Log(new(source), StreamReader.Null);
                }

                // The first roll fails and so does opening the file again, and the second line has
                // nowhere to go. The second roll opens the file again, and the copy goes on into it.
                // Had the copy ended, its exception would have been reported as well.
                Assert.Collection(
                    events.Entries,
                    entry => Assert.StartsWith(rollFailure, entry.Message),
                    entry => Assert.StartsWith("Failed to open the log file after rolling it", entry.Message),
                    entry => Assert.StartsWith(rollFailure, entry.Message));
                Assert.Equal(first.Concat(third), ReadAllBytes(outFilePath));
            }
            finally
            {
                if (Exists(outFilePath))
                {
                    SetAttributes(outFilePath, FileAttributes.Normal);
                }
            }
        }

        [Fact]
        public async Task StreamCopyOperation_WritesToTheWriterPutInWhileItWaitedAsync()
        {
            byte[] line = { 0x4e, 0x65, CR, LF };
            var writerLock = new object();
            var first = new MemoryStream();
            var second = new MemoryStream();
            StreamCopyOperation copy = null;

            // The roll at a set time of day runs on a timer thread, most likely while the copy waits
            // for the program's output, as it does here in the read. It closes the writer and puts
            // in the next one.
            var source = new ChunkStream((line, () =>
            {
                lock (writerLock)
                {
                    first.Dispose();
                    copy.Writer = second;
                }
            }));
            copy = new StreamCopyOperation(source, first, writerLock);

            Assert.Equal(line.Length, await copy.CopyLineAsync());
            Assert.Equal(line, second.ToArray());
        }

        [Fact]
        public void TimeBasedRollingLogAppender_PurgeOldFiles_RemovesExcessFiles()
        {
            using var data = TestData.Create();

            string outFileExt = ".out.log";
            const int keepFiles = 3;

            for (int i = 0; i < 5; i++)
            {
                string filePath = Path.Combine(data.path, $"{data.name}_{i:D8}{outFileExt}");
                File.WriteAllText(filePath, "old log");
                File.SetLastWriteTime(filePath, DateTime.Now.AddDays(-i));
            }

            var appender = new TimeBasedRollingLogAppender(
                data.path, data.name,
                false, false,
                outFileExt, ".err.log",
                "yyyyMMdd", 1,
                filesToKeep: keepFiles);

            appender.PurgeOldFiles(outFileExt);

            var remaining = Directory.GetFiles(data.path, $"{data.name}_*{outFileExt}");
            Assert.Equal(keepFiles, remaining.Length);
        }

        [Fact]
        public void TimeBasedRollingLogAppender_PurgeOldFiles_NoLimitKeepsAllFiles()
        {
            using var data = TestData.Create();

            string outFileExt = ".out.log";
            const int totalFiles = 5;

            for (int i = 0; i < totalFiles; i++)
            {
                File.WriteAllText(Path.Combine(data.path, $"{data.name}_{i:D8}{outFileExt}"), "log");
            }

            var appender = new TimeBasedRollingLogAppender(
                data.path, data.name,
                false, false,
                outFileExt, ".err.log",
                "yyyyMMdd", 1,
                filesToKeep: -1);

            appender.PurgeOldFiles(outFileExt);

            var remaining = Directory.GetFiles(data.path, $"{data.name}_*{outFileExt}");
            Assert.Equal(totalFiles, remaining.Length);
        }

        [Fact]
        public void TimeBasedRollingLogAppender_PurgeOldFiles_GoesOnPastAFileItCannotDelete()
        {
            using var data = TestData.Create();

            string outFileExt = ".out.log";
            const int keepFiles = 3;

            string[] filePaths = new string[5];
            for (int i = 0; i < filePaths.Length; i++)
            {
                filePaths[i] = Path.Combine(data.path, $"{data.name}_{i:D8}{outFileExt}");
                File.WriteAllText(filePaths[i], "old log");
                File.SetLastWriteTime(filePaths[i], DateTime.Now.AddDays(-i));
            }

            // Of the two files past the limit, the newer one is purged first, and it cannot be.
            SetAttributes(filePaths[3], FileAttributes.ReadOnly);
            try
            {
                var events = new EventRecorder();
                var appender = new TimeBasedRollingLogAppender(
                    data.path, data.name,
                    false, false,
                    outFileExt, ".err.log",
                    "yyyyMMdd", 1,
                    filesToKeep: keepFiles)
                {
                    EventLogger = events,
                };

                appender.PurgeOldFiles(outFileExt);

                Assert.StartsWith("Failed to purge old log file: ", Assert.Single(events.Entries).Message);
                Assert.True(Exists(filePaths[3]));
                Assert.False(Exists(filePaths[4]));
            }
            finally
            {
                // Checked, so that a file the test expected to stay but did not shows as the failure.
                if (Exists(filePaths[3]))
                {
                    SetAttributes(filePaths[3], FileAttributes.Normal);
                }
            }
        }

        // Hands out one chunk a read, and runs the chunk's action, if it has one, just before. Like a
        // MemoryStream's, its reads are done at once, so the whole copy runs inside the call that
        // starts it.
        private sealed class ChunkStream : Stream
        {
            private readonly Queue<(byte[] Bytes, Action Before)> chunks;

            internal ChunkStream(params (byte[] Bytes, Action Before)[] chunks)
            {
                this.chunks = new(chunks);
            }

            public override bool CanRead => true;

            public override bool CanSeek => false;

            public override bool CanWrite => false;

            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (this.chunks.Count == 0)
                {
                    return 0;
                }

                var (bytes, before) = this.chunks.Dequeue();
                before?.Invoke();
                Array.Copy(bytes, 0, buffer, offset, bytes.Length);
                return bytes.Length;
            }

            // Stream's own would read on another thread.
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                return Task.FromResult(this.Read(buffer, offset, count));
            }

            public override void Flush()
            {
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        private sealed class EventRecorder : IEventLogger
        {
            internal List<(string Message, EventLogEntryType Type)> Entries { get; } = new();

            public void WriteEntry(string message) => this.Entries.Add((message, EventLogEntryType.Information));

            public void WriteEntry(string message, EventLogEntryType type) => this.Entries.Add((message, type));
        }

        private readonly struct TestData : IDisposable
        {
            internal readonly string name;
            internal readonly string path;

            private TestData(string name, string path)
            {
                this.name = name;
                this.path = path;
            }

            internal static TestData Create([CallerMemberName] string name = null)
            {
                string path = Path.Combine(Path.GetTempPath(), name);
                _ = Directory.CreateDirectory(path);

                return new(name, path);
            }

            public void Dispose()
            {
                Directory.Delete(this.path, true);
            }
        }
    }
}
using System;
using System.IO;
using System.Linq;
using System.Text;
using WinSW.Gui.Services;
using Xunit;

namespace WinSW.Gui.Tests
{
    public class LogTailReaderTests : IDisposable
    {
        private readonly string path = Path.Combine(Path.GetTempPath(), "WinSW.Gui.Tests", Guid.NewGuid().ToString("N") + ".log");

        public LogTailReaderTests()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(this.path)!);
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }

        public void Dispose()
        {
            if (File.Exists(this.path))
            {
                File.Delete(this.path);
            }
        }

        [Fact]
        public void ReturnsOnlyCompleteLines_ThenTheRest()
        {
            File.WriteAllText(this.path, "one\r\ntwo\nthr");
            using var reader = new LogTailReader(this.path);

            var lines = reader.ReadNewLines();
            Assert.Equal(new[] { "one", "two" }, lines);
            Assert.Empty(reader.ReadNewLines());

            File.AppendAllText(this.path, "ee\nfour\n");
            Assert.Equal(new[] { "three", "four" }, reader.ReadNewLines());
        }

        [Fact]
        public void DetectsTruncation()
        {
            File.WriteAllText(this.path, "a\nb\nc\n");
            using var reader = new LogTailReader(this.path);
            Assert.Equal(3, reader.ReadNewLines().Count);

            File.WriteAllText(this.path, "x\n");
            var lines = reader.ReadNewLines();
            Assert.True(reader.Restarted);
            Assert.Equal(new[] { "x" }, lines);
        }

        /// <summary>
        /// A file that has grown by more than the catch-up budget since the last read is
        /// joined again at a whole line inside the budget, and the reader says how much it
        /// passed over. Reading it all would have been work for lines the viewer drops.
        /// </summary>
        [Fact]
        public void SkipsAheadWhenTheBacklogIsMoreThanTheViewerKeeps()
        {
            File.WriteAllText(this.path, "start\n");
            using var reader = new LogTailReader(this.path);
            Assert.Equal(new[] { "start" }, reader.ReadNewLines());

            var backlog = new StringBuilder();
            int count = 0;
            while (backlog.Length < LogTailReader.MaxCatchUpBytes + 100_000)
            {
                backlog.Append("line ").Append(count++).Append('\n');
            }

            File.AppendAllText(this.path, backlog.ToString());
            File.AppendAllText(this.path, "last\n");

            var lines = reader.ReadNewLines();

            Assert.True(reader.SkippedBytes > 0);
            Assert.True(reader.SkippedBytes < backlog.Length);
            Assert.True(lines.Count < count);
            Assert.DoesNotContain("line 0", lines);
            Assert.Equal("last", lines[^1]);

            // Resynchronised onto a line boundary: nothing shown is a fragment.
            Assert.All(lines, line => Assert.True(line == "last" || line.StartsWith("line ", StringComparison.Ordinal), line));

            // Consecutive numbers from wherever it joined: nothing inside the budget was lost.
            int first = int.Parse(lines[0].Substring("line ".Length));
            for (int i = 1; i < lines.Count - 1; i++)
            {
                Assert.Equal("line " + (first + i), lines[i]);
            }

            Assert.Empty(reader.ReadNewLines());
            Assert.Equal(0, reader.SkippedBytes);

            File.AppendAllText(this.path, "after\n");
            Assert.Equal(new[] { "after" }, reader.ReadNewLines());
        }

        [Fact]
        public void AutoDetection_FallsBackToAnsiForGbk()
        {
            var gbk = Encoding.GetEncoding(936);
            File.WriteAllBytes(this.path, gbk.GetBytes("服务已启动\n"));

            using var reader = new LogTailReader(this.path);

            // Only meaningful when the machine's ANSI code page is GBK; elsewhere the
            // decision still must be "not UTF-8".
            var lines = reader.ReadNewLines();
            Assert.Single(lines);
            Assert.NotNull(reader.Encoding);
            Assert.NotEqual("utf-8", reader.Encoding!.WebName);
        }

        [Fact]
        public void AutoDetection_KeepsUtf8WhenValid()
        {
            File.WriteAllBytes(this.path, new UTF8Encoding(false).GetBytes("服务已启动\n"));
            using var reader = new LogTailReader(this.path);

            var lines = reader.ReadNewLines();
            Assert.Equal("服务已启动", lines[0]);
            Assert.Equal("utf-8", reader.Encoding!.WebName);
        }

        [Fact]
        public void ExplicitEncoding_IsHonoured()
        {
            var gbk = Encoding.GetEncoding(936);
            File.WriteAllBytes(this.path, gbk.GetBytes("中文\n"));

            using var reader = new LogTailReader(this.path, LogEncodingChoice.SystemAnsi);
            var lines = reader.ReadNewLines();

            // Decoding with the system ANSI page is only lossless where that page is GBK;
            // what must hold everywhere is that the reader used the ANSI choice, not UTF-8.
            Assert.Single(lines);
            Assert.Equal(LogTailReader.SystemAnsiEncoding.WebName, reader.Encoding!.WebName);
        }

        /// <summary>
        /// A file shorter than the tail is read whole, first line included, when it starts with
        /// a byte-order mark — as the output of a .NET Framework program that sets its console
        /// to UTF-8 does. The mark moved the place off the start, and the first line, the
        /// start-up banner, was skipped as if the tail had begun inside it.
        /// </summary>
        [Fact]
        public void AByteOrderMarkDoesNotCostTheFirstLine()
        {
            byte[] text = new UTF8Encoding(false).GetBytes("first line\nsecond line\n");
            File.WriteAllBytes(this.path, Encoding.UTF8.GetPreamble().Concat(text).ToArray());
            using var reader = new LogTailReader(this.path);

            Assert.Equal(new[] { "first line", "second line" }, reader.ReadNewLines());
            Assert.Equal("utf-8", reader.Encoding!.WebName);
        }

        /// <summary>
        /// The tail begins just inside the mark, a file a byte or two longer than the tail: the
        /// first line is still whole, and kept. One byte past the mark, the tail starts inside
        /// that line, which is then left out rather than shown cut.
        /// </summary>
        [Theory]
        [InlineData(1, true)]
        [InlineData(2, true)]
        [InlineData(3, true)]
        [InlineData(4, false)]
        public void ATailStartingAtTheMarkKeepsTheFirstLine(int tailStart, bool kept)
        {
            // The tail is the last 128 KiB, so a file this long starts it at tailStart.
            const int tail = 128 * 1024;
            byte[] mark = Encoding.UTF8.GetPreamble();
            string first = "first line\n";
            string rest = new string('x', tail + tailStart - mark.Length - first.Length - 1) + "\n";
            File.WriteAllBytes(this.path, mark.Concat(new UTF8Encoding(false).GetBytes(first + rest)).ToArray());
            using var reader = new LogTailReader(this.path);

            var lines = reader.ReadNewLines();

            Assert.Equal(kept, lines.Contains("first line"));
            Assert.Equal(kept ? 2 : 1, lines.Count);
        }

        /// <summary>Longer than the tail, a file with a mark is still joined at a whole line.</summary>
        [Fact]
        public void AByteOrderMarkedFileLongerThanTheTailStartsAtAWholeLine()
        {
            var text = new StringBuilder();
            for (int i = 0; text.Length < 200 * 1024; i++)
            {
                text.Append("line ").Append(i.ToString("D6", System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
            }

            File.WriteAllBytes(this.path, Encoding.UTF8.GetPreamble().Concat(new UTF8Encoding(false).GetBytes(text.ToString())).ToArray());
            using var reader = new LogTailReader(this.path);

            var lines = reader.ReadNewLines();

            Assert.All(lines, line => Assert.Matches(@"^line \d{6}$", line));
            Assert.NotEqual("line 000000", lines[0]);
        }
    }
}

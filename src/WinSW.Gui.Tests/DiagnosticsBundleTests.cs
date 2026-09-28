using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using WinSW.Gui.Services;
using Xunit;

namespace WinSW.Gui.Tests
{
    /// <summary>
    /// The diagnostics bundle is the copy of a service's logs that gets sent on. It read every
    /// log as UTF-8, which turned the GBK a program writes on Chinese Windows into replacement
    /// characters for good; it printed the service control manager's event IDs with their
    /// qualifier bits; it left out the wrapper's log whenever the service had a log name of its
    /// own, and roll mode's .old files always; and it had nothing of what the console had done.
    /// </summary>
    public sealed class DiagnosticsBundleTests : IDisposable
    {
        private static readonly DateTime Noon = new(2026, 9, 23, 12, 0, 0);

        private static readonly Encoding Utf8 = new UTF8Encoding(false);

        private readonly string directory = Path.Combine(Path.GetTempPath(), "winsw-gui-bundle-" + Guid.NewGuid().ToString("n"));

        public DiagnosticsBundleTests()
        {
            Directory.CreateDirectory(this.directory);
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }

        public void Dispose() => Directory.Delete(this.directory, recursive: true);

        // Reading a tail -----------------------------------------------------------

        /// <summary>
        /// GBK is not valid UTF-8, so it is read the way the viewer reads it: in the system ANSI
        /// code page, which is GBK on Chinese Windows. Elsewhere the text is not Chinese, but
        /// what must hold everywhere is that the viewer's choice was made, not UTF-8.
        /// </summary>
        [Fact]
        public void GbkIsReadInTheAnsiCodePageAsTheViewerReadsIt()
        {
            byte[] bytes = Encoding.GetEncoding(936).GetBytes("服务已启动\r\n端口 8000 被占用\r\n");
            string path = this.WriteBytes("svc.err.log", bytes);

            var tail = LogTailReader.ReadTail(path, 1024);

            Assert.Equal(LogTailReader.SystemAnsiEncoding.WebName, tail.Encoding!.WebName);
            Assert.Equal(LogTailReader.SystemAnsiEncoding.GetString(bytes), tail.Text);
        }

        [Fact]
        public void Utf8IsReadAsUtf8()
        {
            string path = this.WriteText("svc.out.log", "服务已启动\n端口 8000\n");

            var tail = LogTailReader.ReadTail(path, 1024);

            Assert.Equal("UTF-8", tail.EncodingName);
            Assert.Equal("服务已启动\n端口 8000\n", tail.Text);
            Assert.Equal(0, tail.SkippedBytes);
        }

        [Fact]
        public void PlainAsciiNamesNoEncoding()
        {
            var tail = LogTailReader.ReadTail(this.WriteText("svc.out.log", "INFO started\n"), 1024);

            Assert.Null(tail.Encoding);
            Assert.Equal("ASCII", tail.EncodingName);
            Assert.Equal("INFO started\n", tail.Text);
        }

        /// <summary>
        /// The cut falls inside a character. The tail starts at the next whole line, so the half
        /// character neither shows as a replacement character nor passes the file off as ANSI.
        /// </summary>
        [Fact]
        public void ACutInsideACharacterStartsAtTheNextWholeLine()
        {
            // Twenty bytes: "第一行\n" is the first ten. The last twelve start on the last
            // byte of its 行.
            string path = this.WriteText("svc.out.log", "第一行\n第二行\n");

            var tail = LogTailReader.ReadTail(path, 12);

            Assert.Equal("UTF-8", tail.EncodingName);
            Assert.Equal("第二行\n", tail.Text);
            Assert.Equal(10, tail.SkippedBytes);
        }

        /// <summary>A cut that falls just after a newline loses no line.</summary>
        [Fact]
        public void ACutRightAfterANewlineKeepsTheLineThatStartsThere()
        {
            string path = this.WriteText("svc.out.log", "one\ntwo\nthree\n");

            var tail = LogTailReader.ReadTail(path, "two\nthree\n".Length);

            Assert.Equal("two\nthree\n", tail.Text);
            Assert.Equal(4, tail.SkippedBytes);
        }

        /// <summary>A last line still being written can end inside a character. Only whole lines decide.</summary>
        [Fact]
        public void AHalfWrittenLastCharacterDoesNotTurnTheFileToAnsi()
        {
            byte[] whole = Utf8.GetBytes("服务已启动\n");
            byte[] half = Utf8.GetBytes("端").Take(2).ToArray();
            string path = this.WriteBytes("svc.out.log", whole.Concat(half).ToArray());

            var tail = LogTailReader.ReadTail(path, 1024);

            Assert.Equal("UTF-8", tail.EncodingName);
            Assert.StartsWith("服务已启动\n", tail.Text, StringComparison.Ordinal);
        }

        /// <summary>A byte-order mark is at the start of the file, and decides even when the tail does not reach it.</summary>
        [Fact]
        public void AByteOrderMarkDecidesAlthoughTheTailDoesNotReachIt()
        {
            byte[] bytes = Encoding.UTF8.GetPreamble().Concat(Utf8.GetBytes("header\nline\n")).ToArray();
            string path = this.WriteBytes("svc.out.log", bytes);

            var tail = LogTailReader.ReadTail(path, "line\n".Length);

            Assert.Equal("UTF-8", tail.EncodingName);
            Assert.Equal("line\n", tail.Text);
            Assert.Equal("header\n".Length, tail.SkippedBytes);
        }

        /// <summary>UTF-16 is cut on whole characters, counted from its mark, and on a whole line.</summary>
        [Fact]
        public void Utf16IsCutOnWholeCharactersAndLines()
        {
            byte[] bytes = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("第一行\r\n第二行\r\n")).ToArray();
            string path = this.WriteBytes("svc.out.log", bytes);

            // An odd number of bytes from the end: the cut is moved onto a whole character.
            var tail = LogTailReader.ReadTail(path, 15);

            Assert.Equal("UTF-16", tail.EncodingName);
            Assert.Equal("第二行\r\n", tail.Text);
            Assert.Equal(10, tail.SkippedBytes);
        }

        /// <summary>A file with no mark is read in the encoding chosen, when one is.</summary>
        [Fact]
        public void AChosenEncodingIsHonoured()
        {
            string path = this.WriteText("svc.out.log", "服务\n");

            var tail = LogTailReader.ReadTail(path, 1024, LogEncodingChoice.SystemAnsi);

            Assert.Equal(LogTailReader.SystemAnsiEncoding.WebName, tail.Encoding!.WebName);
        }

        /// <summary>Past the last so many lines the rest is left out, and the header says how much.</summary>
        [Fact]
        public void LinesBeforeTheLastSoManyAreLeftOutAndCounted()
        {
            var text = new StringBuilder();
            for (int i = 0; i < DiagnosticsBundle.TailLines + 10; i++)
            {
                text.Append("line ").Append(i).Append('\n');
            }

            string path = this.WriteText("svc.out.log", text.ToString());
            int dropped = Enumerable.Range(0, 10).Sum(i => $"line {i}\n".Length);

            string tail = DiagnosticsBundle.Tail(path, out string? encoding);

            Assert.Equal("ASCII", encoding);
            Assert.StartsWith($"[... {dropped:N0} bytes skipped ...]\r\nline 10\n", tail, StringComparison.Ordinal);
            Assert.EndsWith($"line {DiagnosticsBundle.TailLines + 9}\n", tail, StringComparison.Ordinal);
        }

        /// <summary>A file that fits is copied whole, with no header.</summary>
        [Fact]
        public void AFileThatFitsIsTakenWhole()
        {
            string path = this.WriteText("svc.out.log", "one\ntwo");

            Assert.Equal("one\ntwo", DiagnosticsBundle.Tail(path, out _));
        }

        // Which files --------------------------------------------------------------

        [Theory]
        [InlineData("svc.out.log", "svc.out.log")]
        [InlineData("svc.3.out.log", "svc.out.log")]
        [InlineData("svc_20260923.out.log", "svc.out.log")]
        [InlineData("svc.20260923.#0001.out.log", "svc.out.log")]
        [InlineData("svc.out.log.old", "svc.out.log.old")]
        [InlineData("SVC.Err.log", "svc.err.log")]
        [InlineData("api.wrapper.log", "api.wrapper.log")]
        public void ARolledFileIsOfTheKindOfTheFileItWasRolledFrom(string name, string kind)
        {
            Assert.Equal(kind, DiagnosticsBundle.KindOf(name));
        }

        /// <summary>
        /// A busy .out.log rolled by size leaves more copies than the bundle takes, all newer
        /// than the .err.log and the wrapper's log. The newest of each kind goes in first.
        /// </summary>
        [Fact]
        public void TheNewestOfEachKindGoesInBeforeTheRest()
        {
            this.Write("svc.wrapper.log", Noon.AddDays(-2));
            this.Write("svc.err.log", Noon.AddDays(-1));
            this.Write("svc.out.log", Noon);
            for (int i = 0; i < 10; i++)
            {
                this.Write($"svc.{i}.out.log", Noon.AddMinutes(-10 * (i + 1)));
            }

            var found = ConfigPaths.FindLogFiles(this.directory, "svc", "svc.wrapper.log");
            var picked = DiagnosticsBundle.PickLogs(found, DiagnosticsBundle.MaxLogFiles).Select(f => f.Name);

            Assert.Equal(
                new[] { "svc.out.log", "svc.0.out.log", "svc.1.out.log", "svc.2.out.log", "svc.3.out.log", "svc.4.out.log", "svc.err.log", "svc.wrapper.log" },
                picked);
        }

        /// <summary>
        /// The wrapper's log is named after the configuration, and roll mode's .old files hold
        /// the run before the current one; the bundle takes them, each once, and reads each
        /// file the way the viewer would.
        /// </summary>
        [Fact]
        public void TheBundleTakesTheWrappersLogAndTheOldFiles()
        {
            byte[] gbk = Encoding.GetEncoding(936).GetBytes("端口 8000 被占用\r\n");
            this.WriteBytes("backend.err.log", gbk);
            this.Write("backend.out.log", Noon);
            this.Write("backend.out.log.old", Noon.AddHours(-1));
            this.Write("backend.err.log.old", Noon.AddHours(-1));
            this.Write("api.wrapper.log", Noon.AddMinutes(-1));
            this.Write("api.out.log", Noon);

            var entries = Bundle(zip => DiagnosticsBundle.AddLogs(zip, this.directory, "backend", "api.wrapper.log"));

            Assert.Equal(
                new[] { "logs/api.wrapper.log", "logs/backend.err.log", "logs/backend.err.log.old", "logs/backend.out.log", "logs/backend.out.log.old", "logs/index.txt" },
                entries.Select(e => e.Name).OrderBy(n => n, StringComparer.Ordinal));
            Assert.Equal(LogTailReader.SystemAnsiEncoding.GetString(gbk), entries.Single(e => e.Name == "logs/backend.err.log").Text);

            string index = entries.Single(e => e.Name == "logs/index.txt").Text;
            Assert.Contains("read as " + LogTailReader.SystemAnsiEncoding.WebName.ToUpperInvariant(), index, StringComparison.Ordinal);
            Assert.DoesNotContain("not included", index, StringComparison.Ordinal);
        }

        /// <summary>When the log name is the configuration's name, the wrapper's log goes in once.</summary>
        [Fact]
        public void AWrapperLogUnderTheStemGoesInOnce()
        {
            this.Write("svc.out.log", Noon);
            this.Write("svc.wrapper.log", Noon.AddMinutes(-1));

            var entries = Bundle(zip => DiagnosticsBundle.AddLogs(zip, this.directory, "svc", "svc.wrapper.log"));

            Assert.Equal(
                new[] { "logs/index.txt", "logs/svc.out.log", "logs/svc.wrapper.log" },
                entries.Select(e => e.Name).OrderBy(n => n, StringComparer.Ordinal));
        }

        /// <summary>The index lists what was left out as well as what went in.</summary>
        [Fact]
        public void TheIndexNamesWhatWasLeftOut()
        {
            this.Write("svc.out.log", Noon);
            for (int i = 0; i < DiagnosticsBundle.MaxLogFiles + 2; i++)
            {
                this.Write($"svc.{i}.out.log", Noon.AddMinutes(-10 * (i + 1)));
            }

            var entries = Bundle(zip => DiagnosticsBundle.AddLogs(zip, this.directory, "svc", "svc.wrapper.log"));

            Assert.Equal(DiagnosticsBundle.MaxLogFiles + 1, entries.Count);
            string oldest = entries.Single(e => e.Name == "logs/index.txt").Text
                .Split('\n')
                .Single(line => line.StartsWith($"svc.{DiagnosticsBundle.MaxLogFiles + 1}.out.log", StringComparison.Ordinal));
            Assert.EndsWith("not included", oldest.TrimEnd('\r'), StringComparison.Ordinal);
        }

        // The console's own logs ---------------------------------------------------

        [Fact]
        public void TheConsolesOwnLogsGoInWhenTheyExist()
        {
            string actions = this.WriteText("actions.log", "2026-09-23 12:00:00\tCORP\\henry\tstop\tsvc\tok\n");
            string errors = Path.Combine(this.directory, "errors.log");

            var entries = Bundle(zip => DiagnosticsBundle.AddConsoleLogs(zip, actions, errors));

            var entry = Assert.Single(entries);
            Assert.Equal("console/actions.log", entry.Name);
            Assert.Contains("stop\tsvc\tok", entry.Text, StringComparison.Ordinal);

            File.WriteAllText(errors, "2026-09-23 12:00:01 boom\n");
            entries = Bundle(zip => DiagnosticsBundle.AddConsoleLogs(zip, actions, errors));

            Assert.Equal(new[] { "console/actions.log", "console/errors.log" }, entries.Select(e => e.Name));
        }

        /// <summary>The console writes its error log beside its action log; the bundle looks for it there.</summary>
        [Fact]
        public void TheErrorLogIsLookedForBesideTheActionLog()
        {
            Assert.Equal(Path.GetDirectoryName(ActionLog.FilePath), Path.GetDirectoryName(DiagnosticsBundle.ErrorLogPath));
            Assert.Equal("errors.log", Path.GetFileName(DiagnosticsBundle.ErrorLogPath));
        }

        // Events -------------------------------------------------------------------

        /// <summary>An EventLogEntry's InstanceId carries the qualifier bits: 3221232503 is 7031.</summary>
        [Fact]
        public void EventsCarryTheIdEventViewerShows()
        {
            var search = new EventSearch(
                new[] { new ServiceEvent(Noon, EventLogEntryType.Error, 3221232503, "Service Control Manager", "The svc service terminated unexpectedly.") },
                new EventScan(12, null));

            string text = DiagnosticsBundle.FormatEvents(search);

            Assert.Contains("[7031]", text, StringComparison.Ordinal);
            Assert.DoesNotContain("3221232503", text, StringComparison.Ordinal);
        }

        /// <summary>A search that stopped short says how far back it looked, rather than "no events".</summary>
        [Fact]
        public void ASearchCutShortSaysHowFarBackItLooked()
        {
            var since = new DateTime(2026, 9, 22, 8, 0, 0);

            string none = DiagnosticsBundle.FormatEvents(new EventSearch(Array.Empty<ServiceEvent>(), new EventScan(400, since)));
            string some = DiagnosticsBundle.FormatEvents(new EventSearch(
                new[] { new ServiceEvent(Noon, EventLogEntryType.Information, 7036, "Service Control Manager", "The svc service entered the running state.") },
                new EventScan(400, since)));

            Assert.StartsWith("(no events for this service in the last 400 records, since 2026-09-22 08:00:00", none, StringComparison.Ordinal);
            Assert.Contains("[7036]", some, StringComparison.Ordinal);
            Assert.Contains("(searched the last 400 records, since 2026-09-22 08:00:00", some, StringComparison.Ordinal);
        }

        [Fact]
        public void NoEventsInLogsSearchedToTheirStartSaysSo()
        {
            Assert.Equal("(no events)", DiagnosticsBundle.FormatEvents(new EventSearch(Array.Empty<ServiceEvent>(), new EventScan(10, null))));
        }

        /// <summary>Builds a zip in memory with <paramref name="add"/> and reads back each entry's name and text.</summary>
        private static List<(string Name, string Text)> Bundle(Action<ZipArchive> add)
        {
            using var stream = new MemoryStream();
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                add(zip);
            }

            stream.Position = 0;
            using var read = new ZipArchive(stream, ZipArchiveMode.Read);
            var entries = new List<(string Name, string Text)>();
            foreach (var entry in read.Entries)
            {
                using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
                entries.Add((entry.FullName, reader.ReadToEnd()));
            }

            return entries;
        }

        private string Write(string name, DateTime written)
        {
            string path = this.WriteText(name, name + "\n");
            File.SetLastWriteTime(path, written);
            return path;
        }

        private string WriteText(string name, string text) => this.WriteBytes(name, Utf8.GetBytes(text));

        private string WriteBytes(string name, byte[] bytes)
        {
            string path = Path.Combine(this.directory, name);
            File.WriteAllBytes(path, bytes);
            return path;
        }
    }
}

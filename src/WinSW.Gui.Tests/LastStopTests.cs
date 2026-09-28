using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.ServiceProcess;
using System.Text;
using WinSW.Gui.Model;
using WinSW.Gui.Services;
using Xunit;

namespace WinSW.Gui.Tests
{
    /// <summary>
    /// The Last stop card: why a stopped service stopped, read from the end of its err.log and of the
    /// wrapper log since the last start, from the System log, and from the program's own exit code.
    /// The event log is handed in; everything else is real files in a directory of the test's own.
    /// </summary>
    public sealed class LastStopTests : IDisposable
    {
        private static readonly DateTime Start = new(2026, 9, 24, 10, 15, 0, 123, DateTimeKind.Local);

        private readonly string directory = Path.Combine(Path.GetTempPath(), "winsw-gui-" + Guid.NewGuid().ToString("n"));

        public LastStopTests()
        {
            Directory.CreateDirectory(this.directory);
        }

        public void Dispose() => Directory.Delete(this.directory, recursive: true);

        // The wrapper log -------------------------------------------------------

        /// <summary>
        /// The console's own 'winsw start' writes to the same log, and asks for a start that may never
        /// happen: the start is the wrapper's first line in service mode.
        /// </summary>
        [Fact]
        public void TheLastStartIsTheWrappersOwnLineInServiceMode()
        {
            var lines = new[]
            {
                "2026-09-23T08:00:00.000 DEBUG WinSW.Program - Starting WinSW in service mode.",
                "2026-09-23T08:00:00.500 WARN  WinSW.WrapperService - Child process 'uvicorn (100)' finished with code 3.",
                "2026-09-24T10:14:59.900 INFO  WinSW.Program - Starting service 'api (api)'...",
                "2026-09-24T10:15:00.123 DEBUG WinSW.Program - Starting WinSW in service mode.",
                "2026-09-24T10:15:00.300 INFO  WinSW.WrapperService - Starting D:\\apps\\api\\.venv\\Scripts\\uvicorn.exe",
                "2026-09-24T10:15:02.000 WARN  WinSW.WrapperService - Child process 'uvicorn (4312)' finished with code 1.",
                "2026-09-24T10:16:00.000 DEBUG WinSW.Program - Starting WinSW in console mode.",
            };

            var (index, startedAt) = LastStopReader.FindLastStart(lines);

            Assert.Equal(3, index);
            Assert.Equal(Start, startedAt);
            Assert.Equal(1, LastStopReader.FindProgramExitCode(lines, index));
        }

        /// <summary>2.x logs "Starting WinSW in the service mode" with a comma before the milliseconds.</summary>
        [Fact]
        public void A2xWrapperLogIsReadToo()
        {
            var lines = new[]
            {
                "2026-09-24 10:15:00,123 DEBUG - Starting WinSW in the service mode",
                "2026-09-24 10:15:02,000 WARN  - Child process [uvicorn.exe main:app] finished with 1",
            };

            var (index, startedAt) = LastStopReader.FindLastStart(lines);

            Assert.Equal(0, index);
            Assert.Equal(Start, startedAt);
            Assert.Equal(1, LastStopReader.FindProgramExitCode(lines, index));
        }

        [Theory]
        [InlineData("Child process 'uvicorn (4312)' finished with code 1.", 1)]
        [InlineData("Child process 'java (88)' finished with code '0'.", 0)]
        [InlineData("Child process 'node (7)' finished with code -1073741819.", -1073741819)]
        [InlineData("Child process [python.exe app.py] finished with 2", 2)]
        public void TheProgramsOwnCodeIsReadFromEveryShapeOfTheLine(string message, int code)
        {
            var lines = new[] { "2026-09-24T10:15:02.000 WARN  WinSW.WrapperService - " + message };

            Assert.Equal(code, LastStopReader.FindProgramExitCode(lines, 0));
        }

        /// <summary>A code from before the last start is an earlier run's, and says nothing about this stop.</summary>
        [Fact]
        public void AnEarlierRunsCodeIsNotThisOnes()
        {
            var lines = new[]
            {
                "2026-09-23T08:00:00.500 WARN  WinSW.WrapperService - Child process 'uvicorn (100)' finished with code 3.",
                "2026-09-24T10:15:00.123 DEBUG WinSW.Program - Starting WinSW in service mode.",
                "2026-09-24T10:15:00.300 INFO  WinSW.WrapperService - Service started successfully.",
            };

            Assert.Null(LastStopReader.FindProgramExitCode(lines, 1));
            Assert.Equal(3, LastStopReader.FindProgramExitCode(lines, 0));
        }

        [Fact]
        public void ALogWithNoStartSaysNothingOfWhen()
        {
            var (index, startedAt) = LastStopReader.FindLastStart(new[] { "Traceback (most recent call last):", "OSError: [Errno 10048]" });

            Assert.Equal(-1, index);
            Assert.Null(startedAt);
        }

        [Theory]
        [InlineData("2026-09-24T10:15:00.123 DEBUG x", true)]
        [InlineData("2026-09-24 10:15:00,123 DEBUG - x", true)]
        [InlineData("2026-09-24 10:15:00", false)]
        [InlineData("Starting WinSW in service mode.", false)]
        public void OnlyTheWrappersTimestampsAreTimes(string line, bool parsed)
        {
            Assert.Equal(parsed, LastStopReader.TryParseTime(line, out _));
        }

        // The System log ------------------------------------------------------------

        /// <summary>
        /// Read through EventLogEntry, 7031 comes with its qualifiers as 3221232503; a record already
        /// cut down to its ID is the same record.
        /// </summary>
        [Fact]
        public void TheNewestRecordOfAnUnaskedStopIsTheOne()
        {
            var events = new[]
            {
                Event(Start.AddSeconds(3), 7036, "The api service entered the stopped state."),
                Event(Start.AddSeconds(2), 3221232503, "The api service terminated unexpectedly. It has done this 1 time(s)."),
                Event(Start.AddSeconds(1), 7034, "The api service terminated unexpectedly."),
                Event(Start.AddSeconds(4), 7031, "Something else wrote this.", source: "Application Error"),
            };

            var picked = LastStopReader.PickStopEvent(events, Start);

            Assert.NotNull(picked);
            Assert.Equal(Start.AddSeconds(2), picked!.Time);
            Assert.Equal(7031, LastStopReader.EventNumber(picked.EventId));
        }

        [Theory]
        [InlineData(7000L)]
        [InlineData(7009L)]
        [InlineData(7023L)]
        [InlineData(7024L)]
        [InlineData(7031L)]
        [InlineData(7034L)]
        public void EveryUnaskedStopIsOne(long id)
        {
            Assert.True(LastStopReader.IsStopEvent(Event(Start, id, "x")));
        }

        [Theory]
        [InlineData(7036L)]
        [InlineData(7040L)]
        [InlineData(7045L)]
        public void AStateChangeOrAConfigurationChangeIsNot(long id)
        {
            Assert.False(LastStopReader.IsStopEvent(Event(Start, id, "x")));
        }

        /// <summary>
        /// A record older than the last start is about an earlier run. The record's time is to the
        /// second, so one from the start's own second is kept.
        /// </summary>
        [Fact]
        public void ARecordFromBeforeTheLastStartIsAnEarlierRuns()
        {
            var lastWeek = Event(Start.AddDays(-7), 7034, "The api service terminated unexpectedly.");
            var sameSecond = Event(new DateTime(2026, 9, 24, 10, 15, 0, DateTimeKind.Local), 7000, "The api service failed to start.");

            Assert.Null(LastStopReader.PickStopEvent(new[] { lastWeek }, Start));
            Assert.Same(sameSecond, LastStopReader.PickStopEvent(new[] { lastWeek, sameSecond }, Start));

            // Not knowing when it started, the newest is shown, with its time beside it.
            Assert.Same(lastWeek, LastStopReader.PickStopEvent(new[] { lastWeek }, null));
        }

        // Finding err.log ----------------------------------------------------------

        [Theory]
        [InlineData("api.err.log", true)]
        [InlineData("API.ERR.LOG", true)]
        [InlineData("api_20260924.err.log", true)]
        [InlineData("api.0.err.log", true)]
        [InlineData("api.20260924.#0001.err.log", true)]
        [InlineData("api-worker.err.log", false)]
        [InlineData("api.worker.err.log", false)]
        [InlineData("api.err.log.old", false)]
        [InlineData("api.out.log", false)]
        [InlineData("api.wrapper.log", false)]
        public void OnlyThisServicesErrorOutputIsItsErrLog(string fileName, bool matches)
        {
            Assert.Equal(matches, LastStopReader.IsErrorLogName(fileName, "api", ".err.log"));
        }

        /// <summary>Rolled by time, the one written to is the newest of the dated files.</summary>
        [Fact]
        public void TheNewestErrorOutputIsTheOneBeingWritten()
        {
            this.WriteLog("api_20260923.err.log", "old", Start.AddDays(-1));
            this.WriteLog("api_20260924.err.log", "new", Start.AddSeconds(2));
            this.WriteLog("api-worker.err.log", "another service's", Start.AddSeconds(5));

            Assert.Equal(Path.Combine(this.directory, "api_20260924.err.log"), LastStopReader.FindErrorLog(this.directory, "api", null));
        }

        [Fact]
        public void TheErrFilePatternOfTheRollingModesIsFollowed()
        {
            this.WriteLog("api.error.txt", "x", Start.AddSeconds(2));

            Assert.Null(LastStopReader.FindErrorLog(this.directory, "api", null));
            Assert.Equal(Path.Combine(this.directory, "api.error.txt"), LastStopReader.FindErrorLog(this.directory, "api", ".error.txt"));
        }

        [Fact]
        public void ADirectoryThatIsNotThereHasNoErrLog()
        {
            Assert.Null(LastStopReader.FindErrorLog(Path.Combine(this.directory, "missing"), "api", null));
        }

        // Putting it together ------------------------------------------------------

        /// <summary>uvicorn on a port that is taken: the reason is the last lines of err.log.</summary>
        [Fact]
        public void AnErrorWrittenSinceTheStartIsShownWithTheWrapperLinesSinceIt()
        {
            string config = this.WriteConfig("api.xml", "<service><id>api</id><executable>uvicorn.exe</executable></service>");
            this.WriteLog(
                "api.wrapper.log",
                "2026-09-23T08:00:00.000 DEBUG WinSW.Program - Starting WinSW in service mode.\n"
                + "2026-09-23T08:00:05.000 WARN  WinSW.WrapperService - Child process 'uvicorn (100)' finished with code 3.\n"
                + "2026-09-24T10:15:00.123 DEBUG WinSW.Program - Starting WinSW in service mode.\n"
                + "2026-09-24T10:15:02.000 WARN  WinSW.WrapperService - Child process 'uvicorn (4312)' finished with code 1.\n",
                Start.AddSeconds(2));
            this.WriteLog(
                "api.err.log",
                "INFO:     Started server process [4312]\r\n"
                + "ERROR:    [Errno 10048] error while attempting to bind on address ('0.0.0.0', 8000)\r\n"
                + "INFO:     Waiting for application shutdown.",
                Start.AddSeconds(2));
            var scm = Event(Start.AddSeconds(2), 3221232503, "The api service terminated unexpectedly.  It has done this 1 time(s).\r\n");

            var report = LastStopReader.Read(config, LogEncodingChoice.Auto, () => new[] { scm });

            Assert.Equal(Start, report.StartedAt);
            Assert.Equal(1, report.ProgramExitCode);
            Assert.Equal(7031, report.EventId);
            Assert.Equal("The api service terminated unexpectedly.  It has done this 1 time(s).", report.EventMessage);

            Assert.Equal(LogExcerptState.Lines, report.ErrorLog!.State);
            Assert.Equal(Path.Combine(this.directory, "api.err.log"), report.ErrorLog.Path);
            Assert.Equal(
                new[]
                {
                    "INFO:     Started server process [4312]",
                    "ERROR:    [Errno 10048] error while attempting to bind on address ('0.0.0.0', 8000)",
                    "INFO:     Waiting for application shutdown.",
                },
                report.ErrorLog.Lines);

            // The earlier run's lines are not this stop's.
            Assert.Equal(LogExcerptState.Lines, report.WrapperLog!.State);
            Assert.Equal(2, report.WrapperLog.Lines.Length);
            Assert.StartsWith("2026-09-24T10:15:00.123", report.WrapperLog.Lines[0]);
        }

        /// <summary>
        /// err.log has no times on its lines. Last written before the start, what it holds is an
        /// earlier run's, and showing it would put last week's traceback under today's stop.
        /// </summary>
        [Fact]
        public void AnErrLogLastWrittenBeforeTheStartHasNothingOfThisRun()
        {
            string config = this.WriteConfig("api.xml", "<service><id>api</id><executable>uvicorn.exe</executable></service>");
            this.WriteLog("api.wrapper.log", "2026-09-24T10:15:00.123 DEBUG WinSW.Program - Starting WinSW in service mode.\n", Start);
            this.WriteLog("api.err.log", "Traceback from last week\n", Start.AddDays(-7));

            var report = LastStopReader.Read(config, LogEncodingChoice.Auto, NoEvents);

            Assert.Equal(LogExcerptState.NothingSinceStart, report.ErrorLog!.State);
            Assert.Empty(report.ErrorLog.Lines);
            Assert.Null(report.EventTime);
        }

        /// <summary>Roll mode sets the old file aside at each start, and the new one can stay empty.</summary>
        [Fact]
        public void AnEmptyErrLogSinceAKnownStartHasNothingOfThisRun()
        {
            string config = this.WriteConfig("api.xml", "<service><id>api</id><executable>uvicorn.exe</executable></service>");
            this.WriteLog("api.wrapper.log", "2026-09-24T10:15:00.123 DEBUG WinSW.Program - Starting WinSW in service mode.\n", Start);
            this.WriteLog("api.err.log", string.Empty, Start.AddSeconds(1));

            var report = LastStopReader.Read(config, LogEncodingChoice.Auto, NoEvents);

            Assert.Equal(LogExcerptState.NothingSinceStart, report.ErrorLog!.State);
        }

        /// <summary>Without a wrapper log there is no telling when it started: the end of err.log is shown as it is.</summary>
        [Fact]
        public void WithoutAWrapperLogTheEndOfErrLogIsShown()
        {
            string config = this.WriteConfig("api.xml", "<service><id>api</id><executable>uvicorn.exe</executable></service>");
            this.WriteLog("api.err.log", string.Concat(Enumerable.Range(1, 30).Select(i => "line " + i + "\n")), Start.AddDays(-7));
            var old = Event(Start.AddDays(-7), 7034, "The api service terminated unexpectedly.");

            var report = LastStopReader.Read(config, LogEncodingChoice.Auto, () => new[] { old });

            Assert.Null(report.StartedAt);
            Assert.Null(report.ProgramExitCode);
            Assert.Equal(LogExcerptState.Missing, report.WrapperLog!.State);
            Assert.Equal(Path.Combine(this.directory, "api.wrapper.log"), report.WrapperLog.Path);
            Assert.Equal(LastStopReader.LineCount, report.ErrorLog!.Lines.Length);
            Assert.Equal("line 11", report.ErrorLog.Lines[0]);
            Assert.Equal("line 30", report.ErrorLog.Lines[^1]);
            Assert.Equal(old.Time, report.EventTime);
        }

        /// <summary>
        /// The error output is named after &lt;logname&gt; and goes to &lt;logpath&gt;; the wrapper's own
        /// log goes there too, but is named after the configuration file whatever &lt;logname&gt; says.
        /// </summary>
        [Fact]
        public void TheFilesAreWhereTheConfigurationPutsThem()
        {
            string logs = Path.Combine(this.directory, "logs");
            Directory.CreateDirectory(logs);
            string config = this.WriteConfig(
                "api.xml",
                "<service><id>api</id><executable>uvicorn.exe</executable><logpath>" + logs + "</logpath><logname>web</logname></service>");
            this.WriteLog(Path.Combine("logs", "api.wrapper.log"), "2026-09-24T10:15:00.123 DEBUG WinSW.Program - Starting WinSW in service mode.\n", Start);
            this.WriteLog(Path.Combine("logs", "web.err.log"), "boom\n", Start.AddSeconds(2));

            var report = LastStopReader.Read(config, LogEncodingChoice.Auto, NoEvents);

            Assert.Equal(Path.Combine(logs, "web.err.log"), report.ErrorLog!.Path);
            Assert.Equal(new[] { "boom" }, report.ErrorLog.Lines);
            Assert.Equal(Path.Combine(logs, "api.wrapper.log"), report.WrapperLog!.Path);
            Assert.Equal(LogExcerptState.Lines, report.WrapperLog.State);
        }

        [Fact]
        public void ErrorOutputTheConfigurationDoesNotWriteIsNotLookedFor()
        {
            string config = this.WriteConfig("api.xml", "<service><id>api</id><executable>uvicorn.exe</executable><errfiledisabled>true</errfiledisabled></service>");
            this.WriteLog("api.err.log", "an old file\n", Start);

            var report = LastStopReader.Read(config, LogEncodingChoice.Auto, NoEvents);

            Assert.Equal(LogExcerptState.NotWritten, report.ErrorLog!.State);
            Assert.False(report.ErrorLog.Exists);
        }

        [Fact]
        public void AMissingErrLogIsSaidToBeMissingWhereItWasLookedFor()
        {
            string config = this.WriteConfig("api.xml", "<service><id>api</id><executable>uvicorn.exe</executable></service>");

            var report = LastStopReader.Read(config, LogEncodingChoice.Auto, NoEvents);

            Assert.Equal(LogExcerptState.Missing, report.ErrorLog!.State);
            Assert.Equal(Path.Combine(this.directory, "api.err.log"), report.ErrorLog.Path);
            Assert.Equal(this.directory, report.ErrorLog.Directory);
        }

        /// <summary>The events do not depend on the configuration, and are still read without one.</summary>
        [Fact]
        public void WithoutAConfigurationTheEventIsStillFound()
        {
            var scm = Event(Start, 7000, "The api service failed to start due to the following error: ...");

            var report = LastStopReader.Read(null, LogEncodingChoice.Auto, () => new[] { scm });

            Assert.True(report.NoConfig);
            Assert.Null(report.ErrorLog);
            Assert.Null(report.WrapperLog);
            Assert.Equal(7000, report.EventId);
        }

        [Fact]
        public void AConfigurationThatCannotBeReadIsSaidSo()
        {
            string config = this.WriteConfig("api.xml", "<not-a-service />");

            var report = LastStopReader.Read(config, LogEncodingChoice.Auto, NoEvents);

            Assert.NotNull(report.ConfigError);
            Assert.Null(report.ErrorLog);
        }

        // Decoding -----------------------------------------------------------------

        [Fact]
        public void Utf8IsRead()
        {
            string plain = this.WriteBytes("plain.err.log", Encoding.UTF8.GetBytes("端口被占用\n"));

            Assert.Equal(new[] { "端口被占用" }, LastStopReader.ReadFile(plain, LogEncodingChoice.Auto).Lines);
        }

        /// <summary>
        /// A byte order mark says UTF-8 whatever follows. Only the last line is looked at: the reader
        /// shared with the Logs page passes over the first line of a file it reads from the start
        /// when that file begins with a mark, which is the reader's to put right.
        /// </summary>
        [Fact]
        public void AByteOrderMarkIsFollowed()
        {
            string bom = this.WriteBytes("bom.err.log", new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes("first\n端口被占用\n")).ToArray());

            var lines = LastStopReader.ReadFile(bom, LogEncodingChoice.Auto).Lines;

            Assert.Equal("端口被占用", lines[^1]);
            Assert.DoesNotContain(lines, line => line.Contains('\uFEFF'));
        }

        /// <summary>
        /// What is not UTF-8 is the system's ANSI code page, GBK on Chinese Windows, as on the Logs
        /// page; decoded as UTF-8 regardless, it would be replacement characters.
        /// </summary>
        [Fact]
        public void BytesThatAreNotUtf8AreTheSystemsAnsiCodePage()
        {
            byte[] gbk = { 0xB6, 0xCB, 0xBF, 0xDA, 0xB1, 0xBB, 0xD5, 0xBC, 0xD3, 0xC3, (byte)'\n' };
            string path = this.WriteBytes("gbk.err.log", gbk);

            var read = LastStopReader.ReadFile(path, LogEncodingChoice.Auto);

            Assert.Equal(new[] { LogTailReader.SystemAnsiEncoding.GetString(gbk, 0, gbk.Length - 1) }, read.Lines);
            Assert.DoesNotContain('\uFFFD', read.Lines[0]);
        }

        [Fact]
        public void TheEncodingChosenOnTheLogsPageIsHonoured()
        {
            string path = this.WriteBytes("forced.err.log", Encoding.UTF8.GetBytes("中文\n"));

            Assert.Equal(new[] { LogTailReader.SystemAnsiEncoding.GetString(Encoding.UTF8.GetBytes("中文")) }, LastStopReader.ReadFile(path, LogEncodingChoice.SystemAnsi).Lines);
        }

        /// <summary>A program that died mid-line leaves its last line without a line break; it is the one that matters.</summary>
        [Fact]
        public void TheLastLineIsReadWithoutItsLineBreak()
        {
            string path = this.WriteBytes("partial.err.log", Encoding.UTF8.GetBytes("first\r\nOSError: [Errno 10048]"));

            Assert.Equal(new[] { "first", "OSError: [Errno 10048]" }, LastStopReader.ReadFile(path, LogEncodingChoice.Auto).Lines);
        }

        [Fact]
        public void AFileThatIsNotThereIsMissing()
        {
            Assert.Equal(LastStopReader.LogFileState.Missing, LastStopReader.ReadFile(Path.Combine(this.directory, "none.log"), LogEncodingChoice.Auto).State);
        }

        // The card's text ---------------------------------------------------------------

        [Theory]
        [InlineData(1, "1")]
        [InlineData(0, "0")]
        [InlineData(-1073741819, "-1073741819 (0xC0000005)")]
        [InlineData(-1, "-1 (0xFFFFFFFF)")]
        public void AnExitCodeThatIsAnNtStatusIsShownInHexToo(int code, string text)
        {
            Assert.Equal(text, LastStopReader.ExitCodeText(code));
        }

        [Fact]
        public void TheCardSaysSinceWhenWhatTheProgramExitedWithAndWhatWindowsRecorded()
        {
            var report = new LastStopReport(
                Start,
                1,
                new LogExcerpt(LogExcerptState.Lines, "/logs/api.err.log", ImmutableLines("a", "b")),
                new LogExcerpt(LogExcerptState.NothingSinceStart, "/logs/api.wrapper.log"),
                Event(Start.AddSeconds(2), 7031, "The api service terminated unexpectedly."));

            var text = report.Describe(Keys);

            Assert.Equal("M.LastStop.Since(2026-09-24 10:15:00)", text.Since);
            Assert.Equal("1", text.ProgramExitCode);
            Assert.Equal("M.LastStop.EventTitle(2026-09-24 10:15:02, 7031)", text.EventTitle);
            Assert.Equal("The api service terminated unexpectedly.", text.EventText);
            Assert.Equal("M.LastStop.ErrorLog(api.err.log)", text.ErrorHeading);
            Assert.Equal("a" + Environment.NewLine + "b", text.ErrorLines);
            Assert.Equal(string.Empty, text.ErrorNote);
            Assert.Equal("M.LastStop.WrapperLog(api.wrapper.log)", text.WrapperHeading);
            Assert.Equal(string.Empty, text.WrapperLines);
            Assert.Equal("M.LastStop.NothingSince()", text.WrapperNote);
        }

        /// <summary>"No failure since the start" only when the start is known; otherwise only the recent log was searched.</summary>
        [Fact]
        public void NoRecordIsSaidOfWhatWasSearched()
        {
            Assert.Equal("M.LastStop.NoEvent()", new LastStopReport(Start, null, null, null, null).Describe(Keys).EventText);
            Assert.Equal("M.LastStop.NoEventRecent()", new LastStopReport(null, null, null, null, null).Describe(Keys).EventText);
            Assert.Equal(string.Empty, new LastStopReport(null, null, null, null, null, readError: "x").Describe(Keys).EventText);
        }

        [Fact]
        public void WhatCouldNotBeReadIsSaidInsteadOfSinceWhen()
        {
            Assert.Equal("M.LastStop.NoConfig()", new LastStopReport(null, null, null, null, null, noConfig: true).Describe(Keys).Since);
            Assert.Equal("M.LastStop.ConfigUnreadable(bad xml)", new LastStopReport(null, null, null, null, null, configError: "bad xml").Describe(Keys).Since);
            Assert.Equal("M.LastStop.CannotRead(denied)", new LastStopReport(null, null, null, null, null, readError: "denied").Describe(Keys).Since);
            Assert.Equal("M.LastStop.SinceUnknown()", new LastStopReport(null, null, null, null, null).Describe(Keys).Since);

            // No section for a file whose place is not known.
            var text = new LastStopReport(null, null, null, null, null, noConfig: true).Describe(Keys);
            Assert.Equal(string.Empty, text.ErrorHeading);
            Assert.Equal(string.Empty, text.WrapperHeading);
            Assert.Equal(string.Empty, text.ProgramExitCode);
        }

        [Theory]
        [InlineData(LogExcerptState.Empty, "M.LastStop.EmptyFile()")]
        [InlineData(LogExcerptState.NotWritten, "M.LastStop.NotWritten()")]
        [InlineData(LogExcerptState.Unreadable, "M.LastStop.CannotRead(denied)")]
        [InlineData(LogExcerptState.Missing, "M.LastStop.NoFile(/logs)")]
        public void EachStateOfAFileHasItsNote(LogExcerptState state, string note)
        {
            var report = new LastStopReport(Start, null, new LogExcerpt(state, "/logs/api.err.log", error: "denied"), null, null);

            Assert.Equal(note, report.Describe(Keys).ErrorNote);
        }

        /// <summary>
        /// The keys the card's text is made from are named through a rule, which the check of keys
        /// named in code cannot see: every one is in every language, and takes the arguments given.
        /// </summary>
        [Fact]
        public void EveryKeyTheCardAsksForIsInEveryLanguage()
        {
            var asked = new List<(string Key, object?[] Args)>();
            string Record(string key, object?[] args)
            {
                asked.Add((key, args));
                return key;
            }

            var excerpts = new[] { LogExcerptState.Lines, LogExcerptState.NothingSinceStart, LogExcerptState.Empty, LogExcerptState.Missing, LogExcerptState.NotWritten, LogExcerptState.Unreadable }
                .Select(state => new LogExcerpt(state, "/logs/api.err.log", ImmutableLines("x"), "denied"))
                .ToList();
            var reports = new List<LastStopReport>
            {
                new(Start, 1, null, null, Event(Start, 7031, "x")),
                new(Start, null, null, null, null),
                new(null, null, null, null, null),
                new(null, null, null, null, null, noConfig: true),
                new(null, null, null, null, null, configError: "bad"),
                new(null, null, null, null, null, readError: "bad"),
            };
            reports.AddRange(excerpts.Select(e => new LastStopReport(Start, null, e, e, null)));

            foreach (var report in reports)
            {
                report.Describe(Record);
            }

            foreach (string code in new[] { "en", "zh-CN", "zh-TW", "ja" })
            {
                var values = StringDictionaries.ValuesOf(code);
                foreach (var (key, args) in asked)
                {
                    Assert.True(values.ContainsKey(key), $"{key} is missing from Strings.{code}.xaml");
                    _ = string.Format(values[key], args);
                }
            }

            Assert.Contains(asked, a => a.Key == "M.LastStop.CannotRead");
            Assert.Contains(asked, a => a.Key == "M.LastStop.NoFile");
        }

        // Kept until the stop is over ----------------------------------------------------------

        [Fact]
        public void AStopIsReadOnceAndKeptUntilTheServiceMovesOn()
        {
            var entry = Stopped(1067);

            int stop = Assert.IsType<int>(entry.BeginLastStopRead());
            Assert.Null(entry.BeginLastStopRead());
            Assert.True(entry.IsReadingLastStop);

            var report = new LastStopReport(Start, 1, null, null, null);
            entry.EndLastStopRead(stop, report);

            Assert.Same(report, entry.LastStop);
            Assert.False(entry.IsReadingLastStop);
            Assert.Null(entry.BeginLastStopRead());

            // Started again: the report is about a stop that is over.
            entry.Status = ServiceControllerStatus.Running;
            Assert.Null(entry.LastStop);
            Assert.False(entry.IsStopped);
            Assert.Null(entry.BeginLastStopRead());

            entry.Status = ServiceControllerStatus.Stopped;
            Assert.NotNull(entry.BeginLastStopRead());
        }

        /// <summary>A read that comes back after the service has moved on describes a stop that is over, and is dropped.</summary>
        [Fact]
        public void AReadThatComesBackLateIsDropped()
        {
            var entry = Stopped(1067);
            int stop = Assert.IsType<int>(entry.BeginLastStopRead());

            entry.Status = ServiceControllerStatus.StartPending;
            entry.Status = ServiceControllerStatus.Stopped;
            entry.EndLastStopRead(stop, new LastStopReport(Start, 1, null, null, null));

            Assert.Null(entry.LastStop);
            Assert.NotNull(entry.BeginLastStopRead());
        }

        /// <summary>Stopped at both readings, with a new code: it ran and stopped again in between.</summary>
        [Fact]
        public void ANewExitCodeIsANewStop()
        {
            var entry = Stopped(1067);
            int stop = Assert.IsType<int>(entry.BeginLastStopRead());
            entry.EndLastStopRead(stop, new LastStopReport(Start, 1, null, null, null));

            entry.LastExitCode = 1;

            Assert.Null(entry.LastStop);
            Assert.NotNull(entry.BeginLastStopRead());
        }

        [Fact]
        public void AReadGivenUpOrForgottenIsMadeAgain()
        {
            var entry = Stopped(1067);
            int stop = Assert.IsType<int>(entry.BeginLastStopRead());
            entry.EndLastStopRead(stop, null);
            stop = Assert.IsType<int>(entry.BeginLastStopRead());
            entry.EndLastStopRead(stop, new LastStopReport(Start, 1, null, null, null));

            entry.ForgetLastStop();

            Assert.Null(entry.LastStop);
            Assert.True(entry.IsReadingLastStop);
            Assert.NotNull(entry.BeginLastStopRead());
        }

        [Fact]
        public void ARunningServiceHasNoLastStopToRead()
        {
            var entry = new ServiceEntry("api", "api", "/bin/WinSW.exe", "/svc/api.xml") { Status = ServiceControllerStatus.Running };

            Assert.False(entry.IsStopped);
            Assert.False(entry.IsReadingLastStop);
            Assert.Null(entry.BeginLastStopRead());
        }

        private static IReadOnlyList<ServiceEvent> NoEvents() => Array.Empty<ServiceEvent>();

        private static ServiceEntry Stopped(int exitCode) =>
            new("api", "api", "/bin/WinSW.exe", "/svc/api.xml") { Status = ServiceControllerStatus.Stopped, LastExitCode = exitCode };

        private static ServiceEvent Event(DateTime time, long id, string message, string source = "Service Control Manager") =>
            new(time, EventLogEntryType.Error, id, source, message);

        private static System.Collections.Immutable.ImmutableArray<string> ImmutableLines(params string[] lines) =>
            System.Collections.Immutable.ImmutableArray.Create(lines);

        /// <summary>A key and its arguments, for text the dictionaries would make.</summary>
        private static string Keys(string key, object?[] args) => $"{key}({string.Join(", ", args)})";

        private string WriteConfig(string name, string xml)
        {
            string path = Path.Combine(this.directory, name);
            File.WriteAllText(path, xml);
            return path;
        }

        private void WriteLog(string name, string text, DateTime lastWrite)
        {
            string path = Path.Combine(this.directory, name);
            File.WriteAllText(path, text);
            File.SetLastWriteTime(path, lastWrite);
        }

        private string WriteBytes(string name, byte[] bytes)
        {
            string path = Path.Combine(this.directory, name);
            File.WriteAllBytes(path, bytes);
            return path;
        }
    }
}

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using WinSW.Gui.Services;
using Xunit;

namespace WinSW.Gui.Tests
{
    /// <summary>
    /// The lines a crash's chat alert gets under it: the last line the program wrote to its error
    /// output since the run began, masked and cut short, and what a run left running. Real files,
    /// in a directory of the test's own.
    /// </summary>
    public sealed class StopCauseTests : IDisposable
    {
        private const string StartLine = "2026-09-24T10:15:00.123 DEBUG WinSW.Program - Starting WinSW in service mode.\n";

        private static readonly DateTime Start = new(2026, 9, 24, 10, 15, 0, 123, DateTimeKind.Local);

        private readonly string directory = Path.Combine(Path.GetTempPath(), "winsw-gui-" + Guid.NewGuid().ToString("n"));

        public StopCauseTests()
        {
            Directory.CreateDirectory(this.directory);
        }

        public void Dispose() => Directory.Delete(this.directory, recursive: true);

        // The line -------------------------------------------------------------------

        [Fact]
        public void TheLastLineWithAnythingOnItIsTaken()
        {
            Assert.Equal("ModuleNotFoundError: No module named 'main'", StopCause.LastLine("Traceback (most recent call last):\r\n  File \"x.py\"\r\nModuleNotFoundError: No module named 'main'\r\n\r\n  \n"));
            Assert.Equal("only", StopCause.LastLine("only"));
            Assert.Null(StopCause.LastLine("\r\n \n\t\n"));
            Assert.Null(StopCause.LastLine(string.Empty));
        }

        /// <summary>A progress bar redraws itself with carriage returns; its last state is the one.</summary>
        [Fact]
        public void ACarriageReturnEndsALineToo()
        {
            Assert.Equal("100%", StopCause.LastLine("10%\r50%\r100%\r"));
        }

        [Fact]
        public void ALongLineIsCutWithAnEllipsis()
        {
            string cut = StopCause.Shorten(new string('x', 500));

            Assert.Equal(StopCause.MaxLength, cut.Length);
            Assert.EndsWith("…", cut);
            Assert.Equal("short", StopCause.Shorten("  short\t"));
            Assert.Equal("a b", StopCause.Shorten("a\tb"));
        }

        /// <summary>Never between the two halves of a character outside the basic plane.</summary>
        [Fact]
        public void TheCutNeverSplitsACharacter()
        {
            // The character's two halves stand either side of where the cut would fall.
            string emoji = "\U0001F600";
            string line = new string('x', StopCause.MaxLength - 2) + emoji + new string('y', 50);

            string cut = StopCause.Shorten(line);

            Assert.Equal(new string('x', StopCause.MaxLength - 2) + "…", cut);
        }

        /// <summary>
        /// The line leaves the machine: a connection string's password, or a secret passed by name,
        /// is masked before it goes.
        /// </summary>
        [Fact]
        public void SecretsInTheLineAreMasked()
        {
            string masked = ConfigRedactor.RedactText("sqlalchemy.exc.OperationalError: could not connect to postgresql://app:s3cret@db:5432/app");
            Assert.DoesNotContain("s3cret", masked);
            Assert.Contains("postgresql://" + ConfigRedactor.Mask + "@db:5432/app", masked);

            string named = ConfigRedactor.RedactText("login failed: password=hunter2 for user app");
            Assert.DoesNotContain("hunter2", named);
            Assert.Contains("for user app", named);

            Assert.Equal("ERROR: [Errno 10048] address already in use", ConfigRedactor.RedactText("ERROR: [Errno 10048] address already in use"));
        }

        [Fact]
        public void TheCauseGoesUnderTheMessageEachOnALine()
        {
            string text = StopCause.Append("[WinSW] web01: api stopped.", new ErrorLine("api.err.log", "boom"), "Last line of {0}: {1}", " Port 8000 is held by python.exe (PID 4312). ");

            Assert.Equal("[WinSW] web01: api stopped.\nLast line of api.err.log: boom\nPort 8000 is held by python.exe (PID 4312).", text);
            Assert.Equal("[WinSW] web01: api stopped.", StopCause.Append("[WinSW] web01: api stopped.", null, "{0}: {1}", null));
        }

        // Reading it -----------------------------------------------------------------

        [Fact]
        public void TheLastLineWrittenSinceTheStartIsRead()
        {
            string config = this.WriteConfig();
            this.WriteLog("api.wrapper.log", "2026-09-23T08:00:00.000 DEBUG WinSW.Program - Starting WinSW in service mode.\n" + StartLine, Start);
            this.WriteLog("api.err.log", "INFO:     Started server process [4312]\r\nERROR:    [Errno 10048] error while attempting to bind on address ('0.0.0.0', 8000)\r\n", Start.AddSeconds(2));

            var line = StopCause.Read(Source(config));

            Assert.Equal(new ErrorLine("api.err.log", "ERROR:    [Errno 10048] error while attempting to bind on address ('0.0.0.0', 8000)"), line);
        }

        /// <summary>Last written before the run began, the file holds an earlier run's cause, not this one's.</summary>
        [Fact]
        public void AnErrLogFromBeforeTheStartIsNotQuoted()
        {
            string config = this.WriteConfig();
            this.WriteLog("api.wrapper.log", StartLine, Start);
            this.WriteLog("api.err.log", "Traceback from last week\n", Start.AddDays(-7));

            Assert.Null(StopCause.Read(Source(config)));
        }

        /// <summary>
        /// Without the wrapper's line for the start, the start the console saw decides; without that
        /// either, nothing is quoted rather than a line of unknown age.
        /// </summary>
        [Fact]
        public void WithoutTheWrappersLineTheStartTheConsoleSawDecides()
        {
            string config = this.WriteConfig();
            this.WriteLog("api.err.log", "boom\n", Start.AddSeconds(2));

            Assert.Equal("boom", StopCause.Read(Source(config, lastSeenStart: Start))?.Text);
            Assert.Null(StopCause.Read(Source(config, lastSeenStart: Start.AddSeconds(5))));
            Assert.Null(StopCause.Read(Source(config)));
        }

        /// <summary>A desktop task's run began at its last run, which the task scheduler knows.</summary>
        [Fact]
        public void AKnownStartNeedsNoWrapperLog()
        {
            string config = this.WriteConfig();
            this.WriteLog("api.err.log", "robot lost the window\n", Start.AddMinutes(1));

            Assert.Equal("robot lost the window", StopCause.Read(new StopCauseSource(config, LogEncodingChoice.Auto, Start, null, null))?.Text);
        }

        [Fact]
        public void ErrorOutputTheConfigurationDoesNotWriteIsNotLookedFor()
        {
            string config = this.WriteConfig("<service><id>api</id><executable>uvicorn.exe</executable><errfiledisabled>true</errfiledisabled></service>");
            this.WriteLog("api.wrapper.log", StartLine, Start);
            this.WriteLog("api.err.log", "boom\n", Start.AddSeconds(2));

            Assert.Null(StopCause.Read(Source(config)));
        }

        [Fact]
        public void WhatCannotBeReadIsLeftOut()
        {
            Assert.Null(StopCause.Read(Source(Path.Combine(this.directory, "missing.xml"))));
            Assert.Null(StopCause.Read(Source(this.WriteConfig("<not-a-service />"))));
            Assert.Null(StopCause.Read(Source(null)));
        }

        /// <summary>A file that begins with a byte order mark and has one line: the line is still there.</summary>
        [Fact]
        public void AOneLineFileWithAByteOrderMarkIsRead()
        {
            string config = this.WriteConfig();
            this.WriteLog("api.wrapper.log", StartLine, Start);
            string path = Path.Combine(this.directory, "api.err.log");
            File.WriteAllBytes(path, new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes("端口被占用")).ToArray());
            File.SetLastWriteTime(path, Start.AddSeconds(2));

            Assert.Equal("端口被占用", StopCause.Read(Source(config))?.Text);
        }

        /// <summary>The alert goes whether or not there is a cause to give: none for a service without a configuration.</summary>
        [Fact]
        public async Task ReadingForAServiceWithoutAConfigurationGivesNothing()
        {
            Assert.Null(await StopCause.ReadAsync(Source(null)));
        }

        [Fact]
        public void OnlyACrashAndALoopHaveACause()
        {
            Assert.True(StopCause.Explains(StopNoticeKind.UnexpectedStop));
            Assert.True(StopCause.Explains(StopNoticeKind.RepeatedStops));
            Assert.False(StopCause.Explains(StopNoticeKind.CleanStop));
            Assert.False(StopCause.Explains(StopNoticeKind.Recovered));
        }

        private static StopCauseSource Source(string? config, DateTime? lastSeenStart = null) =>
            new(config, LogEncodingChoice.Auto, StartedAt: null, LastSeenStart: lastSeenStart, Stray: null);

        private string WriteConfig(string xml = "<service><id>api</id><executable>uvicorn.exe</executable></service>")
        {
            string path = Path.Combine(this.directory, "api.xml");
            File.WriteAllText(path, xml);
            return path;
        }

        private void WriteLog(string name, string text, DateTime lastWrite)
        {
            string path = Path.Combine(this.directory, name);
            File.WriteAllText(path, text);
            File.SetLastWriteTime(path, lastWrite);
        }
    }
}

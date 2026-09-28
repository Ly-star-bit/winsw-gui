using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using WinSW.Gui.Services;
using Xunit;

namespace WinSW.Gui.Tests
{
    /// <summary>
    /// The error log is what gets sent when the console misbehaves somewhere else, so what it
    /// promises is that an entry carries enough to act on: when, where it was caught, which
    /// build on which system, and the whole exception.
    /// </summary>
    public class ErrorLogTests
    {
        private const string Process = "WinSW GUI 1.2.3+abcdef\tMicrosoft Windows NT 6.3.9600.0 X64\t.NET 8.0.8 X64\tstandard user";

        [Fact]
        public void AnEntryNamesTheTimeThePlaceAndTheBuild()
        {
            string entry = ErrorLog.Format(new DateTime(2026, 9, 23, 14, 5, 12), "UI thread", Process, Thrown("boom"));

            string header = entry.Split(Environment.NewLine)[0];
            string[] fields = header.Split('\t');
            Assert.Equal("2026-09-23 14:05:12", fields[0]);
            Assert.Equal("UI thread", fields[1]);
            Assert.Equal("WinSW GUI 1.2.3+abcdef", fields[2]);
            Assert.Equal("Microsoft Windows NT 6.3.9600.0 X64", fields[3]);
            Assert.Equal(".NET 8.0.8 X64", fields[4]);
            Assert.Equal("standard user", fields[5]);
        }

        /// <summary>The cause as well as the wrapper, and where each was thrown.</summary>
        [Fact]
        public void AnEntryCarriesTheWholeChainWithItsStacks()
        {
            Exception outer;
            try
            {
                try
                {
                    throw new FileNotFoundException("settings.json is gone");
                }
                catch (FileNotFoundException inner)
                {
                    throw new InvalidOperationException("The settings could not be read.", inner);
                }
            }
            catch (InvalidOperationException e)
            {
                outer = e;
            }

            string entry = ErrorLog.Format(DateTime.Now, "command", Process, outer);

            Assert.Contains("System.InvalidOperationException: The settings could not be read.", entry, StringComparison.Ordinal);
            Assert.Contains("System.IO.FileNotFoundException: settings.json is gone", entry, StringComparison.Ordinal);
            Assert.Contains(nameof(this.AnEntryCarriesTheWholeChainWithItsStacks), entry, StringComparison.Ordinal);
        }

        /// <summary>A faulted task's aggregate of aggregates is printed as one level of failures.</summary>
        [Fact]
        public void AnAggregateIsFlattened()
        {
            var nested = new AggregateException(
                new AggregateException(Thrown("first")),
                Thrown("second"));

            string entry = ErrorLog.Format(DateTime.Now, "unobserved task", Process, nested);

            Assert.Single(Regex.Matches(entry, "System.AggregateException"));
            Assert.Contains("first", entry, StringComparison.Ordinal);
            Assert.Contains("second", entry, StringComparison.Ordinal);
        }

        /// <summary>The fatal handler hands over whatever the runtime gave it, which need not be an exception.</summary>
        [Fact]
        public void NoExceptionIsStillAnEntry()
        {
            string entry = ErrorLog.Format(DateTime.Now, "background thread, fatal", Process, null);

            Assert.Contains("No exception object was given.", entry, StringComparison.Ordinal);
        }

        /// <summary>
        /// Entries are separated by a blank line, and the file is kept like the action log:
        /// set aside as errors.1.log past the size limit.
        /// </summary>
        [Fact]
        public void EntriesAreSeparatedAndTheFileIsSetAsideWhenFull()
        {
            string directory = Path.Combine(Path.GetTempPath(), "winsw-gui-" + Guid.NewGuid().ToString("n"));
            string path = Path.Combine(directory, "errors.log");

            try
            {
                string first = ErrorLog.Format(new DateTime(2026, 9, 23, 14, 0, 0), "UI thread", Process, Thrown("one"));
                string second = ErrorLog.Format(new DateTime(2026, 9, 23, 14, 0, 2), "UI thread", Process, Thrown("two"));
                ActionLog.Append(path, first);
                ActionLog.Append(path, second);

                string text = File.ReadAllText(path);
                Assert.Equal(first + Environment.NewLine + second + Environment.NewLine, text);
                Assert.Contains(Environment.NewLine + Environment.NewLine + "2026-09-23 14:00:02\t", text, StringComparison.Ordinal);

                File.WriteAllText(path, new string('x', (int)ActionLog.MaxBytes));
                ActionLog.Append(path, second);

                Assert.Equal(second + Environment.NewLine, File.ReadAllText(path));
                Assert.Equal(ActionLog.MaxBytes, new FileInfo(Path.Combine(directory, "errors.1.log")).Length);
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        /// <summary>
        /// The diagnostics bundle finds the file by computing this path itself, so it must stay
        /// exactly this.
        /// </summary>
        [Fact]
        public void TheFileIsWhereTheDiagnosticsBundleLooks()
        {
            Assert.Equal(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinSW.Gui", "errors.log"),
                ErrorLog.FilePath);
        }

        [Fact]
        public async Task ATaskNobodyAwaitsIsRecordedWhenItFails()
        {
            var recorded = new List<(string What, Exception? Exception)>();
            var source = new TaskCompletionSource();

            var continuation = ErrorLog.Observe(source.Task, "update check", (what, e) => recorded.Add((what, e)));
            source.SetException(new InvalidOperationException("no network"));
            await continuation;

            var (what, exception) = Assert.Single(recorded);
            Assert.Equal("update check", what);
            Assert.Equal("no network", Assert.IsType<AggregateException>(exception).InnerException!.Message);
        }

        [Fact]
        public async Task ATaskThatSucceedsOrIsCancelledRecordsNothing()
        {
            var recorded = new List<string>();

            var succeeded = ErrorLog.Observe(Task.CompletedTask, "update check", (what, _) => recorded.Add(what));
            var cancelled = ErrorLog.Observe(Task.FromCanceled(new System.Threading.CancellationToken(true)), "update check", (what, _) => recorded.Add(what));
            await Task.WhenAny(succeeded);
            await Task.WhenAny(cancelled);

            Assert.True(succeeded.IsCanceled);
            Assert.True(cancelled.IsCanceled);
            Assert.Empty(recorded);
        }

        private static Exception Thrown(string message)
        {
            try
            {
                throw new InvalidOperationException(message);
            }
            catch (InvalidOperationException e)
            {
                return e;
            }
        }
    }
}

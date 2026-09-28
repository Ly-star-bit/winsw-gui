using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using WinSW.Gui.ViewModels;
using Xunit;

namespace WinSW.Gui.Tests
{
    /// <summary>
    /// The log viewer's file list is looked at again every half minute while a file is on
    /// screen, so it is brought up to date in place: the entry on screen is never replaced or
    /// dropped, which would make the picker let go of it and the viewer start the file over.
    /// </summary>
    public sealed class LogFileListTests : IDisposable
    {
        private static readonly DateTime Noon = new(2026, 9, 23, 12, 0, 0);

        private readonly string directory = Path.Combine(Path.GetTempPath(), "winsw-gui-list-" + Guid.NewGuid().ToString("n"));

        public LogFileListTests()
        {
            Directory.CreateDirectory(this.directory);
        }

        public void Dispose() => Directory.Delete(this.directory, recursive: true);

        /// <summary>
        /// Only this service's logs, newest first — among them the .log.old files roll mode
        /// sets aside at each start, which hold the run before the one on screen.
        /// </summary>
        [Fact]
        public void TheScanFindsTheServicesLogsNewestFirst()
        {
            this.Write("svc.out.log", Noon);
            this.Write("svc.0.out.log", Noon.AddHours(-2));
            this.Write("svc.err.log", Noon.AddHours(-1));
            this.Write("svc.out.log.old", Noon.AddHours(-3));
            this.Write("svc.out.old", Noon);
            this.Write("other.out.log", Noon);

            var names = this.Scan().Select(f => f.Name);

            Assert.Equal(new[] { "svc.out.log", "svc.err.log", "svc.0.out.log", "svc.out.log.old" }, names);
        }

        /// <summary>
        /// The wrapper names its own log after the configuration file, whatever
        /// <c>&lt;logname&gt;</c> says, so with a log name of its own a service's wrapper log
        /// does not start with it — and is listed all the same.
        /// </summary>
        [Fact]
        public void TheWrappersLogIsFoundUnderTheConfigurationsName()
        {
            this.Write("backend.out.log", Noon);
            this.Write("api.wrapper.log", Noon.AddMinutes(-1));
            this.Write("api.out.log", Noon.AddMinutes(-2));

            var names = LogFileEntry.Scan(this.directory, "backend", "api.wrapper.log").Select(f => f.Name);

            Assert.Equal(new[] { "backend.out.log", "api.wrapper.log" }, names);
        }

        /// <summary>When the log name is the configuration's name, the wrapper's log is listed once.</summary>
        [Fact]
        public void AWrapperLogUnderTheStemIsListedOnce()
        {
            this.Write("svc.out.log", Noon);
            this.Write("svc.wrapper.log", Noon.AddMinutes(-1));

            var names = this.Scan().Select(f => f.Name);

            Assert.Equal(new[] { "svc.out.log", "svc.wrapper.log" }, names);
        }

        /// <summary>
        /// roll-by-time's next file turns up at the top, the entry on screen stays the same
        /// object, and what is gone goes.
        /// </summary>
        [Fact]
        public void ANewFileIsAddedAndTheOneOnScreenStays()
        {
            this.Write("svc_20260922.out.log", Noon.AddDays(-1));
            this.Write("svc_20260921.out.log", Noon.AddDays(-2));
            var files = new ObservableCollection<LogFileEntry>(this.Scan());
            var onScreen = files[0];

            File.Delete(Path.Combine(this.directory, "svc_20260921.out.log"));
            this.Write("svc_20260923.out.log", Noon);

            var added = LogFileEntry.Merge(files, this.Scan(), onScreen);

            Assert.Equal(new[] { "svc_20260923.out.log" }, added.Select(f => f.Name));
            Assert.Equal(new[] { "svc_20260923.out.log", "svc_20260922.out.log" }, files.Select(f => f.Name));
            Assert.Same(onScreen, files[1]);
        }

        /// <summary>
        /// In roll mode the file on screen is missing for a moment at each start. It is kept
        /// until it is back, rather than taken from under the viewer.
        /// </summary>
        [Fact]
        public void TheFileOnScreenIsKeptWhenTheScanMissesIt()
        {
            this.Write("svc.out.log", Noon);
            this.Write("svc.err.log", Noon.AddMinutes(-1));
            var files = new ObservableCollection<LogFileEntry>(this.Scan());
            var onScreen = files.Single(f => f.Name == "svc.out.log");

            var added = LogFileEntry.Merge(files, Array.Empty<LogFileEntry>(), onScreen);

            Assert.Empty(added);
            Assert.Same(onScreen, Assert.Single(files));
        }

        /// <summary>An entry that is still there takes its new size and time, and says so for the caption.</summary>
        [Fact]
        public void AnEntryStillThereIsUpdatedInPlace()
        {
            string path = this.Write("svc.out.log", Noon.AddMinutes(-5));
            var files = new ObservableCollection<LogFileEntry>(this.Scan());
            var entry = files[0];
            var changed = new List<string?>();
            entry.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

            File.AppendAllText(path, "more output\n");
            File.SetLastWriteTime(path, Noon);
            LogFileEntry.Merge(files, this.Scan(), entry);

            Assert.Same(entry, Assert.Single(files));
            Assert.Equal(new FileInfo(path).Length, entry.Length);
            Assert.Equal(Noon, entry.LastWrite);
            Assert.Contains(nameof(LogFileEntry.Caption), changed);
        }

        /// <summary>Nothing changed, nothing announced: a scan every half minute must not churn the list.</summary>
        [Fact]
        public void AnUnchangedScanChangesNothing()
        {
            this.Write("svc.out.log", Noon);
            this.Write("svc.err.log", Noon.AddMinutes(-1));
            var files = new ObservableCollection<LogFileEntry>(this.Scan());
            var before = files.ToList();
            int notices = 0;
            files.CollectionChanged += (_, _) => notices++;
            foreach (var file in files)
            {
                file.PropertyChanged += (_, _) => notices++;
            }

            var added = LogFileEntry.Merge(files, this.Scan(), files[0]);

            Assert.Empty(added);
            Assert.Equal(0, notices);
            Assert.Equal(before, files);
        }

        /// <summary>roll-by-time's next period is news: written later than anything listed before.</summary>
        [Fact]
        public void ANewPeriodsFileIsNews()
        {
            this.Write("svc_20260922.out.log", Noon.AddDays(-1));
            this.Write("svc.wrapper.log", Noon.AddHours(-3));
            var files = new ObservableCollection<LogFileEntry>(this.Scan());

            this.Write("svc_20260923.out.log", Noon);
            var added = LogFileEntry.Merge(files, this.Scan(), files[0]);

            Assert.Equal(new[] { "svc_20260923.out.log" }, LogFileEntry.NewerThanTheRest(files, added).Select(f => f.Name));
        }

        /// <summary>
        /// A busy .err.log rolled by size beside a quiet .out.log on screen: the rolled file is
        /// newer than the one on screen, but not than the .err.log that took over its name, so
        /// it is not news.
        /// </summary>
        [Fact]
        public void AFileRolledAwayBySizeIsNotNews()
        {
            string err = this.Write("svc.err.log", Noon.AddMinutes(-5));
            this.Write("svc.out.log", Noon.AddMinutes(-30));
            var files = new ObservableCollection<LogFileEntry>(this.Scan());
            var onScreen = files.Single(f => f.Name == "svc.out.log");

            File.SetLastWriteTime(err, Noon.AddMinutes(-1));
            File.Move(err, Path.Combine(this.directory, "svc.0.err.log"));
            this.Write("svc.err.log", Noon);
            var added = LogFileEntry.Merge(files, this.Scan(), onScreen);

            Assert.Equal(new[] { "svc.0.err.log" }, added.Select(f => f.Name));
            Assert.Empty(LogFileEntry.NewerThanTheRest(files, added));
        }

        /// <summary>
        /// At each start roll mode sets the file on screen aside as .old and begins a new one
        /// under its name: the .old file joins the list, and is not news, since the file that
        /// took over the name is newer.
        /// </summary>
        [Fact]
        public void RollModesOldFileJoinsTheListWithoutBeingNews()
        {
            string output = this.Write("svc.out.log", Noon.AddMinutes(-5));
            var files = new ObservableCollection<LogFileEntry>(this.Scan());
            var onScreen = files[0];

            File.Move(output, output + ".old");
            this.Write("svc.out.log", Noon);
            var added = LogFileEntry.Merge(files, this.Scan(), onScreen);

            Assert.Equal(new[] { "svc.out.log.old" }, added.Select(f => f.Name));
            Assert.Empty(LogFileEntry.NewerThanTheRest(files, added));
            Assert.Same(onScreen, files[0]);
        }

        /// <summary>The first files of a service that had written none are not news, only the list.</summary>
        [Fact]
        public void TheFirstFilesAreNotNews()
        {
            var files = new ObservableCollection<LogFileEntry>();
            this.Write("svc.out.log", Noon);

            var added = LogFileEntry.Merge(files, this.Scan(), onScreen: null);

            Assert.Single(added);
            Assert.Empty(LogFileEntry.NewerThanTheRest(files, added));
        }

        private IReadOnlyList<LogFileEntry> Scan() => LogFileEntry.Scan(this.directory, "svc", "svc.wrapper.log");

        private string Write(string name, DateTime written)
        {
            string path = Path.Combine(this.directory, name);
            File.WriteAllText(path, name + "\n");
            File.SetLastWriteTime(path, written);
            return path;
        }
    }
}

using System;
using System.IO;
using System.Linq;
using WinSW.Gui.Services;
using WinSW.Gui.ViewModels;
using Xunit;

namespace WinSW.Gui.Tests
{
    /// <summary>Which log files cleanup takes, and how it counts what it did.</summary>
    public sealed class LogCleanupTests : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "winsw-gui-" + Guid.NewGuid().ToString("n"));

        public LogCleanupTests()
        {
            Directory.CreateDirectory(this.directory);
        }

        public void Dispose() => Directory.Delete(this.directory, recursive: true);

        /// <summary>By age alone, and never the file on screen, however old it is.</summary>
        [Fact]
        public void OnlyFilesOlderThanTheCutoffAreTaken()
        {
            var now = new DateTime(2026, 9, 22, 12, 0, 0);
            var fresh = this.Entry("app.out.log", now.AddHours(-1));
            var old = this.Entry("app.out.1.log", now.AddDays(-40));
            var older = this.Entry("app.out.2.log", now.AddDays(-90));

            var taken = LogFileEntry.OlderThan(new[] { fresh, old, older }, now.AddDays(-30), onScreen: older.Path);

            Assert.Equal(new[] { old.Path }, taken.Select(f => f.Path));
        }

        [Fact]
        public void WhatIsGoneIsCountedAndWhatIsLeftIsSaid()
        {
            var gone = this.Entry("a.log", DateTime.Now);
            var kept = this.Entry("b.log", DateTime.Now);
            string missing = Path.Combine(this.directory, "already-gone.log");

            var refused = LogCleanup.DeleteDirectly(new[] { gone.Path, missing });
            Assert.Empty(refused);
            Assert.False(File.Exists(gone.Path));

            var outcome = LogCleanup.Tally(new[] { (gone.Path, 100L), (kept.Path, 50L) }, declined: false);
            Assert.Equal(new LogCleanupOutcome(1, 100, 1, false), outcome);
        }

        private LogFileEntry Entry(string name, DateTime written)
        {
            string path = Path.Combine(this.directory, name);
            File.WriteAllText(path, "x");
            File.SetLastWriteTime(path, written);
            return new LogFileEntry(new FileInfo(path));
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using WinSW.Gui.ViewModels;
using Xunit;

namespace WinSW.Gui.Tests
{
    /// <summary>
    /// The log viewer can be asked to open one of a service's files rather than the newest —
    /// the dashboard's last-stop card asks for the .err.log it read, where a program says why
    /// it would not start, while the newest file is as often the wrapper's log. The path it
    /// asks with is put together from the configuration, not taken from the viewer's scan.
    /// </summary>
    public sealed class LogFilePreferenceTests : IDisposable
    {
        private static readonly DateTime Noon = new(2026, 9, 24, 12, 0, 0);

        private readonly string directory = Path.Combine(Path.GetTempPath(), "winsw-gui-prefer-" + Guid.NewGuid().ToString("n"));

        public LogFilePreferenceTests()
        {
            Directory.CreateDirectory(this.directory);
        }

        public void Dispose() => Directory.Delete(this.directory, recursive: true);

        /// <summary>The file asked for, not the newest: the wrapper wrote its log after the program failed.</summary>
        [Fact]
        public void TheFileAskedForIsFoundByItsPath()
        {
            string err = this.Write("svc.err.log", Noon.AddSeconds(-2));
            this.Write("svc.wrapper.log", Noon);
            this.Write("svc.out.log", Noon.AddSeconds(-1));
            var files = this.Scan();

            var found = LogFileEntry.Find(files, err);

            Assert.Equal("svc.wrapper.log", files[0].Name);
            Assert.NotNull(found);
            Assert.Equal("svc.err.log", found!.Name);
        }

        /// <summary>Windows paths are not case-sensitive, and the one asked with need not match the scan's case.</summary>
        [Fact]
        public void ThePathIsComparedWithoutRegardToCase()
        {
            string err = this.Write("svc.err.log", Noon);
            this.Write("svc.out.log", Noon.AddMinutes(1));

            var found = LogFileEntry.Find(this.Scan(), err.ToUpperInvariant());

            Assert.Equal("svc.err.log", found?.Name);
        }

        /// <summary>
        /// A log directory written with a <c>..</c> in it, or otherwise spelled unlike the
        /// scan's, names the same file: every file listed for a service is in its one directory.
        /// </summary>
        [Fact]
        public void ADirectorySpelledDifferentlyStillFindsTheFile()
        {
            this.Write("svc.err.log", Noon);
            this.Write("svc.out.log", Noon.AddMinutes(1));
            string roundabout = Path.Combine(this.directory, "sub", "..", "svc.err.log");

            var found = LogFileEntry.Find(this.Scan(), roundabout);

            Assert.Equal("svc.err.log", found?.Name);
        }

        /// <summary>A file name alone is enough.</summary>
        [Fact]
        public void AFileNameAloneIsFound()
        {
            this.Write("svc.err.log", Noon);
            this.Write("svc.out.log", Noon.AddMinutes(1));

            var found = LogFileEntry.Find(this.Scan(), "SVC.ERR.LOG");

            Assert.Equal("svc.err.log", found?.Name);
        }

        /// <summary>The whole path comes first: of two files of the same name, the one in the directory asked for.</summary>
        [Fact]
        public void TheWholePathComesBeforeTheName()
        {
            string other = Path.Combine(this.directory, "other");
            Directory.CreateDirectory(other);
            this.Write("svc.err.log", Noon);
            string wanted = Path.Combine(other, "svc.err.log");
            File.WriteAllText(wanted, "other\n");
            var files = this.Scan().Concat(LogFileEntry.Scan(other, "svc", "svc.wrapper.log")).ToList();

            var found = LogFileEntry.Find(files, wanted);

            Assert.Same(files[1], found);
        }

        /// <summary>A file that is not there is not found, and the viewer opens the newest instead.</summary>
        [Fact]
        public void AFileNotListedIsNotFound()
        {
            this.Write("svc.out.log", Noon);

            Assert.Null(LogFileEntry.Find(this.Scan(), Path.Combine(this.directory, "svc.err.log")));
        }

        /// <summary>Nothing asked for, or a directory rather than a file, finds nothing.</summary>
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void NothingAskedForFindsNothing(string? path)
        {
            this.Write("svc.out.log", Noon);

            Assert.Null(LogFileEntry.Find(this.Scan(), path));
            Assert.Null(LogFileEntry.Find(this.Scan(), this.directory + Path.DirectorySeparatorChar));
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

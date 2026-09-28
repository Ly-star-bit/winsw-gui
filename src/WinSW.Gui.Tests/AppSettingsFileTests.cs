using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using WinSW.Gui.Services;
using Xunit;

namespace WinSW.Gui.Tests
{
    /// <summary>
    /// What the settings file promises: a save is all or nothing, and a file that cannot be
    /// used is kept rather than written over with the defaults. Each test works on a file of its
    /// own; none touches <see cref="AppSettings.Current"/>, which is this user's real settings.
    /// </summary>
    public sealed class AppSettingsFileTests : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "winsw-gui-" + Guid.NewGuid().ToString("n"));

        public AppSettingsFileTests()
        {
            Directory.CreateDirectory(this.directory);
        }

        private string SettingsPath => Path.Combine(this.directory, "settings.json");

        private string SetAsidePath => Path.Combine(this.directory, "settings.bad.json");

        public void Dispose() => Directory.Delete(this.directory, recursive: true);

        [Fact]
        public void WhatIsSavedIsWhatIsReadBack()
        {
            var settings = AppSettings.Load(this.SettingsPath);
            settings.Language = "zh-CN";
            settings.AutoRescanSeconds = 45;
            settings.MinimizeToTray = true;
            settings.InstallRoot = @"D:\Services";
            settings.ServiceGroups["Api"] = "web";
            settings.Save();

            var read = AppSettings.Load(this.SettingsPath);

            Assert.Equal("zh-CN", read.Language);
            Assert.Equal(45, read.AutoRescanSeconds);
            Assert.True(read.MinimizeToTray);
            Assert.Equal(@"D:\Services", read.InstallRoot);
            Assert.Equal("web", read.ServiceGroups["Api"]);
        }

        /// <summary>Service names are not case-sensitive, and the groups filed under them must not be either.</summary>
        [Fact]
        public void GroupsAreFoundWhateverTheCaseOfTheServiceName()
        {
            File.WriteAllText(this.SettingsPath, "{ \"serviceGroups\": { \"Api\": \"web\" } }");

            var read = AppSettings.Load(this.SettingsPath);

            Assert.Equal("web", read.ServiceGroups["API"]);
        }

        [Fact]
        public void NoFileMeansTheDefaultsAndNothingToReport()
        {
            var failures = new List<Exception>();

            var settings = AppSettings.Load(this.SettingsPath, failures.Add);

            Assert.Empty(failures);
            Assert.Null(settings.SetAsidePath);
            Assert.False(settings.IsFileUnreadable);
            Assert.Equal(30, settings.AutoRescanSeconds);
            Assert.False(File.Exists(this.SetAsidePath));
        }

        /// <summary>The write goes to a file beside the settings and is swapped in; nothing is left beside them.</summary>
        [Fact]
        public void ASaveReplacesTheFileAndLeavesNothingElse()
        {
            var settings = AppSettings.Load(this.SettingsPath);
            settings.Language = "ja";
            settings.Save();
            settings.Language = "en";
            settings.Save();

            Assert.Equal(new[] { "settings.json" }, Directory.GetFiles(this.directory).Select(Path.GetFileName));
            Assert.Equal("en", AppSettings.Load(this.SettingsPath).Language);
        }

        /// <summary>
        /// A save that fails is a preference lost for this session, which is acceptable; a
        /// temporary file left lying next to the settings each time is not.
        /// </summary>
        [Fact]
        public void ASaveThatFailsLeavesNoTemporaryFile()
        {
            // A folder where the file should be: the swap cannot put a file there.
            Directory.CreateDirectory(this.SettingsPath);
            var settings = AppSettings.Load(this.SettingsPath);
            settings.Language = "ja";

            settings.Save();

            Assert.Empty(Directory.GetFiles(this.directory));
        }

        [Fact]
        public void AFileThatDoesNotParseIsSetAsideRatherThanOverwritten()
        {
            const string Damaged = "{ \"language\": \"zh-CN\", \"alertWeb";
            File.WriteAllText(this.SettingsPath, Damaged);
            var failures = new List<Exception>();

            var settings = AppSettings.Load(this.SettingsPath, failures.Add);

            Assert.IsAssignableFrom<JsonException>(Assert.Single(failures));
            Assert.Null(settings.Language);
            Assert.Equal(this.SetAsidePath, settings.SetAsidePath);
            Assert.False(settings.IsFileUnreadable);
            Assert.Equal(Damaged, File.ReadAllText(this.SetAsidePath));
            Assert.False(File.Exists(this.SettingsPath));

            // The first change writes a fresh file; the damaged one stays where it was put.
            settings.Language = "ja";
            settings.Save();

            Assert.Equal("ja", AppSettings.Load(this.SettingsPath).Language);
            Assert.Equal(Damaged, File.ReadAllText(this.SetAsidePath));
        }

        /// <summary>Well-formed JSON with a value of the wrong kind is as unusable as a torn file.</summary>
        [Fact]
        public void AValueOfTheWrongKindCountsAsUnusable()
        {
            File.WriteAllText(this.SettingsPath, "{ \"autoRescanSeconds\": \"often\" }");

            var settings = AppSettings.Load(this.SettingsPath);

            Assert.Equal(this.SetAsidePath, settings.SetAsidePath);
            Assert.Equal(30, settings.AutoRescanSeconds);
        }

        /// <summary>The file kept is the one from this start, the one worth looking at.</summary>
        [Fact]
        public void AnOlderSetAsideFileIsReplaced()
        {
            File.WriteAllText(this.SetAsidePath, "older");
            File.WriteAllText(this.SettingsPath, "newer");

            var settings = AppSettings.Load(this.SettingsPath);

            Assert.Equal(this.SetAsidePath, settings.SetAsidePath);
            Assert.Equal("newer", File.ReadAllText(this.SetAsidePath));
        }

        /// <summary>
        /// A file another process holds open may be perfectly good. Whatever this start could
        /// or could not do with it, it must still be there afterwards: in place, or set aside.
        /// </summary>
        /// <remarks>
        /// Windows refuses the read and the move alike, so the file stays where it is and the
        /// save is skipped. Elsewhere the lock is advisory: the read is refused, but the move
        /// goes through, and the file is set aside instead. Either way nothing is lost.
        /// </remarks>
        [Fact]
        public void AFileThatCannotBeReadIsNeverWrittenOver()
        {
            const string Good = "{ \"language\": \"ja\" }";
            File.WriteAllText(this.SettingsPath, Good);

            AppSettings settings;
            using (new FileStream(this.SettingsPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                settings = AppSettings.Load(this.SettingsPath);
                settings.Language = "en";
                settings.Save();
            }

            bool keptInPlace = AppSettings.Load(this.SettingsPath).Language == "ja";
            bool keptAside = File.Exists(this.SetAsidePath) && File.ReadAllText(this.SetAsidePath) == Good;
            Assert.True(keptInPlace || keptAside, "the settings file should survive, in place or as settings.bad.json");

            if (settings.IsFileUnreadable)
            {
                Assert.Null(settings.SetAsidePath);
                Assert.Equal(Good, File.ReadAllText(this.SettingsPath));
            }
        }
    }
}

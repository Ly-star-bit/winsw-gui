using System;
using System.IO;
using System.Linq;
using WinSW.Gui.Services;
using Xunit;

namespace WinSW.Gui.Tests
{
    /// <summary>
    /// The copies a save sets aside: one per distinct version, the newest first, and never
    /// more than the configured number.
    /// </summary>
    public sealed class ConfigHistoryTests : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "winsw-gui-" + Guid.NewGuid().ToString("n"));

        public ConfigHistoryTests()
        {
            Directory.CreateDirectory(this.directory);
        }

        private string Root => Path.Combine(this.directory, "history");

        public void Dispose() => Directory.Delete(this.directory, recursive: true);

        [Fact]
        public void TheVersionOnDiskIsSetAsideBeforeItIsReplaced()
        {
            string config = this.Write("app.xml", "<service><id>one</id></service>", new DateTime(2026, 9, 21, 15, 0, 0));

            ConfigHistory.Preserve(config, this.Root);

            var version = Assert.Single(ConfigHistory.List(config, this.Root));
            Assert.Equal(new DateTime(2026, 9, 21, 15, 0, 0), version.SavedAt);
            Assert.Equal("<service><id>one</id></service>", File.ReadAllText(version.Path));
        }

        /// <summary>A save with nothing changed in between is not a new version.</summary>
        [Fact]
        public void TheSameContentIsKeptOnce()
        {
            string config = this.Write("app.xml", "<service />", new DateTime(2026, 9, 21, 15, 0, 0));

            ConfigHistory.Preserve(config, this.Root);
            File.SetLastWriteTime(config, new DateTime(2026, 9, 21, 16, 0, 0));
            ConfigHistory.Preserve(config, this.Root);

            Assert.Single(ConfigHistory.List(config, this.Root));
        }

        [Fact]
        public void OnlyTheNewestAreKeptAndTheyComeFirst()
        {
            string config = Path.Combine(this.directory, "app.xml");
            var start = new DateTime(2026, 9, 1, 8, 0, 0);

            for (int i = 0; i < ConfigHistory.Keep + 5; i++)
            {
                this.Write("app.xml", "<service><id>v" + i + "</id></service>", start.AddHours(i));
                ConfigHistory.Preserve(config, this.Root);
            }

            var versions = ConfigHistory.List(config, this.Root);
            Assert.Equal(ConfigHistory.Keep, versions.Count);
            Assert.Equal(start.AddHours(ConfigHistory.Keep + 4), versions[0].SavedAt);
            Assert.Equal(start.AddHours(5), versions[^1].SavedAt);
            Assert.True(versions.Zip(versions.Skip(1)).All(pair => pair.First.SavedAt > pair.Second.SavedAt));
        }

        /// <summary>Two services' configurations are often both called app.xml.</summary>
        [Fact]
        public void EachConfigurationHasAFolderOfItsOwn()
        {
            string a = Path.Combine(this.directory, "a", "app.xml");
            string b = Path.Combine(this.directory, "b", "app.xml");

            Assert.NotEqual(ConfigHistory.FolderFor(a, this.Root), ConfigHistory.FolderFor(b, this.Root));
            Assert.StartsWith("app-", Path.GetFileName(ConfigHistory.FolderFor(a, this.Root)), StringComparison.Ordinal);
            Assert.Equal(ConfigHistory.FolderFor(a, this.Root), ConfigHistory.FolderFor(a.ToUpperInvariant(), this.Root), StringComparer.OrdinalIgnoreCase);
        }

        [Fact]
        public void NothingToSetAsideIsNotAnError()
        {
            string missing = Path.Combine(this.directory, "missing.xml");

            ConfigHistory.Preserve(missing, this.Root);

            Assert.Empty(ConfigHistory.List(missing, this.Root));
        }

        private string Write(string name, string content, DateTime writtenAt)
        {
            string path = Path.Combine(this.directory, name);
            File.WriteAllText(path, content);
            File.SetLastWriteTime(path, writtenAt);
            return path;
        }
    }
}

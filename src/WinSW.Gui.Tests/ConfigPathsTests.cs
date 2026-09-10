using WinSW.Gui.Model;
using WinSW.Gui.Services;
using Xunit;

namespace WinSW.Gui.Tests
{
    /// <summary>
    /// Where a configuration's paths actually point. Pure string work — nothing here touches
    /// the disk, so a directory that does not exist is still resolved.
    /// </summary>
    public class ConfigPathsTests
    {
        [Fact]
        public void AConfigurationWithNoWorkingDirectoryRunsWhereItSits()
        {
            Assert.Equal(
                @"C:\services\demo",
                ConfigPaths.ResolveWorkingDirectory(new ServiceConfigModel(), @"C:\services\demo\demo.xml"));
        }

        [Fact]
        public void AWorkingDirectoryOfWhitespaceCountsAsUnset()
        {
            Assert.Equal(
                @"C:\services\demo",
                ConfigPaths.ResolveWorkingDirectory(
                    new ServiceConfigModel { WorkingDirectory = "   " },
                    @"C:\services\demo\demo.xml"));
        }

        /// <summary>
        /// The editor writes every path it is given as <c>%BASE%\…</c>, so this is the form
        /// most configurations the GUI has touched are in.
        /// </summary>
        [Fact]
        public void BaseIsTheDirectoryHoldingTheConfiguration()
        {
            Assert.Equal(
                @"C:\services\demo\app",
                ConfigPaths.ResolveWorkingDirectory(
                    new ServiceConfigModel { WorkingDirectory = @"%BASE%\app" },
                    @"C:\services\demo\demo.xml"));
        }

        [Fact]
        public void AnAbsoluteWorkingDirectoryIsTakenAsItIs()
        {
            Assert.Equal(
                @"D:\data\app",
                ConfigPaths.ResolveWorkingDirectory(
                    new ServiceConfigModel { WorkingDirectory = @"D:\data\app" },
                    @"C:\services\demo\demo.xml"));
        }

        /// <summary>
        /// A hand-written configuration may carry a bare relative path, which the wrapper
        /// would resolve against a service's current directory — <c>system32</c>. Combining
        /// it with the configuration's directory is a guess, and the only one that names a
        /// folder worth opening.
        /// </summary>
        [Fact]
        public void ARelativeWorkingDirectoryHangsOffTheConfiguration()
        {
            Assert.Equal(
                @"C:\services\demo\app",
                ConfigPaths.ResolveWorkingDirectory(
                    new ServiceConfigModel { WorkingDirectory = "app" },
                    @"C:\services\demo\demo.xml"));
        }
    }
}

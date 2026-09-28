using System.IO;
using WinSW.Gui.Services;
using Xunit;

namespace WinSW.Gui.Tests
{
    /// <summary>
    /// What a second launch tells the running console about itself, and how that console tells
    /// itself from another: a newer download double-clicked beside an old console in the tray
    /// used to wake the old one, which looked like an update that had worked.
    /// </summary>
    public class ConsoleLaunchTests
    {
        private static readonly string Folder = Path.Combine(Path.GetTempPath(), "winsw-launch");

        private static readonly string Executable = Path.Combine(Folder, "WinSW.Gui.exe");

        private static readonly string Download = Path.Combine(Folder, "Downloads", "WinSW.Gui-win-x64.exe");

        private static readonly string Config = Path.Combine(Folder, "svc", "my app.xml");

        [Fact]
        public void ALaunchWithAConfigurationReadsBackAsItWasSent()
        {
            var sent = new ConsoleLaunch("1.2.0", Download, Config);

            var read = ConsoleLaunch.FromRequest(sent.ToRequest());

            Assert.NotNull(read);
            Assert.Equal("1.2.0", read!.Version);
            Assert.Equal(Download, read.ExecutablePath);
            Assert.Equal(Config, read.ConfigPath);
        }

        /// <summary>A plain double-click says who it is as well; it used to only set an event.</summary>
        [Fact]
        public void ALaunchWithoutAConfigurationReadsBackWithNone()
        {
            var read = ConsoleLaunch.FromRequest(new ConsoleLaunch("1.2.0-ci.7", Executable, null).ToRequest());

            Assert.NotNull(read);
            Assert.Equal("1.2.0-ci.7", read!.Version);
            Assert.Null(read.ConfigPath);
        }

        /// <summary>The pipe also takes a bare path, from a launch that says nothing else.</summary>
        [Fact]
        public void ALaunchIsOneLineThatNoConfigurationPathCanBeTakenFor()
        {
            string request = new ConsoleLaunch("1.2.0", Executable, Config).ToRequest();

            Assert.DoesNotContain('\n', request);
            Assert.False(ConfigHandoff.IsConfigurationPath(request));
            Assert.Null(ConsoleLaunch.FromRequest(Config));
        }

        [Fact]
        public void TheWrongNumberOfFieldsIsNoLaunch()
        {
            Assert.Null(ConsoleLaunch.FromRequest("1.2.0\t" + Executable));
            Assert.Null(ConsoleLaunch.FromRequest("1.2.0\t" + Executable + "\t" + Config + "\textra"));
            Assert.Null(ConsoleLaunch.FromRequest(string.Empty));
        }

        [Fact]
        public void ALaunchMustSayWhichVersionItIs()
        {
            Assert.Null(ConsoleLaunch.FromRequest("\t" + Executable + "\t"));
            Assert.Null(ConsoleLaunch.FromRequest("1.2\u00070\t" + Executable + "\t"));
        }

        /// <summary>The running console may start the executable named, so it has to be one.</summary>
        [Fact]
        public void TheExecutableMustBeAFullPathToAnExe()
        {
            Assert.Null(ConsoleLaunch.FromRequest("1.2.0\tWinSW.Gui.exe\t"));
            Assert.Null(ConsoleLaunch.FromRequest("1.2.0\t" + Path.Combine(Folder, "WinSW.Gui.dll") + "\t"));
            Assert.Null(ConsoleLaunch.FromRequest("1.2.0\t\t"));
        }

        [Fact]
        public void TheConfigurationMustBeAFullPathToAnXmlFile()
        {
            Assert.Null(ConsoleLaunch.FromRequest("1.2.0\t" + Executable + "\tmy app.xml"));
            Assert.Null(ConsoleLaunch.FromRequest("1.2.0\t" + Executable + "\t" + Path.Combine(Folder, "notes.txt")));
        }

        [Fact]
        public void TheSameExecutableAtTheSameVersionIsTheSameConsole()
        {
            var launch = new ConsoleLaunch("1.2.0", Executable, null);

            Assert.True(launch.IsSameConsole("1.2.0", Executable));
        }

        /// <summary>Windows paths ignore case, and a path may come the long way round.</summary>
        [Fact]
        public void TheSameExecutableIsRecognisedHoweverItIsWritten()
        {
            var launch = new ConsoleLaunch("1.2.0", Executable.ToUpperInvariant(), null);
            string roundabout = Path.Combine(Folder, "svc", "..", "WinSW.Gui.exe");

            Assert.True(launch.IsSameConsole("1.2.0", Executable));
            Assert.True(new ConsoleLaunch("1.2.0", roundabout, null).IsSameConsole("1.2.0", Executable));
        }

        /// <summary>A download saved under another name, beside or anywhere else.</summary>
        [Fact]
        public void AnotherExecutableIsAnotherConsole()
        {
            Assert.False(new ConsoleLaunch("1.2.0", Download, null).IsSameConsole("1.2.0", Executable));
        }

        /// <summary>The same path, now holding another build: replaced by hand while the old one ran.</summary>
        [Fact]
        public void AnotherVersionAtTheSamePathIsAnotherConsole()
        {
            Assert.False(new ConsoleLaunch("1.3.0", Executable, null).IsSameConsole("1.2.0", Executable));
        }

        /// <summary>A console that cannot tell where it runs from does not offer to hand over to anything.</summary>
        [Fact]
        public void ARunningConsoleWithNoPathTakesEveryLaunchForItself()
        {
            Assert.True(new ConsoleLaunch("1.3.0", Download, null).IsSameConsole("1.2.0", null));
        }
    }
}

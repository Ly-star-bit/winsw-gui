using System;
using System.Collections.Generic;
using System.IO;
using WinSW.Gui.Services;
using Xunit;

namespace WinSW.Gui.Tests
{
    /// <summary>
    /// The console's command line. What "Restart as administrator" depends on: the copy it
    /// starts reads the configuration that was open and the tray watch it is to keep, and still
    /// knows it is replacing the copy that started it — where only the first argument used to be
    /// read, and a configuration there meant the replacing was never looked at.
    /// </summary>
    public class StartupArgumentsTests
    {
        /// <summary>Relative, and with a space in it: resolved the way the application resolves it.</summary>
        private const string Config = "my app.xml";

        private static readonly Func<string, bool> Everything = _ => true;

        private static readonly Func<string, bool> Nothing = _ => false;

        [Fact]
        public void NoArgumentsIsAPlainStart()
        {
            var arguments = StartupArguments.Parse(Array.Empty<string>(), Everything);

            Assert.Null(arguments.ConfigPath);
            Assert.False(arguments.Tray);
            Assert.False(arguments.KeepTray);
            Assert.False(arguments.Replacing);
        }

        /// <summary>"WinSW.Gui.exe myapp.xml", and the Explorer verb, which puts the file first.</summary>
        [Fact]
        public void AConfigurationAloneIsOpened()
        {
            var arguments = StartupArguments.Parse(new[] { Config }, Everything);

            Assert.Equal(Path.GetFullPath(Config), arguments.ConfigPath);
            Assert.False(arguments.Replacing);
            Assert.False(arguments.KeepTray);
        }

        /// <summary>The restart puts its switches first and the configuration after them.</summary>
        [Fact]
        public void ReplacingIsReadAlongsideAConfigurationAfterIt()
        {
            var arguments = StartupArguments.Parse(new[] { "--replace", "--keep-tray", Config }, Everything);

            Assert.True(arguments.Replacing);
            Assert.True(arguments.KeepTray);
            Assert.False(arguments.Tray);
            Assert.Equal(Path.GetFullPath(Config), arguments.ConfigPath);
        }

        [Fact]
        public void ReplacingIsReadAlongsideAConfigurationBeforeIt()
        {
            var arguments = StartupArguments.Parse(new[] { Config, "--replace" }, Everything);

            Assert.True(arguments.Replacing);
            Assert.Equal(Path.GetFullPath(Config), arguments.ConfigPath);
        }

        /// <summary>The start at sign-in; see <see cref="Autostart"/>.</summary>
        [Fact]
        public void TheTraySwitchStartsInTheTray()
        {
            var arguments = StartupArguments.Parse(new[] { Autostart.TrayArgument }, Everything);

            Assert.True(arguments.Tray);
            Assert.False(arguments.KeepTray);
            Assert.False(arguments.Replacing);
            Assert.Null(arguments.ConfigPath);
        }

        [Fact]
        public void SwitchesIgnoreCase()
        {
            var arguments = StartupArguments.Parse(new[] { "--REPLACE", "--Keep-Tray", "--Tray" }, Everything);

            Assert.True(arguments.Replacing);
            Assert.True(arguments.KeepTray);
            Assert.True(arguments.Tray);
        }

        /// <summary>
        /// An argument that merely ends in ".xml" is not a configuration to open unless there is
        /// such a file; the first one that exists is the one opened.
        /// </summary>
        [Fact]
        public void TheFirstConfigurationThatExistsIsOpened()
        {
            var existing = new HashSet<string> { "a.xml", "b.xml" };

            var arguments = StartupArguments.Parse(new[] { "missing.xml", "a.xml", "b.xml" }, existing.Contains);

            Assert.Equal(Path.GetFullPath("a.xml"), arguments.ConfigPath);
        }

        [Fact]
        public void AConfigurationThatDoesNotExistIsNotOpened()
        {
            var arguments = StartupArguments.Parse(new[] { "--replace", Config }, Nothing);

            Assert.Null(arguments.ConfigPath);
            Assert.True(arguments.Replacing);
        }

        [Fact]
        public void OnlyXmlFilesAreConfigurations()
        {
            Assert.Null(StartupArguments.Parse(new[] { "notes.txt" }, Everything).ConfigPath);
            Assert.Equal(Path.GetFullPath("SERVICE.XML"), StartupArguments.Parse(new[] { "SERVICE.XML" }, Everything).ConfigPath);
        }

        [Fact]
        public void ARestartWithNothingOpenOnlyReplaces()
        {
            Assert.Equal(new[] { StartupArguments.ReplaceArgument }, StartupArguments.ForElevatedRestart(null, keepTray: false));
        }

        /// <summary>
        /// A console watching from the tray hands the watch on, but not by starting its successor
        /// in the tray: the user who asked for the restart is looking for the window.
        /// </summary>
        [Fact]
        public void ATrayWatchIsKeptWithoutStartingInTheTray()
        {
            string[] restart = StartupArguments.ForElevatedRestart(null, keepTray: true);

            Assert.Contains(StartupArguments.KeepTrayArgument, restart);
            Assert.DoesNotContain(Autostart.TrayArgument, restart, StringComparer.OrdinalIgnoreCase);

            var arguments = StartupArguments.Parse(restart, Everything);
            Assert.True(arguments.KeepTray);
            Assert.False(arguments.Tray);
        }

        /// <summary>
        /// What the restarted copy reads is what the copy restarting it meant: the path, spaces
        /// and all, in one piece, and the replacing that makes it wait rather than defer.
        /// </summary>
        [Fact]
        public void ARestartReadsBackAsItWasWritten()
        {
            string path = Path.GetFullPath(Path.Combine("Program Files", "WinSW", Config));
            var existing = new HashSet<string> { path };

            string[] restart = StartupArguments.ForElevatedRestart(path, keepTray: true);
            var arguments = StartupArguments.Parse(restart, existing.Contains);

            Assert.Equal(path, arguments.ConfigPath);
            Assert.True(arguments.Replacing);
            Assert.True(arguments.KeepTray);
            Assert.False(arguments.Tray);
        }

        [Fact]
        public void ARestartWithoutATrayWatchDoesNotAskForOne()
        {
            string path = Path.GetFullPath(Config);

            string[] restart = StartupArguments.ForElevatedRestart(path, keepTray: false);

            Assert.Equal(new[] { StartupArguments.ReplaceArgument, path }, restart);
            Assert.False(StartupArguments.Parse(restart, Everything).KeepTray);
        }
    }
}

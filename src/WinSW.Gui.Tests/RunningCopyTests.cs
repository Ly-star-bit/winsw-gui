using WinSW.Gui.Services;
using Xunit;

namespace WinSW.Gui.Tests
{
    /// <summary>
    /// How a launch recognises a console from an earlier release by its executable, which is the
    /// only way to reach one: it cannot be told who the launch is.
    /// </summary>
    public class RunningCopyTests
    {
        /// <summary>
        /// Every console build carries the managed assembly's name in its version resource, the
        /// self-contained single files included; the wrapper carries its own.
        /// </summary>
        [Fact]
        public void AConsoleIsRecognisedByItsOriginalFileNameWhateverItIsCalledNow()
        {
            Assert.True(RunningCopy.IsConsole("WinSW.Gui.dll"));
            Assert.True(RunningCopy.IsConsole("winsw.gui.DLL"));
        }

        [Fact]
        public void TheWrapperAndEverythingElseAreNotConsoles()
        {
            Assert.False(RunningCopy.IsConsole("WinSW.dll"));
            Assert.False(RunningCopy.IsConsole("WinSW.exe"));
            Assert.False(RunningCopy.IsConsole("explorer.exe"));
            Assert.False(RunningCopy.IsConsole(string.Empty));
            Assert.False(RunningCopy.IsConsole(null));
        }

        /// <summary>Read the way the running console reads its own, so that the two compare.</summary>
        [Fact]
        public void AProductVersionIsReadWithoutItsCommit()
        {
            Assert.Equal("1.2.0", RunningCopy.VersionOf("1.2.0+4f3c2a1"));
            Assert.Equal("0.0.0-ci.41", RunningCopy.VersionOf("0.0.0-ci.41+4f3c2a1"));
            Assert.Equal("1.2.0", RunningCopy.VersionOf(" 1.2.0 "));
        }

        [Fact]
        public void AFileThatDeclaresNoVersionHasABlankOne()
        {
            Assert.Equal(string.Empty, RunningCopy.VersionOf(null));
            Assert.Equal(string.Empty, RunningCopy.VersionOf(string.Empty));
        }
    }
}

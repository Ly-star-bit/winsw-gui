using WinSW.Gui.Services;
using Xunit;

namespace WinSW.Gui.Tests
{
    /// <summary>
    /// What the "upgrade wrapper" question says about the .NET Framework the bundled wrapper needs:
    /// the machine's own version and the installer that puts it right, when it is known to be too
    /// old; nothing when it is known to be new enough.
    /// </summary>
    public class UpgradeFrameworkTests
    {
        /// <summary>Server 2012 R2 as it ships.</summary>
        private static readonly NetFrameworkInfo Net451 = new(378675);

        private static readonly NetFrameworkInfo Net48 = new(528040);

        [Fact]
        public void AMachineTooOldIsToldWhatItHasAndWhatToInstall()
        {
            Assert.Equal(
                "M.Dash.UpgradeFrameworkTooOld(4.5.1,ndp48-x86-x64-allos-enu.exe)",
                UpgradeFramework.Note("WinSW-x64.exe", Net451, Echo));
        }

        /// <summary>Upstream's net461 build is no help to the net462 one on a machine that has 4.5.1.</summary>
        [Fact]
        public void AFrameworkBuildIsNoSafeguard()
        {
            Assert.Equal(
                "M.Dash.UpgradeFrameworkTooOld(4.5.1,ndp48-x86-x64-allos-enu.exe)",
                UpgradeFramework.Note(UpgradeFramework.FrameworkAsset, Net451, Echo));
            Assert.Equal(
                "M.Dash.UpgradeFrameworkTooOld(4.5.1,ndp48-x86-x64-allos-enu.exe)",
                UpgradeFramework.Note(null, Net451, Echo));
        }

        [Fact]
        public void AMachineThatRunsItIsToldNothing()
        {
            Assert.Null(UpgradeFramework.Note("WinSW-x64.exe", Net48, Echo));
            Assert.Null(UpgradeFramework.Note(UpgradeFramework.FrameworkAsset, Net48, Echo));
        }

        /// <summary>Nothing known either way: the general note, for the only kind of service that gains the dependency.</summary>
        [Fact]
        public void AnUnknownMachineKeepsTheGeneralNoteForASelfContainedBuild()
        {
            Assert.Equal("M.Dash.UpgradeFrameworkBuild()", UpgradeFramework.Note("WinSW-x64.exe", NetFrameworkInfo.Unknown, Echo));
            Assert.Null(UpgradeFramework.Note(UpgradeFramework.FrameworkAsset, NetFrameworkInfo.Unknown, Echo));
            Assert.Null(UpgradeFramework.Note(null, NetFrameworkInfo.Unknown, Echo));
        }

        [Fact]
        public void BothNotesAreInEveryLanguage()
        {
            foreach (string language in new[] { "en", "zh-CN", "zh-TW", "ja" })
            {
                var values = StringDictionaries.ValuesOf(language);
                Assert.Contains("{0}", values["M.Dash.UpgradeFrameworkTooOld"]);
                Assert.Contains("{1}", values["M.Dash.UpgradeFrameworkTooOld"]);
                Assert.True(values.ContainsKey("M.Dash.UpgradeFrameworkBuild"), language);
            }
        }

        private static string Echo(string key, object?[] args) => key + "(" + string.Join(",", args) + ")";
    }
}

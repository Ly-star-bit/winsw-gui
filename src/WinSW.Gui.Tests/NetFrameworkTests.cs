using System;
using WinSW.Gui.Services;
using WinSW.Gui.ViewModels;
using Xunit;

namespace WinSW.Gui.Tests
{
    /// <summary>
    /// The bundled wrapper is the .NET Framework 4.6.2 build and carries no <c>.config</c>, so
    /// on a machine with less — a stock Windows Server 2012 R2 has 4.5.1 — it fails at install
    /// or at the first start. The wizard reads the framework's <c>Release</c> and, below 4.6.2,
    /// starts on the self-contained download instead.
    /// </summary>
    [Collection("install root")]
    public class NetFrameworkTests
    {
        /// <summary>A few versions around the line, each by a <c>Release</c> Microsoft documents for it.</summary>
        [Theory]
        [InlineData(378389, true)] // 4.5
        [InlineData(378675, true)] // 4.5.1 on Windows 8.1 and Server 2012 R2
        [InlineData(378758, true)] // 4.5.1 elsewhere
        [InlineData(379893, true)] // 4.5.2
        [InlineData(393297, true)] // 4.6
        [InlineData(394254, true)] // 4.6.1
        [InlineData(394271, true)] // 4.6.1
        [InlineData(394802, false)] // 4.6.2 on Windows 10 1607
        [InlineData(394806, false)] // 4.6.2 elsewhere
        [InlineData(460805, false)] // 4.7
        [InlineData(461814, false)] // 4.7.2
        [InlineData(528049, false)] // 4.8
        [InlineData(533325, false)] // 4.8.1
        public void Below462TheWrapperCannotRun(int release, bool tooOld)
        {
            var framework = NetFramework.FromRegistry(true, release);

            Assert.True(framework.IsKnown);
            Assert.Equal(tooOld, framework.TooOldForWrapper);
        }

        /// <summary>
        /// A key or value that is not there is 4.0 or no 4.x at all: too old, and known to be.
        /// Setup has only ever written a DWORD, so anything else counts as missing.
        /// </summary>
        [Theory]
        [InlineData(false, null)]
        [InlineData(true, null)]
        [InlineData(true, "394802")]
        [InlineData(true, 0)]
        public void AMissingReleaseIsTooOld(bool keyFound, object? release)
        {
            var framework = NetFramework.FromRegistry(keyFound, release);

            Assert.True(framework.IsKnown);
            Assert.True(framework.TooOldForWrapper);
            Assert.Equal("< 4.5", framework.Version);
        }

        /// <summary>Nothing read is nothing said: the check must never be what stops an install.</summary>
        [Fact]
        public void AnUnreadableMachineIsNotTooOld()
        {
            var framework = NetFrameworkInfo.Unknown;

            Assert.False(framework.IsKnown);
            Assert.False(framework.TooOldForWrapper);
            Assert.Equal(string.Empty, framework.Version);
        }

        [Theory]
        [InlineData(378675, "4.5.1")]
        [InlineData(378758, "4.5.1")]
        [InlineData(379893, "4.5.2")]
        [InlineData(394254, "4.6.1")]
        [InlineData(394806, "4.6.2")]
        [InlineData(461308, "4.7.1")]
        [InlineData(528449, "4.8")]
        [InlineData(533320, "4.8.1")]
        [InlineData(1, "< 4.5")]
        public void TheReleaseIsNamedByItsVersion(int release, string version)
        {
            Assert.Equal(version, NetFramework.VersionOf(release));
        }

        /// <summary>On Windows the value is read; anywhere else there is no registry, and that is no error.</summary>
        [Fact]
        public void ReadingTheRegistryNeverThrows()
        {
            var framework = NetFramework.Read();

            Assert.Equal(OperatingSystem.IsWindows(), framework.IsKnown);
        }

        [Theory]
        [InlineData(true, 528040, true)]
        [InlineData(true, 394802, true)]
        [InlineData(true, 378675, false)]
        [InlineData(true, null, true)]
        [InlineData(false, 528040, false)]
        [InlineData(false, null, false)]
        public void TheBundledWrapperIsTheDefaultOnlyWhereItRuns(bool bundled, int? release, bool expected)
        {
            Assert.Equal(expected, WizardViewModel.PrefersBundledWrapper(bundled, new NetFrameworkInfo(release)));
        }

        /// <summary>
        /// On 2012 R2's 4.5.1 the wizard starts on the download and says why, and starting over
        /// does not put the bundled wrapper back.
        /// </summary>
        [Fact]
        public void AnOldFrameworkStartsTheWizardOnTheDownload()
        {
            var wizard = new WizardViewModel(new NetFrameworkInfo(378675), () => ServiceNames.None);

            Assert.True(wizard.FrameworkTooOld);
            Assert.False(wizard.UseBundledWrapper);

            wizard.UseBundledWrapper = true;
            wizard.ResetCommand.Execute(null);

            Assert.False(wizard.UseBundledWrapper);
            Assert.True(wizard.FrameworkTooOld);
        }

        [Fact]
        public void ACurrentFrameworkKeepsTheBundledDefault()
        {
            var wizard = new WizardViewModel(new NetFrameworkInfo(528040), () => ServiceNames.None);

            Assert.False(wizard.FrameworkTooOld);
            Assert.Equal(BundledWrapper.IsAvailable, wizard.UseBundledWrapper);
            Assert.Equal(string.Empty, wizard.FrameworkHint);
        }
    }
}

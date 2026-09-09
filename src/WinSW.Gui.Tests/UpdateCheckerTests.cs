using System.Collections.Generic;
using WinSW.Gui.Services;
using Xunit;

namespace WinSW.Gui.Tests
{
    /// <summary>
    /// The version arithmetic behind the "an update is available" badge. Nothing here goes to
    /// the network: what a release contains is GitHub's to say, but what counts as newer than
    /// what is this code's.
    /// </summary>
    public class UpdateCheckerTests
    {
        [Theory]
        [InlineData("v3.0.0", "3.0.0")]
        [InlineData("gui-v0.4.0", "0.4.0")]
        [InlineData("gui-v0.11.0", "0.11.0")]
        [InlineData("v3.0.0-alpha.11", "3.0.0-alpha.11")]
        [InlineData("v2.9.0", "2.9.0")]
        public void TheVersionIsTakenOutOfTheTag(string tag, string expected)
        {
            var release = new ReleaseInfo(tag, "https://example.com", new Dictionary<string, string>());

            Assert.Equal(expected, release.Version);
        }

        /// <summary>
        /// A tag whose suffix contains a 'v' of its own. No release has carried one yet, and
        /// cutting at the last 'v' would have turned this into "iew".
        /// </summary>
        [Fact]
        public void ASuffixContainingAVDoesNotSwallowTheVersion()
        {
            var release = new ReleaseInfo("v3.1.0-preview", "https://example.com", new Dictionary<string, string>());

            Assert.Equal("3.1.0-preview", release.Version);
        }

        [Theory]
        [InlineData("3.0.1", "3.0.0")]
        [InlineData("3.1.0", "3.0.9")]
        [InlineData("4.0.0", "3.99.99")]
        [InlineData("0.11.0", "0.9.0")]
        public void AHigherVersionIsNewer(string candidate, string current)
        {
            Assert.True(UpdateChecker.IsNewer(candidate, current));
        }

        [Theory]
        [InlineData("3.0.0", "3.0.1")]
        [InlineData("0.9.0", "0.11.0")]
        [InlineData("3.0.0", "3.0.0")]
        public void ALowerOrEqualVersionIsNot(string candidate, string current)
        {
            Assert.False(UpdateChecker.IsNewer(candidate, current));
        }

        /// <summary>
        /// The wrapper stamps a four-part FileVersion; a release tag carries three. Left
        /// unpadded, System.Version reads the component that was not written as -1, and
        /// "3.0.0" then compares as older than the "3.0.0.0" on disk — an update offered
        /// forever, against a file that already is that version.
        /// </summary>
        [Theory]
        [InlineData("3.0.0", "3.0.0.0")]
        [InlineData("3.0.0.0", "3.0.0")]
        [InlineData("3", "3.0.0.0")]
        [InlineData("3.0", "3.0.0")]
        public void AVersionWrittenWithFewerPartsIsNotNewerThanTheSameVersion(string candidate, string current)
        {
            Assert.False(UpdateChecker.IsNewer(candidate, current));
        }

        [Fact]
        public void ABuildNumberStillCounts()
        {
            Assert.True(UpdateChecker.IsNewer("3.0.0.97", "3.0.0.96"));
            Assert.False(UpdateChecker.IsNewer("3.0.0.96", "3.0.0.97"));
        }

        /// <summary>A pre-release suffix is dropped, not parsed: it is not a version ordering.</summary>
        [Fact]
        public void APreReleaseSuffixIsIgnoredRatherThanMisread()
        {
            Assert.False(UpdateChecker.IsNewer("3.0.0-alpha.11", "3.0.0"));
            Assert.True(UpdateChecker.IsNewer("3.0.1-alpha.1", "3.0.0"));
        }

        [Theory]
        [InlineData("", "3.0.0")]
        [InlineData("not-a-version", "3.0.0")]
        [InlineData("3.0.0", "")]
        public void NonsenseIsNeverNewer(string candidate, string current)
        {
            Assert.False(UpdateChecker.IsNewer(candidate, current));
        }

        [Fact]
        public void TheGuiKnowsItsOwnVersion()
        {
            Assert.False(string.IsNullOrWhiteSpace(UpdateChecker.CurrentGuiVersion));
            Assert.DoesNotContain('+', UpdateChecker.CurrentGuiVersion);
        }
    }
}

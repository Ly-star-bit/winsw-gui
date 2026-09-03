using System;
using WinSW.Gui.Services;
using Xunit;

namespace WinSW.Gui.Tests
{
    public class BundledWrapperTests
    {
        /// <summary>
        /// The wrapper is embedded by the workflow, which builds it just before the console.
        /// A local build without it is fine — the wizard falls back to downloading — but a CI
        /// build that lost it would ship a wizard quietly missing its main path.
        /// </summary>
        [Fact]
        public void BundledWrapperUnpacksToARealWrapper()
        {
            if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase))
            {
                Assert.True(BundledWrapper.IsAvailable, "the workflow builds the wrapper before the console, so it must be embedded here");
            }
            else if (!BundledWrapper.IsAvailable)
            {
                return;
            }

            string? path = BundledWrapper.Extract();

            Assert.NotNull(path);
            Assert.True(ServiceDiscovery.IsWrapperExecutable(path!), path + " does not identify itself as WinSW");
            Assert.False(string.IsNullOrWhiteSpace(BundledWrapper.Version));
        }
    }
}

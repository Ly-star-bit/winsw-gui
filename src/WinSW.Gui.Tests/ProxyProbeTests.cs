using System;
using System.Threading.Tasks;
using WinSW.Gui.Services;
using Xunit;

namespace WinSW.Gui.Tests
{
    /// <summary>
    /// Only the answers the probe reaches before it opens a socket. What happens after that
    /// is the network's to say, and a test that asked it would be reporting on the machine it
    /// runs on rather than on this code.
    /// </summary>
    public class ProxyProbeTests
    {
        [Fact]
        public async Task ThereIsNothingToTestWithoutAnAddress()
        {
            var result = await ProxyProbe.RunAsync(null, null);

            Assert.Equal(ProxyProbeOutcome.NoAddress, result.Outcome);
        }

        /// <summary>
        /// The probe parses with the wrapper's own type, so the button cannot report success
        /// for an address the service would then refuse to start on.
        /// </summary>
        [Fact]
        public async Task AnAddressTheWrapperWouldRefuseIsRefusedHereToo()
        {
            var result = await ProxyProbe.RunAsync("proxy.example.com:8080", null);

            Assert.Equal(ProxyProbeOutcome.BadAddress, result.Outcome);
            Assert.Contains("scheme", result.Detail, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task TheTargetHasToBeSomethingHttpCanFetch()
        {
            var result = await ProxyProbe.RunAsync("http://127.0.0.1:1", "ftp://files.example.com");

            Assert.Equal(ProxyProbeOutcome.BadTarget, result.Outcome);

            // Named even though the probe stopped early: it is what the button would have tried.
            Assert.Equal("127.0.0.1:1", result.Endpoint);
        }

        /// <summary>
        /// An https target is the point: reaching it means the proxy accepted a CONNECT and
        /// carried a tunnel, which is what everything the wrapper proxies actually needs.
        /// </summary>
        [Fact]
        public void TheDefaultTargetExercisesTheTunnel() =>
            Assert.StartsWith("https://", ProxyProbe.DefaultTarget, StringComparison.Ordinal);
    }
}

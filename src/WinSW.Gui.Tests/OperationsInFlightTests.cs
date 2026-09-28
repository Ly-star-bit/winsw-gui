using System;
using System.Linq;
using WinSW.Gui.Model;
using WinSW.Gui.Services;
using Xunit;

namespace WinSW.Gui.Tests
{
    /// <summary>
    /// Which services a command from the dashboard works on, and holding them while it runs, so
    /// that a second command on the same service is not offered meanwhile.
    /// </summary>
    public class OperationsInFlightTests
    {
        private const string SharedWrapper = @"C:\ProgramData\WinSW\bin\WinSW.exe";

        [Theory]
        [InlineData("start")]
        [InlineData("refresh")]
        [InlineData("dev kill")]
        [InlineData("uninstall")]
        public void ACommandOnOneServiceHoldsThatServiceAlone(string command)
        {
            var api = Entry("api", SharedWrapper, "worker");
            var other = Entry("other", SharedWrapper);

            var names = OperationsInFlight.NamesFor(command, api, new[] { api, other });

            Assert.Equal(new[] { "api" }, names);
        }

        /// <summary>The wrapper's stop stops every dependent first, and a restart starts them again.</summary>
        [Theory]
        [InlineData("stop")]
        [InlineData("restart")]
        public void AStopOrRestartHoldsTheServicesThatDependOnIt(string command)
        {
            // "Spooler" is not a WinSW service and not on the list; it is held all the same.
            var api = Entry("api", SharedWrapper, "worker", "Spooler");
            var worker = Entry("worker", SharedWrapper);

            var names = OperationsInFlight.NamesFor(command, api, new[] { api, worker });

            Assert.Equal(new[] { "Spooler", "api", "worker" }, names.OrderBy(n => n, StringComparer.Ordinal));
        }

        /// <summary>Under the install root one wrapper file serves every service, and the upgrade stops them all around the copy.</summary>
        [Fact]
        public void AnUpgradeHoldsEveryServiceSharingTheWrapperFile()
        {
            var api = Entry("api", SharedWrapper);
            var worker = Entry("worker", SharedWrapper.ToUpperInvariant());
            var own = Entry("own", @"D:\apps\own\own.exe");

            var names = OperationsInFlight.NamesFor("upgrade", api, new[] { api, worker, own });

            Assert.Equal(new[] { "api", "worker" }, names.OrderBy(n => n, StringComparer.Ordinal));
        }

        [Fact]
        public void AHeldServiceIsFoundWhateverTheCase()
        {
            var inFlight = new OperationsInFlight();

            using (inFlight.Begin(new[] { "MyApi" }))
            {
                Assert.True(inFlight.Contains("myapi"));
                Assert.True(inFlight.ContainsAny(new[] { "other", "MYAPI" }));
                Assert.False(inFlight.Contains("other"));
            }

            Assert.False(inFlight.Contains("MyApi"));
            Assert.Equal(0, inFlight.Count);
        }

        /// <summary>
        /// A service held by two operations stays held until both have finished: the first to end
        /// must not free it while the other is still working on it.
        /// </summary>
        [Fact]
        public void AServiceHeldTwiceIsReleasedWhenTheLastHolderEnds()
        {
            var inFlight = new OperationsInFlight();

            var stopApi = inFlight.Begin(new[] { "api", "worker" });
            var restartWorker = inFlight.Begin(new[] { "worker", "reporter" });

            stopApi.Dispose();
            Assert.False(inFlight.Contains("api"));
            Assert.True(inFlight.Contains("worker"));
            Assert.True(inFlight.Contains("reporter"));

            restartWorker.Dispose();
            Assert.Equal(0, inFlight.Count);
        }

        /// <summary>A batch stop of a service and of its dependent names the dependent twice; it is held once.</summary>
        [Fact]
        public void ANameGivenTwiceInOneOperationIsReleasedWithIt()
        {
            var inFlight = new OperationsInFlight();

            var batch = inFlight.Begin(new[] { "api", "worker", "Worker" });
            Assert.Equal(2, inFlight.Count);

            batch.Dispose();
            Assert.Equal(0, inFlight.Count);
        }

        [Fact]
        public void EndingAnOperationTwiceReleasesItOnce()
        {
            var inFlight = new OperationsInFlight();
            var first = inFlight.Begin(new[] { "api" });
            var second = inFlight.Begin(new[] { "api" });

            first.Dispose();
            first.Dispose();

            Assert.True(inFlight.Contains("api"));
            second.Dispose();
            Assert.False(inFlight.Contains("api"));
        }

        /// <summary>The dashboard re-asks its commands on this event, both when an operation begins and when it ends.</summary>
        [Fact]
        public void BeginningAndEndingAreAnnounced()
        {
            var inFlight = new OperationsInFlight();
            int raised = 0;
            bool heldWhenBegun = false;
            inFlight.Changed += () =>
            {
                raised++;
                if (raised == 1)
                {
                    heldWhenBegun = inFlight.Contains("api");
                }
            };

            var held = inFlight.Begin(new[] { "api" });
            Assert.Equal(1, raised);
            Assert.True(heldWhenBegun);

            held.Dispose();
            Assert.Equal(2, raised);
            Assert.False(inFlight.Contains("api"));
        }

        private static ServiceEntry Entry(string name, string wrapper, params string[] dependedBy) =>
            new(name, name, wrapper, @"C:\ProgramData\WinSW\" + name + @"\" + name + ".xml") { DependedBy = dependedBy };
    }
}

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.ServiceProcess;
using WinSW.Gui.Model;
using WinSW.Gui.Services;
using Xunit;

namespace WinSW.Gui.Tests
{
    /// <summary>
    /// The ports half of a status poll, over a machine built by hand: when the port table is read,
    /// what the samples carry back, and what the entry makes of them on the UI thread.
    /// </summary>
    public sealed class PortWatchTests : IDisposable
    {
        private const int Console = 900;

        private static readonly DateTime T0 = new(2026, 9, 24, 8, 0, 0);
        private static readonly ISet<string> Wrappers = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "WinSW.exe" };

        /// <summary>
        /// The machine: the api service's wrapper (100) runs python (200), which listens on 8000; a
        /// copy of python started by hand from a prompt (4312, under 3000) listens on 9000.
        /// </summary>
        private static readonly ProcessSnapshot Machine = new(new[]
        {
            P(100, 1, "WinSW.exe", 0),
            P(200, 100, "python.exe", 0),
            P(3000, 1, "cmd.exe", 0),
            P(4312, 3000, "python.exe", 1),
        });

        private static readonly PortTable Listeners = new(new[]
        {
            new ListeningPort("0.0.0.0", 8000, 200),
            new ListeningPort("::", 8000, 200),
            new ListeningPort("127.0.0.1", 50123, 200),
            new ListeningPort("0.0.0.0", 9000, 4312),
        });

        /// <summary>
        /// The same machine in a restart loop: the api service's python cannot bind, and listens on
        /// nothing; the copy started by hand holds 9000.
        /// </summary>
        private static readonly PortTable LoopListeners = new(new[]
        {
            new ListeningPort("0.0.0.0", 9000, 4312),
        });

        private readonly string directory = Path.Combine(Path.GetTempPath(), "winsw-gui-" + Guid.NewGuid().ToString("n"));
        private readonly RememberedRuns runs;

        private PortTable table = Listeners;
        private int reads;

        public PortWatchTests() => this.runs = new RememberedRuns(Path.Combine(this.directory, "remembered-runs.json"));

        public void Dispose()
        {
            if (Directory.Exists(this.directory))
            {
                Directory.Delete(this.directory, recursive: true);
            }
        }

        // When the table is read -------------------------------------------------------------

        /// <summary>The service on screen shows what it listens on: its whole tree's ports, the program's included.</summary>
        [Fact]
        public void TheSelectedServiceHasItsTreesPortsRead()
        {
            var samples = new[] { Running() };
            this.runs.NotePorts("api", Wrapper, new[] { 8000 }, T0.AddMinutes(1));

            Assert.True(this.Look(samples, new PolledService("api", Selected: true, HolderShown: false)));

            Assert.Equal(1, this.reads);
            Assert.Equal("0.0.0.0:8000, [::]:8000, 127.0.0.1:50123", PortTable.Describe(samples[0].Listening));
        }

        /// <summary>A console watching services it already knows the ports of, none of them on screen, reads nothing.</summary>
        [Fact]
        public void NothingIsReadWhenNothingNeedsIt()
        {
            var samples = new[] { Running(), Stopped() };
            this.runs.NotePorts("api", Wrapper, new[] { 8000 }, T0.AddMinutes(1));

            Assert.False(this.Look(samples, new PolledService("api", false, false), new PolledService("web", false, false)));

            Assert.Equal(0, this.reads);
            Assert.True(samples[0].Listening.IsDefault);
        }

        /// <summary>A run not looked at yet is read for, and what it listens on is remembered for when it cannot start.</summary>
        [Fact]
        public void ARunNotLookedAtYetIsReadAndRemembered()
        {
            var samples = new[] { Running() };

            this.Look(samples, new PolledService("api", false, false));

            Assert.Equal(1, this.reads);
            Assert.Equal(new[] { 8000 }, this.runs.PortsOf("api"));

            this.Look(new[] { Running() }, new PolledService("api", false, false));
            Assert.Equal(1, this.reads);
        }

        /// <summary>However many services need it, the table is read once a poll.</summary>
        [Fact]
        public void TheTableIsReadOnceForEveryService()
        {
            this.runs.NotePorts("web", new ProcessMark(5000, T0, string.Empty), new[] { 9000 }, T0);
            var samples = new[] { Running(), Stopped() };

            this.Look(samples, new PolledService("api", true, false), new PolledService("web", false, false));

            Assert.Equal(1, this.reads);
            Assert.False(samples[0].Listening.IsDefault);
            Assert.Equal(9000, samples[1].Stray?.Port);
        }

        /// <summary>A stopped service with no port remembered has nothing to look for.</summary>
        [Fact]
        public void AStoppedServiceWithNoPortRememberedReadsNothing()
        {
            Assert.False(this.Look(new[] { Stopped() }, new PolledService("web", true, false)));
            Assert.Equal(0, this.reads);
        }

        // The holder -------------------------------------------------------------------------

        /// <summary>
        /// The port names what the next start fails on, whoever holds it: it takes the banner's place
        /// from a leftover found by its program.
        /// </summary>
        [Fact]
        public void AStoppedServicesPortHolderIsNamed()
        {
            this.runs.NotePorts("web", new ProcessMark(5000, T0, string.Empty), new[] { 9000 }, T0);
            var leftover = new StrayFinding(new ProcessMark(200, T0, "python.exe"), null);
            var samples = new[] { Stopped() with { Stray = leftover } };

            this.Look(samples, new PolledService("web", false, false));

            Assert.Equal(4312, samples[0].Stray?.Process.ProcessId);
            Assert.Equal("cmd.exe", samples[0].Stray?.Parent?.Name);
            Assert.Equal(9000, samples[0].Stray?.Port);
        }

        /// <summary>A remembered port nobody holds leaves what was found by the program alone.</summary>
        [Fact]
        public void AFreePortLeavesTheLeftoverFound()
        {
            this.runs.NotePorts("web", new ProcessMark(5000, T0, string.Empty), new[] { 7000 }, T0);
            var leftover = new StrayFinding(new ProcessMark(200, T0, "python.exe"), null);
            var samples = new[] { Stopped() with { Stray = leftover } };

            this.Look(samples, new PolledService("web", false, false));

            Assert.Equal(1, this.reads);
            Assert.Equal(leftover, samples[0].Stray);
        }

        /// <summary>
        /// Between two failed starts of a restart loop the service runs for a moment; a holder already
        /// on its banner is looked for then too, so the banner does not blink at every restart.
        /// </summary>
        [Fact]
        public void AHolderOnTheBannerIsKeptWhileTheServiceTriesAgain()
        {
            this.table = LoopListeners;
            this.runs.NotePorts("api", new ProcessMark(99, T0.AddMinutes(-10), string.Empty), new[] { 9000 }, T0);
            var samples = new[] { Running() };

            this.Look(samples, new PolledService("api", false, HolderShown: true));

            Assert.Equal(4312, samples[0].Stray?.Process.ProcessId);
        }

        /// <summary>Running with nothing on its banner, a service is not second-guessed over a port it no longer uses.</summary>
        [Fact]
        public void ARunningServiceIsNotLookedAtForAHolder()
        {
            this.table = LoopListeners;
            this.runs.NotePorts("api", new ProcessMark(99, T0.AddMinutes(-10), string.Empty), new[] { 9000 }, T0);
            var samples = new[] { Running() };

            this.Look(samples, new PolledService("api", true, false));

            Assert.Equal(1, this.reads);
            Assert.Null(samples[0].Stray);
        }

        /// <summary>Once the service holds its port itself, the holder is its own and the banner has nothing to say.</summary>
        [Fact]
        public void AServiceHoldingItsOwnPortHasNoHolder()
        {
            this.runs.NotePorts("api", Wrapper, new[] { 8000 }, T0);
            var samples = new[] { Running() };

            this.Look(samples, new PolledService("api", false, HolderShown: true));

            Assert.Null(samples[0].Stray);
        }

        /// <summary>A table that could not be read changes nothing.</summary>
        [Fact]
        public void AnUnreadableTableChangesNothing()
        {
            var samples = new[] { Running() };

            Assert.True(PortWatch.Look(samples, new[] { new PolledService("api", true, false) }, Machine, () => null, this.runs, Wrappers, Console, T0.AddMinutes(1)));

            Assert.True(samples[0].Listening.IsDefault);
            Assert.Empty(this.runs.PortsOf("api"));
        }

        // On the entry -----------------------------------------------------------------------

        /// <summary>Ports not read at a reading stay with the process they were read for, as its counters do.</summary>
        [Fact]
        public void PortsNotReadStayWithTheProcess()
        {
            var entry = new ServiceEntry("api", "API", "C:/bin/WinSW.exe", "C:/svc/api.xml");
            var ports = ImmutableArray.Create(new ListeningPort("0.0.0.0", 8000, 200));

            ServiceDiscovery.Apply(entry, Running() with { Listening = ports });
            Assert.Equal(ports, entry.ListeningPorts);

            ServiceDiscovery.Apply(entry, Running());
            Assert.Equal(ports, entry.ListeningPorts);

            ServiceDiscovery.Apply(entry, Running() with { ProcessId = 101 });
            Assert.Empty(entry.ListeningPorts);
        }

        [Fact]
        public void AStoppedServiceListensOnNothing()
        {
            var entry = new ServiceEntry("api", "API", "C:/bin/WinSW.exe", "C:/svc/api.xml");
            ServiceDiscovery.Apply(entry, Running() with { Listening = ImmutableArray.Create(new ListeningPort("0.0.0.0", 8000, 200)) });

            ServiceDiscovery.Apply(entry, Stopped());

            Assert.Empty(entry.ListeningPorts);
            Assert.Equal(string.Empty, entry.ListeningText);
        }

        private static ProcessMark Wrapper => new(100, T0, string.Empty);

        private static ServiceSample Running() => new()
        {
            Queried = true,
            Status = ServiceControllerStatus.Running,
            ProcessId = 100,
            HasProcess = true,
            StartedAt = T0,
            Descendants = ImmutableArray.Create(new ProcessMark(200, T0, "python.exe")),
        };

        private static ServiceSample Stopped() => new()
        {
            Queried = true,
            Status = ServiceControllerStatus.Stopped,
            LastExitCode = 1,
        };

        private static ProcessRecord P(int id, int parent, string name, int minutes) =>
            new(id, parent, name, T0.AddMinutes(minutes), TimeSpan.Zero, 0, 0);

        private bool Look(ServiceSample[] samples, params PolledService[] services) =>
            PortWatch.Look(samples, services, Machine, this.Read, this.runs, Wrappers, Console, T0.AddMinutes(1));

        private PortTable Read()
        {
            this.reads++;
            return this.table;
        }
    }
}

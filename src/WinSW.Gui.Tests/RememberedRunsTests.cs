using System;
using System.IO;
using System.Linq;
using WinSW.Gui.Services;
using Xunit;

namespace WinSW.Gui.Tests
{
    /// <summary>
    /// The ports and processes remembered for each service from one run to the next, and from one
    /// start of the console to the next, in a file of its own under a temporary directory.
    /// </summary>
    public sealed class RememberedRunsTests : IDisposable
    {
        private static readonly DateTime T0 = new(2026, 9, 24, 8, 0, 0);

        /// <summary>One run's wrapper, and the next run's, which was given the same ID.</summary>
        private static readonly ProcessMark FirstRun = new(3000, T0, "WinSW.exe");
        private static readonly ProcessMark NextRun = new(3000, T0.AddMinutes(5), "WinSW.exe");

        private readonly string directory = Path.Combine(Path.GetTempPath(), "winsw-gui-" + Guid.NewGuid().ToString("n"));

        private string FilePath => Path.Combine(this.directory, "remembered-runs.json");

        public void Dispose()
        {
            if (Directory.Exists(this.directory))
            {
                Directory.Delete(this.directory, recursive: true);
            }
        }

        [Fact]
        public void NothingIsRememberedForAServiceNeverSeen()
        {
            Assert.Empty(new RememberedRuns(this.FilePath).PortsOf("api"));
            Assert.Empty(new RememberedRuns(this.FilePath).ProcessesOf("api"));
        }

        [Fact]
        public void PortsAreRememberedAcrossARestartOfTheConsole()
        {
            var runs = new RememberedRuns(this.FilePath);

            Assert.True(runs.NotePorts("api", FirstRun, new[] { 9000, 8000, 8000 }, T0));

            Assert.Equal(new[] { 8000, 9000 }, new RememberedRuns(this.FilePath).PortsOf("API"));
        }

        /// <summary>A program opening its listeners one at a time, as it starts, has all of them remembered.</summary>
        [Fact]
        public void PortsSeenLaterInTheSameRunAreAdded()
        {
            var runs = new RememberedRuns(this.FilePath);

            runs.NotePorts("api", FirstRun, new[] { 8000 }, T0);
            runs.NotePorts("api", FirstRun, new[] { 9000 }, T0.AddSeconds(2));

            Assert.Equal(new[] { 8000, 9000 }, runs.PortsOf("api"));
        }

        /// <summary>A port the configuration has since moved off is not looked for forever.</summary>
        [Fact]
        public void TheFirstPortsOfANewRunReplaceTheLastRuns()
        {
            var runs = new RememberedRuns(this.FilePath);

            runs.NotePorts("api", FirstRun, new[] { 8000 }, T0);
            runs.NotePorts("api", NextRun, new[] { 8001 }, T0.AddMinutes(5));

            Assert.Equal(new[] { 8001 }, runs.PortsOf("api"));
        }

        /// <summary>
        /// A new run looked at before it listened on anything still replaces the last run's ports when
        /// it does, rather than being taken for a run whose ports are already known.
        /// </summary>
        [Fact]
        public void ANewRunSeenListeningOnNothingFirstStillReplaces()
        {
            var runs = new RememberedRuns(this.FilePath);
            runs.NotePorts("api", FirstRun, new[] { 8000 }, T0);

            Assert.False(runs.NotePorts("api", NextRun, Array.Empty<int>(), T0.AddMinutes(5)));
            Assert.Equal(new[] { 8000 }, runs.PortsOf("api"));

            runs.NotePorts("api", NextRun, new[] { 8001 }, T0.AddMinutes(5).AddSeconds(4));
            Assert.Equal(new[] { 8001 }, runs.PortsOf("api"));
        }

        /// <summary>A service failing on its port never listens on it; that is not remembered as nothing.</summary>
        [Fact]
        public void NothingSeenIsNotRememberedAsNothing()
        {
            var runs = new RememberedRuns(this.FilePath);
            runs.NotePorts("api", FirstRun, new[] { 8000 }, T0);

            runs.NotePorts("api", NextRun, Array.Empty<int>(), T0.AddMinutes(5));

            Assert.Equal(new[] { 8000 }, new RememberedRuns(this.FilePath).PortsOf("api"));
        }

        /// <summary>A port the system handed out at random is some other program's tomorrow.</summary>
        [Fact]
        public void DynamicPortsAreNotRemembered()
        {
            var runs = new RememberedRuns(this.FilePath);

            Assert.False(runs.NotePorts("api", FirstRun, new[] { 49152, 61234 }, T0));
            runs.NotePorts("api", FirstRun, new[] { 8000, 55000 }, T0);

            Assert.Equal(new[] { 8000 }, runs.PortsOf("api"));
        }

        /// <summary>The same ports seen again change nothing, and the file is not written for them.</summary>
        [Fact]
        public void TheSamePortsAgainAreNoChange()
        {
            var runs = new RememberedRuns(this.FilePath);
            runs.NotePorts("api", FirstRun, new[] { 8000 }, T0);
            File.Delete(this.FilePath);

            Assert.False(runs.NotePorts("api", FirstRun, new[] { 8000 }, T0.AddSeconds(2)));
            Assert.False(runs.NotePorts("api", NextRun, new[] { 8000 }, T0.AddMinutes(5)));
            Assert.False(File.Exists(this.FilePath));
        }

        [Fact]
        public void AServiceKeepsSoManyPortsAtMost()
        {
            var runs = new RememberedRuns(this.FilePath);

            runs.NotePorts("api", FirstRun, Enumerable.Range(8000, RememberedRuns.MaxPorts + 10), T0);

            Assert.Equal(Enumerable.Range(8000, RememberedRuns.MaxPorts), runs.PortsOf("api"));
        }

        /// <summary>Services come and go; the ones noted longest ago make room for the rest.</summary>
        [Fact]
        public void TheServicesNotedLongestAgoAreDroppedFirst()
        {
            var runs = new RememberedRuns(this.FilePath);
            for (int i = 0; i <= RememberedRuns.MaxServices; i++)
            {
                runs.NotePorts("svc" + i, FirstRun, new[] { 8000 }, T0.AddMinutes(i));
            }

            var reread = new RememberedRuns(this.FilePath);
            Assert.Empty(reread.PortsOf("svc0"));
            Assert.Equal(new[] { 8000 }, reread.PortsOf("svc1"));
            Assert.Equal(new[] { 8000 }, reread.PortsOf("svc" + RememberedRuns.MaxServices));
        }

        /// <summary>The file is anyone's to edit: what is read back goes through the limits it was written under.</summary>
        [Fact]
        public void AnEditedFileIsHeldToTheLimits()
        {
            Directory.CreateDirectory(this.directory);
            File.WriteAllText(this.FilePath, @"{
                ""api"": { ""Ports"": [ 8000, 0, -1, 70000, 55000, 8000, 443 ], ""Noted"": ""2026-09-24T08:00:00"" },
                ""empty"": { ""Ports"": [], ""Processes"": [ { ""Id"": 0, ""Name"": ""x.exe"" }, { ""Id"": 5, ""Name"": """" } ] },
                ""nothing"": null,
                ""web"": { ""Processes"": [ { ""Id"": 4312, ""StartedAt"": ""2026-09-24T08:00:00"", ""Name"": ""python.exe"" } ] }
            }");

            var runs = new RememberedRuns(this.FilePath);

            Assert.Equal(new[] { 443, 8000 }, runs.PortsOf("api"));
            Assert.Empty(runs.PortsOf("empty"));
            Assert.Empty(runs.ProcessesOf("empty"));
            Assert.Empty(runs.PortsOf("nothing"));
            Assert.Equal(new[] { new ProcessMark(4312, T0, "python.exe") }, runs.ProcessesOf("web"));
        }

        /// <summary>Unreadable is the same as empty; the next change writes a whole file over it.</summary>
        [Fact]
        public void AnUnreadableFileIsTakenForEmpty()
        {
            Directory.CreateDirectory(this.directory);
            File.WriteAllText(this.FilePath, "{ not json");

            var runs = new RememberedRuns(this.FilePath);
            Assert.Empty(runs.PortsOf("api"));

            runs.NotePorts("api", FirstRun, new[] { 8000 }, T0);
            Assert.Equal(new[] { 8000 }, new RememberedRuns(this.FilePath).PortsOf("api"));
        }

        // Which runs are worth reading the ports for ----------------------------------------

        /// <summary>A run this console has not looked at yet may listen on anything.</summary>
        [Fact]
        public void ARunNotLookedAtYetIsWanted()
        {
            var runs = new RememberedRuns(this.FilePath);

            Assert.True(runs.WantsPortsOf("api", FirstRun, T0.AddHours(3)));
        }

        [Fact]
        public void ARunWhosePortsAreKnownIsNotWantedAgain()
        {
            var runs = new RememberedRuns(this.FilePath);
            runs.NotePorts("api", FirstRun, new[] { 8000 }, T0.AddSeconds(5));

            Assert.False(runs.WantsPortsOf("api", FirstRun, T0.AddSeconds(7)));
            Assert.True(runs.WantsPortsOf("api", NextRun, T0.AddMinutes(5)));
        }

        /// <summary>
        /// A young run that listens on nothing yet is looked at again, as a server still starting; one
        /// that has listened on nothing for the learning period is not a server, and is left alone.
        /// </summary>
        [Fact]
        public void ARunListeningOnNothingIsWantedOnlyWhileYoung()
        {
            var runs = new RememberedRuns(this.FilePath);
            runs.NotePorts("api", FirstRun, Array.Empty<int>(), T0.AddSeconds(2));

            Assert.True(runs.WantsPortsOf("api", FirstRun, T0.AddSeconds(4)));
            Assert.False(runs.WantsPortsOf("api", FirstRun, T0 + RememberedRuns.LearningPeriod));
        }

        // Processes, for what a run leaves behind --------------------------------------------

        [Fact]
        public void ProcessesAreRememberedAcrossARestartOfTheConsole()
        {
            var runs = new RememberedRuns(this.FilePath);
            var processes = new[] { new ProcessMark(4312, T0, "python.exe"), new ProcessMark(4400, T0.AddSeconds(1), "python.exe") };

            Assert.True(runs.NoteProcesses("api", processes, T0));
            Assert.False(runs.NoteProcesses("api", processes, T0.AddSeconds(2)));

            Assert.Equal(processes, new RememberedRuns(this.FilePath).ProcessesOf("api"));
        }

        /// <summary>The ports and the processes are kept side by side, and forgetting one keeps the other.</summary>
        [Fact]
        public void PortsAndProcessesShareAnEntry()
        {
            var runs = new RememberedRuns(this.FilePath);
            runs.NotePorts("api", FirstRun, new[] { 8000 }, T0);
            runs.NoteProcesses("api", new[] { new ProcessMark(4312, T0, "python.exe") }, T0);

            runs.NoteProcesses("api", Array.Empty<ProcessMark>(), T0.AddMinutes(1));

            var reread = new RememberedRuns(this.FilePath);
            Assert.Equal(new[] { 8000 }, reread.PortsOf("api"));
            Assert.Empty(reread.ProcessesOf("api"));
        }

        [Fact]
        public void AServiceKeepsSoManyProcessesAtMost()
        {
            var runs = new RememberedRuns(this.FilePath);

            runs.NoteProcesses("api", Enumerable.Range(100, RememberedRuns.MaxProcesses + 5).Select(id => new ProcessMark(id, T0, "worker.exe")), T0);

            Assert.Equal(RememberedRuns.MaxProcesses, runs.ProcessesOf("api").Length);
        }
    }
}

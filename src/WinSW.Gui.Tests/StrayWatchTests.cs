using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.ServiceProcess;
using WinSW.Gui.Model;
using WinSW.Gui.Services;
using Xunit;

namespace WinSW.Gui.Tests
{
    /// <summary>
    /// What a service's runs leave running, remembered past the next wrapper and past a restart of
    /// the console, and looked for in every state: over snapshots built by hand, the file under a
    /// temporary directory, and the banner's words in the real dictionaries.
    /// </summary>
    public sealed class StrayWatchTests : IDisposable
    {
        private const int Console = 900;
        private const int ServicesExe = 10;

        /// <summary>With forward slashes, which Windows takes as well, so the tests read the same on any host.</summary>
        private const string Program = "C:/apps/server/server.exe";

        private static readonly DateTime T0 = new(2026, 9, 24, 8, 0, 0);
        private static readonly ISet<string> Wrappers = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "WinSW.exe" };

        private readonly string directory = Path.Combine(Path.GetTempPath(), "winsw-gui-" + Guid.NewGuid().ToString("n"));

        private string FilePath => Path.Combine(this.directory, "remembered-runs.json");

        public void Dispose()
        {
            if (Directory.Exists(this.directory))
            {
                Directory.Delete(this.directory, recursive: true);
            }
        }

        // Past the next wrapper ---------------------------------------------------------------

        /// <summary>
        /// The case the list was wiped in: a wrapper crashes, its python goes on holding the port, and
        /// Windows' recovery starts the next wrapper. The python is what the next wrapper started
        /// beside, and it is named as an earlier run's while the service runs.
        /// </summary>
        [Fact]
        public void TheNextWrapperDoesNotForgetWhatTheLastOneLeft()
        {
            var firstRun = Snapshot(P(ServicesExe, 1, "services.exe", 0), P(100, ServicesExe, "WinSW.exe", 1), P(200, 100, "python.exe", 1));
            var first = Look(Running(firstRun, 100), Polled(), firstRun);
            Assert.Null(first.Stray);
            Assert.Equal(new[] { 200 }, first.Remembered.Select(p => p.ProcessId));

            var nextRun = Snapshot(P(ServicesExe, 1, "services.exe", 0), P(300, ServicesExe, "WinSW.exe", 5), P(400, 300, "python.exe", 5), P(200, 100, "python.exe", 1));
            var next = Look(Running(nextRun, 300), Polled(first.Remembered), nextRun);

            Assert.Equal(new StrayFinding(Mark(200, 1, "python.exe"), null, EarlierRun: true), next.Stray);
            Assert.Equal(new[] { 200, 400 }, next.Remembered.Select(p => p.ProcessId));
        }

        /// <summary>
        /// What started after the wrapper now running is this run's, under it or not: a program started
        /// through a launcher that has exited is no leftover while the run goes on, and is remembered
        /// so that it is one once the service stops.
        /// </summary>
        [Fact]
        public void WhatTheRunningWrapperStartedIsItsOwnWhereverItIs()
        {
            var machine = Snapshot(P(ServicesExe, 1, "services.exe", 0), P(300, ServicesExe, "WinSW.exe", 5), P(400, 350, "python.exe", 6));

            var running = Look(Running(machine, 300), Polled(Mark(400, 6, "python.exe")), machine);
            Assert.Null(running.Stray);
            Assert.Equal(new[] { 400 }, running.Remembered.Select(p => p.ProcessId));

            var afterStop = Snapshot(P(ServicesExe, 1, "services.exe", 0), P(400, 350, "python.exe", 6));
            var stopped = Look(InState(ServiceControllerStatus.Stopped), Polled(running.Remembered), afterStop);
            Assert.Equal(400, stopped.Stray?.Process.ProcessId);
            Assert.False(stopped.Stray?.EarlierRun);
        }

        /// <summary>A remembered process whose ID a later process was given is forgotten, and never found.</summary>
        [Fact]
        public void AReusedIdIsForgottenAndNeverFound()
        {
            var machine = Snapshot(P(ServicesExe, 1, "services.exe", 0), P(300, ServicesExe, "WinSW.exe", 5), P(200, 77, "notepad.exe", 30));

            var running = Look(Running(machine, 300), Polled(Mark(200, 1, "python.exe")), machine);

            Assert.Null(running.Stray);
            Assert.Empty(running.Remembered);
        }

        /// <summary>
        /// The other half of item 17's PID reuse: a new wrapper given a dead launcher's ID does not
        /// take the launcher's orphan for its own, so the orphan is neither remembered as this run's
        /// nor, once the service stops, offered for ending as its leftover.
        /// </summary>
        [Fact]
        public void AnOrphanUnderAReusedIdIsNeverTheServices()
        {
            var machine = Snapshot(P(ServicesExe, 1, "services.exe", 0), P(20, ServicesExe, "WinSW.exe", 50), P(500, 20, "explorer.exe", 5));

            var running = Look(Running(machine, 20), Polled(), machine);
            Assert.Empty(running.Remembered);

            var stopped = Look(InState(ServiceControllerStatus.Stopped), Polled(running.Remembered), Snapshot(P(500, 20, "explorer.exe", 5)));
            Assert.Null(stopped.Stray);
        }

        // In every state ----------------------------------------------------------------------

        /// <summary>Nothing of a run that is starting has been noted yet, so whatever is remembered and still runs is left over.</summary>
        [Fact]
        public void StartingTellsWhatAnEarlierRunLeft()
        {
            var machine = Snapshot(P(ServicesExe, 1, "services.exe", 0), P(200, 100, "python.exe", 1));

            var starting = Look(InState(ServiceControllerStatus.StartPending), Polled(Mark(200, 1, "python.exe")), machine);

            Assert.Equal(new StrayFinding(Mark(200, 1, "python.exe"), null), starting.Stray);
        }

        /// <summary>
        /// Starting, the program is not looked for by its path: the new wrapper may be starting it that
        /// moment, under a launcher the walk up its ancestry cannot see yet. Stopped, it is.
        /// </summary>
        [Fact]
        public void StartingDoesNotLookForTheProgramByItsPath()
        {
            var machine = Snapshot(P(ServicesExe, 1, "services.exe", 0), P(500, 77, "server.exe", 5));

            Assert.Null(Look(InState(ServiceControllerStatus.StartPending), Polled(executable: Program), machine, _ => Program).Stray);
            Assert.Equal(500, Look(InState(ServiceControllerStatus.Stopped), Polled(executable: Program), machine, _ => Program).Stray?.Process.ProcessId);
        }

        /// <summary>
        /// Stopping, the run being stopped still has its processes up, ending them being what a stop
        /// does; only what started before that run began is an earlier run's.
        /// </summary>
        [Fact]
        public void StoppingTellsOnlyWhatStartedBeforeTheRunBeingStopped()
        {
            var machine = Snapshot(P(ServicesExe, 1, "services.exe", 0), P(200, 100, "python.exe", 1), P(400, 300, "python.exe", 6));
            var stopping = InState(ServiceControllerStatus.StopPending);
            var runStarted = T0.AddMinutes(5);

            var both = Look(stopping, Polled(new[] { Mark(400, 6, "python.exe"), Mark(200, 1, "python.exe") }, runStarted), machine);
            Assert.Equal(new StrayFinding(Mark(200, 1, "python.exe"), null, EarlierRun: true), both.Stray);
            Assert.Equal(new[] { 200, 400 }, both.Remembered.Select(p => p.ProcessId));

            Assert.Null(Look(stopping, Polled(new[] { Mark(400, 6, "python.exe") }, runStarted), machine).Stray);

            // Not known when the run began, as after a restart of the console: nothing is claimed.
            Assert.Null(Look(stopping, Polled(new[] { Mark(200, 1, "python.exe") }), machine).Stray);
        }

        /// <summary>
        /// Running, with a wrapper the snapshot missed: it started after the snapshot was taken, so
        /// everything remembered that the snapshot has is older than it.
        /// </summary>
        [Fact]
        public void AWrapperTheSnapshotMissedIsNewerThanEverythingInIt()
        {
            var machine = Snapshot(P(ServicesExe, 1, "services.exe", 0), P(400, 300, "python.exe", 6));
            var running = new ServiceSample { Queried = true, Status = ServiceControllerStatus.Running, ProcessId = 700 };

            var sample = Look(running, Polled(Mark(400, 6, "python.exe")), machine);

            Assert.Equal(new StrayFinding(Mark(400, 6, "python.exe"), null, EarlierRun: true), sample.Stray);
        }

        /// <summary>What was found by its path while the service was stopped is still told once it runs again.</summary>
        [Fact]
        public void TheProgramFoundByItsPathIsRememberedForTheNextRun()
        {
            var stoppedMachine = Snapshot(P(ServicesExe, 1, "services.exe", 0), P(500, 77, "server.exe", 2));
            var stopped = Look(InState(ServiceControllerStatus.Stopped), Polled(executable: Program), stoppedMachine, _ => Program);
            Assert.Equal(new[] { 500 }, stopped.Remembered.Select(p => p.ProcessId));

            var runningMachine = Snapshot(P(ServicesExe, 1, "services.exe", 0), P(300, ServicesExe, "WinSW.exe", 5), P(310, 300, "server.exe", 5), P(500, 77, "server.exe", 2));
            var running = Look(Running(runningMachine, 300), Polled(stopped.Remembered, executable: Program), runningMachine, _ => Program);

            Assert.Equal(new StrayFinding(Mark(500, 2, "server.exe"), null, EarlierRun: true), running.Stray);
        }

        /// <summary>The oldest leftover is named, whatever order it was noted in: the top of what it left, and the same from one reading to the next.</summary>
        [Fact]
        public void TheOldestLeftoverIsNamed()
        {
            var machine = Snapshot(P(ServicesExe, 1, "services.exe", 0), P(200, 100, "python.exe", 1), P(210, 200, "python.exe", 2));

            var stopped = Look(InState(ServiceControllerStatus.Stopped), Polled(Mark(210, 2, "python.exe"), Mark(200, 1, "python.exe")), machine);

            Assert.Equal(200, stopped.Stray?.Process.ProcessId);
        }

        [Fact]
        public void AServiceThatCannotBeQueriedIsLeftAsItIs()
        {
            var sample = Look(default, Polled(Mark(200, 1, "python.exe")), Snapshot(P(200, 100, "python.exe", 1)));

            Assert.True(sample.Remembered.IsDefault);
            Assert.Null(sample.Stray);
        }

        /// <summary>
        /// The flicker this is for: through a crash, Windows' restart and the next failed run, the
        /// banner, once up, stays up, rather than going at every state that is not Stopped and waiting
        /// out its seconds again at the next.
        /// </summary>
        [Fact]
        public void TheBannerStaysUpThroughARestartLoop()
        {
            var entry = new ServiceEntry("api", "API", "C:/bin/WinSW.exe", "C:/svc/api.xml");
            var python = S(200, 100, "python.exe", 1);

            var firstRun = Snapshot(S(ServicesExe, 1, "services.exe", 0), S(100, ServicesExe, "WinSW.exe", 1), python);
            Read(entry, firstRun, Running(firstRun, 100), 2);
            Assert.False(entry.HasStrayProcess);

            var crashed = Snapshot(S(ServicesExe, 1, "services.exe", 0), python);
            Read(entry, crashed, InState(ServiceControllerStatus.Stopped), 10);
            Assert.False(entry.HasStrayProcess);
            Read(entry, crashed, InState(ServiceControllerStatus.Stopped), 13);
            Assert.True(entry.HasStrayProcess);

            Read(entry, crashed, InState(ServiceControllerStatus.StartPending), 15);
            Assert.True(entry.HasStrayProcess);

            var restarted = Snapshot(S(ServicesExe, 1, "services.exe", 0), S(300, ServicesExe, "WinSW.exe", 16), S(400, 300, "python.exe", 16), python);
            Read(entry, restarted, Running(restarted, 300), 17);
            Assert.Equal(new StrayFinding(new ProcessMark(200, T0.AddSeconds(1), "python.exe"), null, EarlierRun: true), entry.StrayProcess);

            // The next run failed on the port the leftover holds, and Windows is about to try again.
            var failedAgain = Snapshot(S(ServicesExe, 1, "services.exe", 0), S(300, ServicesExe, "WinSW.exe", 16), python);
            Read(entry, failedAgain, InState(ServiceControllerStatus.StopPending), 18);
            Assert.Equal(200, entry.StrayProcess?.Process.ProcessId);

            Read(entry, crashed, InState(ServiceControllerStatus.Stopped), 19);
            Assert.Equal(new StrayFinding(new ProcessMark(200, T0.AddSeconds(1), "python.exe"), null), entry.StrayProcess);
        }

        // Past a restart of the console -------------------------------------------------------

        [Fact]
        public void OnlyWhatHasRunForAMinuteGoesIntoTheFile()
        {
            var runs = new RememberedRuns(this.FilePath);
            var now = T0.AddMinutes(10);

            Assert.True(StrayWatch.Keep(runs, "api", ImmutableArray.Create(Mark(200, 1, "python.exe"), new ProcessMark(260, now.AddSeconds(-10), "curl.exe")), now));

            Assert.Equal(new[] { Mark(200, 1, "python.exe") }, new RememberedRuns(this.FilePath).ProcessesOf("api"));
        }

        /// <summary>A process gone from the list is gone from the file at the same reading.</summary>
        [Fact]
        public void WhatHasExitedIsDroppedFromTheFile()
        {
            var runs = new RememberedRuns(this.FilePath);
            StrayWatch.Keep(runs, "api", ImmutableArray.Create(Mark(200, 1, "python.exe")), T0.AddMinutes(10));

            Assert.True(StrayWatch.Keep(runs, "api", ImmutableArray<ProcessMark>.Empty, T0.AddMinutes(11)));

            Assert.Empty(new RememberedRuns(this.FilePath).ProcessesOf("api"));
        }

        /// <summary>The same processes in another order, or with a short-lived helper come and gone, write nothing.</summary>
        [Fact]
        public void TheSameProcessesAgainAreNoWrite()
        {
            var runs = new RememberedRuns(this.FilePath);
            var now = T0.AddMinutes(10);
            StrayWatch.Keep(runs, "api", ImmutableArray.Create(Mark(200, 1, "python.exe"), Mark(300, 2, "WinSW.exe")), now);

            Assert.False(StrayWatch.Keep(runs, "api", ImmutableArray.Create(Mark(300, 2, "WinSW.exe"), Mark(200, 1, "python.exe")), now.AddSeconds(2)));
            Assert.False(StrayWatch.Keep(runs, "api", ImmutableArray.Create(Mark(300, 2, "WinSW.exe"), new ProcessMark(260, now, "curl.exe"), Mark(200, 1, "python.exe")), now.AddSeconds(4)));
            Assert.False(StrayWatch.Keep(runs, "api", default, now.AddSeconds(6)));
        }

        /// <summary>Capped, the file keeps the processes left longest.</summary>
        [Fact]
        public void TheFileKeepsTheOldestWhenThereAreTooMany()
        {
            var runs = new RememberedRuns(this.FilePath);
            var many = Enumerable.Range(0, RememberedRuns.MaxProcesses + 8).Select(i => new ProcessMark(1000 + i, T0.AddSeconds(-i), "worker.exe")).ToImmutableArray();

            StrayWatch.Keep(runs, "api", many, T0.AddMinutes(10));

            var kept = runs.ProcessesOf("api");
            Assert.Equal(RememberedRuns.MaxProcesses, kept.Length);
            Assert.Contains(kept, p => p.ProcessId == 1000 + RememberedRuns.MaxProcesses + 7);
            Assert.DoesNotContain(kept, p => p.ProcessId == 1000);
        }

        /// <summary>A console restarted while a leftover runs knows it for one, from the file alone.</summary>
        [Fact]
        public void AConsoleRestartedStillKnowsALeftover()
        {
            StrayWatch.Keep(new RememberedRuns(this.FilePath), "api", ImmutableArray.Create(Mark(200, 1, "python.exe")), T0.AddMinutes(3));

            var remembered = new RememberedRuns(this.FilePath).ProcessesOf("api");
            var machine = Snapshot(P(ServicesExe, 1, "services.exe", 0), P(300, ServicesExe, "WinSW.exe", 5), P(200, 100, "python.exe", 1));

            Assert.Equal(200, Look(Running(machine, 300), Polled(remembered), machine).Stray?.Process.ProcessId);
        }

        // Onto the entry ----------------------------------------------------------------------

        /// <summary>A new wrapper no longer empties the list; a reading that did not look leaves it; the run's start outlives the run.</summary>
        [Fact]
        public void TheEntryKeepsItsListAcrossWrappersAndItsRunStartAcrossTheStop()
        {
            var entry = new ServiceEntry("api", "API", "C:/bin/WinSW.exe", "C:/svc/api.xml");

            ServiceDiscovery.Apply(entry, Sample(ServiceControllerStatus.Running, 100, T0.AddMinutes(1)) with { Remembered = ImmutableArray.Create(Mark(200, 1, "python.exe")) });
            Assert.Equal(T0.AddMinutes(1), entry.RunStartedAt);

            ServiceDiscovery.Apply(entry, Sample(ServiceControllerStatus.Running, 300, T0.AddMinutes(5)));
            Assert.Equal(new[] { Mark(200, 1, "python.exe") }, entry.RememberedProcesses);
            Assert.Equal(T0.AddMinutes(5), entry.RunStartedAt);

            ServiceDiscovery.Apply(entry, InState(ServiceControllerStatus.StopPending));
            ServiceDiscovery.ApplyStatus(entry, InState(ServiceControllerStatus.Stopped));
            Assert.Equal(T0.AddMinutes(5), entry.RunStartedAt);
            Assert.Single(entry.RememberedProcesses);
        }

        // In words ----------------------------------------------------------------------------

        /// <summary>The sentence the console is actually read in, from the real dictionary.</summary>
        [Fact]
        public void BesideARunningServiceTheBannerSaysAnEarlierRunLeftIt()
        {
            var finding = new StrayFinding(Mark(4312, 1, "python.exe"), null, EarlierRun: true);

            var zh = finding.Describe("api", FormatIn("zh-CN"));
            Assert.Equal("python.exe（PID 4312）是之前某次运行遗留的，仍在运行，但不在服务之下。", zh.Banner);
            Assert.Contains("同时在跑", zh.Hint, StringComparison.Ordinal);

            var en = finding.Describe("api", FormatIn("en"));
            Assert.Equal("python.exe (PID 4312), left by an earlier run, is still running outside the service.", en.Banner);

            // Stopped, it is said as before.
            Assert.Equal("python.exe (PID 4312) is still running, outside the service.", (finding with { EarlierRun = false }).Describe("api", FormatIn("en")).Banner);
        }

        /// <summary>The questions before ending it name the service as left running, in every language, with every placeholder filled.</summary>
        [Theory]
        [InlineData("en")]
        [InlineData("zh-CN")]
        [InlineData("zh-TW")]
        [InlineData("ja")]
        public void TheQuestionsSayTheServiceIsLeftAlone(string code)
        {
            var format = FormatIn(code);

            string body = format("M.Dash.StrayEarlierRunBody", new object?[] { "python.exe", 4312, "api" });
            string parentBody = format("M.Dash.StrayEarlierRunParentBody", new object?[] { "cmd.exe", 3000, "python.exe", 4312, "api" });

            Assert.Contains("4312", body, StringComparison.Ordinal);
            Assert.Contains("api", body, StringComparison.Ordinal);
            Assert.Contains("3000", parentBody, StringComparison.Ordinal);
            Assert.Contains("api", parentBody, StringComparison.Ordinal);
        }

        /// <summary>Beside a running service as when stopped, one of Windows' own is never offered for ending.</summary>
        [Fact]
        public void WindowsOwnIsNeverOfferedForEnding()
        {
            var entry = new ServiceEntry("api", "API", "C:/bin/WinSW.exe", "C:/svc/api.xml");
            var finding = new StrayFinding(Mark(3200, 1, "explorer.exe"), null, EarlierRun: true);
            entry.NoteStray(finding, T0);
            entry.NoteStray(finding, T0.AddSeconds(3));

            Assert.True(entry.HasStrayProcess);
            Assert.False(entry.CanEndStray);
        }

        private static ServiceSample Look(ServiceSample sample, PolledService service, ProcessSnapshot snapshot, Func<int, string?>? imagePathOf = null) =>
            StrayWatch.Look(sample, service, snapshot, Wrappers, Console, imagePathOf ?? (_ => null));

        private static PolledService Polled(params ProcessMark[] remembered) => Polled((IEnumerable<ProcessMark>)remembered);

        private static PolledService Polled(IEnumerable<ProcessMark>? remembered = null, DateTime? runStartedAt = null, string? executable = null) =>
            new("api", false, false, (remembered ?? Array.Empty<ProcessMark>()).ToImmutableArray(), runStartedAt, executable);

        /// <summary>Running under <paramref name="wrapper"/>, as the poll's worker reads it: counters, start, and what is under it.</summary>
        private static ServiceSample Running(ProcessSnapshot snapshot, int wrapper)
        {
            Assert.True(snapshot.TryGet(wrapper, out var record));
            return Sample(ServiceControllerStatus.Running, wrapper, record.StartedAt) with { Descendants = StrayProcesses.DescendantsOf(snapshot, wrapper) };
        }

        private static ServiceSample Sample(ServiceControllerStatus status, int processId, DateTime? startedAt) => new()
        {
            Queried = true,
            Status = status,
            ProcessId = processId,
            HasProcess = true,
            StartedAt = startedAt,
        };

        private static ServiceSample InState(ServiceControllerStatus status) => new() { Queried = true, Status = status };

        private static Func<string, object?[], string> FormatIn(string code)
        {
            var values = StringDictionaries.ValuesOf(code);
            return (key, args) => string.Format(CultureInfo.InvariantCulture, values[key], args);
        }

        private static ProcessRecord P(int id, int parent, string name, int minutes) =>
            new(id, parent, name, T0.AddMinutes(minutes), TimeSpan.Zero, 0, 0);

        /// <summary>A process started <paramref name="seconds"/> in, for a loop that runs in seconds.</summary>
        private static ProcessRecord S(int id, int parent, string name, int seconds) =>
            new(id, parent, name, T0.AddSeconds(seconds), TimeSpan.Zero, 0, 0);

        private static ProcessMark Mark(int id, int minutes, string name) => new(id, T0.AddMinutes(minutes), name);

        private static ProcessSnapshot Snapshot(params ProcessRecord[] records) => new(records);

        /// <summary>
        /// One full reading of <paramref name="entry"/>, as the poll takes it: the worker's half from
        /// what the entry held, then the entry's, at <paramref name="seconds"/> in. Written onto the
        /// entry by hand rather than through <see cref="ServiceDiscovery.Apply"/>, which notes the
        /// finding at the clock's time and would have the wait be real seconds.
        /// </summary>
        private static void Read(ServiceEntry entry, ProcessSnapshot snapshot, ServiceSample sample, int seconds)
        {
            var polled = new PolledService(entry.ServiceName, true, false, entry.RememberedProcesses, entry.RunStartedAt, entry.ExecutablePath);
            var looked = Look(sample, polled, snapshot);

            entry.RememberedProcesses = looked.Remembered;
            if (looked.HasProcess && looked.StartedAt is DateTime started)
            {
                entry.RunStartedAt = started;
            }

            entry.NoteStray(looked.Stray, T0.AddSeconds(seconds));
        }
    }
}

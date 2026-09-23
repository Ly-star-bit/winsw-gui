using System;
using System.Collections.Generic;
using System.Linq;
using WinSW.Gui.Services;
using Xunit;

namespace WinSW.Gui.Tests
{
    /// <summary>
    /// Finding a stopped service's program still running, over a snapshot built by hand. The
    /// full-path read is the one part that needs Windows; here it is a lookup table.
    /// </summary>
    public class StrayProcessesTests
    {
        /// <summary>With forward slashes, which Windows takes as well, so the tests read the same on any host.</summary>
        private const string Program = "C:/apps/server/server.exe";
        private const int Console = 900;

        private static readonly DateTime T0 = new(2026, 9, 23, 8, 0, 0);
        private static readonly ISet<string> Wrappers = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "WinSW.exe" };
        private static readonly IReadOnlyList<ProcessMark> Nothing = Array.Empty<ProcessMark>();

        [Fact]
        public void WhatIsUnderTheWrapperIsNoted()
        {
            var snapshot = Snapshot(P(100, 1, "WinSW.exe", 0), P(200, 100, "server.exe", 1), P(300, 200, "helper.exe", 2), P(400, 1, "other.exe", 0));

            var under = StrayProcesses.DescendantsOf(snapshot, 100);

            Assert.Equal(new[] { 200, 300 }, under.Select(m => m.ProcessId).OrderBy(id => id));
        }

        /// <summary>The wrapper has gone; the program it started has not.</summary>
        [Fact]
        public void AProgramThatOutlivedItsWrapperIsFound()
        {
            var snapshot = Snapshot(P(200, 100, "server.exe", 1));

            var stray = StrayProcesses.Find(snapshot, new[] { Mark(200, 1, "server.exe") }, null, Wrappers, Console, _ => null);

            Assert.Equal(200, stray?.Process.ProcessId);
        }

        /// <summary>The same ID on a process that started later is somebody else's.</summary>
        [Fact]
        public void AReusedIdIsNotTheProcessThatWasNoted()
        {
            var snapshot = Snapshot(P(200, 50, "notepad.exe", 30));

            Assert.Null(StrayProcesses.Find(snapshot, new[] { Mark(200, 1, "server.exe") }, null, Wrappers, Console, _ => null));
        }

        [Fact]
        public void TheProgramRunningWithNoWrapperAboveItIsFoundByItsPath()
        {
            var snapshot = Snapshot(P(500, 77, "server.exe", 5));

            var stray = StrayProcesses.Find(snapshot, Nothing, Program, Wrappers, Console, pid => pid == 500 ? Program : null);

            Assert.Equal(500, stray?.Process.ProcessId);
        }

        /// <summary>Another service, or a desktop task, running the same program is not stray.</summary>
        [Fact]
        public void TheSameProgramUnderAWrapperIsSomeoneElses()
        {
            var snapshot = Snapshot(P(10, 1, "services.exe", 0), P(20, 10, "WinSW.exe", 1), P(500, 20, "server.exe", 2));

            Assert.Null(StrayProcesses.Find(snapshot, Nothing, Program, Wrappers, Console, _ => Program));
        }

        [Fact]
        public void ATryRunFromThisConsoleIsItsOwn()
        {
            var snapshot = Snapshot(P(Console, 1, "WinSW.Gui.exe", 0), P(500, Console, "server.exe", 1));

            Assert.Null(StrayProcesses.Find(snapshot, Nothing, Program, Wrappers, Console, _ => Program));
        }

        /// <summary>The same file name elsewhere on disk, or a path that cannot be read, is not claimed.</summary>
        [Fact]
        public void OnlyTheFullPathClaimsAProcess()
        {
            var snapshot = Snapshot(P(500, 77, "server.exe", 5), P(600, 77, "server.exe", 5));

            var stray = StrayProcesses.Find(
                snapshot,
                Nothing,
                Program,
                Wrappers,
                Console,
                pid => pid == 500 ? "D:/elsewhere/server.exe" : null);

            Assert.Null(stray);
        }

        /// <summary>A bare name such as java has no path to match; only what was noted counts.</summary>
        [Fact]
        public void ABareNameIsLeftToWhatWasNoted()
        {
            var snapshot = Snapshot(P(500, 77, "java.exe", 5));

            Assert.Null(StrayProcesses.Find(snapshot, Nothing, null, Wrappers, Console, _ => @"C:\jdk\bin\java.exe"));
        }

        /// <summary>A parent ID now held by a process that started after the child is not its parent.</summary>
        [Fact]
        public void AReusedParentIdOwnsNothing()
        {
            var snapshot = Snapshot(P(20, 1, "WinSW.exe", 50), P(500, 20, "server.exe", 5));

            Assert.False(StrayProcesses.IsOwned(snapshot, P(500, 20, "server.exe", 5), Wrappers, Console));
        }

        /// <summary>
        /// Called stray only after a few seconds: a clean stop leaves the program's children a
        /// moment to exit, and a banner that flashed on every stop would go unread.
        /// </summary>
        [Fact]
        public void AProcessIsCalledStrayOnlyOnceItHasStayed()
        {
            var entry = new Model.ServiceEntry("demo", "Demo", "C:/bin/WinSW.exe", "C:/svc/demo.xml");
            var stray = new StrayFinding(Mark(200, 1, "server.exe"), null);

            entry.NoteStray(stray, T0);
            Assert.False(entry.HasStrayProcess);

            entry.NoteStray(stray, T0.AddSeconds(1));
            Assert.False(entry.HasStrayProcess);

            entry.NoteStray(stray, T0.AddSeconds(3));
            Assert.Equal(stray, entry.StrayProcess);

            entry.NoteStray(null, T0.AddSeconds(4));
            Assert.False(entry.HasStrayProcess);
        }

        /// <summary>
        /// A parent exiting while the banner is up is news for the banner, not a new process: it
        /// stays up and says so, rather than vanishing for another three seconds.
        /// </summary>
        [Fact]
        public void AParentExitingChangesTheBannerWithoutHidingIt()
        {
            var entry = new Model.ServiceEntry("demo", "Demo", "C:/bin/WinSW.exe", "C:/svc/demo.xml");
            var withParent = new StrayFinding(Mark(200, 1, "server.exe"), Mark(150, 0, "cmd.exe"));
            var orphaned = withParent with { Parent = null };

            entry.NoteStray(withParent, T0);
            entry.NoteStray(withParent, T0.AddSeconds(3));
            Assert.Equal(withParent, entry.StrayProcess);

            entry.NoteStray(orphaned, T0.AddSeconds(4));
            Assert.Equal(orphaned, entry.StrayProcess);
        }

        /// <summary>What keeps bringing a program back is its parent, when that is still running.</summary>
        [Fact]
        public void ALiveParentIsNamed()
        {
            var snapshot = Snapshot(P(150, 1, "cmd.exe", 0), P(500, 150, "server.exe", 5));

            var stray = StrayProcesses.Find(snapshot, Nothing, Program, Wrappers, Console, _ => Program);

            Assert.Equal(150, stray?.Parent?.ProcessId);
            Assert.Equal("cmd.exe", stray?.Parent?.Name);
        }

        /// <summary>A parent gone, or an ID since reused by a later process, is no parent.</summary>
        [Fact]
        public void AnExitedOrReusedParentIsNone()
        {
            Assert.Null(StrayProcesses.FindingFor(Snapshot(P(500, 150, "server.exe", 5)), P(500, 150, "server.exe", 5)).Parent);
            Assert.Null(StrayProcesses.FindingFor(Snapshot(P(150, 1, "notepad.exe", 30), P(500, 150, "server.exe", 5)), P(500, 150, "server.exe", 5)).Parent);
        }

        /// <summary>
        /// Windows' own processes are never offered for ending, whatever the program is under; a
        /// launcher, or a script looping over the program, is exactly what should be.
        /// </summary>
        [Theory]
        [InlineData("uvicorn.exe", 2540, true)]
        [InlineData("cmd.exe", 3000, true)]
        [InlineData("python.exe", 3100, true)]
        [InlineData("explorer.exe", 3200, false)]
        [InlineData("SVCHOST.EXE", 3300, false)]
        [InlineData("services.exe", 700, false)]
        [InlineData("System", 4, false)]
        public void OnlyAParentThatIsNotWindowsOwnMayBeEnded(string name, int id, bool mayEnd)
        {
            Assert.Equal(mayEnd, StrayProcesses.MayEnd(new ProcessMark(id, T0, name)));
        }

        /// <summary>The button to end the parent appears for a living parent that may be ended, and only then.</summary>
        [Fact]
        public void TheParentsButtonFollowsTheParent()
        {
            Assert.True(Confirmed(new StrayFinding(Mark(11380, 5, "python.exe"), Mark(2540, 1, "uvicorn.exe"))).CanEndStrayParent);
            Assert.False(Confirmed(new StrayFinding(Mark(11380, 5, "python.exe"), Mark(900, 1, "svchost.exe"))).CanEndStrayParent);
            Assert.False(Confirmed(new StrayFinding(Mark(11380, 5, "python.exe"), null)).CanEndStrayParent);
        }

        private static Model.ServiceEntry Confirmed(StrayFinding finding)
        {
            var entry = new Model.ServiceEntry("demo", "Demo", "C:/bin/WinSW.exe", "C:/svc/demo.xml");
            entry.NoteStray(finding, T0);
            entry.NoteStray(finding, T0.AddSeconds(3));
            return entry;
        }

        private static ProcessRecord P(int id, int parent, string name, int minutes) =>
            new(id, parent, name, T0.AddMinutes(minutes), TimeSpan.Zero, 0, 0);

        private static ProcessMark Mark(int id, int minutes, string name) => new(id, T0.AddMinutes(minutes), name);

        private static ProcessSnapshot Snapshot(params ProcessRecord[] records) => new(records);
    }
}

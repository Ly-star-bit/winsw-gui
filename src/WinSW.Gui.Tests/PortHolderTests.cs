using System;
using System.Collections.Generic;
using System.Globalization;
using WinSW.Gui.Model;
using WinSW.Gui.Services;
using Xunit;

namespace WinSW.Gui.Tests
{
    /// <summary>
    /// What holds the port a stopped service last listened on, over a snapshot and a port table built
    /// by hand; how the banner words it, in the real dictionaries; and what it offers to end.
    /// </summary>
    public class PortHolderTests
    {
        private const int Console = 900;

        private static readonly DateTime T0 = new(2026, 9, 24, 8, 0, 0);
        private static readonly ISet<string> Wrappers = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "WinSW.exe" };
        private static readonly int[] Port8000 = { 8000 };

        // Finding the holder ------------------------------------------------------------------

        /// <summary>A copy started by hand from a prompt: named, with the prompt that started it.</summary>
        [Fact]
        public void AProcessOutsideEveryWrapperHoldingThePortIsFound()
        {
            var snapshot = Snapshot(P(3000, 1, "cmd.exe", 0), P(4312, 3000, "python.exe", 1));
            var ports = Table((8000, 4312));

            var holder = StrayProcesses.FindPortHolder(snapshot, ports, Port8000, Wrappers, Console);

            Assert.Equal(8000, holder?.Port);
            Assert.True(holder?.HoldsPort);
            Assert.Equal(4312, holder?.Process.ProcessId);
            Assert.Equal("cmd.exe", holder?.Parent?.Name);
        }

        /// <summary>Another service, or a desktop task, on the port is somebody's; its banner is elsewhere.</summary>
        [Fact]
        public void AHolderUnderAWrapperIsSomeoneElses()
        {
            var snapshot = Snapshot(P(20, 1, "WinSW.exe", 0), P(4312, 20, "python.exe", 1));

            Assert.Null(StrayProcesses.FindPortHolder(snapshot, Table((8000, 4312)), Port8000, Wrappers, Console));
        }

        [Fact]
        public void ATryRunFromThisConsoleIsItsOwn()
        {
            var snapshot = Snapshot(P(Console, 1, "WinSW.Gui.exe", 0), P(4312, Console, "python.exe", 1));

            Assert.Null(StrayProcesses.FindPortHolder(snapshot, Table((8000, 4312)), Port8000, Wrappers, Console));
        }

        /// <summary>
        /// HTTP.sys takes its ports in System's name: named, since that is who holds it, but with no
        /// parent — System's is Idle, and nothing is started by Idle — and never offered for ending.
        /// </summary>
        [Fact]
        public void SystemIsNamedButNotOfferedForEnding()
        {
            var snapshot = Snapshot(P(0, 0, "Idle", 0), P(4, 0, "System", 0));

            var holder = StrayProcesses.FindPortHolder(snapshot, Table((8000, 4)), Port8000, Wrappers, Console);

            Assert.Equal(4, holder?.Process.ProcessId);
            Assert.Null(holder?.Parent);

            var entry = Shown(holder!.Value);
            Assert.False(entry.CanEndStray);
            Assert.False(entry.CanEndStrayParent);
        }

        /// <summary>No listener is owned by the idle process; one claiming to be is not named.</summary>
        [Fact]
        public void PidZeroIsNeverAHolder()
        {
            var snapshot = Snapshot(P(0, 0, "Idle", 0));

            Assert.Null(StrayProcesses.FindPortHolder(snapshot, Table((8000, 0)), Port8000, Wrappers, Console));
        }

        /// <summary>One of Windows' own on the port is named, but neither it nor its parent is offered for ending.</summary>
        [Fact]
        public void AWindowsProcessIsNamedButNotOfferedForEnding()
        {
            var snapshot = Snapshot(P(700, 1, "services.exe", 0), P(1200, 700, "svchost.exe", 1));

            var holder = StrayProcesses.FindPortHolder(snapshot, Table((8000, 1200)), Port8000, Wrappers, Console);

            var entry = Shown(holder!.Value);
            Assert.False(entry.CanEndStray);
            Assert.False(entry.CanEndStrayParent);
        }

        /// <summary>A program the user may end, started by a script the user may end: both are offered.</summary>
        [Fact]
        public void AProgramAndTheScriptRestartingItAreOffered()
        {
            var snapshot = Snapshot(P(3000, 1, "cmd.exe", 0), P(4312, 3000, "python.exe", 1));

            var entry = Shown(StrayProcesses.FindPortHolder(snapshot, Table((8000, 4312)), Port8000, Wrappers, Console)!.Value);

            Assert.True(entry.CanEndStray);
            Assert.True(entry.CanEndStrayParent);
        }

        /// <summary>A process that started after the snapshot is named at the next poll, when it is in one.</summary>
        [Fact]
        public void AHolderNotInTheSnapshotIsLeftForTheNextPoll()
        {
            Assert.Null(StrayProcesses.FindPortHolder(Snapshot(), Table((8000, 4312)), Port8000, Wrappers, Console));
        }

        /// <summary>Of the ports remembered, the lowest held is named; a port nobody holds is passed over.</summary>
        [Fact]
        public void TheFirstRememberedPortHeldIsNamed()
        {
            var snapshot = Snapshot(P(4312, 1, "python.exe", 1), P(5000, 1, "node.exe", 1));

            var holder = StrayProcesses.FindPortHolder(snapshot, Table((9000, 5000), (9001, 4312)), new[] { 8000, 9000, 9001 }, Wrappers, Console);

            Assert.Equal(9000, holder?.Port);
            Assert.Equal("node.exe", holder?.Process.Name);
        }

        [Fact]
        public void APortNotRememberedIsNotLookedAt()
        {
            var snapshot = Snapshot(P(4312, 1, "python.exe", 1));

            Assert.Null(StrayProcesses.FindPortHolder(snapshot, Table((8000, 4312)), new[] { 8001 }, Wrappers, Console));
        }

        // The banner ---------------------------------------------------------------------------

        /// <summary>
        /// A stranger on the port is not one of the program's own children taking a moment to exit
        /// after a stop: it is shown at once, since a service restarted by Windows can spend less than
        /// the wait stopped between two runs.
        /// </summary>
        [Fact]
        public void AStrangerOnThePortIsShownAtOnce()
        {
            var entry = new ServiceEntry("api", "API", "C:/bin/WinSW.exe", "C:/svc/api.xml");
            var holder = new StrayFinding(Mark(4312, 1, "python.exe"), null, 8000);

            entry.NoteStray(holder, T0);

            Assert.Equal(holder, entry.StrayProcess);
        }

        /// <summary>One of the service's own processes on its port is still given the moment a clean stop takes.</summary>
        [Fact]
        public void TheServicesOwnProcessOnThePortWaitsLikeAnyLeftover()
        {
            var entry = new ServiceEntry("api", "API", "C:/bin/WinSW.exe", "C:/svc/api.xml")
            {
                Descendants = new[] { Mark(4312, 1, "python.exe") },
            };
            var holder = new StrayFinding(Mark(4312, 1, "python.exe"), null, 8000);

            entry.NoteStray(holder, T0);
            Assert.False(entry.HasStrayProcess);

            entry.NoteStray(holder, T0.AddSeconds(3));
            Assert.Equal(holder, entry.StrayProcess);
        }

        /// <summary>The sentence the console is actually read in, from the real dictionary.</summary>
        [Fact]
        public void TheBannerNamesThePortTheHolderAndWhoStartedIt()
        {
            var text = new StrayFinding(Mark(4312, 1, "python.exe"), Mark(3000, 0, "cmd.exe"), 8000).Describe("api", FormatIn("zh-CN"));

            Assert.Equal("端口 8000 被 python.exe（PID 4312，由 cmd.exe 启动）占用。", text.Banner);
            Assert.Equal("cmd.exe（PID 3000）仍在运行：如果 python.exe 结束后又反复出现，就是它在重新拉起。", text.Parent);
            Assert.Contains("“api”", text.Hint, StringComparison.Ordinal);
        }

        [Fact]
        public void AnOrphanedHolderIsSaidToBeLeftBehind()
        {
            var values = StringDictionaries.ValuesOf("en");
            var text = new StrayFinding(Mark(4312, 1, "python.exe"), null, 8000).Describe("api", FormatIn("en"));

            Assert.Equal("Port 8000 is held by python.exe (PID 4312).", text.Banner);
            Assert.Equal(values["M.Dash.StrayOrphan"], text.Parent);
        }

        /// <summary>What may not be ended says why in place of who started it, since there is no button for it.</summary>
        [Fact]
        public void WhatMayNotBeEndedSaysWhy()
        {
            var values = StringDictionaries.ValuesOf("en");

            var system = new StrayFinding(Mark(4, 0, "System"), null, 80).Describe("api", FormatIn("en"));
            Assert.Equal(values["M.Dash.PortHeldBySystem"], system.Parent);

            var windows = new StrayFinding(Mark(1200, 0, "svchost.exe"), Mark(700, 0, "services.exe"), 80).Describe("api", FormatIn("en"));
            Assert.Equal(string.Format(CultureInfo.InvariantCulture, values["M.Dash.PortHeldByWindows"], "svchost.exe"), windows.Parent);
        }

        /// <summary>The service's own program, found as it always was, keeps its own words.</summary>
        [Fact]
        public void AProgramLeftRunningKeepsItsBanner()
        {
            var values = StringDictionaries.ValuesOf("en");
            var text = new StrayFinding(Mark(4312, 1, "server.exe"), null).Describe("api", FormatIn("en"));

            Assert.Equal("server.exe (PID 4312) is still running, outside the service.", text.Banner);
            Assert.Equal(values["M.Dash.StrayOrphan"], text.Parent);
            Assert.Equal(values["M.Dash.StrayHint"], text.Hint);
        }

        /// <summary>
        /// Every phrase the banner can be made of is in every language: they are looked up through a
        /// rule, which the check on literal keys cannot see. With the keys the page asks for by rule too.
        /// </summary>
        [Fact]
        public void EveryLanguageHasEveryPhraseOfTheBanner()
        {
            var findings = new[]
            {
                new StrayFinding(Mark(4312, 1, "python.exe"), Mark(3000, 0, "cmd.exe"), 8000),
                new StrayFinding(Mark(4312, 1, "python.exe"), null, 8000),
                new StrayFinding(Mark(4, 0, "System"), null, 8000),
                new StrayFinding(Mark(1200, 0, "svchost.exe"), null, 8000),
                new StrayFinding(Mark(4312, 1, "server.exe"), Mark(3000, 0, "cmd.exe")),
                new StrayFinding(Mark(4312, 1, "server.exe"), null),
            };

            foreach (string code in new[] { "en", "zh-CN", "zh-TW", "ja" })
            {
                var values = StringDictionaries.ValuesOf(code);
                foreach (var finding in findings)
                {
                    var text = finding.Describe("api", FormatIn(code));
                    Assert.False(string.IsNullOrWhiteSpace(text.Banner + text.Parent + text.Hint));
                }

                Assert.Contains("M.Dash.PortEndTitle", values.Keys);
                Assert.Contains("M.Dash.Listening", values.Keys);
            }
        }

        private static Func<string, object?[], string> FormatIn(string code)
        {
            var values = StringDictionaries.ValuesOf(code);
            return (key, args) => string.Format(CultureInfo.InvariantCulture, values[key], args);
        }

        private static ServiceEntry Shown(StrayFinding finding)
        {
            var entry = new ServiceEntry("api", "API", "C:/bin/WinSW.exe", "C:/svc/api.xml");
            entry.NoteStray(finding, T0);
            entry.NoteStray(finding, T0.AddSeconds(3));
            return entry;
        }

        private static PortTable Table(params (int Port, int ProcessId)[] listeners)
        {
            var all = new List<ListeningPort>();
            foreach (var (port, processId) in listeners)
            {
                all.Add(new ListeningPort("0.0.0.0", port, processId));
                all.Add(new ListeningPort("::", port, processId));
            }

            return new PortTable(all);
        }

        private static ProcessRecord P(int id, int parent, string name, int minutes) =>
            new(id, parent, name, id <= 4 ? null : T0.AddMinutes(minutes), TimeSpan.Zero, 0, 0);

        private static ProcessMark Mark(int id, int minutes, string name) => new(id, id <= 4 ? null : T0.AddMinutes(minutes), name);

        private static ProcessSnapshot Snapshot(params ProcessRecord[] records) => new(records);
    }
}

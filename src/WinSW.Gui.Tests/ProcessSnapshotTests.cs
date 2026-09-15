using System;
using System.Diagnostics;
using System.ServiceProcess;
using WinSW.Gui.Services;
using Xunit;

namespace WinSW.Gui.Tests
{
    /// <summary>
    /// The snapshot is read straight out of a native buffer whose layout this code declares
    /// by hand, and a wrong offset does not fail: it yields a plausible-looking number for
    /// the wrong thing. The runtime's own reading of this process is the check, on the one
    /// process every test run is guaranteed to have.
    /// </summary>
    public class ProcessSnapshotTests
    {
        [Fact]
        public void AgreesWithTheRuntimeAboutThisProcess()
        {
            using var current = Process.GetCurrentProcess();

            var snapshot = ProcessSnapshot.Take();
            Assert.NotNull(snapshot);
            Assert.True(snapshot!.Count > 1);
            Assert.True(snapshot.TryGet(current.Id, out var record));

            Assert.Equal(current.Id, record.ProcessId);
            Assert.Equal(current.ProcessName + ".exe", record.Name, ignoreCase: true);
            Assert.NotEqual(0, record.ParentProcessId);
            Assert.NotEqual(current.Id, record.ParentProcessId);

            Assert.NotNull(record.StartedAt);
            Assert.True(
                Math.Abs((record.StartedAt!.Value - current.StartTime).TotalSeconds) < 1,
                $"snapshot says {record.StartedAt:O}, the runtime says {current.StartTime:O}");

            // The counters move while the test runs, so they are held to the right order of
            // magnitude rather than to equality. A wrong offset is off by far more than that.
            current.Refresh();
            Assert.InRange(record.WorkingSetBytes, current.WorkingSet64 / 2, current.WorkingSet64 * 2);
            Assert.InRange(record.HandleCount, current.HandleCount / 2, current.HandleCount * 2);
            Assert.InRange(record.ProcessorTime, TimeSpan.Zero, current.TotalProcessorTime + TimeSpan.FromSeconds(1));
        }

        [Fact]
        public void TheTreeUnderThisProcessStartsWithIt()
        {
            using var current = Process.GetCurrentProcess();

            var tree = ProcessTreeProvider.Build(current.Id);

            Assert.NotNull(tree);
            Assert.Equal(current.Id, tree!.ProcessId);
            Assert.Equal(current.ProcessName + ".exe", tree.Name, ignoreCase: true);
        }

        [Fact]
        public void AProcessIdNobodyHasIsNotFound()
        {
            // Windows hands out process IDs in multiples of four; 1 is never one.
            var snapshot = ProcessSnapshot.Take();
            Assert.NotNull(snapshot);

            Assert.False(snapshot!.TryGet(1, out _));
            Assert.Empty(snapshot.ChildrenOf(1));
            Assert.Null(ProcessTreeProvider.Build(snapshot, 1));
            Assert.Null(ProcessTreeProvider.Build(0));
        }

        /// <summary>
        /// The whole path the dashboard takes, against a service every Windows has. A
        /// standard user can query its status; the counters come from the snapshot, which
        /// asks nobody's permission.
        /// </summary>
        [Fact]
        public void ReadsAServiceAndItsProcessThroughOneReading()
        {
            using var reading = new StatusReading();

            var sample = reading.Sample("EventLog");
            Assert.True(sample.Queried);

            if (sample.Status == ServiceControllerStatus.Running)
            {
                Assert.True(sample.ProcessId > 0);
                Assert.True(sample.HasProcess);
                Assert.True(sample.WorkingSet > 0);
                Assert.True(sample.Handles > 0);
                Assert.NotNull(sample.StartedAt);
                Assert.NotNull(reading.Tree(sample.ProcessId));
            }

            Assert.False(reading.Sample("WinSW.Gui.Tests.NoSuchService").Queried);
        }

        [Fact]
        public void AChildOlderThanItsParentBelongsToAnEarlierHolderOfTheId()
        {
            var started = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Local);
            var snapshot = new ProcessSnapshot(new[]
            {
                Record(100, 4, "WinSW.exe", started),
                Record(200, 100, "java.exe", started.AddSeconds(5)),

                // Spawned by whatever held ID 100 ten minutes before this wrapper did.
                Record(300, 100, "orphan.exe", started.AddMinutes(-10)),
                Record(400, 200, "cmd.exe", started.AddSeconds(6)),
            });

            var tree = ProcessTreeProvider.Build(snapshot, 100);

            Assert.NotNull(tree);
            var child = Assert.Single(tree!.Children);
            Assert.Equal(200, child.ProcessId);
            var grandchild = Assert.Single(child.Children);
            Assert.Equal(400, grandchild.ProcessId);
        }

        [Fact]
        public void AStaleParentLinkThatLoopsStillEnds()
        {
            // Each names the other as its parent: an ID recycled twice over. Without start
            // times to settle it, the walk has to stop on its own.
            var snapshot = new ProcessSnapshot(new[]
            {
                Record(100, 200, "a.exe", null),
                Record(200, 100, "b.exe", null),
            });

            var tree = ProcessTreeProvider.Build(snapshot, 100);

            Assert.NotNull(tree);
            var child = Assert.Single(tree!.Children);
            Assert.Equal(200, child.ProcessId);
            Assert.Empty(child.Children);
        }

        [Fact]
        public void ChildrenAreOrderedByProcessId()
        {
            var snapshot = new ProcessSnapshot(new[]
            {
                Record(100, 4, "WinSW.exe", null),
                Record(900, 100, "c.exe", null),
                Record(300, 100, "a.exe", null),
                Record(600, 100, "b.exe", null),
            });

            Assert.Equal(new[] { 300, 600, 900 }, snapshot.ChildrenOf(100));

            var tree = ProcessTreeProvider.Build(snapshot, 100);
            Assert.Equal(new[] { "a.exe", "b.exe", "c.exe" }, Array.ConvertAll(new[] { 0, 1, 2 }, i => tree!.Children[i].Name));
        }

        private static ProcessRecord Record(int id, int parent, string name, DateTime? started) =>
            new(id, parent, name, started, TimeSpan.Zero, 0, 0);
    }
}

using System.Collections.Generic;
using System.ComponentModel;
using WinSW.Gui.Model;
using Xunit;

namespace WinSW.Gui.Tests
{
    /// <summary>
    /// What a background rescan is allowed to carry into an entry that is already on screen.
    /// </summary>
    /// <remarks>
    /// The dashboard merges a rescan into the existing rows rather than replacing them, so
    /// that a selection, a scroll position and the per-row health history survive it. The
    /// merge therefore has to bring the registry's answers forward by hand — and has to leave
    /// the status poll's answers alone, because the freshly discovered entry has none.
    /// </remarks>
    public class ServiceEntryTests
    {
        [Fact]
        public void ARescanBringsForwardWhatTheRegistrySees()
        {
            var onScreen = new ServiceEntry("demo", "Demo", @"C:\bin\WinSW.exe", @"C:\svc\demo.xml")
            {
                Description = "old",
                StartMode = "Automatic",
                Account = "LocalSystem",
                WrapperVersion = "3.0.0.10",
                DependsOn = new[] { "Tcpip" },
            };

            var found = new ServiceEntry("demo", "Demo", @"C:\bin\WinSW.exe", @"C:\svc\demo.xml")
            {
                Description = "new",
                StartMode = "Manual",
                Account = @"CORP\svc",
                WrapperVersion = "3.0.0.11",
                DependsOn = new[] { "Tcpip", "Dnscache" },
                Problem = "something",
            };

            onScreen.MergeMetadataFrom(found);

            Assert.Equal("new", onScreen.Description);
            Assert.Equal("Manual", onScreen.StartMode);
            Assert.Equal(@"CORP\svc", onScreen.Account);
            Assert.Equal("3.0.0.11", onScreen.WrapperVersion);
            Assert.Equal("Tcpip, Dnscache", onScreen.DependsOnText);
            Assert.Equal("something", onScreen.Problem);
        }

        /// <summary>
        /// The live metrics belong to the status poll. A discovered entry carries none, so
        /// copying them across would blank the panel every time the rescan ran.
        /// </summary>
        [Fact]
        public void ARescanDoesNotDisturbTheLiveMetrics()
        {
            var onScreen = new ServiceEntry("demo", "Demo", @"C:\bin\WinSW.exe", @"C:\svc\demo.xml")
            {
                Status = System.ServiceProcess.ServiceControllerStatus.Running,
                ProcessId = 4321,
                LastExitCode = 7,
            };

            onScreen.MergeMetadataFrom(new ServiceEntry("demo", "Demo", @"C:\bin\WinSW.exe", @"C:\svc\demo.xml"));

            Assert.Equal(System.ServiceProcess.ServiceControllerStatus.Running, onScreen.Status);
            Assert.Equal(4321, onScreen.ProcessId);
            Assert.Equal(7, onScreen.LastExitCode);
        }

        [Fact]
        public void ChangedMetadataRaisesForTheBoundText()
        {
            var entry = new ServiceEntry("demo", "Demo", @"C:\bin\WinSW.exe", @"C:\svc\demo.xml");
            var raised = new List<string?>();
            ((INotifyPropertyChanged)entry).PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            entry.DependsOn = new[] { "Tcpip" };

            Assert.Contains(nameof(ServiceEntry.DependsOn), raised);
            Assert.Contains(nameof(ServiceEntry.DependsOnText), raised);
        }

        /// <summary>
        /// A rescan hands over a fresh array every time. Compared by reference, each one would
        /// count as a change and repaint the detail panel twice a minute for nothing.
        /// </summary>
        [Fact]
        public void UnchangedDependenciesRaiseNothing()
        {
            var entry = new ServiceEntry("demo", "Demo", @"C:\bin\WinSW.exe", @"C:\svc\demo.xml")
            {
                DependsOn = new[] { "Tcpip", "Dnscache" },
            };

            var raised = new List<string?>();
            ((INotifyPropertyChanged)entry).PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            entry.DependsOn = new[] { "Tcpip", "Dnscache" };

            Assert.Empty(raised);
        }

        [Fact]
        public void TheSameNameOnTheSameFilesIsTheSameInstallation()
        {
            var a = new ServiceEntry("demo", "Demo", @"C:\bin\WinSW.exe", @"C:\svc\demo.xml");
            var b = new ServiceEntry("demo", "Demo", @"C:\BIN\winsw.EXE", @"C:\SVC\DEMO.XML");

            Assert.True(a.IsSameInstallationAs(b));
        }

        /// <summary>
        /// Reinstalled onto a different executable or a different configuration, it is a
        /// different thing wearing the same name, and its recorded history describes the old
        /// one. The dashboard drops and re-adds it rather than merging.
        /// </summary>
        [Theory]
        [InlineData(@"C:\other\WinSW.exe", @"C:\svc\demo.xml")]
        [InlineData(@"C:\bin\WinSW.exe", @"C:\svc\other.xml")]
        public void ADifferentExecutableOrConfigurationIsNot(string wrapper, string config)
        {
            var a = new ServiceEntry("demo", "Demo", @"C:\bin\WinSW.exe", @"C:\svc\demo.xml");
            var b = new ServiceEntry("demo", "Demo", wrapper, config);

            Assert.False(a.IsSameInstallationAs(b));
        }

        [Fact]
        public void AConfigurationlessEntryIsComparableToAnother()
        {
            var a = new ServiceEntry("demo", "Demo", @"C:\bin\WinSW.exe", null);
            var b = new ServiceEntry("demo", "Demo", @"C:\bin\WinSW.exe", null);

            Assert.True(a.IsSameInstallationAs(b));
            Assert.False(a.IsSameInstallationAs(new ServiceEntry("demo", "Demo", @"C:\bin\WinSW.exe", @"C:\svc\demo.xml")));
        }

        /// <summary>
        /// The memory trace takes one point a minute: the first sample of a process is one,
        /// and the samples that follow within the minute are not.
        /// </summary>
        [Fact]
        public void MemoryIsTracedOncePerMinute()
        {
            var entry = new ServiceEntry("demo", "Demo", @"C:\bin\WinSW.exe", @"C:\svc\demo.xml") { ProcessId = 100 };

            entry.Sample(System.TimeSpan.Zero, 64 * 1024 * 1024, 10, null);
            entry.Sample(System.TimeSpan.Zero, 96 * 1024 * 1024, 10, null);

            Assert.Equal(new[] { 64.0 }, entry.MemoryHistory);
        }

        /// <summary>
        /// A poll that misses the process in its snapshot keeps the hour already traced; a
        /// different process, or none, starts the trace over.
        /// </summary>
        [Fact]
        public void TheMemoryTraceBelongsToOneProcess()
        {
            var entry = new ServiceEntry("demo", "Demo", @"C:\bin\WinSW.exe", @"C:\svc\demo.xml") { ProcessId = 100 };
            entry.Sample(System.TimeSpan.Zero, 64 * 1024 * 1024, 10, null);

            entry.ClearSample();
            Assert.Single(entry.MemoryHistory);

            entry.ProcessId = 200;
            entry.Sample(System.TimeSpan.Zero, 32 * 1024 * 1024, 10, null);
            Assert.Equal(new[] { 32.0 }, entry.MemoryHistory);

            entry.ProcessId = 0;
            entry.ClearSample();
            Assert.Empty(entry.MemoryHistory);
        }
    }
}

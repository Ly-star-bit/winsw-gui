using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel;
using System.ServiceProcess;
using WinSW.Gui.Model;
using WinSW.Gui.Services;
using Xunit;

namespace WinSW.Gui.Tests
{
    /// <summary>
    /// A service Windows is about to start again is shown as restarting, not as stopped: from the
    /// stop the dashboard saw, for as long as the restart that answers it can take.
    /// </summary>
    public class RecoveryRestartingTests
    {
        private static readonly DateTime T0 = new(2026, 9, 23, 10, 0, 0, DateTimeKind.Utc);

        private static readonly RecoverySettings RestartAfterTenSeconds =
            new(ImmutableArray.Create(new RecoveryAction(RecoveryActionKind.Restart, TimeSpan.FromSeconds(10))), TimeSpan.FromDays(1), false);

        [Fact]
        public void ACrashAnsweredWithARestartIsRestartingUntilTheRestartIsOverdue()
        {
            var entry = Service();
            Read(entry, ServiceControllerStatus.Running, 0, T0);

            Read(entry, ServiceControllerStatus.Stopped, RecoverySettings.ProcessAborted, T0.AddSeconds(2), crashCount: 1);
            Assert.True(entry.IsRestartingByRecovery);
            Assert.Equal(ServiceHealth.Pending, entry.Health);

            // Still stopped, and still within ten seconds and the margin of the stop.
            Read(entry, ServiceControllerStatus.Stopped, RecoverySettings.ProcessAborted, T0.AddSeconds(20), crashCount: 1);
            Assert.True(entry.IsRestartingByRecovery);

            // Overdue: whatever Windows meant to do, it has not, and the service is stopped.
            Read(entry, ServiceControllerStatus.Stopped, RecoverySettings.ProcessAborted, T0.AddSeconds(30), crashCount: 1);
            Assert.False(entry.IsRestartingByRecovery);
            Assert.Equal(ServiceHealth.Stopped, entry.Health);
        }

        [Fact]
        public void TheRestartThatComesEndsTheWait()
        {
            var entry = Service();
            Read(entry, ServiceControllerStatus.Running, 0, T0);
            Read(entry, ServiceControllerStatus.Stopped, RecoverySettings.ProcessAborted, T0.AddSeconds(2), crashCount: 1);

            Read(entry, ServiceControllerStatus.StartPending, RecoverySettings.ProcessAborted, T0.AddSeconds(12), crashCount: 1);

            Assert.False(entry.IsRestartingByRecovery);
            Assert.Equal(ServiceHealth.Pending, entry.Health);
        }

        /// <summary>
        /// Found stopped, at the first reading, it stopped while nobody was looking: when is not
        /// known, and a wait it may have finished long ago is not made up for it.
        /// </summary>
        [Fact]
        public void AServiceFoundStoppedIsNotGivenAWait()
        {
            var entry = Service();

            Read(entry, ServiceControllerStatus.Stopped, RecoverySettings.ProcessAborted, T0, crashCount: 0);

            Assert.False(entry.IsRestartingByRecovery);
            Assert.Equal(ServiceHealth.Stopped, entry.Health);
        }

        [Fact]
        public void AStopWithExitCodeZeroIsNotAnsweredByRecovery()
        {
            var entry = Service();
            Read(entry, ServiceControllerStatus.Running, 0, T0);

            Read(entry, ServiceControllerStatus.Stopped, 0, T0.AddSeconds(2), crashCount: 0);

            Assert.False(entry.IsRestartingByRecovery);
        }

        [Fact]
        public void AServiceWithoutARestartActionIsStopped()
        {
            var entry = Service(recovery: new RecoverySettings(ImmutableArray<RecoveryAction>.Empty, TimeSpan.FromDays(1), false));
            Read(entry, ServiceControllerStatus.Running, 0, T0);

            Read(entry, ServiceControllerStatus.Stopped, RecoverySettings.ProcessAborted, T0.AddSeconds(2), crashCount: 1);

            Assert.False(entry.IsRestartingByRecovery);
        }

        /// <summary>A disabled service cannot be started, by its recovery or anyone else: that is what Stop restarting relies on.</summary>
        [Fact]
        public void ADisabledServiceIsNotRestarting()
        {
            var entry = Service();
            Read(entry, ServiceControllerStatus.Running, 0, T0);
            Read(entry, ServiceControllerStatus.Stopped, RecoverySettings.ProcessAborted, T0.AddSeconds(2), crashCount: 1);
            Assert.True(entry.IsRestartingByRecovery);

            entry.StartType = ServiceStartMode.Disabled;
            Read(entry, ServiceControllerStatus.Stopped, RecoverySettings.ProcessAborted, T0.AddSeconds(4), crashCount: 1);

            Assert.False(entry.IsRestartingByRecovery);
            Assert.False(entry.CanStopRestarting);
        }

        /// <summary>Only a service a failure would have restarted has anything for Stop restarting to stop.</summary>
        [Fact]
        public void StopRestartingIsOfferedOnlyWhereAFailureRestarts()
        {
            Assert.True(Service().CanStopRestarting);
            Assert.False(Service(recovery: new RecoverySettings(ImmutableArray<RecoveryAction>.Empty, null, false)).CanStopRestarting);

            var unread = Service();
            unread.Recovery = null;
            Assert.False(unread.CanStopRestarting);
        }

        /// <summary>The rows the view binds to hear that the state has changed.</summary>
        [Fact]
        public void TheStateChangeIsAnnouncedToTheRow()
        {
            var entry = Service();
            Read(entry, ServiceControllerStatus.Running, 0, T0);

            var raised = new List<string?>();
            ((INotifyPropertyChanged)entry).PropertyChanged += (_, e) => raised.Add(e.PropertyName);
            Read(entry, ServiceControllerStatus.Stopped, RecoverySettings.ProcessAborted, T0.AddSeconds(2), crashCount: 1);

            Assert.Contains(nameof(ServiceEntry.IsRestartingByRecovery), raised);
            Assert.Contains(nameof(ServiceEntry.Health), raised);
            Assert.Contains(nameof(ServiceEntry.StatusText), raised);
            Assert.Contains(nameof(ServiceEntry.SortRank), raised);
        }

        [Fact]
        public void ARescanBringsTheRecoverySettingsAndTheStartTypeForward()
        {
            var onScreen = Service();
            var declared = new RecoverySettings(ImmutableArray.Create(new RecoveryAction(RecoveryActionKind.Restart, TimeSpan.FromSeconds(30))), TimeSpan.FromDays(1), false);

            onScreen.MergeMetadataFrom(new ServiceEntry("demo", "Demo", @"C:\bin\WinSW.exe", @"C:\svc\demo.xml")
            {
                StartType = ServiceStartMode.Automatic,
                DelayedAutoStart = true,
                Recovery = RestartAfterTenSeconds,
                DeclaredRecovery = declared,
            });

            Assert.Equal(ServiceStartMode.Automatic, onScreen.StartType);
            Assert.True(onScreen.DelayedAutoStart);
            Assert.Same(declared, onScreen.DeclaredRecovery);
            Assert.True(onScreen.RecoveryDiffers);
        }

        /// <summary>A fresh reading of the same settings is not a change: the panel is not repainted for it.</summary>
        [Fact]
        public void ARescanOfTheSameSettingsChangesNothing()
        {
            var onScreen = Service();
            var raised = new List<string?>();
            ((INotifyPropertyChanged)onScreen).PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            onScreen.Recovery = new RecoverySettings(ImmutableArray.Create(new RecoveryAction(RecoveryActionKind.Restart, TimeSpan.FromSeconds(10))), TimeSpan.FromDays(1), false);

            Assert.DoesNotContain(nameof(ServiceEntry.Recovery), raised);
        }

        /// <summary>A file with no failure actions leaves the service's own alone, so nothing differs from it.</summary>
        [Fact]
        public void AFileThatDeclaresNoRecoveryNeverDiffers()
        {
            var entry = Service();
            entry.DeclaredRecovery = null;

            Assert.False(entry.RecoveryDiffers);
        }

        private static ServiceEntry Service(RecoverySettings? recovery = null) =>
            new("demo", "Demo", @"C:\bin\WinSW.exe", @"C:\svc\demo.xml")
            {
                StartType = ServiceStartMode.Automatic,
                Recovery = recovery ?? RestartAfterTenSeconds,
            };

        /// <summary>One reading, as the dashboard hands it over: the state, then the crash count, then the time.</summary>
        private static void Read(ServiceEntry entry, ServiceControllerStatus status, int exitCode, DateTime now, int crashCount = 0)
        {
            ServiceDiscovery.ApplyStatus(entry, new ServiceSample { Queried = true, Status = status, LastExitCode = exitCode });
            entry.CrashCount = crashCount;
            entry.NoteRecovery(now);
        }
    }
}

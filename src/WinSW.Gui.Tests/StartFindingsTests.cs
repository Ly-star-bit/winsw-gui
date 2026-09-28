using System.Collections.Immutable;
using System.ServiceProcess;
using WinSW.Gui.Model;
using Xunit;

namespace WinSW.Gui.Tests
{
    /// <summary>
    /// What the environment check found after a failed start, as the Last stop card keeps it: for
    /// the start that failed, and not past the service's next run. The check runs off the UI
    /// thread, so what it finds can come back after the service has moved on.
    /// </summary>
    public class StartFindingsTests
    {
        private static readonly ImmutableArray<EnvironmentFinding> Found = ImmutableArray.Create(
            new EnvironmentFinding("M.Warn.OnUserPathOnly", "python", @"C:\Users\ops\AppData\Local\Programs\Python\Python312\python.exe"));

        [Fact]
        public void WhatTheCheckFoundIsKeptForTheStopped()
        {
            var entry = Stopped();

            entry.EndStartCheck(entry.BeginStartCheck(), Found);

            Assert.True(entry.HasStartFindings);
            Assert.Equal(Found, entry.StartFindings);
        }

        /// <summary>Started again while the check ran: what it found is about a start that is over.</summary>
        [Fact]
        public void ACheckThatComesBackAfterTheServiceStartedIsDropped()
        {
            var entry = Stopped();
            int run = entry.BeginStartCheck();

            entry.Status = ServiceControllerStatus.StartPending;
            entry.Status = ServiceControllerStatus.Stopped;
            entry.EndStartCheck(run, Found);

            Assert.False(entry.HasStartFindings);
        }

        [Fact]
        public void TheNextRunTakesThemAway()
        {
            var entry = Stopped();
            entry.EndStartCheck(entry.BeginStartCheck(), Found);

            entry.Status = ServiceControllerStatus.Running;

            Assert.False(entry.HasStartFindings);
            Assert.True(entry.StartFindings.IsEmpty);
        }

        /// <summary>Stopping, or being read again while stopped, is still the same stop.</summary>
        [Fact]
        public void TheStopTheyAreAboutKeepsThem()
        {
            var entry = Stopped();
            entry.EndStartCheck(entry.BeginStartCheck(), Found);

            entry.LastExitCode = 1067;
            entry.ForgetLastStop();

            Assert.True(entry.HasStartFindings);
        }

        [Fact]
        public void NothingFoundShowsNothing()
        {
            var entry = Stopped();

            entry.EndStartCheck(entry.BeginStartCheck(), ImmutableArray<EnvironmentFinding>.Empty);

            Assert.False(entry.HasStartFindings);
        }

        private static ServiceEntry Stopped() =>
            new("api", "api", "/bin/WinSW.exe", "/svc/api.xml") { Status = ServiceControllerStatus.Stopped, LastExitCode = 1 };
    }
}

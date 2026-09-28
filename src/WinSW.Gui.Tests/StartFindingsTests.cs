using System;
using System.Collections.Immutable;
using System.ServiceProcess;
using WinSW.Gui.Model;
using WinSW.Gui.Services;
using Xunit;

namespace WinSW.Gui.Tests
{
    /// <summary>
    /// What the environment check found after a failed start, as the Last stop card keeps it: for
    /// the start that failed and the restarts of a loop that repeat it, and not past a run that
    /// holds. The check runs off the UI thread, so what it finds can come back after the service
    /// has moved on.
    /// </summary>
    public class StartFindingsTests
    {
        private static readonly ImmutableArray<EnvironmentFinding> Found = ImmutableArray.Create(
            new EnvironmentFinding("M.Warn.OnUserPathOnly", "python", @"C:\Users\ops\AppData\Local\Programs\Python\Python312\python.exe"));

        private static readonly DateTime T0 = new(2026, 9, 27, 3, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void WhatTheCheckFoundIsKeptForTheStopped()
        {
            var entry = Stopped();

            entry.EndStartCheck(entry.BeginStartCheck(), Found);

            Assert.True(entry.HasStartFindings);
            Assert.Equal(Found, entry.StartFindings);
        }

        /// <summary>A run held while the check ran: what it found is about a start that is over.</summary>
        [Fact]
        public void ACheckThatComesBackAfterARunHeldIsDropped()
        {
            var entry = Stopped();
            int run = entry.BeginStartCheck();

            Run(entry, T0, CrashAnnouncer.RecoveredAfter);
            Reading(entry, ServiceControllerStatus.Stopped, T0.AddMinutes(5));
            entry.EndStartCheck(run, Found);

            Assert.False(entry.HasStartFindings);
        }

        /// <summary>
        /// Windows' recovery started it again while the check ran, and it failed again: the check is
        /// about that start as much as the one before, and is kept.
        /// </summary>
        [Fact]
        public void ACheckThatComesBackDuringALoopIsKept()
        {
            var entry = Stopped();
            int run = entry.BeginStartCheck();

            Reading(entry, ServiceControllerStatus.StartPending, T0);
            Reading(entry, ServiceControllerStatus.Running, T0.AddSeconds(2));
            entry.EndStartCheck(run, Found);

            Assert.True(entry.HasStartFindings);
        }

        /// <summary>
        /// A restart loop: every restart fails as the start that was checked did, so what the check
        /// found stays on the card through the loop rather than going at the first restart.
        /// </summary>
        [Fact]
        public void ARestartLoopKeepsThem()
        {
            var entry = Stopped();
            entry.EndStartCheck(entry.BeginStartCheck(), Found);

            for (int restart = 0; restart < 5; restart++)
            {
                var at = T0.AddSeconds(30 * restart);
                Reading(entry, ServiceControllerStatus.StartPending, at);
                Reading(entry, ServiceControllerStatus.Running, at.AddSeconds(2));
                Reading(entry, ServiceControllerStatus.Running, at.AddSeconds(4));
                Reading(entry, ServiceControllerStatus.Stopped, at.AddSeconds(6));

                Assert.True(entry.HasStartFindings);
            }
        }

        /// <summary>A run that holds as long as a recovery takes to be told was a start that worked.</summary>
        [Fact]
        public void ARunThatHoldsTakesThemAway()
        {
            var entry = Stopped();
            entry.EndStartCheck(entry.BeginStartCheck(), Found);

            Run(entry, T0, CrashAnnouncer.RecoveredAfter - TimeSpan.FromSeconds(2));
            Assert.True(entry.HasStartFindings);

            Reading(entry, ServiceControllerStatus.Running, T0 + CrashAnnouncer.RecoveredAfter);
            Assert.False(entry.HasStartFindings);
            Assert.True(entry.StartFindings.IsEmpty);
        }

        /// <summary>The clock starts over at each stop: two short runs are not one that held.</summary>
        [Fact]
        public void ShortRunsDoNotAddUp()
        {
            var entry = Stopped();
            entry.EndStartCheck(entry.BeginStartCheck(), Found);

            Run(entry, T0, TimeSpan.FromSeconds(90));
            Reading(entry, ServiceControllerStatus.Stopped, T0.AddSeconds(92));
            Run(entry, T0.AddSeconds(100), TimeSpan.FromSeconds(90));

            Assert.True(entry.HasStartFindings);
        }

        /// <summary>A reading that could not be made says nothing about the run, and does not start its clock again.</summary>
        [Fact]
        public void AReadingThatCouldNotBeMadeIsPassedOver()
        {
            var entry = Stopped();
            entry.EndStartCheck(entry.BeginStartCheck(), Found);

            Reading(entry, ServiceControllerStatus.Running, T0);
            Reading(entry, null, T0.AddSeconds(60));
            Reading(entry, ServiceControllerStatus.Running, T0 + CrashAnnouncer.RecoveredAfter);

            Assert.False(entry.HasStartFindings);
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

        /// <summary>One reading, as the dashboard's poll takes it.</summary>
        private static void Reading(ServiceEntry entry, ServiceControllerStatus? status, DateTime at)
        {
            entry.Status = status;
            entry.NoteRun(at);
        }

        /// <summary>Running from <paramref name="from"/> for <paramref name="length"/>, read every two seconds.</summary>
        private static void Run(ServiceEntry entry, DateTime from, TimeSpan length)
        {
            for (var at = from; at <= from + length; at += TimeSpan.FromSeconds(2))
            {
                Reading(entry, ServiceControllerStatus.Running, at);
            }
        }
    }
}

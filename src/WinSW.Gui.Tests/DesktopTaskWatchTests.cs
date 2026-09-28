using System;
using System.Collections.Generic;
using System.Linq;
using System.ServiceProcess;
using WinSW.Gui.Services;
using Xunit;

namespace WinSW.Gui.Tests
{
    /// <summary>
    /// A desktop task's stops, told by the services' rule: a robot seen running and then ready again
    /// with a failure for its last result has crashed, unless the console ended it; a loop is one
    /// count per window; a crashed robot running again is told once.
    /// </summary>
    public class DesktopTaskWatchTests
    {
        private static readonly DateTime T0 = new(2026, 9, 24, 9, 0, 0, DateTimeKind.Utc);

        private readonly DesktopTaskWatch watch = new();

        [Fact]
        public void ARobotThatEndsWithAFailureHasCrashed()
        {
            this.Running("robot", T0);

            var told = this.Ready("robot", T0.AddSeconds(30), 1);

            var notice = Assert.Single(told);
            Assert.Equal(new StopNotice("robot", StopNoticeKind.UnexpectedStop, 1, 1) { DesktopTask = true }, notice);
        }

        /// <summary>A robot finishing its work, or closed at the desktop, ends with 0; the keep-alive is for that.</summary>
        [Fact]
        public void ARobotThatEndsWith0IsNotTold()
        {
            this.Running("robot", T0);

            Assert.Empty(this.Ready("robot", T0.AddSeconds(30), 0));
        }

        [Fact]
        public void AStopTheConsoleAskedForIsNotTold()
        {
            this.Running("robot", T0);

            Assert.Empty(this.watch.Observe("robot", DesktopTaskState.Ready, 1, requested: true, T0.AddSeconds(4)));

            // Nor is it found again once the operation has let go.
            Assert.Empty(this.Ready("robot", T0.AddSeconds(8), 1));
        }

        /// <summary>Ended from the task scheduler, or disabled there: somebody meant it.</summary>
        [Fact]
        public void ARobotEndedOrDisabledInTheTaskSchedulerIsNotTold()
        {
            this.Running("robot", T0);
            Assert.Empty(this.Ready("robot", T0.AddSeconds(30), DesktopTaskWatch.Terminated));

            this.Running("other", T0);
            Assert.Empty(this.watch.Observe("other", DesktopTaskState.Disabled, 1, requested: false, T0.AddSeconds(30)));
        }

        /// <summary>
        /// Ready again with the result still saying it runs: the scheduler has not caught up. The
        /// reading is passed over, and the next one, with the real result, is the stop.
        /// </summary>
        [Fact]
        public void AReadingWhoseResultHasNotCaughtUpIsPassedOver()
        {
            this.Running("robot", T0);

            Assert.Empty(this.Ready("robot", T0.AddSeconds(4), DesktopTaskWatch.StillRunning));

            var notice = Assert.Single(this.Ready("robot", T0.AddSeconds(8), 3));
            Assert.Equal(StopNoticeKind.UnexpectedStop, notice.Kind);
            Assert.Equal(3, notice.ExitCode);
        }

        [Fact]
        public void ARobotFoundReadyAtTheFirstReadingHasNotStoppedWhileAnybodyWatched()
        {
            Assert.Empty(this.Ready("robot", T0, 1));
            Assert.Empty(this.Ready("robot", T0.AddSeconds(30), 1));
        }

        /// <summary>The keep-alive starts a crashing robot every minute: one notice, then one count per window.</summary>
        [Fact]
        public void ALoopIsOneNoticeAndThenACount()
        {
            // Five crashes a minute apart, the first at 0:30, then left stopped.
            var told = new List<StopNotice>();
            var at = T0;
            for (int i = 0; i < 5; i++)
            {
                told.AddRange(this.Running("robot", at));
                at = at.AddSeconds(30);
                told.AddRange(this.Ready("robot", at, 1));
                at = at.AddSeconds(30);
            }

            // The window ends at the first reading five minutes after the first crash.
            told.AddRange(this.Ready("robot", T0.AddSeconds(30) + CrashAnnouncer.Window, 1));

            Assert.Equal(new[] { StopNoticeKind.UnexpectedStop, StopNoticeKind.RepeatedStops }, told.Select(n => n.Kind));
            Assert.Equal(5, told[1].Count);
            Assert.All(told, n => Assert.True(n.DesktopTask));
        }

        [Fact]
        public void ACrashedRobotRunningAgainIsToldOnce()
        {
            this.Running("robot", T0);
            this.Ready("robot", T0.AddSeconds(30), 1);
            this.Running("robot", T0.AddSeconds(60));

            Assert.Empty(this.Running("robot", T0.AddSeconds(90)));
            var recovered = Assert.Single(this.Running("robot", T0.AddSeconds(60) + CrashAnnouncer.RecoveredAfter));
            Assert.Equal(StopNoticeKind.Recovered, recovered.Kind);
            Assert.True(recovered.DesktopTask);
            Assert.Empty(this.Running("robot", T0.AddMinutes(10)));
        }

        [Theory]
        [InlineData(DesktopTaskState.Running, 0x41301, ServiceControllerStatus.Running, false)]
        [InlineData(DesktopTaskState.Queued, 0, ServiceControllerStatus.StartPending, false)]
        [InlineData(DesktopTaskState.Ready, 1, ServiceControllerStatus.Stopped, false)]
        [InlineData(DesktopTaskState.Ready, 0, ServiceControllerStatus.Stopped, true)]
        [InlineData(DesktopTaskState.Ready, 0x41303, ServiceControllerStatus.Stopped, true)]
        [InlineData(DesktopTaskState.Ready, 0x41306, ServiceControllerStatus.Stopped, true)]
        [InlineData(DesktopTaskState.Disabled, 1, ServiceControllerStatus.Stopped, true)]
        public void EachStateReadsAsAServicesWould(DesktopTaskState state, int lastResult, ServiceControllerStatus status, bool quiet)
        {
            Assert.Equal(((ServiceControllerStatus?)status, quiet), DesktopTaskWatch.Reading(state, lastResult));
        }

        [Fact]
        public void AnUnknownStateSaysNothing()
        {
            Assert.Null(DesktopTaskWatch.Reading(DesktopTaskState.Unknown, 1).Status);
            Assert.Null(DesktopTaskWatch.Reading(DesktopTaskState.Ready, DesktopTaskWatch.StillRunning).Status);
        }

        private IReadOnlyList<StopNotice> Running(string name, DateTime at) =>
            this.watch.Observe(name, DesktopTaskState.Running, DesktopTaskWatch.StillRunning, requested: false, at);

        private IReadOnlyList<StopNotice> Ready(string name, DateTime at, int lastResult) =>
            this.watch.Observe(name, DesktopTaskState.Ready, lastResult, requested: false, at);
    }
}

using System;
using System.ServiceProcess;
using WinSW.Gui.Services;
using Xunit;

namespace WinSW.Gui.Tests
{
    /// <summary>
    /// 'winsw start' completes as soon as Windows reports the service running, and a program that
    /// cannot run falls over a second or two later. For the few seconds after a start from the
    /// console, a stop means the start did not hold, and the green notice is replaced with one that
    /// says so.
    /// </summary>
    public class StoppedAfterStartTests
    {
        private static readonly DateTime Started = new(2026, 9, 24, 10, 15, 0, DateTimeKind.Utc);

        [Theory]
        [InlineData(ServiceControllerStatus.Running)]
        [InlineData(ServiceControllerStatus.StartPending)]
        [InlineData(ServiceControllerStatus.StopPending)]
        public void AServiceThatHasNotStoppedIsStillWatched(ServiceControllerStatus status)
        {
            var watch = new StartWatch("api", Started.AddSeconds(10));

            Assert.Equal(StartWatchOutcome.Watching, watch.Observe(status, Started.AddSeconds(2)));
        }

        [Fact]
        public void StoppedWithinTheWatchIsAStartThatDidNotHold()
        {
            var watch = new StartWatch("api", Started.AddSeconds(10));

            Assert.Equal(StartWatchOutcome.StoppedAgain, watch.Observe(ServiceControllerStatus.Stopped, Started.AddSeconds(2)));
            Assert.Equal(StartWatchOutcome.StoppedAgain, watch.Observe(ServiceControllerStatus.Stopped, Started.AddSeconds(10)));
        }

        /// <summary>After the watch, a stop is an ordinary one: the crash notice speaks for it.</summary>
        [Fact]
        public void AStopAfterTheWatchIsNotTheStarts()
        {
            var watch = new StartWatch("api", Started.AddSeconds(10));

            Assert.Equal(StartWatchOutcome.Expired, watch.Observe(ServiceControllerStatus.Stopped, Started.AddSeconds(11)));
            Assert.Equal(StartWatchOutcome.Expired, watch.Observe(ServiceControllerStatus.Running, Started.AddSeconds(11)));
        }

        /// <summary>A service that could not be read at all has not been seen to stop.</summary>
        [Fact]
        public void AnUnreadServiceHasNotStopped()
        {
            var watch = new StartWatch("api", Started.AddSeconds(10));

            Assert.Equal(StartWatchOutcome.Watching, watch.Observe(null, Started.AddSeconds(2)));
        }
    }
}

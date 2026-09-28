using System;
using System.Collections.Generic;
using System.Linq;
using System.ServiceProcess;
using WinSW.Gui.Services;
using Xunit;

namespace WinSW.Gui.Tests
{
    /// <summary>
    /// Which stops reach the tray and the group chat, and in what words: the first at once, the
    /// rest as one count when the window ends, and one notice once a crashed service runs again.
    /// </summary>
    public class CrashAnnouncerTests
    {
        /// <summary>What the service control manager reports for a service process that died without saying so.</summary>
        private const int ProcessAborted = 1067;

        private static readonly DateTime T0 = new(2026, 9, 23, 10, 0, 0, DateTimeKind.Utc);

        private readonly CrashAnnouncer announcer = new();

        [Fact]
        public void TheFirstCrashIsToldAtOnce()
        {
            this.Running("api", T0);

            var told = this.Stopped("api", T0.AddSeconds(2), ProcessAborted);

            var notice = Assert.Single(told);
            Assert.Equal(new StopNotice("api", StopNoticeKind.UnexpectedStop, 1, ProcessAborted), notice);
            Assert.Equal(1, this.announcer.CountFor("api"));
        }

        /// <summary>A service found stopped at the first reading has not stopped while anybody watched.</summary>
        [Fact]
        public void TheStateAServiceIsFoundInIsNotAStop()
        {
            Assert.Empty(this.Stopped("api", T0, ProcessAborted));
            Assert.Empty(this.Stopped("api", T0.AddSeconds(2), ProcessAborted));
        }

        [Fact]
        public void StopsAfterTheFirstAreCountedAndNotToldAtOnce()
        {
            var told = this.Loop("api", T0, every: TimeSpan.FromSeconds(30), stops: 4);

            Assert.Equal(new[] { StopNoticeKind.UnexpectedStop }, told.Select(n => n.Kind));
            Assert.Equal(4, this.announcer.CountFor("api"));
        }

        /// <summary>
        /// The count goes out when the window ends, at a reading that finds nothing new: a loop
        /// that has stopped looping, or a service left stopped, is still reported.
        /// </summary>
        [Fact]
        public void TheCountGoesOutWhenTheWindowEndsWithoutAnotherStop()
        {
            // Four crashes, the last at 2:00, and the service left stopped after it.
            this.Loop("api", T0, every: TimeSpan.FromSeconds(30), stops: 4, lastExitCode: 3);
            var windowEnd = T0.AddSeconds(30) + CrashAnnouncer.Window;

            Assert.Empty(this.Stopped("api", windowEnd - TimeSpan.FromSeconds(1), 3));

            var told = this.Stopped("api", windowEnd, 3);
            Assert.Equal(new StopNotice("api", StopNoticeKind.RepeatedStops, 4, 3), Assert.Single(told));

            // Told once, and the window after it, with nothing in it, ends without a word.
            Assert.Empty(this.Stopped("api", windowEnd + TimeSpan.FromSeconds(2), 3));
            Assert.Empty(this.Stopped("api", windowEnd + TimeSpan.FromMinutes(6), 3));
            Assert.Equal(0, this.announcer.CountFor("api"));
        }

        /// <summary>One stop in five minutes was told in full when it happened; there is no count to add.</summary>
        [Fact]
        public void AWindowWithOnlyTheFirstStopEndsQuietly()
        {
            this.Running("api", T0);
            this.Stopped("api", T0.AddSeconds(2), ProcessAborted);

            Assert.Empty(this.Stopped("api", T0 + CrashAnnouncer.Window + TimeSpan.FromSeconds(2), ProcessAborted));
            Assert.Equal(0, this.announcer.CountFor("api"));

            // The run of notices is over: the next crash is told at once again.
            this.Running("api", T0.AddMinutes(20));
            var told = this.Stopped("api", T0.AddMinutes(20).AddSeconds(2), ProcessAborted);
            Assert.Equal(StopNoticeKind.UnexpectedStop, Assert.Single(told).Kind);
        }

        /// <summary>
        /// A loop that goes on is one notice at its start and then one count per window, each
        /// with the stops of its own window: not a fresh "stopped unexpectedly" every window.
        /// </summary>
        [Fact]
        public void ALoopThatGoesOnIsToldOnceAWindow()
        {
            // A stop every 30 seconds for twelve minutes and a half: 25 stops.
            var told = this.Loop("api", T0, every: TimeSpan.FromSeconds(30), stops: 25);

            Assert.Equal(
                new[] { StopNoticeKind.UnexpectedStop, StopNoticeKind.RepeatedStops, StopNoticeKind.RepeatedStops },
                told.Select(n => n.Kind));

            // The first window opens at the first crash, 0:30, and holds the ten up to 5:00, the
            // told one included; the second opens at the reading that closed the first, 5:30,
            // and holds the ten up to 10:00. The five after that are still being counted.
            Assert.Equal(1, told[0].Count);
            Assert.Equal(10, told[1].Count);
            Assert.Equal(10, told[2].Count);
        }

        /// <summary>
        /// A program that ends by itself with exit code 0 has stopped, not crashed: told once the
        /// next reading finds it still stopped.
        /// </summary>
        [Fact]
        public void AStopWithExitCodeZeroIsToldAsAStop()
        {
            this.Running("api", T0);

            Assert.Empty(this.Stopped("api", T0.AddSeconds(2), 0));
            var told = this.Stopped("api", T0.AddSeconds(4), 0);

            Assert.Equal(new StopNotice("api", StopNoticeKind.CleanStop, 1, 0), Assert.Single(told));
            Assert.Equal(0, this.announcer.CountFor("api"));

            // Once.
            Assert.Empty(this.Stopped("api", T0.AddSeconds(6), 0));
        }

        /// <summary>
        /// The scheduled restart at night: 'winsw restart' leaves the service stopped for under a
        /// second, and a reading that lands there without having seen it stopping must not tell the
        /// group that it stopped. The next reading finds it on its way back.
        /// </summary>
        [Theory]
        [InlineData(ServiceControllerStatus.StartPending)]
        [InlineData(ServiceControllerStatus.Running)]
        public void ARestartCaughtBetweenItsStopAndItsStartIsNotTold(ServiceControllerStatus next)
        {
            this.Running("api", T0);
            Assert.Empty(this.Stopped("api", T0.AddSeconds(2), 0));

            Assert.Empty(this.announcer.Observe("api", next, 0, held: false, T0.AddSeconds(4)));
            Assert.Empty(this.Running("api", T0.AddSeconds(6)));
            Assert.Empty(this.Running("api", T0.AddMinutes(10)));
            Assert.Equal(0, this.announcer.RecentStopsFor("api"));
        }

        /// <summary>A reading that could not be made in between says nothing either way: the stop waits for the next.</summary>
        [Fact]
        public void AReadingThatCouldNotBeMadeKeepsACleanStopWaiting()
        {
            this.Running("api", T0);
            Assert.Empty(this.Stopped("api", T0.AddSeconds(2), 0));
            Assert.Empty(this.announcer.Observe("api", null, 0, held: false, T0.AddSeconds(4)));

            var told = this.Stopped("api", T0.AddSeconds(6), 0);

            Assert.Equal(StopNoticeKind.CleanStop, Assert.Single(told).Kind);
        }

        /// <summary>
        /// Windows' recovery does not act on a stop with exit code 0, so nothing is restarting the
        /// service in a loop: a crash after someone has started it again is told at once, not
        /// held back for a count that would blame the recovery.
        /// </summary>
        [Fact]
        public void ACrashSoonAfterAStopWithExitCodeZeroIsToldAtOnce()
        {
            this.Running("api", T0);
            this.Stopped("api", T0.AddSeconds(2), 0);
            Assert.Equal(StopNoticeKind.CleanStop, Assert.Single(this.Stopped("api", T0.AddSeconds(4), 0)).Kind);
            this.Running("api", T0.AddSeconds(12));

            var told = this.Stopped("api", T0.AddSeconds(90), ProcessAborted);

            Assert.Equal(new StopNotice("api", StopNoticeKind.UnexpectedStop, 1, ProcessAborted), Assert.Single(told));
            Assert.Equal(1, this.announcer.CountFor("api"));
        }

        /// <summary>Inside a loop already told, a stop with exit code 0 is one more for the count.</summary>
        [Fact]
        public void AStopWithExitCodeZeroInALoopIsCounted()
        {
            // Crashes at 0:30, 1:00 and 1:30; started again, then a stop with exit code 0.
            this.Loop("api", T0, every: TimeSpan.FromSeconds(30), stops: 3);
            this.Running("api", T0.AddSeconds(100));
            Assert.Empty(this.Stopped("api", T0.AddSeconds(110), 0));

            var told = this.Stopped("api", T0.AddSeconds(30) + CrashAnnouncer.Window, 0);

            Assert.Equal(new StopNotice("api", StopNoticeKind.RepeatedStops, 4, 0), Assert.Single(told));
        }

        /// <summary>Nothing crashed, so nothing recovers.</summary>
        [Fact]
        public void ACleanStopIsNotFollowedByARecoveredNotice()
        {
            this.Running("api", T0);
            this.Stopped("api", T0.AddSeconds(2), 0);
            this.Stopped("api", T0.AddSeconds(4), 0);
            this.Running("api", T0.AddSeconds(30));

            Assert.Empty(this.Running("api", T0.AddMinutes(10)));
        }

        [Fact]
        public void ACrashedServiceIsToldToHaveRecoveredOnceItHasRunForTwoMinutes()
        {
            this.Running("api", T0);
            this.Stopped("api", T0.AddSeconds(2), ProcessAborted);

            // Windows' recovery starts it again ten seconds later.
            var back = T0.AddSeconds(12);
            Assert.Empty(this.Running("api", back));
            Assert.Empty(this.Running("api", back + CrashAnnouncer.RecoveredAfter - TimeSpan.FromSeconds(1)));

            var told = this.Running("api", back + CrashAnnouncer.RecoveredAfter);
            Assert.Equal(new StopNotice("api", StopNoticeKind.Recovered, 0, ProcessAborted), Assert.Single(told));

            // Once.
            Assert.Empty(this.Running("api", back + CrashAnnouncer.RecoveredAfter + TimeSpan.FromMinutes(10)));
        }

        /// <summary>Running between two crashes of a loop is not running again: the clock starts over at each stop.</summary>
        [Fact]
        public void TheRecoveredClockStartsOverAtEachStop()
        {
            this.Running("api", T0);
            this.Stopped("api", T0.AddSeconds(2), ProcessAborted);
            this.Running("api", T0.AddSeconds(12));
            this.Stopped("api", T0.AddSeconds(90), ProcessAborted);
            this.Running("api", T0.AddSeconds(100));

            // Two minutes after the first start again, but only half a minute after the second.
            Assert.Empty(this.Running("api", T0.AddSeconds(12) + CrashAnnouncer.RecoveredAfter));
            Assert.Contains(this.Running("api", T0.AddSeconds(100) + CrashAnnouncer.RecoveredAfter), n => n.Kind == StopNoticeKind.Recovered);
        }

        /// <summary>
        /// A service that recovers halfway through a window with stops not yet told has them told
        /// first: "running again" and then a count saying it keeps being restarted would read as
        /// the opposite of what happened. Nothing is left over for the window's end.
        /// </summary>
        [Fact]
        public void StopsNotYetToldGoOutBeforeTheRecoveredNotice()
        {
            // Crashes at 0:20, 0:40 and 1:00; the first is told.
            var told = this.Loop("api", T0, every: TimeSpan.FromSeconds(20), stops: 3);
            Assert.Single(told);

            var back = T0.AddSeconds(70);
            Assert.Empty(this.Running("api", back));
            var recovered = this.Running("api", back + CrashAnnouncer.RecoveredAfter);

            Assert.Equal(
                new[] { new StopNotice("api", StopNoticeKind.RepeatedStops, 3, ProcessAborted), new StopNotice("api", StopNoticeKind.Recovered, 0, ProcessAborted) },
                recovered);
            Assert.Empty(this.Running("api", T0.AddSeconds(20) + CrashAnnouncer.Window));
        }

        /// <summary>
        /// A service seen stopping on the way was asked to stop — services.msc, a script, the
        /// scheduled restart — and a program that crashes does not pass through stopping.
        /// </summary>
        [Fact]
        public void AServiceSeenStoppingWasAskedToStop()
        {
            this.Running("api", T0);
            Assert.Empty(this.announcer.Observe("api", ServiceControllerStatus.StopPending, 0, held: false, T0.AddSeconds(2)));
            Assert.Empty(this.Stopped("api", T0.AddSeconds(4), 0));
        }

        /// <summary>A reading that could not be made in between does not hide a crash.</summary>
        [Fact]
        public void AReadingThatCouldNotBeMadeIsPassedOver()
        {
            this.Running("api", T0);
            Assert.Empty(this.announcer.Observe("api", null, 0, held: false, T0.AddSeconds(2)));

            Assert.Equal(StopNoticeKind.UnexpectedStop, Assert.Single(this.Stopped("api", T0.AddSeconds(4), ProcessAborted)).Kind);
        }

        /// <summary>
        /// The console's own stop is neither told nor counted, and it is not found again as a new
        /// stop once the operation has let go of the service.
        /// </summary>
        [Fact]
        public void AStopTheConsoleIsCausingIsNotedButNotTold()
        {
            this.Running("api", T0);

            Assert.Empty(this.Stopped("api", T0.AddSeconds(2), 0, held: true));
            Assert.Empty(this.Stopped("api", T0.AddSeconds(4), 0));
            Assert.Equal(0, this.announcer.CountFor("api"));
        }

        /// <summary>An operation on one service holds that service: a crash of another one meanwhile is told.</summary>
        [Fact]
        public void AnotherServicesCrashIsToldWhileOneIsHeld()
        {
            this.Running("api", T0);
            this.Running("worker", T0);

            Assert.Empty(this.Stopped("api", T0.AddSeconds(2), 0, held: true));
            var told = this.Stopped("worker", T0.AddSeconds(2), ProcessAborted);

            Assert.Equal(new StopNotice("worker", StopNoticeKind.UnexpectedStop, 1, ProcessAborted), Assert.Single(told));
        }

        /// <summary>
        /// A console stop in the middle of a loop is not one more for the count; the count still
        /// goes out for the crashes before it.
        /// </summary>
        [Fact]
        public void AConsoleStopInALoopIsNotCounted()
        {
            // Crashes at 0:30, 1:00 and 1:30; then the service is started, and stopped from the
            // console.
            this.Loop("api", T0, every: TimeSpan.FromSeconds(30), stops: 3);
            this.Running("api", T0.AddSeconds(100));
            Assert.Empty(this.Stopped("api", T0.AddSeconds(110), 0, held: true));

            var told = this.Stopped("api", T0.AddSeconds(30) + CrashAnnouncer.Window, 0);

            Assert.Equal(new StopNotice("api", StopNoticeKind.RepeatedStops, 3, ProcessAborted), Assert.Single(told));
        }

        /// <summary>
        /// A crashed service the console starts again is still told to have recovered: the start
        /// happens under an operation, and what it does to the state is noted all the same.
        /// </summary>
        [Fact]
        public void AServiceStartedAgainByTheConsoleStillRecovers()
        {
            this.Running("api", T0);
            this.Stopped("api", T0.AddSeconds(2), ProcessAborted);

            var started = T0.AddMinutes(30);
            Assert.Empty(this.Running("api", started, held: true));

            var told = this.Running("api", started + CrashAnnouncer.RecoveredAfter);
            Assert.Equal(StopNoticeKind.Recovered, Assert.Single(told).Kind);
        }

        /// <summary>A stop on the reading that ends a quiet window is the first of a new run, and is told at once.</summary>
        [Fact]
        public void AStopAsAQuietWindowEndsIsToldAtOnce()
        {
            this.Running("api", T0);
            this.Stopped("api", T0.AddSeconds(2), ProcessAborted);
            this.Running("api", T0.AddSeconds(12));
            this.Running("api", T0.AddSeconds(90));

            var told = this.Stopped("api", T0.AddSeconds(2) + CrashAnnouncer.Window, ProcessAborted);

            Assert.Equal(StopNoticeKind.UnexpectedStop, Assert.Single(told).Kind);
            Assert.Equal(1, this.announcer.CountFor("api"));
        }

        /// <summary>A stop on the reading that ends a window with a count goes into the next window's count.</summary>
        [Fact]
        public void AStopAsACountGoesOutIsCountedInTheNextWindow()
        {
            // Crashes at 0:30 and 1:00; started again shortly before the window ends.
            this.Loop("api", T0, every: TimeSpan.FromSeconds(30), stops: 2);
            var windowEnd = T0.AddSeconds(30) + CrashAnnouncer.Window;
            this.Running("api", windowEnd - TimeSpan.FromSeconds(10));

            var told = this.Stopped("api", windowEnd, ProcessAborted);

            Assert.Equal(new StopNotice("api", StopNoticeKind.RepeatedStops, 2, ProcessAborted), Assert.Single(told));
            Assert.Equal(1, this.announcer.CountFor("api"));
        }

        [Fact]
        public void NamesAreMatchedWhateverTheCase()
        {
            this.Running("MyApi", T0);

            var told = this.Stopped("myapi", T0.AddSeconds(2), ProcessAborted);

            Assert.Equal("myapi", Assert.Single(told).ServiceName);
            Assert.Equal(1, this.announcer.CountFor("MYAPI"));
        }

        /// <summary>A service dropped from the list and found again starts with no history: its first reading is not a stop.</summary>
        [Fact]
        public void AForgottenServiceStartsOver()
        {
            this.Running("api", T0);
            this.announcer.Forget("api");

            Assert.Empty(this.Stopped("api", T0.AddSeconds(2), ProcessAborted));
            Assert.Equal(0, this.announcer.CountFor("api"));
        }

        // Recent stops, for "needs attention" -------------------------------------------------

        [Fact]
        public void ACrashIsRecentUntilAWholeWindowHasPassedWithoutAnother()
        {
            this.Running("api", T0);
            this.Stopped("api", T0.AddSeconds(2), ProcessAborted);
            this.Running("api", T0.AddSeconds(12));

            Assert.Equal(1, this.announcer.RecentStopsFor("api"));

            this.Running("api", T0.AddSeconds(2) + CrashAnnouncer.Window);
            Assert.Equal(0, this.announcer.RecentStopsFor("api"));
        }

        /// <summary>
        /// A loop running between two of its stops when its window's count goes out is still a loop:
        /// the stops of the window before stay counted until the one that follows has had its own.
        /// </summary>
        [Fact]
        public void ALoopStaysRecentAcrossTheWindowsEnd()
        {
            // Ten crashes, the first at 0:30 and the last at 5:00, and started again at 5:30.
            this.Loop("api", T0, every: TimeSpan.FromSeconds(30), stops: 10);
            var told = this.Running("api", T0.AddSeconds(30) + CrashAnnouncer.Window);

            Assert.Equal(StopNoticeKind.RepeatedStops, Assert.Single(told).Kind);
            Assert.Equal(0, this.announcer.CountFor("api"));
            Assert.Equal(10, this.announcer.RecentStopsFor("api"));

            // One more in the window that followed on: counted with the ten.
            this.Stopped("api", T0.AddMinutes(6), ProcessAborted);
            Assert.Equal(11, this.announcer.RecentStopsFor("api"));

            // A whole window with nothing new in it after that ends the run.
            this.Running("api", T0.AddMinutes(6).AddSeconds(10));
            this.Running("api", T0.AddMinutes(11).AddSeconds(30));
            this.Running("api", T0.AddMinutes(16).AddSeconds(30));
            Assert.Equal(0, this.announcer.RecentStopsFor("api"));
        }

        /// <summary>Neither a stop with exit code 0 on its own nor a stop the console caused is one to look at.</summary>
        [Fact]
        public void ACleanStopOrAHeldOneIsNotRecent()
        {
            this.Running("api", T0);
            this.Stopped("api", T0.AddSeconds(2), 0);
            this.Stopped("api", T0.AddSeconds(4), 0);
            Assert.Equal(0, this.announcer.RecentStopsFor("api"));

            this.Running("web", T0);
            this.Stopped("web", T0.AddSeconds(2), ProcessAborted, held: true);
            Assert.Equal(0, this.announcer.RecentStopsFor("web"));
        }

        private IReadOnlyList<StopNotice> Running(string name, DateTime at, bool held = false) =>
            this.announcer.Observe(name, ServiceControllerStatus.Running, 0, held, at);

        private IReadOnlyList<StopNotice> Stopped(string name, DateTime at, int exitCode, bool held = false) =>
            this.announcer.Observe(name, ServiceControllerStatus.Stopped, exitCode, held, at);

        /// <summary>
        /// A restart loop, read every two seconds: running at <paramref name="start"/>, a crash
        /// every <paramref name="every"/> after it, each started again ten seconds later but the
        /// last, which leaves the service stopped at <paramref name="start"/> +
        /// <paramref name="every"/> × <paramref name="stops"/>. Returns everything told on the way.
        /// </summary>
        private List<StopNotice> Loop(string name, DateTime start, TimeSpan every, int stops, int lastExitCode = ProcessAborted)
        {
            var told = new List<StopNotice>();
            var downFor = TimeSpan.FromSeconds(10);
            var end = start + (every * stops);

            for (var at = start; at <= end; at += TimeSpan.FromSeconds(2))
            {
                var since = at - start;
                int crashes = (int)(since.Ticks / every.Ticks);
                bool down = crashes >= 1 && since - (every * crashes) < downFor;
                told.AddRange(down
                    ? this.Stopped(name, at, crashes == stops ? lastExitCode : ProcessAborted)
                    : this.Running(name, at));
            }

            return told;
        }
    }
}

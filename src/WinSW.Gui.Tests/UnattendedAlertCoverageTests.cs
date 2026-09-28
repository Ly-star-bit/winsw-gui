using WinSW.Gui.Services;
using Xunit;

namespace WinSW.Gui.Tests
{
    /// <summary>
    /// Which of the console's notices the unattended alert posts itself, so that the console,
    /// while the task posts to its webhook, leaves them to it: only what the service control
    /// manager records as a failure, and the count the task keeps of those.
    /// </summary>
    public class UnattendedAlertCoverageTests
    {
        [Fact]
        public void ACrashIsTheTasks()
        {
            Assert.True(UnattendedAlert.Covers(new StopNotice("api", StopNoticeKind.UnexpectedStop, 1, 1067)));
        }

        /// <summary>The task holds back a loop's failures and says how many in its next message.</summary>
        [Fact]
        public void ALoopsCountIsTheTasksWhenItsStopsWereFailures()
        {
            Assert.True(UnattendedAlert.Covers(new StopNotice("api", StopNoticeKind.RepeatedStops, 6, 1067)));
            Assert.False(UnattendedAlert.Covers(new StopNotice("api", StopNoticeKind.RepeatedStops, 6, 0)));
        }

        /// <summary>Windows records no failure for an exit with 0, and the task never runs for it.</summary>
        [Fact]
        public void ACleanStopIsTheConsoles()
        {
            Assert.False(UnattendedAlert.Covers(new StopNotice("api", StopNoticeKind.CleanStop, 1, 0)));
        }

        /// <summary>The task sees failures only, never a service running again.</summary>
        [Fact]
        public void ARecoveryIsTheConsoles()
        {
            Assert.False(UnattendedAlert.Covers(new StopNotice("api", StopNoticeKind.Recovered, 0, 1067)));
        }

        /// <summary>A desktop task is the task scheduler's, and puts nothing in the System log.</summary>
        [Fact]
        public void NothingAboutADesktopTaskIsTheTasks()
        {
            Assert.False(UnattendedAlert.Covers(new StopNotice("robot", StopNoticeKind.UnexpectedStop, 1, 1) { DesktopTask = true }));
            Assert.False(UnattendedAlert.Covers(new StopNotice("robot", StopNoticeKind.RepeatedStops, 4, 1) { DesktopTask = true }));
        }
    }
}

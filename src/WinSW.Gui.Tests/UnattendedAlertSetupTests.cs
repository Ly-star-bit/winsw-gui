using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using WinSW.Gui.Services;
using Xunit;

namespace WinSW.Gui.Tests
{
    /// <summary>
    /// The elevated step's wait for a run of the task to let go of the old copy. What it is
    /// waiting for — a running executable that Windows will not delete — and the folder
    /// permissions need Windows and are not tested here; the retry rule and the schedule are.
    /// </summary>
    public class UnattendedAlertSetupTests
    {
        private static readonly TimeSpan[] ThreeDelays = { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3) };

        /// <summary>
        /// Deleting a running executable is access denied, which .NET throws as an
        /// UnauthorizedAccessException: that is the failure a run of the task causes, and it
        /// used to fail the step at once.
        /// </summary>
        [Fact]
        public void AccessDeniedIsWaitedOut()
        {
            int calls = 0;
            var waited = new List<TimeSpan>();

            UnattendedAlertSetup.RetryWhileInUse(
                () =>
                {
                    if (++calls < 3)
                    {
                        throw new UnauthorizedAccessException("Access to the path 'WinSW.Gui.exe' is denied.");
                    }
                },
                ThreeDelays,
                waited.Add);

            Assert.Equal(3, calls);
            Assert.Equal(ThreeDelays.Take(2), waited);
        }

        [Fact]
        public void AFileOpenElsewhereIsWaitedOutToo()
        {
            int calls = 0;

            UnattendedAlertSetup.RetryWhileInUse(
                () =>
                {
                    if (++calls == 1)
                    {
                        throw new IOException("The process cannot access the file because it is being used by another process.");
                    }
                },
                ThreeDelays,
                _ => { });

            Assert.Equal(2, calls);
        }

        /// <summary>Once every delay has been waited, the failure goes through to the step, which reports it.</summary>
        [Fact]
        public void AFolderStillInUseAfterTheLastDelayFails()
        {
            int calls = 0;
            var waited = new List<TimeSpan>();

            Assert.Throws<UnauthorizedAccessException>(() => UnattendedAlertSetup.RetryWhileInUse(
                () =>
                {
                    calls++;
                    throw new UnauthorizedAccessException("denied");
                },
                ThreeDelays,
                waited.Add));

            Assert.Equal(ThreeDelays.Length + 1, calls);
            Assert.Equal(ThreeDelays, waited);
        }

        [Fact]
        public void AnyOtherFailureIsNotRetried()
        {
            int calls = 0;

            Assert.Throws<InvalidOperationException>(() => UnattendedAlertSetup.RetryWhileInUse(
                () =>
                {
                    calls++;
                    throw new InvalidOperationException("not a file in use");
                },
                ThreeDelays,
                _ => throw new Xunit.Sdk.XunitException("nothing is to be waited for")));

            Assert.Equal(1, calls);
        }

        /// <summary>
        /// Longer than a run of the task holds the copy — three sends that each time out, with
        /// the run's retry delays between them — and still inside the console's wait for the
        /// step, with room for copying the executable and for schtasks.
        /// </summary>
        [Fact]
        public void TheWaitOutlastsARunAndLeavesRoomForTheRestOfTheStep()
        {
            var wait = TimeSpan.FromTicks(UnattendedAlertSetup.InUseDelays.Sum(d => d.Ticks));
            var run = TimeSpan.FromTicks(UnattendedAlertRun.RetryDelays.Sum(d => d.Ticks))
                + (AlertWebhook.SendTimeout * (UnattendedAlertRun.RetryDelays.Length + 1));

            Assert.True(wait > run, $"waits {wait}, a run takes up to {run}");
            Assert.True(wait + TimeSpan.FromMinutes(1) <= UnattendedAlert.ElevatedTimeout, $"waits {wait} of {UnattendedAlert.ElevatedTimeout}");
        }

        [Fact]
        public void AFolderNotInUseIsDeletedWithEverythingInIt()
        {
            string folder = Path.Combine(Path.GetTempPath(), "winsw-gui-tests", Guid.NewGuid().ToString("N"), "Alert");
            Directory.CreateDirectory(Path.Combine(folder, "runtime", "WinSW.Gui"));
            File.WriteAllText(Path.Combine(folder, "webhook.dat"), "sealed");
            File.WriteAllText(Path.Combine(folder, "runtime", "WinSW.Gui", "native.dll"), "library");

            try
            {
                UnattendedAlertSetup.DeleteFolder(folder);

                Assert.False(Directory.Exists(folder));
            }
            finally
            {
                Directory.Delete(Path.GetDirectoryName(folder)!, recursive: true);
            }
        }

        [Fact]
        public void AFolderThatIsNotThereIsNothingToDelete()
        {
            string folder = Path.Combine(Path.GetTempPath(), "winsw-gui-tests", Guid.NewGuid().ToString("N"));

            UnattendedAlertSetup.DeleteFolder(folder);

            Assert.False(Directory.Exists(folder));
        }
    }
}

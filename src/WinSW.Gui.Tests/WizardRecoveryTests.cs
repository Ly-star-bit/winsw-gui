using System;
using System.Linq;
using WinSW.Gui.Model;
using WinSW.Gui.ViewModels;
using Xunit;

namespace WinSW.Gui.Tests
{
    /// <summary>
    /// What a new service does when its program keeps failing. The service control manager
    /// repeats the last recovery action for every failure after the list runs out, so a lone
    /// ten-second restart started a program that could never run some 360 times an hour. The
    /// wizard now backs off: the chosen delay, then a minute, then five minutes.
    /// </summary>
    public class WizardRecoveryTests
    {
        [Fact]
        public void TheDefaultBacksOffToFiveMinutesAndResetsAfterAnHour()
        {
            var model = new WizardViewModel { TargetPath = @"C:\apps\server.exe" }.BuildModel();

            Assert.Equal(new[] { "restart", "restart", "restart" }, model.FailureActions.Select(a => a.Action));
            Assert.Equal(new[] { "10 sec", "1 min", "5 min" }, model.FailureActions.Select(a => a.Delay));
            Assert.Equal("1 hour", model.ResetFailureAfter);
        }

        /// <summary>
        /// About twelve starts in the first hour of a program that can never run, rather than
        /// the 360 a lone ten-second restart comes to.
        /// </summary>
        [Fact]
        public void AHopelessProgramIsStartedAboutTwelveTimesAnHour()
        {
            var waits = WizardViewModel.RestartDelays("10 sec").Select(Wait).ToList();

            // The last wait repeats once the list runs out; the program is taken to fail at once.
            int starts = 0;
            double elapsed = 0;
            while (elapsed + waits[Math.Min(starts, waits.Count - 1)] <= 3600)
            {
                elapsed += waits[Math.Min(starts, waits.Count - 1)];
                starts++;
            }

            Assert.InRange(starts, 12, 14);
        }

        [Theory]
        [InlineData("30 sec", new[] { "30 sec", "1 min", "5 min" })]
        [InlineData("  30 sec ", new[] { "30 sec", "1 min", "5 min" })]
        [InlineData("500 ms", new[] { "500 ms", "1 min", "5 min" })]
        [InlineData("1 min", new[] { "1 min", "5 min" })]
        [InlineData("2 min", new[] { "2 min", "5 min" })]
        [InlineData("5 min", new[] { "5 min" })]
        [InlineData("10 min", new[] { "10 min" })]
        [InlineData("1 hour", new[] { "1 hour" })]
        public void TheWaitsStartFromTheChosenDelayAndOnlyGrow(string chosen, string[] expected)
        {
            Assert.Equal(expected, WizardViewModel.RestartDelays(chosen));
        }

        /// <summary>
        /// A delay emptied in the editor is the ten seconds the wizard starts with; one that is
        /// not yet a duration is kept as typed, for validation to point at.
        /// </summary>
        [Theory]
        [InlineData(null, new[] { "10 sec", "1 min", "5 min" })]
        [InlineData("", new[] { "10 sec", "1 min", "5 min" })]
        [InlineData(" ", new[] { "10 sec", "1 min", "5 min" })]
        [InlineData("soon", new[] { "soon", "1 min", "5 min" })]
        public void ABlankOrUnreadableDelayStillGivesTheLadder(string? chosen, string[] expected)
        {
            Assert.Equal(expected, WizardViewModel.RestartDelays(chosen));
        }

        /// <summary>Every wait the wizard writes is one the wrapper can read.</summary>
        [Theory]
        [InlineData("10 sec")]
        [InlineData("2 min")]
        [InlineData("1 hour")]
        public void EveryWaitWrittenParses(string chosen)
        {
            var model = new WizardViewModel { TargetPath = @"C:\apps\server.exe", RestartDelay = chosen }.BuildModel();

            Assert.All(model.FailureActions, a => Assert.True(ServiceConfigModel.TryParseTime(a.Delay!), a.Delay));
            Assert.True(ServiceConfigModel.TryParseTime(model.ResetFailureAfter!));
            Assert.Equal(chosen, model.FailureActions[0].Delay);
        }

        [Fact]
        public void NoRecoveryWhenRestartingIsTurnedOff()
        {
            var model = new WizardViewModel { TargetPath = @"C:\apps\server.exe", RestartOnFailure = false }.BuildModel();

            Assert.Empty(model.FailureActions);
            Assert.Null(model.ResetFailureAfter);
        }

        /// <summary>A desktop task is never seen by the service control manager; its trigger brings it back instead.</summary>
        [Fact]
        public void ADesktopTaskGetsNoRecoveryActions()
        {
            var model = new WizardViewModel { TargetPath = @"C:\apps\server.exe", DesktopTask = true }.BuildModel();

            Assert.Empty(model.FailureActions);
            Assert.Null(model.ResetFailureAfter);
        }

        /// <summary>The ladder is what goes into the file, in order, each row a restart.</summary>
        [Fact]
        public void TheLadderIsWrittenAsOnFailureRows()
        {
            var wizard = new WizardViewModel { TargetPath = @"C:\apps\server.exe", ServiceId = "demo", RestartDelay = "30 sec" };

            var written = ServiceConfigModel.FromXml(wizard.BuildModel().ToXmlString(), null);

            Assert.Equal(new[] { ("restart", "30 sec"), ("restart", "1 min"), ("restart", "5 min") }, written.FailureActions.Select(a => (a.Action, a.Delay)));
            Assert.Equal("1 hour", written.ResetFailureAfter);
        }

        private static double Wait(string delay)
        {
            Assert.True(ServiceConfigModel.TryParseTime(delay, out var wait));
            return wait.TotalSeconds;
        }
    }
}

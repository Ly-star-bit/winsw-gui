using System;
using System.Linq;
using System.Xml.Linq;
using WinSW.Gui.Services;
using Xunit;

namespace WinSW.Gui.Tests
{
    /// <summary>
    /// The task definition a scheduled restart registers, and reading it back.
    /// </summary>
    public class ScheduledRestartTests
    {
        private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

        private const string Wrapper = @"C:\ProgramData\WinSW\bin\WinSW.exe";

        private const string Config = @"C:\ProgramData\WinSW\demo\demo.xml";

        [Fact]
        public void ADailyRestartRunsAsSystemAtTheChosenTime()
        {
            var task = Build(new RestartSchedule(null, new TimeSpan(3, 0, 0)));

            var trigger = task.Descendants(Ns + "CalendarTrigger").Single();
            Assert.Equal("2026-09-22T03:00:00", trigger.Element(Ns + "StartBoundary")!.Value);
            Assert.Equal("1", trigger.Element(Ns + "ScheduleByDay")!.Element(Ns + "DaysInterval")!.Value);
            Assert.Null(trigger.Element(Ns + "ScheduleByWeek"));

            Assert.Equal("S-1-5-18", task.Descendants(Ns + "UserId").Single().Value);
            Assert.Equal(ScheduledRestart.SecurityDescriptor, task.Descendants(Ns + "SecurityDescriptor").Single().Value);
            Assert.Equal("false", task.Descendants(Ns + "StartWhenAvailable").Single().Value);
            Assert.Equal(@"%SystemRoot%\System32\cmd.exe", task.Descendants(Ns + "Command").Single().Value);
        }

        [Fact]
        public void AWeeklyRestartNamesItsDay()
        {
            var task = Build(new RestartSchedule(DayOfWeek.Sunday, new TimeSpan(4, 30, 0)));

            var days = task.Descendants(Ns + "DaysOfWeek").Single().Elements().ToList();
            Assert.Equal("Sunday", Assert.Single(days).Name.LocalName);
            Assert.Null(task.Descendants(Ns + "ScheduleByDay").SingleOrDefault());
        }

        /// <summary>The task scheduler's schema is a sequence: the trigger's children come in its order.</summary>
        [Fact]
        public void TheTriggerIsWrittenInTheSchemasOrder()
        {
            var trigger = Build(new RestartSchedule(null, TimeSpan.FromHours(3))).Descendants(Ns + "CalendarTrigger").Single();

            Assert.Equal(
                new[] { "StartBoundary", "Enabled", "ScheduleByDay" },
                trigger.Elements().Select(e => e.Name.LocalName));
        }

        [Theory]
        [InlineData(null, 3, 0)]
        [InlineData(DayOfWeek.Monday, 23, 45)]
        public void WhatIsWrittenReadsBackAsTheSameSchedule(DayOfWeek? day, int hour, int minute)
        {
            var schedule = new RestartSchedule(day, new TimeSpan(hour, minute, 0));

            string xml = ScheduledRestart.BuildXml("demo", Wrapper, Config, schedule, new DateTime(2026, 9, 22));

            Assert.Equal(schedule, ScheduledRestart.Parse(xml));
        }

        /// <summary>A trigger edited by hand into a shape the picker cannot show is not guessed at.</summary>
        [Fact]
        public void ATriggerThisConsoleDidNotWriteIsNotAScheduleItCanShow()
        {
            const string twoDays = "<Task xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\"><Triggers><CalendarTrigger>"
                + "<StartBoundary>2026-09-22T03:00:00</StartBoundary>"
                + "<ScheduleByWeek><WeeksInterval>1</WeeksInterval><DaysOfWeek><Monday /><Friday /></DaysOfWeek></ScheduleByWeek>"
                + "</CalendarTrigger></Triggers></Task>";

            Assert.Null(ScheduledRestart.Parse(twoDays));
        }

        /// <summary>
        /// Restart only what is running: the wrapper is asked first, and the restart is chained
        /// on its answer. cmd strips the outermost pair of quotes and runs the rest.
        /// </summary>
        [Fact]
        public void TheActionRestartsOnlyARunningService()
        {
            string arguments = ScheduledRestart.BuildArguments(Wrapper, Config);

            Assert.StartsWith("/d /c \"", arguments, StringComparison.Ordinal);
            Assert.EndsWith("\"", arguments, StringComparison.Ordinal);

            string script = arguments.Substring("/d /c \"".Length, arguments.Length - "/d /c \"".Length - 1);
            Assert.Equal(
                $"\"{Wrapper}\" status \"{Config}\" | %SystemRoot%\\System32\\findstr.exe /C:\"Active (running)\" /C:\"Started\" >nul && \"{Wrapper}\" restart \"{Config}\" --no-elevate",
                script);
            Assert.DoesNotContain("--force", script, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData("03:00", 3, 0)]
        [InlineData("3:05", 3, 5)]
        [InlineData(" 23:59 ", 23, 59)]
        public void ATimeOfDayIsReadAsTyped(string text, int hour, int minute)
        {
            Assert.True(ScheduledRestart.TryParseTime(text, out var at));
            Assert.Equal(new TimeSpan(hour, minute, 0), at);
        }

        [Theory]
        [InlineData("24:00")]
        [InlineData("3")]
        [InlineData("03:60")]
        [InlineData("")]
        public void AnythingElseIsNot(string text)
        {
            Assert.False(ScheduledRestart.TryParseTime(text, out _));
        }

        private static XDocument Build(RestartSchedule schedule) =>
            XDocument.Parse(ScheduledRestart.BuildXml("demo", Wrapper, Config, schedule, new DateTime(2026, 9, 22)));
    }
}

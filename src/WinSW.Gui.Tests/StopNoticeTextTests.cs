using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using WinSW.Gui.Services;
using Xunit;

namespace WinSW.Gui.Tests
{
    /// <summary>
    /// The words a stop notice goes out in — the tray balloon and the group chat's message, for a
    /// service and for a desktop task — and the tag a click on the balloon hands back. The keys are
    /// chosen by a rule, which the test that looks for literal keys in the code cannot see.
    /// </summary>
    public class StopNoticeTextTests
    {
        private static readonly string[] Languages = { "en", "zh-CN", "zh-TW", "ja" };

        [Fact]
        public void AServicesNoticesAreWordedKindByKind()
        {
            Assert.Equal(
                ("M.Dash.UnexpectedStopTitle()", "M.Dash.UnexpectedStopBody(api)", true),
                StopNoticeText.Balloon(new StopNotice("api", StopNoticeKind.UnexpectedStop, 1, 1067), Echo));
            Assert.Equal(
                ("M.Dash.StopLoopTitle()", "M.Dash.StopLoopBody(api,7)", true),
                StopNoticeText.Balloon(new StopNotice("api", StopNoticeKind.RepeatedStops, 7, 1067), Echo));
            Assert.Equal(
                ("M.Dash.CleanStopTitle()", "M.Dash.CleanStopBody(api)", false),
                StopNoticeText.Balloon(new StopNotice("api", StopNoticeKind.CleanStop, 1, 0), Echo));
            Assert.Equal(
                ("M.Dash.RecoveredTitle()", "M.Dash.RecoveredBody(api)", false),
                StopNoticeText.Balloon(new StopNotice("api", StopNoticeKind.Recovered, 0, 1067), Echo));

            Assert.Equal("M.Alert.Stopped(web01,api,1067)", StopNoticeText.Message(new StopNotice("api", StopNoticeKind.UnexpectedStop, 1, 1067), "web01", Echo));
            Assert.Equal("M.Alert.StopLoop(web01,api,1067,7)", StopNoticeText.Message(new StopNotice("api", StopNoticeKind.RepeatedStops, 7, 1067), "web01", Echo));
            Assert.Equal("M.Alert.CleanStop(web01,api)", StopNoticeText.Message(new StopNotice("api", StopNoticeKind.CleanStop, 1, 0), "web01", Echo));
            Assert.Equal("M.Alert.Recovered(web01,api)", StopNoticeText.Message(new StopNotice("api", StopNoticeKind.Recovered, 0, 1067), "web01", Echo));
        }

        /// <summary>A robot is not a service, and the task scheduler, not Windows' recovery, starts it again.</summary>
        [Fact]
        public void ADesktopTasksNoticesSayItIsATask()
        {
            Assert.Equal(
                ("M.Task.StopTitle()", "M.Task.StopBody(robot,1)", true),
                StopNoticeText.Balloon(new StopNotice("robot", StopNoticeKind.UnexpectedStop, 1, 1) { DesktopTask = true }, Echo));
            Assert.Equal(
                ("M.Task.StopLoopTitle()", "M.Task.StopLoopBody(robot,3)", true),
                StopNoticeText.Balloon(new StopNotice("robot", StopNoticeKind.RepeatedStops, 3, 1) { DesktopTask = true }, Echo));
            Assert.Equal(
                ("M.Task.RecoveredTitle()", "M.Task.RecoveredBody(robot)", false),
                StopNoticeText.Balloon(new StopNotice("robot", StopNoticeKind.Recovered, 0, 1) { DesktopTask = true }, Echo));

            Assert.Equal("M.Alert.TaskStopped(pc1,robot,1)", StopNoticeText.Message(new StopNotice("robot", StopNoticeKind.UnexpectedStop, 1, 1) { DesktopTask = true }, "pc1", Echo));
            Assert.Equal("M.Alert.TaskStopLoop(pc1,robot,1,3)", StopNoticeText.Message(new StopNotice("robot", StopNoticeKind.RepeatedStops, 3, 1) { DesktopTask = true }, "pc1", Echo));
            Assert.Equal("M.Alert.TaskRecovered(pc1,robot)", StopNoticeText.Message(new StopNotice("robot", StopNoticeKind.Recovered, 0, 1) { DesktopTask = true }, "pc1", Echo));
        }

        /// <summary>A task's result is often an NTSTATUS, which is looked up by its hex form.</summary>
        [Fact]
        public void ANegativeExitCodeIsGivenInHexAsWell()
        {
            var notice = new StopNotice("robot", StopNoticeKind.UnexpectedStop, 1, unchecked((int)0xC000013A)) { DesktopTask = true };

            Assert.Equal("M.Alert.TaskStopped(pc1,robot,-1073741510 (0xC000013A))", StopNoticeText.Message(notice, "pc1", Echo));
        }

        /// <summary>
        /// Every key the words can use is in every language, with no placeholder beyond the values
        /// it is given: a missing key shows itself on screen, and one placeholder too many throws.
        /// </summary>
        [Fact]
        public void EveryKeyTheWordsUseIsInEveryLanguageWithTheValuesItIsGiven()
        {
            var used = new Dictionary<string, int>(StringComparer.Ordinal);
            string Record(string key, object?[] args)
            {
                used[key] = args.Length;
                return key;
            }

            foreach (var kind in Enum.GetValues<StopNoticeKind>())
            {
                foreach (bool task in new[] { false, true })
                {
                    var notice = new StopNotice("x", kind, 2, 1) { DesktopTask = task };
                    StopNoticeText.Balloon(notice, Record);
                    StopNoticeText.Message(notice, "m", Record);
                }
            }

            var placeholder = new Regex(@"\{(\d+)[^}]*\}");
            foreach (string language in Languages)
            {
                var values = StringDictionaries.ValuesOf(language);
                foreach (var (key, given) in used)
                {
                    Assert.True(values.TryGetValue(key, out string? text), $"{language} has no {key}");
                    int highest = placeholder.Matches(text!).Select(m => int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)).DefaultIfEmpty(-1).Max();
                    Assert.True(highest < given, $"{language} {key} uses {{{highest}}} but is given {given} value(s)");
                }
            }
        }

        [Fact]
        public void ABalloonsTagSaysWhetherItIsAboutATask()
        {
            string service = StopNoticeText.TagFor(new StopNotice("api", StopNoticeKind.UnexpectedStop, 1, 1067));
            string task = StopNoticeText.TagFor(new StopNotice("robot", StopNoticeKind.UnexpectedStop, 1, 1) { DesktopTask = true });

            Assert.Equal("api", service);
            Assert.False(StopNoticeText.IsTaskTag(service, out _));

            Assert.True(StopNoticeText.IsTaskTag(task, out string name));
            Assert.Equal("robot", name);

            // The folder alone names no task.
            Assert.False(StopNoticeText.IsTaskTag("\\WinSW\\", out _));
        }

        /// <summary>The key and what it was given, so that a test can see what was looked up.</summary>
        private static string Echo(string key, object?[] args) => key + "(" + string.Join(",", args) + ")";
    }
}

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using WinSW.Gui.Model;
using WinSW.Gui.Services;
using Xunit;

namespace WinSW.Gui.Tests
{
    /// <summary>
    /// A service's failure actions: read from the service control manager and from a configuration
    /// file, compared, put into words, and used to tell whether a stopped service is about to be
    /// started again.
    /// </summary>
    public class RecoverySettingsTests
    {
        private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

        private static readonly DateTime T0 = new(2026, 9, 23, 10, 0, 0, DateTimeKind.Utc);

        /// <summary>The wizard's default after this batch: 10 s, 1 min, then 5 min every time; reset after an hour.</summary>
        private static RecoverySettings Stepped => Settings(TimeSpan.FromHours(1), Restart(10), Restart(60), Restart(300));

        // Reading ----------------------------------------------------------------

        [Fact]
        public void TheServiceControlManagersAnswerIsTakenAsItIs()
        {
            var read = RecoverySettings.FromScm(3600, new[] { (1, 10_000u), (0, 0u), (2, 60_000u), (3, 1_000u) }, onNonCrashFailures: true);

            Assert.Equal(
                new[]
                {
                    new RecoveryAction(RecoveryActionKind.Restart, TimeSpan.FromSeconds(10)),
                    new RecoveryAction(RecoveryActionKind.None, TimeSpan.Zero),
                    new RecoveryAction(RecoveryActionKind.Reboot, TimeSpan.FromMinutes(1)),
                    new RecoveryAction(RecoveryActionKind.RunCommand, TimeSpan.FromSeconds(1)),
                },
                read.Actions);
            Assert.Equal(TimeSpan.FromHours(1), read.ResetAfter);
            Assert.True(read.OnNonCrashFailures);
        }

        [Fact]
        public void AnInfiniteResetPeriodNeverResets()
        {
            Assert.Null(RecoverySettings.FromScm(uint.MaxValue, new[] { (1, 0u) }, false).ResetAfter);
        }

        [Fact]
        public void AnActionTypeThatIsNotKnownDoesNothing()
        {
            var read = RecoverySettings.FromScm(0, new[] { (7, 1_000u) }, false);

            Assert.Equal(RecoveryActionKind.None, Assert.Single(read.Actions).Kind);
            Assert.False(read.Restarts);
        }

        /// <summary>
        /// The same settings read twice are equal, so a rescan that reads them again does not tell
        /// the panel that anything changed.
        /// </summary>
        [Fact]
        public void TwoReadingsOfTheSameSettingsAreEqual()
        {
            var first = RecoverySettings.FromScm(86_400, new[] { (1, 10_000u) }, false);
            var second = RecoverySettings.FromScm(86_400, new[] { (1, 10_000u) }, false);

            Assert.Equal(first, second);
            Assert.Equal(first.GetHashCode(), second.GetHashCode());
            Assert.NotEqual(first, RecoverySettings.FromScm(86_400, new[] { (1, 20_000u) }, false));
        }

        /// <summary>
        /// <c>SERVICE_FAILURE_ACTIONSW</c> is a DWORD, two pointers, a DWORD and a pointer; each
        /// pointer aligned to its own size.
        /// </summary>
        [Fact]
        public void TheFailureActionsStructureHasTheWindowsLayout()
        {
            Assert.Equal(IntPtr.Size == 8 ? 40 : 20, Marshal.SizeOf<NativeMethods.SERVICE_FAILURE_ACTIONS>());
            Assert.Equal(8, Marshal.SizeOf<NativeMethods.SC_ACTION>());
        }

        // The configuration file ----------------------------------------------------

        /// <summary>
        /// With no <c>&lt;onfailure&gt;</c> the wrapper leaves the service's failure actions as they
        /// are, so the file says nothing to compare them with.
        /// </summary>
        [Fact]
        public void AFileWithoutFailureActionsDeclaresNone()
        {
            Assert.Null(RecoverySettings.FromConfig(Config("<resetfailure>1 hour</resetfailure>")));
        }

        [Fact]
        public void AFilesFailureActionsAreReadAsTheWrapperReadsThem()
        {
            var declared = RecoverySettings.FromConfig(Config(
                "<onfailure action=\"restart\" delay=\"10 sec\"/>"
                + "<onfailure action=\"reboot\" delay=\"1 min\"/>"
                + "<onfailure action=\"none\"/>"
                + "<resetfailure>1 hour</resetfailure>"));

            Assert.NotNull(declared);
            Assert.Equal(
                new[]
                {
                    new RecoveryAction(RecoveryActionKind.Restart, TimeSpan.FromSeconds(10)),
                    new RecoveryAction(RecoveryActionKind.Reboot, TimeSpan.FromMinutes(1)),
                    new RecoveryAction(RecoveryActionKind.None, TimeSpan.Zero),
                },
                declared!.Actions);
            Assert.Equal(TimeSpan.FromHours(1), declared.ResetAfter);
        }

        /// <summary>No delay is no wait, and no reset period is the wrapper's day.</summary>
        [Fact]
        public void AFileLeavesOutWhatTheWrapperHasDefaultsFor()
        {
            var declared = RecoverySettings.FromConfig(Config("<onfailure action=\"restart\"/>"));

            Assert.Equal(new RecoveryAction(RecoveryActionKind.Restart, TimeSpan.Zero), Assert.Single(declared!.Actions));
            Assert.Equal(TimeSpan.FromDays(1), declared.ResetAfter);
        }

        [Theory]
        [InlineData("<onfailure action=\"Restart\"/>")]
        [InlineData("<onfailure action=\"restart\" delay=\"soon\"/>")]
        [InlineData("<onfailure action=\"restart\"/><resetfailure>later</resetfailure>")]
        public void AFileTheWrapperWouldRefuseDeclaresNothingToCompare(string xml)
        {
            Assert.Null(RecoverySettings.FromConfig(Config(xml)));
        }

        // Comparing -------------------------------------------------------------------

        [Fact]
        public void TheSameActionsHaveTheSameEffect()
        {
            Assert.True(Stepped.SameEffectAs(Settings(TimeSpan.FromHours(1), Restart(10), Restart(60), Restart(300))));
        }

        [Fact]
        public void AnotherDelayIsAnotherEffect()
        {
            Assert.False(Stepped.SameEffectAs(Settings(TimeSpan.FromHours(1), Restart(10), Restart(60), Restart(301))));
        }

        /// <summary>The last action repeats: saying it twice at the end changes nothing.</summary>
        [Fact]
        public void ARepeatedLastActionIsTheSameEffect()
        {
            Assert.True(Stepped.SameEffectAs(Settings(TimeSpan.FromHours(1), Restart(10), Restart(60), Restart(300), Restart(300))));
            Assert.True(Settings(null, Restart(10)).SameEffectAs(Settings(null, Restart(10), Restart(10))));
        }

        /// <summary>
        /// A file that turns recovery off says <c>&lt;onfailure action="none"/&gt;</c>, which the
        /// service control manager keeps as one action doing nothing: the same as none at all.
        /// </summary>
        [Fact]
        public void DoingNothingIsTheSameHoweverItIsSaid()
        {
            var none = Settings(TimeSpan.FromDays(1), new RecoveryAction(RecoveryActionKind.None, TimeSpan.Zero));
            var empty = Settings(TimeSpan.FromSeconds(0));
            var slowNone = Settings(TimeSpan.FromHours(3), new RecoveryAction(RecoveryActionKind.None, TimeSpan.FromMinutes(1)));

            Assert.True(none.SameEffectAs(empty));
            Assert.True(slowNone.SameEffectAs(none));
            Assert.False(none.SameEffectAs(Settings(TimeSpan.FromDays(1), Restart(10))));
        }

        [Fact]
        public void TheResetPeriodMattersWhereTheCountDoes()
        {
            Assert.False(Stepped.SameEffectAs(Settings(TimeSpan.FromDays(1), Restart(10), Restart(60), Restart(300))));
        }

        /// <summary>One action answers every failure; how far the count has got changes nothing.</summary>
        [Fact]
        public void TheResetPeriodDoesNotMatterWhereEveryFailureIsAnsweredAlike()
        {
            Assert.True(Settings(TimeSpan.FromHours(1), Restart(10)).SameEffectAs(Settings(TimeSpan.FromDays(1), Restart(10))));
        }

        /// <summary>The wrapper hands the reset period over in whole seconds.</summary>
        [Fact]
        public void TheResetPeriodIsComparedInWholeSeconds()
        {
            var file = Settings(TimeSpan.FromMilliseconds(90_500), Restart(10), Restart(60));
            var held = Settings(TimeSpan.FromSeconds(90), Restart(10), Restart(60));

            Assert.True(file.SameEffectAs(held));
        }

        [Fact]
        public void EachFailureIsAnsweredByItsActionAndTheLastRepeats()
        {
            Assert.Equal(TimeSpan.FromSeconds(10), Stepped.ActionFor(1)!.Value.Delay);
            Assert.Equal(TimeSpan.FromSeconds(60), Stepped.ActionFor(2)!.Value.Delay);
            Assert.Equal(TimeSpan.FromSeconds(300), Stepped.ActionFor(3)!.Value.Delay);
            Assert.Equal(TimeSpan.FromSeconds(300), Stepped.ActionFor(40)!.Value.Delay);
            Assert.Equal(TimeSpan.FromSeconds(10), Stepped.ActionFor(0)!.Value.Delay);
            Assert.Null(Settings(null).ActionFor(1));
        }

        // Restarting ---------------------------------------------------------------------

        /// <summary>
        /// A crash — the process gone without saying it had stopped — is what the actions answer.
        /// A stop the service reported itself, with whatever code, is not, unless the service is
        /// set to have those answered too.
        /// </summary>
        [Fact]
        public void TheActionsAnswerCrashesAndOnlyWhenAskedOtherFailures()
        {
            var crashesOnly = Settings(null, Restart(10));
            var withErrors = new RecoverySettings(crashesOnly.Actions, null, onNonCrashFailures: true);

            Assert.True(crashesOnly.Answers(RecoverySettings.ProcessAborted));
            Assert.False(crashesOnly.Answers(1064));
            Assert.False(crashesOnly.Answers(0));
            Assert.True(withErrors.Answers(1064));
            Assert.False(withErrors.Answers(0));
        }

        [Fact]
        public void ARestartIsDueAfterItsDelayAndTheMargin()
        {
            var due = Settings(null, Restart(10)).RestartDueBy(RecoverySettings.ProcessAborted, T0, crashCount: 1);

            Assert.Equal(T0 + TimeSpan.FromSeconds(10) + RecoverySettings.RestartMargin, due);
        }

        [Fact]
        public void NothingIsDueForAStopTheActionsDoNotAnswer()
        {
            Assert.Null(Stepped.RestartDueBy(0, T0, 1));
            Assert.Null(Stepped.RestartDueBy(1064, T0, 1));
        }

        [Fact]
        public void NothingIsDueWhenNoActionRestarts()
        {
            Assert.Null(Settings(null).RestartDueBy(RecoverySettings.ProcessAborted, T0, 1));
            Assert.Null(Settings(null, new RecoveryAction(RecoveryActionKind.Reboot, TimeSpan.FromMinutes(1))).RestartDueBy(RecoverySettings.ProcessAborted, T0, 1));
        }

        /// <summary>
        /// The crash window's count tells which action comes next from below only: it starts again
        /// every five minutes while the service control manager's goes on. So the longest restart
        /// from the counted action onwards is waited out; the count narrows it as it grows.
        /// </summary>
        [Fact]
        public void TheLongestRestartFromTheCountedActionOnIsWaitedOut()
        {
            Assert.Equal(T0 + TimeSpan.FromMinutes(5) + RecoverySettings.RestartMargin, Stepped.RestartDueBy(RecoverySettings.ProcessAborted, T0, 1));

            var shrinking = Settings(TimeSpan.FromHours(1), Restart(300), Restart(60), Restart(10));
            Assert.Equal(T0 + TimeSpan.FromMinutes(5) + RecoverySettings.RestartMargin, shrinking.RestartDueBy(RecoverySettings.ProcessAborted, T0, 1));
            Assert.Equal(T0 + TimeSpan.FromMinutes(1) + RecoverySettings.RestartMargin, shrinking.RestartDueBy(RecoverySettings.ProcessAborted, T0, 2));
            Assert.Equal(T0 + TimeSpan.FromSeconds(10) + RecoverySettings.RestartMargin, shrinking.RestartDueBy(RecoverySettings.ProcessAborted, T0, 3));
            Assert.Equal(T0 + TimeSpan.FromSeconds(10) + RecoverySettings.RestartMargin, shrinking.RestartDueBy(RecoverySettings.ProcessAborted, T0, 30));
        }

        /// <summary>Past the last restart, with nothing after it, recovery is done with the service.</summary>
        [Fact]
        public void NothingIsDueOnceTheCountIsPastTheLastRestart()
        {
            var twice = Settings(TimeSpan.FromDays(1), Restart(10), Restart(60), new RecoveryAction(RecoveryActionKind.None, TimeSpan.Zero));

            Assert.NotNull(twice.RestartDueBy(RecoverySettings.ProcessAborted, T0, 2));
            Assert.Null(twice.RestartDueBy(RecoverySettings.ProcessAborted, T0, 3));
        }

        /// <summary>
        /// A stop the window did not count — the console's own Terminate, or none open — and a
        /// reset period short enough to bring the service control manager's count below the
        /// window's, both leave every action in play.
        /// </summary>
        [Fact]
        public void WithoutACountToGoOnEveryActionIsInPlay()
        {
            var shrinking = Settings(TimeSpan.FromHours(1), Restart(300), Restart(10));
            var quickReset = Settings(TimeSpan.FromMinutes(1), Restart(300), Restart(10));

            Assert.Equal(T0 + TimeSpan.FromMinutes(5) + RecoverySettings.RestartMargin, shrinking.RestartDueBy(RecoverySettings.ProcessAborted, T0, 0));
            Assert.Equal(T0 + TimeSpan.FromMinutes(5) + RecoverySettings.RestartMargin, quickReset.RestartDueBy(RecoverySettings.ProcessAborted, T0, 2));
        }

        // Words ------------------------------------------------------------------------------

        /// <summary>The line the dashboard shows for the wizard's default, in the words ops read it in.</summary>
        [Fact]
        public void ASteppedRecoveryReadsAsItsStepsAndSaysTheLastRepeats()
        {
            Assert.Equal("10 秒后重启，1 分钟后重启，此后每次 5 分钟后重启；连续 1 小时没有失败则重新计数", Stepped.Describe(Format("zh-CN")));
            Assert.Equal(
                "restart after 10 s, restart after 1 min, then restart after 5 min every time; the count starts over after 1 h without a failure",
                Stepped.Describe(Format("en")));
        }

        [Fact]
        public void OneActionIsSaidToBeTakenEveryTimeWithoutAResetPeriod()
        {
            Assert.Equal("每次 10 秒后重启", Settings(TimeSpan.FromDays(1), Restart(10)).Describe(Format("zh-CN")));
            Assert.Equal("每次 10 秒后重启", Settings(TimeSpan.FromDays(1), Restart(10), Restart(10)).Describe(Format("zh-CN")));
        }

        [Fact]
        public void DoingNothingIsSaidAsSuch()
        {
            Assert.Equal("不处理", Settings(TimeSpan.FromDays(1)).Describe(Format("zh-CN")));
            Assert.Equal("不处理", Settings(TimeSpan.FromDays(1), new RecoveryAction(RecoveryActionKind.None, TimeSpan.Zero)).Describe(Format("zh-CN")));
        }

        [Fact]
        public void ALastActionThatDoesNothingEndsTheRecovery()
        {
            var twice = Settings(TimeSpan.FromDays(1), Restart(10), Restart(60), new RecoveryAction(RecoveryActionKind.None, TimeSpan.Zero));

            Assert.Equal("10 秒后重启，1 分钟后重启，此后不再处理；连续 1 天没有失败则重新计数", twice.Describe(Format("zh-CN")));
        }

        [Fact]
        public void ACountThatNeverResetsIsSaidToNeverReset()
        {
            var stepped = Settings(null, Restart(10), new RecoveryAction(RecoveryActionKind.Reboot, TimeSpan.FromMinutes(2)));

            Assert.Equal("10 秒后重启，此后每次 2 分钟后重启计算机；失败次数永不重置", stepped.Describe(Format("zh-CN")));
        }

        [Theory]
        [InlineData(0L, 0L, "M.Dash.Recovery.Seconds")]
        [InlineData(1_500L, 1_500L, "M.Dash.Recovery.Milliseconds")]
        [InlineData(90_000L, 90L, "M.Dash.Recovery.Seconds")]
        [InlineData(120_000L, 2L, "M.Dash.Recovery.Minutes")]
        [InlineData(3_600_000L, 1L, "M.Dash.Recovery.Hours")]
        [InlineData(5_400_000L, 90L, "M.Dash.Recovery.Minutes")]
        [InlineData(172_800_000L, 2L, "M.Dash.Recovery.Days")]
        public void ASpanIsSaidInTheLargestUnitItIsAWholeNumberOf(long milliseconds, long count, string unit)
        {
            Assert.Equal((count, unit), RecoverySettings.Unit(TimeSpan.FromMilliseconds(milliseconds)));
        }

        /// <summary>Every phrase the words are built from is in every language.</summary>
        [Fact]
        public void EveryPhraseTheWordsUseIsInEveryLanguage()
        {
            string[] keys =
            {
                "M.Dash.Recovery.Nothing", "M.Dash.Recovery.Restart", "M.Dash.Recovery.Reboot", "M.Dash.Recovery.RunCommand",
                "M.Dash.Recovery.Every", "M.Dash.Recovery.Next", "M.Dash.Recovery.ThenEvery", "M.Dash.Recovery.ThenNothing",
                "M.Dash.Recovery.Reset", "M.Dash.Recovery.NeverReset", "M.Dash.Recovery.Milliseconds", "M.Dash.Recovery.Seconds",
                "M.Dash.Recovery.Minutes", "M.Dash.Recovery.Hours", "M.Dash.Recovery.Days",
            };

            foreach (string code in new[] { "en", "zh-CN", "zh-TW", "ja" })
            {
                var strings = Strings(code);
                Assert.All(keys, key => Assert.True(strings.ContainsKey(key), $"{code} has no {key}"));
            }
        }

        /// <summary>
        /// The words use the dashboard's own phrases only. The editor's ON FAILURE summary has
        /// M.Recovery.* phrases of the same names that take other arguments; sharing a key would
        /// declare it twice once both are in the dictionaries, and whichever text won would be
        /// wrong for the other.
        /// </summary>
        [Fact]
        public void EveryPhraseTheWordsUseIsTheDashboardsOwn()
        {
            var used = new HashSet<string>(StringComparer.Ordinal);
            string Record(string key, object?[] args)
            {
                used.Add(key);
                return key;
            }

            var none = new RecoveryAction(RecoveryActionKind.None, TimeSpan.Zero);
            var reboot = new RecoveryAction(RecoveryActionKind.Reboot, TimeSpan.FromMinutes(2));
            var command = new RecoveryAction(RecoveryActionKind.RunCommand, TimeSpan.FromMilliseconds(1_500));
            foreach (var settings in new[]
            {
                Stepped,
                Settings(TimeSpan.FromDays(1)),
                Settings(TimeSpan.FromDays(2), Restart(10)),
                Settings(null, Restart(10), reboot),
                Settings(TimeSpan.FromDays(1), Restart(10), Restart(60), none),
                Settings(TimeSpan.FromHours(3), command, none, Restart(0)),
            })
            {
                settings.Describe(Record);
            }

            Assert.NotEmpty(used);
            Assert.All(used, key => Assert.StartsWith("M.Dash.Recovery.", key, StringComparison.Ordinal));
        }

        private static RecoveryAction Restart(int seconds) => new(RecoveryActionKind.Restart, TimeSpan.FromSeconds(seconds));

        private static RecoverySettings Settings(TimeSpan? reset, params RecoveryAction[] actions) =>
            new(actions.ToImmutableArray(), reset, false);

        private static ServiceConfigModel Config(string inner) =>
            ServiceConfigModel.FromXml($"<service><id>demo</id><executable>demo.exe</executable>{inner}</service>", null);

        /// <summary>
        /// <c>Localizer.Format</c> over one of the shipped dictionaries, read from the source: the
        /// real one wants a WPF application to look the strings up in.
        /// </summary>
        private static Func<string, object?[], string> Format(string code)
        {
            var strings = Strings(code);
            return (key, args) => string.Format(CultureInfo.InvariantCulture, strings[key], args);
        }

        private static Dictionary<string, string> Strings(string code)
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null && !File.Exists(Path.Combine(directory.FullName, "src", "WinSW.sln")))
            {
                directory = directory.Parent;
            }

            Assert.True(directory != null, "The repository root could not be found from " + AppContext.BaseDirectory);
            string path = Path.Combine(directory!.FullName, "src", "WinSW.Gui", "Localization", $"Strings.{code}.xaml");

            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var element in XDocument.Load(path).Descendants())
            {
                if ((string?)element.Attribute(X + "Key") is { } key)
                {
                    result[key] = element.Value;
                }
            }

            return result;
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using WinSW.Gui.Localization;
using WinSW.Gui.Model;
using Xunit;

namespace WinSW.Gui.Tests
{
    /// <summary>
    /// The failure rows read back as what Windows will do: which failure gets which row, that
    /// the last one repeats, and the warning for a last row that restarts sooner than a minute.
    /// </summary>
    /// <remarks>
    /// The wording is checked against the dictionaries as they are in the source, read the way
    /// <see cref="LocalizationTests"/> reads them: <c>Localizer.Get</c> needs a WPF application
    /// to look resources up in, and the plan takes its templates as a function for that reason.
    /// </remarks>
    public class RecoveryPlanTests
    {
        private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

        private static readonly Dictionary<string, Dictionary<string, string>> Dictionaries = new(StringComparer.Ordinal);

        /// <summary>The wizard's default recovery, and the sentence it was asked for in.</summary>
        [Fact]
        public void TheWizardDefaultReadsAsAskedFor()
        {
            var plan = PlanResettingAfter("1 hour", ("restart", "10 sec"), ("restart", "1 min"), ("restart", "5 min"));

            Assert.Equal(
                "第 1 次失败：10 秒后重启；第 2 次：1 分钟后重启；之后每次：5 分钟后重启；连续 1 小时没有失败则重新计数。",
                plan.Describe(Text("zh-CN")));
            Assert.Equal(
                "Failure 1: restart after 10 seconds; failure 2: restart after 1 minute; every failure after that: restart after 5 minutes; the count starts over after 1 hour without a failure.",
                plan.Describe(Text("en")));
            Assert.False(plan.RestartsInALoop);
            Assert.Equal(string.Empty, plan.DescribeWarning(Text("zh-CN")));
        }

        /// <summary>
        /// One row is every failure's row, and with every failure getting the same, the count
        /// going back to zero changes nothing and is not mentioned.
        /// </summary>
        [Fact]
        public void OneRowIsEveryFailure()
        {
            var plan = Plan(("restart", "10 sec"));

            Assert.Equal("每次失败：10 秒后重启。", plan.Describe(Text("zh-CN")));
            Assert.Equal("Every failure: restart after 10 seconds.", plan.Describe(Text("en")));
            Assert.False(plan.ResetMatters);
        }

        /// <summary>The old wizard default: the loop the warning is for, with how often it comes round.</summary>
        [Fact]
        public void ARepeatingRestartUnderAMinuteIsWarnedAbout()
        {
            var plan = Plan(("restart", "10 sec"));

            Assert.True(plan.RestartsInALoop);
            Assert.Equal(360, plan.RestartsPerHour);
            Assert.Contains("360", plan.DescribeWarning(Text("zh-CN")), StringComparison.Ordinal);
            Assert.Contains("360", plan.DescribeWarning(Text("en")), StringComparison.Ordinal);
        }

        [Theory]
        [InlineData("10 sec", true)]
        [InlineData(null, true)]
        [InlineData("0", true)]
        [InlineData("59 sec", true)]
        [InlineData("59999 ms", true)]
        [InlineData("60 sec", false)]
        [InlineData("1 min", false)]
        [InlineData("1 hour", false)]
        public void OnlyALastRestartSoonerThanAMinuteIsALoop(string? delay, bool loop)
        {
            Assert.Equal(loop, Plan(("restart", "5 min"), ("restart", delay)).RestartsInALoop);
        }

        /// <summary>What matters is the row that repeats, not the quick ones before it.</summary>
        [Fact]
        public void QuickRowsBeforeASlowLastOneAreFine()
        {
            Assert.False(Plan(("restart", "0"), ("restart", "10 sec"), ("restart", "5 min")).RestartsInALoop);
            Assert.True(Plan(("restart", "5 min"), ("restart", "10 sec")).RestartsInALoop);
        }

        [Fact]
        public void ALastRowThatIsNotARestartIsNoLoop()
        {
            Assert.False(Plan(("restart", "10 sec"), ("none", "10 sec")).RestartsInALoop);
            Assert.False(Plan(("restart", "10 sec"), ("reboot", "10 sec")).RestartsInALoop);
            Assert.Equal(0, Plan(("restart", "10 sec"), ("none", null)).RestartsPerHour);
            Assert.False(Plan().RestartsInALoop);
        }

        /// <summary>A restart at once is counted as one a second: the program's own start is the real limit.</summary>
        [Theory]
        [InlineData(null, 3600)]
        [InlineData("500 ms", 3600)]
        [InlineData("45 sec", 80)]
        [InlineData("30 sec", 120)]
        public void RestartsPerHourCountsTheRepeatingDelay(string? delay, int expected)
        {
            Assert.Equal(expected, Plan(("restart", delay)).RestartsPerHour);
        }

        /// <summary>
        /// The last row repeating matters just as much when it is "none": the service is
        /// restarted once and then left alone, until a quiet day starts the count over.
        /// </summary>
        [Fact]
        public void ALastNoneLeavesTheServiceStopped()
        {
            var plan = Plan(("restart", "10 sec"), ("none", null));

            Assert.Equal("第 1 次失败：10 秒后重启；之后每次：不做任何操作；连续 1 天没有失败则重新计数。", plan.Describe(Text("zh-CN")));
        }

        [Fact]
        public void EveryRowNoneIsNothing()
        {
            var plan = Plan(("none", null));

            Assert.True(plan.DoesNothing);
            Assert.Equal(Text("zh-CN")("M.Recovery.Nothing"), plan.Describe(Text("zh-CN")));
        }

        [Fact]
        public void RestartsAndRebootsAtOnceAreSaidSo()
        {
            var plan = Plan(("restart", null), ("reboot", "0"));

            Assert.Equal("第 1 次失败：立即重启；之后每次：立即重启计算机；连续 1 天没有失败则重新计数。", plan.Describe(Text("zh-CN")));
        }

        /// <summary>
        /// No rows says two different things: a file that never had any leaves the service's
        /// recovery alone, and one whose rows were removed writes "none" and clears it.
        /// </summary>
        [Fact]
        public void NoRowsSaysWhetherSavingClearsRecovery()
        {
            var never = ServiceConfigModel.FromXml("<service><id>demo</id><executable>demo.exe</executable></service>", null).DescribeRecovery()!;
            Assert.False(never.WritesNone);
            Assert.Equal(Text("en")("M.Recovery.NotSet"), never.Describe(Text("en")));

            var removed = ServiceConfigModel.FromXml(@"<service><id>demo</id><executable>demo.exe</executable><onfailure action=""restart"" /></service>", null);
            removed.FailureActions.Clear();
            var plan = removed.DescribeRecovery()!;
            Assert.True(plan.WritesNone);
            Assert.Equal(Text("en")("M.Recovery.Cleared"), plan.Describe(Text("en")));
            Assert.Contains(@"<onfailure action=""none""/>", plan.Describe(Text("en")), StringComparison.Ordinal);
        }

        /// <summary>The model reads its rows as the wrapper does: blank delay is none, reset defaults to a day.</summary>
        [Fact]
        public void TheModelReadsItsRowsAsTheWrapperDoes()
        {
            var model = ServiceConfigModel.FromXml(
                @"<service><id>demo</id><executable>demo.exe</executable><onfailure action=""restart"" /><onfailure action=""reboot"" delay=""2 hours"" /></service>",
                null);

            var plan = model.DescribeRecovery()!;
            Assert.Equal(new[] { RecoveryKind.Restart, RecoveryKind.Reboot }, plan.Steps.Select(s => s.Kind));
            Assert.Equal(new[] { TimeSpan.Zero, TimeSpan.FromHours(2) }, plan.Steps.Select(s => s.Delay));
            Assert.Equal(TimeSpan.FromDays(1), plan.ResetAfter);
            Assert.False(plan.WritesNone);

            model.ResetFailureAfter = "90 min";
            Assert.Equal(TimeSpan.FromMinutes(90), model.DescribeRecovery()!.ResetAfter);
        }

        /// <summary>
        /// Nothing to say while something does not parse; the problems list says why. The wrapper
        /// matches the action exactly, so "Restart" is one of those.
        /// </summary>
        [Theory]
        [InlineData(@"<onfailure action=""restart"" delay=""10 seconds"" />")]
        [InlineData(@"<onfailure action=""Restart"" />")]
        [InlineData(@"<onfailure action=""restart"" /><resetfailure>soon</resetfailure>")]
        public void NothingIsDescribedWhileARowDoesNotParse(string rows)
        {
            var model = ServiceConfigModel.FromXml($"<service><id>demo</id><executable>demo.exe</executable>{rows}</service>", null);

            Assert.Null(model.DescribeRecovery());
        }

        [Theory]
        [InlineData(0, "0 秒")]
        [InlineData(1_000, "1 秒")]
        [InlineData(90_000, "90 秒")]
        [InlineData(120_000, "2 分钟")]
        [InlineData(7_200_000, "2 小时")]
        [InlineData(86_400_000, "1 天")]
        [InlineData(1_500, "1500 毫秒")]
        public void ADurationIsSaidInTheUnitItWasWrittenIn(int milliseconds, string expected)
        {
            Assert.Equal(expected, RecoveryPlan.Duration(TimeSpan.FromMilliseconds(milliseconds), Text("zh-CN")));
        }

        [Fact]
        public void EnglishCountsItsUnits()
        {
            Assert.Equal("1 minute", RecoveryPlan.Duration(TimeSpan.FromMinutes(1), Text("en")));
            Assert.Equal("2 minutes", RecoveryPlan.Duration(TimeSpan.FromMinutes(2), Text("en")));
            Assert.Equal("1 day", RecoveryPlan.Duration(TimeSpan.FromDays(1), Text("en")));
        }

        /// <summary>
        /// Every template the plan asks for is in every language, and formats with the arguments
        /// it is given. The plan asks through a function, where the check on literal
        /// <c>Localizer.Get</c> calls cannot see it.
        /// </summary>
        [Fact]
        public void EveryLanguageCanSayEveryPlan()
        {
            var plans = new[]
            {
                Plan(),
                Plan(("none", null)),
                Plan(("restart", "10 sec")),
                Plan(("restart", null), ("reboot", "1500 ms")),
                Plan(("reboot", null), ("restart", "5 min")),
                Plan(("restart", "10 sec"), ("restart", "1 min"), ("restart", "2 hours"), ("reboot", "1 day"), ("none", "3 days")),
                new RecoveryPlan(Array.Empty<RecoveryStep>(), RecoveryPlan.DefaultResetAfter, writesNone: true),
                new RecoveryPlan(new[] { new RecoveryStep(RecoveryKind.Restart, TimeSpan.FromSeconds(1)), new RecoveryStep(RecoveryKind.Restart, TimeSpan.FromSeconds(2)) }, TimeSpan.FromMinutes(1), writesNone: false),
                new RecoveryPlan(new[] { new RecoveryStep(RecoveryKind.Restart, TimeSpan.FromSeconds(1)), new RecoveryStep(RecoveryKind.Restart, TimeSpan.FromSeconds(2)) }, TimeSpan.FromDays(2), writesNone: false),
                new RecoveryPlan(new[] { new RecoveryStep(RecoveryKind.Restart, TimeSpan.FromSeconds(1)), new RecoveryStep(RecoveryKind.Restart, TimeSpan.FromSeconds(2)) }, TimeSpan.FromHours(1), writesNone: false),
            };

            var asked = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var language in Localizer.Languages)
            {
                var dictionary = Text(language.Code);
                string Asked(string key)
                {
                    asked.Add(key);
                    return dictionary(key);
                }

                foreach (var plan in plans)
                {
                    string sentence = plan.Describe(Asked);
                    Assert.False(string.IsNullOrWhiteSpace(sentence));
                    Assert.DoesNotContain("M.Recovery", sentence, StringComparison.Ordinal);
                    Assert.DoesNotContain("{", sentence, StringComparison.Ordinal);

                    string warning = plan.DescribeWarning(Asked);
                    Assert.DoesNotContain("{", warning, StringComparison.Ordinal);
                }
            }

            // And the plans above are enough to need every one of them: no template goes unused.
            var defined = new SortedSet<string>(Dictionaries["en"].Keys.Where(k => k.StartsWith("M.Recovery.", StringComparison.Ordinal)), StringComparer.Ordinal);
            Assert.Equal(defined, asked);
        }

        private static RecoveryPlan Plan(params (string Action, string? Delay)[] rows) => PlanResettingAfter(null, rows);

        /// <summary>The plan the editor would describe for these rows and reset period.</summary>
        private static RecoveryPlan PlanResettingAfter(string? reset, params (string Action, string? Delay)[] rows)
        {
            var model = ServiceConfigModel.CreateNew();
            model.Id = "demo";
            model.Executable = "demo.exe";
            model.ResetFailureAfter = reset;
            foreach (var (action, delay) in rows)
            {
                model.FailureActions.Add(new FailureAction { Action = action, Delay = delay });
            }

            return model.DescribeRecovery() ?? throw new InvalidOperationException("The rows did not parse.");
        }

        /// <summary>A language's templates, from its dictionary as it is in the source.</summary>
        private static Func<string, string> Text(string code)
        {
            lock (Dictionaries)
            {
                if (!Dictionaries.TryGetValue(code, out var values))
                {
                    values = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var element in XDocument.Load(Path.Combine(GuiRoot, "Localization", $"Strings.{code}.xaml")).Descendants())
                    {
                        if ((string?)element.Attribute(X + "Key") is { } key)
                        {
                            values[key] = element.Value;
                        }
                    }

                    Dictionaries[code] = values;
                }

                // A missing key throws here rather than coming back as the key, which is what
                // the application would show.
                return key => values[key];
            }
        }

        /// <summary>The WinSW.Gui project directory, found by walking up to the solution.</summary>
        private static string GuiRoot
        {
            get
            {
                var directory = new DirectoryInfo(AppContext.BaseDirectory);
                while (directory != null && !File.Exists(Path.Combine(directory.FullName, "src", "WinSW.sln")))
                {
                    directory = directory.Parent;
                }

                Assert.True(directory != null, "The repository root could not be found from " + AppContext.BaseDirectory);
                return Path.Combine(directory!.FullName, "src", "WinSW.Gui");
            }
        }
    }
}

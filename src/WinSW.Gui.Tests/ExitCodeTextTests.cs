using System.Collections.Generic;
using System.Linq;
using WinSW.Gui.Localization;
using WinSW.Gui.Services;
using Xunit;

namespace WinSW.Gui.Tests
{
    /// <summary>
    /// What a failed command's exit code is reported as. Read through stand-ins for the
    /// dictionaries, which need WPF, and for Windows' own text for a code, which is whatever
    /// the machine running the tests says.
    /// </summary>
    public class ExitCodeTextTests
    {
        private static readonly string[] WrapperCommands =
        {
            "install", "uninstall", "start", "stop", "restart", "refresh", "dev kill", "install + start", "customize",
        };

        private static readonly string[] OtherCommands = { "copy", "del", "taskkill", "schtasks", "sc config", "upgrade" };

        [Theory]
        [InlineData(1058, "M.Cli.ServiceDisabled")]
        [InlineData(1069, "M.Cli.LogonFailed")]
        [InlineData(1053, "M.Cli.StartTimedOut")]
        [InlineData(-1, "M.Cli.StartFailedEarly")]
        public void AFailedStartSaysWhyInItsOwnLine(int exitCode, string key)
        {
            Assert.Equal($"{key}(start)", Describe("start", exitCode));
            Assert.Equal($"{key}(install + start)", Describe("install + start", exitCode));
        }

        /// <summary>
        /// A disabled service or an account that cannot sign in stops a restart as surely as a
        /// start; the start half of a restart is the only half they can come from.
        /// </summary>
        [Theory]
        [InlineData(1058, "M.Cli.ServiceDisabled")]
        [InlineData(1069, "M.Cli.LogonFailed")]
        public void ADisabledServiceOrAFailedSignInIsExplainedForARestartToo(int exitCode, string key)
        {
            Assert.Equal($"{key}(restart)", Describe("restart", exitCode));
        }

        /// <summary>
        /// -1 is the wrapper's own failure. Only a start is sure to have failed before the service
        /// was up; a stop, a refresh or a restart that throws has its reason in the wrapper log.
        /// Windows has no text for -1, so none is added.
        /// </summary>
        [Theory]
        [InlineData("stop")]
        [InlineData("restart")]
        [InlineData("refresh")]
        [InlineData("uninstall")]
        [InlineData("install")]
        [InlineData("dev kill")]
        public void MinusOneFromAnythingButAStartPointsAtTheWrapperLog(string command)
        {
            Assert.Equal($"M.Cli.WrapperError({command})", Describe(command, -1));
        }

        /// <summary>
        /// Customize makes a copy of the wrapper and touches no service: there is no service's log
        /// to point at, and Windows has no text for -1.
        /// </summary>
        [Fact]
        public void MinusOneFromCustomizeIsReportedBare()
        {
            Assert.Equal("M.Cli.Failed(customize, -1)", Describe("customize", -1));
        }

        /// <summary>
        /// A stop can time out too, and "did not report that it had started" would be wrong for
        /// it; Windows' own text for 1053 speaks of a start or a control request.
        /// </summary>
        [Theory]
        [InlineData("stop")]
        [InlineData("restart")]
        public void ATimeoutOutsideAStartIsGivenWindowsOwnText(string command)
        {
            Assert.Equal($"M.Cli.Failed({command}, 1053) <text for 1053>", Describe(command, 1053));
        }

        [Theory]
        [InlineData(2)]
        [InlineData(5)]
        [InlineData(1067)]
        [InlineData(1077)]
        public void AnyOtherCodeFromTheWrapperCarriesWindowsOwnText(int exitCode)
        {
            Assert.Equal($"M.Cli.Failed(start, {exitCode}) <text for {exitCode}>", Describe("start", exitCode));
        }

        [Fact]
        public void WindowsOwnTextIsTrimmed()
        {
            string text = WinSwCli.DescribeExitCode("stop", 2, Format, _ => "The system cannot find the file specified.\r\n");

            Assert.Equal("M.Cli.Failed(stop, 2) The system cannot find the file specified.", text);
        }

        /// <summary>
        /// The lines that were there before keep their meaning for every command, whether it is
        /// the wrapper's or not.
        /// </summary>
        [Theory]
        [InlineData(1051, "M.Cli.HasDependents")]
        [InlineData(1056, "M.Cli.AlreadyRunning")]
        [InlineData(1060, "M.Cli.NotInstalled")]
        [InlineData(1062, "M.Cli.NotRunning")]
        [InlineData(1073, "M.Cli.AlreadyExists")]
        public void TheCodesThatHadALineOfTheirOwnKeepIt(int exitCode, string key)
        {
            foreach (string command in WrapperCommands.Concat(OtherCommands))
            {
                Assert.Equal($"{key}({command})", Describe(command, exitCode));
            }
        }

        /// <summary>
        /// cmd's copy and del, taskkill and schtasks return 1 for most failures, which Windows
        /// would call "Incorrect function"; their codes are their own, and so is -1 for them.
        /// </summary>
        [Theory]
        [InlineData("copy", 1)]
        [InlineData("del", 1)]
        [InlineData("taskkill", 128)]
        [InlineData("schtasks", 1)]
        [InlineData("sc config", 5)]
        [InlineData("upgrade", 1)]
        [InlineData("taskkill", -1)]
        [InlineData("schtasks", 1058)]
        [InlineData("copy", 1053)]
        public void OtherProgramsCodesAreReportedBare(string command, int exitCode)
        {
            Assert.Equal($"M.Cli.Failed({command}, {exitCode})", Describe(command, exitCode));
        }

        /// <summary>
        /// The keys are chosen by a rule, not named in a <c>Localizer.Get("…")</c> that
        /// <see cref="LocalizationTests"/> would find, so each one it can choose is looked for here.
        /// </summary>
        [Fact]
        public void EveryLineItCanChooseIsInEveryLanguage()
        {
            var keys = new SortedSet<string>(System.StringComparer.Ordinal);
            int[] codes = { -1, 1, 2, 5, 1051, 1053, 1056, 1058, 1060, 1062, 1069, 1073 };
            foreach (string command in WrapperCommands.Concat(OtherCommands))
            {
                foreach (int code in codes)
                {
                    WinSwCli.DescribeExitCode(
                        command,
                        code,
                        (key, _) =>
                        {
                            keys.Add(key);
                            return key;
                        },
                        _ => string.Empty);
                }
            }

            Assert.Contains("M.Cli.StartFailedEarly", keys);

            foreach (var language in Localizer.Languages)
            {
                var defined = StringDictionaries.ValuesOf(language.Code);
                var missing = keys.Where(k => !defined.ContainsKey(k)).ToList();
                Assert.True(missing.Count == 0, $"Strings.{language.Code}.xaml is missing: {string.Join(", ", missing)}");
            }
        }

        /// <summary>The key and its arguments, as the dictionaries would receive them.</summary>
        private static string Describe(string command, int exitCode) =>
            WinSwCli.DescribeExitCode(command, exitCode, Format, code => $"<text for {code}>");

        private static string Format(string key, object?[] args) => $"{key}({string.Join(", ", args)})";
    }
}

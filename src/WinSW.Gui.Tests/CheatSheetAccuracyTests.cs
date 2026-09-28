using System;
using System.IO;
using System.Linq;
using WinSW.Gui.Services;
using Xunit;

namespace WinSW.Gui.Tests
{
    /// <summary>
    /// The cheat sheet is also the AI prompt, handed to an assistant as the exact
    /// specification, so a sentence in it that disagrees with the wrapper becomes a
    /// configuration that disagrees with the wrapper. These pin the statements that once did.
    /// </summary>
    /// <remarks>
    /// Both languages ship, and each is read from the executable, as the guide window and the
    /// prompt read it.
    /// </remarks>
    public class CheatSheetAccuracyTests
    {
        /// <summary>
        /// The wrapper sets <c>BASE</c> to the folder the configuration file is in
        /// (<c>XmlServiceConfig</c>'s constructor). Under the console's layout the wrapper is in
        /// <c>bin\</c>, so calling it the wrapper's folder sends every <c>%BASE%</c> path there.
        /// </summary>
        [Theory]
        [InlineData("en", "| Injected variables |", "`%BASE%` — folder holding the configuration file", "wrapper executable is.")]
        [InlineData("zh-CN", "| 内置变量 |", "`%BASE%` —— 配置文件所在目录", "包装器可执行文件放在哪里无关")]
        public void BaseIsTheConfigurationFilesFolder(string language, string row, string says, string andNotTheWrappers)
        {
            string line = Row(Guide(language), row);

            Assert.Contains(says, line, StringComparison.Ordinal);
            Assert.Contains(andNotTheWrappers, line, StringComparison.Ordinal);
            Assert.DoesNotContain(
                language == "en" ? "`%BASE%` — folder holding the wrapper" : "`%BASE%` —— 包装器可执行文件所在目录",
                Guide(language),
                StringComparison.Ordinal);
        }

        /// <summary>Roll mode appends <c>.old</c> to each file's own name (<c>RollingLogAppender</c>).</summary>
        [Theory]
        [InlineData("en")]
        [InlineData("zh-CN")]
        public void RollModeNamesTheFilesItKeeps(string language)
        {
            string line = Row(Guide(language), "| `roll` |");

            Assert.Contains("`<logname>.out.log.old`", line, StringComparison.Ordinal);
            Assert.Contains("`<logname>.err.log.old`", line, StringComparison.Ordinal);
            Assert.DoesNotContain("*.old.log", line, StringComparison.Ordinal);
        }

        /// <summary>
        /// Auto refresh runs in the command-line start, stop and restart; a start by Windows only
        /// warns, because reconfiguring a service the manager is starting deadlocks.
        /// </summary>
        [Theory]
        [InlineData("en")]
        [InlineData("zh-CN")]
        public void AutoRefreshSaysWhichStartsItRunsOn(string language)
        {
            string line = Row(Guide(language), "| `autoRefresh` |");

            Assert.Contains("`winsw start`", line, StringComparison.Ordinal);
            Assert.Contains("`sc start`", line, StringComparison.Ordinal);
        }

        /// <summary>
        /// What refresh leaves alone when the file stops declaring it, listed where the elements
        /// refresh applies are.
        /// </summary>
        [Theory]
        [InlineData("en")]
        [InlineData("zh-CN")]
        public void TheGuideListsWhatRefreshDoesNotUndo(string language)
        {
            var bullets = Guide(language).Replace("\r\n", "\n").Split('\n')
                .SkipWhile(l => !l.StartsWith("| `preshutdownTimeout` |", StringComparison.Ordinal))
                .TakeWhile(l => !l.StartsWith("### ", StringComparison.Ordinal))
                .Where(l => l.StartsWith("- ", StringComparison.Ordinal))
                .ToList();

            foreach (string element in new[] { "`<serviceaccount>`", "`<onfailure>`", "`<depend>`", "`<securityDescriptor>`" })
            {
                Assert.Contains(bullets, b => b.StartsWith("- " + element, StringComparison.Ordinal));
            }
        }

        /// <summary>
        /// The console installs in a hidden window, where <c>console</c> waits for typing nobody
        /// can see; an assistant reading "dialog or console" would pick either.
        /// </summary>
        [Theory]
        [InlineData("en", "Use `dialog`")]
        [InlineData("zh-CN", "请用 `dialog`")]
        public void ThePromptRowSaysToUseDialog(string language, string advice)
        {
            Assert.Contains(advice, Row(Guide(language), "| `prompt` |"), StringComparison.Ordinal);
        }

        /// <summary>
        /// Roll-by-time refuses a pattern that changes less than daily, and only once the service
        /// runs (<c>PeriodicRollingCalendar</c>); the pattern is also part of a file name.
        /// </summary>
        [Theory]
        [InlineData("en", "`pattern` uses .NET")]
        [InlineData("zh-CN", "`pattern` 用 .NET")]
        public void ThePatternParagraphNamesWhatTheWrapperRefuses(string language, string start)
        {
            string paragraph = string.Join(
                " ",
                Guide(language).Replace("\r\n", "\n").Split('\n')
                    .SkipWhile(l => !l.StartsWith(start, StringComparison.Ordinal))
                    .TakeWhile(l => l.Length > 0));

            Assert.Contains("`yyyyMM`", paragraph, StringComparison.Ordinal);
            Assert.Contains("`yyyy/MM/dd`", paragraph, StringComparison.Ordinal);
            Assert.Contains("`yyyy-MM-dd`", paragraph, StringComparison.Ordinal);
        }

        private static string Row(string document, string start)
        {
            var rows = document.Replace("\r\n", "\n").Split('\n').Where(l => l.StartsWith(start, StringComparison.Ordinal)).ToList();
            return Assert.Single(rows);
        }

        private static string Guide(string code)
        {
            using var stream = typeof(XmlGuide).Assembly.GetManifestResourceStream("WinSW.Gui.Guide." + code + ".md");
            Assert.NotNull(stream);
            return new StreamReader(stream!).ReadToEnd();
        }
    }
}

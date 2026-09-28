using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
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
        private static readonly Regex ListItem = new(@"^\s*([-*+]|\d+[.)])\s+", RegexOptions.CultureInvariant);

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
        /// The console's Services page goes through those commands and refreshes; its Remote page
        /// starts a service through the service control manager, as services.msc does, and does
        /// not. Worded as the wrapper's own documentation words it.
        /// </summary>
        [Theory]
        [InlineData("en", "Services page", "Remote page")]
        [InlineData("zh-CN", "“服务”页", "“远程”页")]
        public void AutoRefreshSaysWhichOfTheConsolesPagesRefresh(string language, string refreshes, string doesNot)
        {
            string line = Row(Guide(language), "| `autoRefresh` |");

            Assert.Contains(refreshes, line, StringComparison.Ordinal);
            Assert.Contains(doesNot, line, StringComparison.Ordinal);
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

        /// <summary>
        /// The guide window joins the lines of a paragraph, a list item or a quote with a space,
        /// as Markdown does. Between two Chinese characters, or next to Chinese punctuation, that
        /// space is a gap in the middle of a sentence; so the Chinese guide never breaks a line
        /// there, and keeps a block that needs it on one line. A break between Chinese and a word
        /// or a code span is fine: the space is the one the guide writes there anyway.
        /// </summary>
        [Fact]
        public void TheChineseGuideNeverBreaksALineInsideChineseText()
        {
            var lines = Guide("zh-CN").Replace("\r\n", "\n").Split('\n');
            var gaps = new List<string>();
            bool code = false;
            for (int i = 0; i + 1 < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.StartsWith("```", StringComparison.Ordinal))
                {
                    code = !code;
                    continue;
                }

                string next = lines[i + 1].Trim();
                if (code || StandsAlone(line) || StandsAlone(next) || ListItem.IsMatch(next) || line.StartsWith('>') != next.StartsWith('>'))
                {
                    continue;
                }

                string continued = next.TrimStart('>').TrimStart();
                if (continued.Length == 0)
                {
                    continue;
                }

                char before = line[line.Length - 1];
                char after = continued[0];
                if (IsChinesePunctuation(before) || IsChinesePunctuation(after) || (IsChinese(before) && IsChinese(after)))
                {
                    gaps.Add($"line {i + 1}: …{line.Substring(Math.Max(0, line.Length - 12))} / {next.Substring(0, Math.Min(12, next.Length))}…");
                }
            }

            Assert.Empty(gaps);

            static bool StandsAlone(string line) =>
                line.Length == 0 || line.StartsWith("```", StringComparison.Ordinal) || line.StartsWith('|') || line.StartsWith('#')
                || line.Trim('-').Length == 0 || line.Trim('*').Length == 0 || line.Trim('_').Length == 0;

            static bool IsChinese(char c) => c is (>= '\u3400' and <= '\u4DBF') or (>= '\u4E00' and <= '\u9FFF') or (>= '\u3040' and <= '\u30FF');

            // The em dash is left out: this guide spaces it, " —— ", as it does a word.
            static bool IsChinesePunctuation(char c) =>
                c is (>= '\u3000' and <= '\u303F') or (>= '\uFF00' and <= '\uFFEF') or '\u201C' or '\u201D' or '\u2018' or '\u2019' or '\u2026';
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

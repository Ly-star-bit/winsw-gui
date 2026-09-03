using System;
using System.Collections.Generic;
using System.Linq;
using WinSW.Gui.Services;
using Xunit;

namespace WinSW.Gui.Tests
{
    /// <summary>
    /// The configuration cheat sheet is shipped inside the executable and rendered by our own
    /// Markdown reader, so both the resource and the shapes the reader depends on are covered
    /// here: a document that no longer embeds, or a table row that gained a column, would
    /// otherwise only show up as a broken page at runtime.
    /// </summary>
    public class XmlGuideTests
    {
        [Fact]
        public void GuideIsEmbeddedAndComplete()
        {
            string markdown = XmlGuide.Markdown;

            Assert.NotEqual(0, markdown.Length);

            // The sections the prompt and the viewer's contents list refer to by number.
            Assert.Contains("## 1.", markdown, StringComparison.Ordinal);
            Assert.Contains("## 4.", markdown, StringComparison.Ordinal);
            Assert.Contains("## 9.", markdown, StringComparison.Ordinal);

            // A few names whose exact spelling is the point of the document.
            foreach (string name in new[] { "delayedAutoStart", "sharedDirectoryMapping", "stdoutPath", "roll-by-size-time", "resetfailure" })
            {
                Assert.Contains(name, markdown, StringComparison.Ordinal);
            }
        }

        [Fact]
        public void PromptCarriesTheConfigurationBeingEdited()
        {
            string prompt = XmlGuide.BuildPrompt("<service><id>demo</id></service>");

            Assert.Contains(XmlGuide.Markdown.TrimEnd(), prompt, StringComparison.Ordinal);
            Assert.Contains("<id>demo</id>", prompt, StringComparison.Ordinal);
            Assert.Contains("```xml", prompt, StringComparison.Ordinal);
        }

        [Fact]
        public void PromptOmitsAnEmptyConfiguration()
        {
            Assert.DoesNotContain("```xml", XmlGuide.BuildPrompt("   "), StringComparison.Ordinal);
            Assert.DoesNotContain("```xml", XmlGuide.BuildPrompt(null), StringComparison.Ordinal);
        }

        [Theory]
        [InlineData("| a | b |", new[] { "a", "b" })]
        [InlineData("| a | b | c |", new[] { "a", "b", "c" })]
        [InlineData(@"| `Automatic` \| `Manual` | text |", new[] { "`Automatic` | `Manual`", "text" })]
        [InlineData("|  spaced  |  cells  |", new[] { "spaced", "cells" })]
        public void TableRowsSplitOnUnescapedPipesOnly(string row, string[] expected) =>
            Assert.Equal(expected, MarkdownRenderer.SplitRow(row));

        [Fact]
        public void EveryTableInTheGuideIsRectangular()
        {
            foreach (string document in new[] { "en", "zh-CN" }.Select(GuideFor))
            {
                var lines = document.Replace("\r\n", "\n").Split('\n');
                bool inCode = false;
                int columns = 0;

                for (int i = 0; i < lines.Length; i++)
                {
                    if (lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal))
                    {
                        inCode = !inCode;
                        continue;
                    }

                    if (inCode || !lines[i].TrimStart().StartsWith("|", StringComparison.Ordinal))
                    {
                        columns = 0;
                        continue;
                    }

                    var cells = MarkdownRenderer.SplitRow(lines[i]);
                    if (columns == 0)
                    {
                        columns = cells.Count;
                        continue;
                    }

                    Assert.True(
                        cells.Count == columns,
                        $"line {i + 1} has {cells.Count} cells, the table's header has {columns}: {lines[i]}");
                }
            }
        }

        [Theory]
        [InlineData("plain text", "plain text")]
        [InlineData("the `id` element", "the id element")]
        [InlineData("**required** value", "required value")]
        [InlineData("see [the docs](https://example.com)", "see the docs")]
        public void HeadingsAreStrippedOfInlineSyntax(string markdown, string expected) =>
            Assert.Equal(expected, MarkdownRenderer.StripInlineSyntax(markdown));

        private static string GuideFor(string code)
        {
            using var stream = typeof(XmlGuide).Assembly.GetManifestResourceStream("WinSW.Gui.Guide." + code + ".md");
            Assert.NotNull(stream);
            return new System.IO.StreamReader(stream!).ReadToEnd();
        }
    }
}

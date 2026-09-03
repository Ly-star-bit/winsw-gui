using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using WinSW.Gui.Localization;

namespace WinSW.Gui.Services
{
    /// <summary>One heading of a rendered document, for the table of contents.</summary>
    public sealed class GuideHeading
    {
        internal GuideHeading(int level, string text, Block anchor)
        {
            this.Level = level;
            this.Text = text;
            this.Anchor = anchor;
        }

        public int Level { get; }

        public string Text { get; }

        /// <summary>The block to scroll into view when this entry is picked.</summary>
        public Block Anchor { get; }

        /// <summary>Nesting is expressed as an indent in the contents list.</summary>
        public Thickness Indent => new(2 + ((this.Level - 1) * 14), 0, 0, 0);

        public double Weight => this.Level <= 2 ? 1.0 : 0.72;
    }

    /// <summary>
    /// A small Markdown-to-<see cref="FlowDocument"/> renderer, just large enough for the
    /// documents this application ships: headings, fenced code, tables, lists, block quotes,
    /// rules, and the inline forms (code, bold, emphasis, links).
    /// </summary>
    /// <remarks>
    /// A full CommonMark implementation would be a dependency and a much larger surface for
    /// no gain here — the input is our own, checked into the repository next to this code.
    /// Colours are attached with <c>SetResourceReference</c> so the document follows a theme
    /// switch like the rest of the shell.
    /// </remarks>
    public static class MarkdownRenderer
    {
        private static readonly Regex InlineSyntax = new(
            @"(?<code>`[^`]+`)|(?<bold>\*\*[^*]+\*\*)|(?<link>\[[^\]]+\]\([^)]+\))|(?<em>(?<![\w*])\*[^*\s][^*]*\*)",
            RegexOptions.Compiled);

        private static readonly Regex Heading = new(@"^(#{1,6})\s+(.*)$", RegexOptions.Compiled);

        private static readonly Regex BulletItem = new(@"^(\s*)[-*+]\s+(.*)$", RegexOptions.Compiled);

        private static readonly Regex NumberedItem = new(@"^(\s*)\d+[.)]\s+(.*)$", RegexOptions.Compiled);

        /// <summary>Renders <paramref name="markdown"/> and collects its headings.</summary>
        public static FlowDocument Render(string markdown, out IReadOnlyList<GuideHeading> headings)
        {
            var document = new FlowDocument
            {
                FontSize = 14,
                LineHeight = 22,
                PagePadding = new Thickness(30, 16, 30, 60),
                Background = Brushes.Transparent,
                TextAlignment = TextAlignment.Left,
            };

            document.SetResourceReference(FlowDocument.FontFamilyProperty, "BodyFont");
            document.SetResourceReference(FlowDocument.ForegroundProperty, "TextPrimaryBrush");

            var toc = new List<GuideHeading>();
            var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            var paragraph = new List<string>();

            void FlushParagraph()
            {
                if (paragraph.Count == 0)
                {
                    return;
                }

                var block = new Paragraph { Margin = new Thickness(0, 0, 0, 12) };
                AddInlines(block.Inlines, string.Join(" ", paragraph));
                document.Blocks.Add(block);
                paragraph.Clear();
            }

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                string trimmed = line.Trim();

                if (trimmed.StartsWith("```", StringComparison.Ordinal))
                {
                    FlushParagraph();

                    var code = new StringBuilder();
                    for (i++; i < lines.Length && !lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal); i++)
                    {
                        code.Append(lines[i]).Append('\n');
                    }

                    document.Blocks.Add(CodeBlock(code.ToString().TrimEnd('\n')));
                    continue;
                }

                if (trimmed.Length == 0)
                {
                    FlushParagraph();
                    continue;
                }

                if (Heading.Match(line) is { Success: true } heading)
                {
                    FlushParagraph();

                    int level = heading.Groups[1].Value.Length;
                    string text = heading.Groups[2].Value.Trim();
                    var block = HeadingBlock(level, text);
                    document.Blocks.Add(block);
                    toc.Add(new GuideHeading(level, StripInlineSyntax(text), block));

                    if (level <= 2)
                    {
                        // A hairline under the top-level headings gives the page its rhythm.
                        document.Blocks.Add(Rule(new Thickness(0, -2, 0, 14)));
                    }

                    continue;
                }

                if (trimmed is "---" or "***" or "___" || (trimmed.Length > 3 && trimmed.All(c => c == '-')))
                {
                    FlushParagraph();
                    document.Blocks.Add(Rule());
                    continue;
                }

                if (trimmed.StartsWith("|", StringComparison.Ordinal) && i + 1 < lines.Length && IsTableSeparator(lines[i + 1]))
                {
                    FlushParagraph();

                    var rows = new List<string> { line };
                    i++; // the separator row itself carries no content
                    while (i + 1 < lines.Length && lines[i + 1].TrimStart().StartsWith("|", StringComparison.Ordinal))
                    {
                        rows.Add(lines[++i]);
                    }

                    document.Blocks.Add(TableBlock(rows));
                    continue;
                }

                if (trimmed.StartsWith(">", StringComparison.Ordinal))
                {
                    FlushParagraph();

                    var quoted = new List<string>();
                    while (i < lines.Length && lines[i].TrimStart().StartsWith(">", StringComparison.Ordinal))
                    {
                        quoted.Add(lines[i].TrimStart().TrimStart('>').Trim());
                        i++;
                    }

                    i--;
                    document.Blocks.Add(Quote(string.Join(" ", quoted)));
                    continue;
                }

                if (BulletItem.IsMatch(line) || NumberedItem.IsMatch(line))
                {
                    FlushParagraph();
                    document.Blocks.Add(ListBlock(lines, ref i));
                    continue;
                }

                paragraph.Add(trimmed);
            }

            FlushParagraph();

            headings = toc;
            return document;
        }

        /// <summary>Removes the inline markers so a heading reads cleanly in the contents list.</summary>
        public static string StripInlineSyntax(string text) =>
            InlineSyntax.Replace(text, m =>
                m.Groups["code"].Success ? m.Value.Trim('`') :
                m.Groups["bold"].Success ? m.Value.Trim('*') :
                m.Groups["em"].Success ? m.Value.Trim('*') :
                LinkText(m.Value));

        private static Block HeadingBlock(int level, string text)
        {
            var block = new Paragraph
            {
                FontSize = level switch { 1 => 26, 2 => 20, 3 => 16, _ => 14 },
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, level <= 2 ? 22 : 16, 0, level <= 2 ? 12 : 8),
            };

            block.SetResourceReference(TextElement.ForegroundProperty, level <= 2 ? "TextPrimaryBrush" : "TextSecondaryBrush");
            AddInlines(block.Inlines, text);
            return block;
        }

        private static Block Rule() => Rule(new Thickness(0, 10, 0, 18));

        private static Block Rule(Thickness margin)
        {
            var border = new Border { Height = 1, Margin = margin };
            border.SetResourceReference(Border.BackgroundProperty, "CardBorderBrush");
            return new BlockUIContainer(border) { Margin = default };
        }

        private static Block Quote(string text)
        {
            var body = new TextBlock { TextWrapping = TextWrapping.Wrap, LineHeight = 22 };
            body.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            AddInlines(body.Inlines, text);

            var accent = new Border { Width = 3, Margin = new Thickness(0, 0, 12, 0), CornerRadius = new CornerRadius(2) };
            accent.SetResourceReference(Border.BackgroundProperty, "AccentBrush");

            var panel = new DockPanel();
            DockPanel.SetDock(accent, Dock.Left);
            panel.Children.Add(accent);
            panel.Children.Add(body);

            var card = new Border { Padding = new Thickness(14, 12, 14, 12), CornerRadius = new CornerRadius(6), Child = panel };
            card.SetResourceReference(Border.BackgroundProperty, "CardBrush");

            return new BlockUIContainer(card) { Margin = new Thickness(0, 0, 0, 14) };
        }

        /// <summary>
        /// Code is shown in a read-only text box rather than a run: it must stay selectable
        /// and horizontally scrollable, and it carries its own copy button, which is the
        /// point of the whole viewer.
        /// </summary>
        private static Block CodeBlock(string code)
        {
            var text = new TextBox
            {
                Text = code,
                IsReadOnly = true,
                IsReadOnlyCaretVisible = true,
                BorderThickness = default,
                Background = Brushes.Transparent,
                Padding = new Thickness(14, 12, 14, 12),
                FontSize = 12.5,
                TextWrapping = TextWrapping.NoWrap,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            };

            text.SetResourceReference(Control.FontFamilyProperty, "MonoFont");
            text.SetResourceReference(Control.ForegroundProperty, "TextPrimaryBrush");

            string idle = Localizer.Get("G.CopyCode");
            var copy = new Button
            {
                Content = idle,
                Padding = new Thickness(9, 2, 9, 3),
                FontSize = 11,
                Margin = new Thickness(0, 6, 8, 0),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Opacity = 0.65,
            };

            copy.Click += (_, _) =>
            {
                if (SystemShell.TryCopy(code))
                {
                    copy.Content = Localizer.Get("G.Copied");
                    var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.4) };
                    timer.Tick += (s, _) =>
                    {
                        timer.Stop();
                        copy.Content = idle;
                    };
                    timer.Start();
                }
            };

            var grid = new Grid();
            grid.Children.Add(text);
            grid.Children.Add(copy);

            var card = new Border { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Child = grid };
            card.SetResourceReference(Border.BackgroundProperty, "InputBrush");
            card.SetResourceReference(Border.BorderBrushProperty, "CardBorderBrush");

            return new BlockUIContainer(card) { Margin = new Thickness(0, 2, 0, 16) };
        }

        private static Block ListBlock(string[] lines, ref int index)
        {
            bool ordered = NumberedItem.IsMatch(lines[index]) && !BulletItem.IsMatch(lines[index]);
            var list = new List
            {
                MarkerStyle = ordered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                Margin = new Thickness(20, 0, 0, 14),
                Padding = default,
            };

            ListItem? last = null;
            for (; index < lines.Length; index++)
            {
                var match = BulletItem.Match(lines[index]);
                if (!match.Success)
                {
                    match = NumberedItem.Match(lines[index]);
                }

                if (!match.Success)
                {
                    // A wrapped continuation line belongs to the item above it.
                    string continuation = lines[index].Trim();
                    if (continuation.Length > 0 && last != null && lines[index].StartsWith("  ", StringComparison.Ordinal))
                    {
                        if (last.Blocks.LastBlock is Paragraph tail)
                        {
                            tail.Inlines.Add(new Run(" "));
                            AddInlines(tail.Inlines, continuation);
                        }

                        continue;
                    }

                    break;
                }

                var body = new Paragraph { Margin = new Thickness(0, 0, 0, 4) };
                AddInlines(body.Inlines, match.Groups[2].Value.Trim());

                if (match.Groups[1].Value.Length >= 2 && last != null)
                {
                    // One level of nesting is enough for the documents we ship.
                    var nested = last.Blocks.OfType<List>().LastOrDefault();
                    if (nested is null)
                    {
                        nested = new List { MarkerStyle = TextMarkerStyle.Circle, Margin = new Thickness(18, 2, 0, 2), Padding = default };
                        last.Blocks.Add(nested);
                    }

                    nested.ListItems.Add(new ListItem(body));
                    continue;
                }

                last = new ListItem(body);
                list.ListItems.Add(last);
            }

            index--;
            return list;
        }

        private static bool IsTableSeparator(string line)
        {
            string trimmed = line.Trim();
            return trimmed.StartsWith("|", StringComparison.Ordinal)
                && trimmed.Length > 2
                && trimmed.All(c => c is '|' or '-' or ':' or ' ');
        }

        private static Block TableBlock(IReadOnlyList<string> rows)
        {
            var cells = rows.Select(SplitRow).ToList();
            int columns = cells.Max(r => r.Count);

            var table = new Table { CellSpacing = 0, Margin = new Thickness(0, 2, 0, 18) };
            for (int c = 0; c < columns; c++)
            {
                // The first column is usually the element name and wants to stay narrow.
                table.Columns.Add(new TableColumn { Width = new GridLength(c == 0 ? 1.1 : 2, GridUnitType.Star) });
            }

            var group = new TableRowGroup();
            table.RowGroups.Add(group);

            for (int r = 0; r < cells.Count; r++)
            {
                var row = new TableRow();
                for (int c = 0; c < columns; c++)
                {
                    var body = new Paragraph { Margin = new Thickness(10, 7, 10, 7), FontSize = 13, LineHeight = 19 };
                    AddInlines(body.Inlines, c < cells[r].Count ? cells[r][c] : string.Empty);

                    var cell = new TableCell(body) { BorderThickness = new Thickness(0, 0, 0, 1) };
                    cell.SetResourceReference(TableCell.BorderBrushProperty, "CardBorderBrush");

                    if (r == 0)
                    {
                        body.FontWeight = FontWeights.SemiBold;
                        cell.SetResourceReference(TableCell.BackgroundProperty, "CardBrush");
                    }

                    row.Cells.Add(cell);
                }

                group.Rows.Add(row);
            }

            return table;
        }

        /// <summary>Splits a table row on unescaped pipes.</summary>
        internal static List<string> SplitRow(string row)
        {
            var result = new List<string>();
            var cell = new StringBuilder();

            string trimmed = row.Trim();
            for (int i = 0; i < trimmed.Length; i++)
            {
                char c = trimmed[i];
                if (c == '\\' && i + 1 < trimmed.Length && trimmed[i + 1] == '|')
                {
                    cell.Append('|');
                    i++;
                }
                else if (c == '|')
                {
                    result.Add(cell.ToString().Trim());
                    cell.Clear();
                }
                else
                {
                    cell.Append(c);
                }
            }

            result.Add(cell.ToString().Trim());

            // A row is written as |a|b|, so the outer pipes leave an empty cell on each side.
            if (result.Count > 0 && result[0].Length == 0)
            {
                result.RemoveAt(0);
            }

            if (result.Count > 0 && result[^1].Length == 0)
            {
                result.RemoveAt(result.Count - 1);
            }

            return result;
        }

        private static void AddInlines(InlineCollection target, string text)
        {
            int cursor = 0;
            foreach (Match match in InlineSyntax.Matches(text))
            {
                if (match.Index > cursor)
                {
                    target.Add(new Run(text[cursor..match.Index]));
                }

                if (match.Groups["code"].Success)
                {
                    var run = new Run(match.Value.Trim('`')) { FontSize = 12.5 };
                    run.SetResourceReference(TextElement.FontFamilyProperty, "MonoFont");
                    run.SetResourceReference(TextElement.ForegroundProperty, "AccentBrush");
                    target.Add(run);
                }
                else if (match.Groups["bold"].Success)
                {
                    target.Add(new Bold(new Run(match.Value.Trim('*'))));
                }
                else if (match.Groups["link"].Success)
                {
                    target.Add(Link(match.Value));
                }
                else
                {
                    target.Add(new Italic(new Run(match.Value.Trim('*'))));
                }

                cursor = match.Index + match.Length;
            }

            if (cursor < text.Length)
            {
                target.Add(new Run(text[cursor..]));
            }
        }

        private static Inline Link(string markdown)
        {
            string label = LinkText(markdown);
            string target = markdown[(markdown.IndexOf('(') + 1)..].TrimEnd(')');

            var link = new Hyperlink(new Run(label)) { ToolTip = target };
            link.SetResourceReference(TextElement.ForegroundProperty, "AccentBrush");

            // Only absolute links can be followed; the relative ones point at sibling
            // documents in the repository, so they are resolved against the project's docs.
            link.Click += (_, _) => SystemShell.OpenUrl(
                target.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? target
                    : ProjectLinks.DocsBase + target);

            return link;
        }

        private static string LinkText(string markdown)
        {
            int end = markdown.IndexOf(']');
            return end > 1 ? markdown[1..end] : markdown;
        }
    }
}

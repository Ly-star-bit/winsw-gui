using System;
using System.Collections.Generic;

namespace WinSW.Gui.Services
{
    public enum DiffKind
    {
        /// <summary>In both.</summary>
        Same,

        /// <summary>Only in the old text.</summary>
        Removed,

        /// <summary>Only in the new text.</summary>
        Added,

        /// <summary>A run of unchanged lines left out; <see cref="DiffLine.Count"/> says how many.</summary>
        Gap,
    }

    /// <summary>One line of a comparison, numbered on the side or sides it appears on.</summary>
    public sealed class DiffLine
    {
        public DiffLine(DiffKind kind, string text, int? oldNumber, int? newNumber, int count = 0)
        {
            this.Kind = kind;
            this.Text = text;
            this.OldNumber = oldNumber;
            this.NewNumber = newNumber;
            this.Count = count;
        }

        public DiffKind Kind { get; }

        public string Text { get; }

        public int? OldNumber { get; }

        public int? NewNumber { get; }

        /// <summary>For a gap, the number of lines it stands for.</summary>
        public int Count { get; }

        public string Marker => this.Kind switch
        {
            DiffKind.Removed => "−",
            DiffKind.Added => "+",
            _ => " ",
        };
    }

    /// <summary>
    /// Line comparison of two texts, for showing what restoring a configuration would change.
    /// </summary>
    /// <remarks>
    /// A longest-common-subsequence table over the lines, after the common beginning and end
    /// are taken off. A configuration is a few hundred lines, so the table is small; past
    /// <see cref="MaxCells"/> the middle is shown as removed-then-added, which is still true,
    /// only not the shortest way to say it.
    /// </remarks>
    public static class TextDiff
    {
        /// <summary>Unchanged lines kept on either side of a change.</summary>
        public const int Context = 3;

        internal const long MaxCells = 4_000_000;

        /// <summary>
        /// The lines of <paramref name="oldText"/> and <paramref name="newText"/>, with runs of
        /// unchanged lines longer than the context on both sides folded into a gap. Empty when
        /// the two are the same.
        /// </summary>
        public static IReadOnlyList<DiffLine> Compare(string oldText, string newText)
        {
            var full = Full(Lines(oldText), Lines(newText));
            return full.TrueForAll(l => l.Kind == DiffKind.Same) ? Array.Empty<DiffLine>() : Fold(full);
        }

        /// <summary>Line endings do not count as a difference; a final line break adds no line.</summary>
        internal static string[] Lines(string text)
        {
            string normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
            if (normalized.EndsWith('\n'))
            {
                normalized = normalized.Substring(0, normalized.Length - 1);
            }

            return normalized.Length == 0 ? Array.Empty<string>() : normalized.Split('\n');
        }

        internal static List<DiffLine> Full(string[] a, string[] b)
        {
            int prefix = 0;
            while (prefix < a.Length && prefix < b.Length && a[prefix] == b[prefix])
            {
                prefix++;
            }

            int suffix = 0;
            while (suffix < a.Length - prefix && suffix < b.Length - prefix && a[a.Length - 1 - suffix] == b[b.Length - 1 - suffix])
            {
                suffix++;
            }

            var result = new List<DiffLine>(a.Length + b.Length);
            for (int i = 0; i < prefix; i++)
            {
                result.Add(new DiffLine(DiffKind.Same, a[i], i + 1, i + 1));
            }

            int n = a.Length - prefix - suffix;
            int m = b.Length - prefix - suffix;

            if ((long)(n + 1) * (m + 1) > MaxCells)
            {
                for (int i = 0; i < n; i++)
                {
                    result.Add(new DiffLine(DiffKind.Removed, a[prefix + i], prefix + i + 1, null));
                }

                for (int j = 0; j < m; j++)
                {
                    result.Add(new DiffLine(DiffKind.Added, b[prefix + j], null, prefix + j + 1));
                }
            }
            else
            {
                // lcs[i, j]: the longest common run of a[prefix + i ..] and b[prefix + j ..].
                var lcs = new int[n + 1, m + 1];
                for (int i = n - 1; i >= 0; i--)
                {
                    for (int j = m - 1; j >= 0; j--)
                    {
                        lcs[i, j] = a[prefix + i] == b[prefix + j]
                            ? lcs[i + 1, j + 1] + 1
                            : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
                    }
                }

                int x = 0;
                int y = 0;
                while (x < n || y < m)
                {
                    if (x < n && y < m && a[prefix + x] == b[prefix + y])
                    {
                        result.Add(new DiffLine(DiffKind.Same, a[prefix + x], prefix + x + 1, prefix + y + 1));
                        x++;
                        y++;
                    }
                    else if (x < n && (y == m || lcs[x + 1, y] >= lcs[x, y + 1]))
                    {
                        // A line replaced shows its old form first, as a unified diff does.
                        result.Add(new DiffLine(DiffKind.Removed, a[prefix + x], prefix + x + 1, null));
                        x++;
                    }
                    else
                    {
                        result.Add(new DiffLine(DiffKind.Added, b[prefix + y], null, prefix + y + 1));
                        y++;
                    }
                }
            }

            for (int k = 0; k < suffix; k++)
            {
                int i = a.Length - suffix + k;
                int j = b.Length - suffix + k;
                result.Add(new DiffLine(DiffKind.Same, a[i], i + 1, j + 1));
            }

            return result;
        }

        /// <summary>Keeps <see cref="Context"/> unchanged lines around each change and folds the rest.</summary>
        internal static List<DiffLine> Fold(List<DiffLine> lines)
        {
            var keep = new bool[lines.Count];
            for (int i = 0; i < lines.Count; i++)
            {
                if (lines[i].Kind != DiffKind.Same)
                {
                    for (int k = Math.Max(0, i - Context); k <= Math.Min(lines.Count - 1, i + Context); k++)
                    {
                        keep[k] = true;
                    }
                }
            }

            var folded = new List<DiffLine>();
            int run = 0;
            for (int i = 0; i < lines.Count; i++)
            {
                if (keep[i])
                {
                    if (run > 0)
                    {
                        folded.Add(new DiffLine(DiffKind.Gap, string.Empty, null, null, run));
                        run = 0;
                    }

                    folded.Add(lines[i]);
                }
                else
                {
                    run++;
                }
            }

            if (run > 0)
            {
                folded.Add(new DiffLine(DiffKind.Gap, string.Empty, null, null, run));
            }

            return folded;
        }
    }
}

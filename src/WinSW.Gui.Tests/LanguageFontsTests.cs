using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using WinSW.Gui.Localization;
using Xunit;

namespace WinSW.Gui.Tests
{
    /// <summary>
    /// <c>BodyFont</c> and <c>MonoFont</c> are declared per language, in the string dictionaries.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Which face draws a Han character decides whether a Chinese line looks Chinese: YaHei for
    /// Simplified, JhengHei for Traditional, a Japanese face for Japanese. A font name that
    /// does not exist on the server is skipped without a word, so a typo or a face only newer
    /// Windows has would go unnoticed on a development machine and show on Server 2012 R2.
    /// </para>
    /// <para>
    /// Read from the source files, as <see cref="LocalizationTests"/> does: a font list is
    /// just text until WPF resolves it, and resolving it needs Windows.
    /// </para>
    /// </remarks>
    public class LanguageFontsTests
    {
        private const string Latin = "Latin";
        private const string Simplified = "Simplified Chinese";
        private const string Traditional = "Traditional Chinese";
        private const string Japanese = "Japanese";

        private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

        private static readonly string[] TextFonts = { "BodyFont", "MonoFont" };

        /// <summary>Every face a list may name: the script it is there for, whether Server 2012 R2 has it, and whether it is monospaced.</summary>
        private static readonly Dictionary<string, (string Script, bool OnServer2012R2, bool Monospaced)> Faces =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["Segoe UI"] = (Latin, true, false),
                ["Consolas"] = (Latin, true, true),
                ["Courier New"] = (Latin, true, true),
                ["Cascadia Mono"] = (Latin, false, true),
                ["Microsoft YaHei UI"] = (Simplified, true, false),
                ["Microsoft YaHei"] = (Simplified, true, false),
                ["Microsoft JhengHei UI"] = (Traditional, true, false),
                ["Microsoft JhengHei"] = (Traditional, true, false),
                ["Yu Gothic UI"] = (Japanese, false, false),
                ["Meiryo UI"] = (Japanese, true, false),
                ["Meiryo"] = (Japanese, true, false),
            };

        /// <summary>
        /// The script whose glyph shapes Han characters take in each language. English uses
        /// Simplified Chinese: service names and log lines are often Chinese whatever language
        /// the console is in.
        /// </summary>
        private static readonly Dictionary<string, string> HanScript = new(StringComparer.Ordinal)
        {
            ["en"] = Simplified,
            ["zh-CN"] = Simplified,
            ["zh-TW"] = Traditional,
            ["ja"] = Japanese,
        };

        [Fact]
        public void EveryLanguageDeclaresBothTextFontsAsFontFamilies()
        {
            foreach (var language in Localizer.Languages)
            {
                foreach (string key in TextFonts)
                {
                    var element = Declaration(language.Code, key);

                    Assert.True(element != null, $"Strings.{language.Code}.xaml does not declare {key}");
                    Assert.True(element!.Name == Presentation + "FontFamily", $"Strings.{language.Code}.xaml declares {key} as {element.Name.LocalName}");
                    Assert.NotEmpty(FacesOf(language.Code, key));
                }
            }
        }

        [Fact]
        public void EveryLanguageIsMappedToAScript()
        {
            foreach (var language in Localizer.Languages)
            {
                Assert.True(HanScript.ContainsKey(language.Code), $"No Han script is expected for {language.Code}; add it to this test");
            }
        }

        /// <summary>A name WPF cannot find is skipped silently; this catches a misspelt one.</summary>
        [Fact]
        public void EveryFaceNamedIsAKnownWindowsFont()
        {
            foreach (var (code, key, faces) in AllLists())
            {
                var unknown = faces.Where(f => !Faces.ContainsKey(f)).ToList();
                Assert.True(unknown.Count == 0, $"{key} in Strings.{code}.xaml names {string.Join(", ", unknown)}, which this test does not know");
            }
        }

        /// <summary>
        /// A face newer Windows has may come first, but a face Server 2012 R2 has must follow
        /// it for the same script, or that script falls to WPF's own fallback there.
        /// </summary>
        [Fact]
        public void EveryNewerFaceIsFollowedByOneServer2012R2Has()
        {
            foreach (var (code, key, faces) in AllLists())
            {
                for (int i = 0; i < faces.Count; i++)
                {
                    var face = Faces[faces[i]];
                    if (face.OnServer2012R2)
                    {
                        continue;
                    }

                    bool covered = faces.Skip(i + 1).Any(f => Faces[f].Script == face.Script && Faces[f].OnServer2012R2);
                    Assert.True(covered, $"{key} in Strings.{code}.xaml: nothing after {faces[i]} draws {face.Script} on Server 2012 R2");
                }
            }
        }

        /// <summary>
        /// The CJK faces carry Latin letters of their own. Named first, one would draw every
        /// letter too, in its own design and, in <c>MonoFont</c>, proportionally spaced.
        /// </summary>
        [Fact]
        public void EveryListDrawsLatinFirst()
        {
            foreach (var (code, key, faces) in AllLists())
            {
                Assert.True(Faces[faces[0]].Script == Latin, $"{key} in Strings.{code}.xaml starts with {faces[0]}");
            }
        }

        /// <summary>
        /// The face that draws Han characters is the first one of any CJK script in the list,
        /// since the Latin faces before it have none.
        /// </summary>
        [Fact]
        public void EveryListDrawsHanInTheLanguagesOwnScript()
        {
            foreach (var (code, key, faces) in AllLists())
            {
                string? first = faces.FirstOrDefault(f => Faces[f].Script != Latin);

                Assert.True(first != null, $"{key} in Strings.{code}.xaml names no CJK face");
                Assert.True(
                    Faces[first!].Script == HanScript[code],
                    $"{key} in Strings.{code}.xaml draws Han with {first}, a {Faces[first!].Script} face; {code} wants {HanScript[code]}");
            }
        }

        /// <summary>
        /// Log lines and XML line up only in a monospaced face: every Latin face in
        /// <c>MonoFont</c> has to be one, or a missing Consolas would turn columns ragged.
        /// </summary>
        [Fact]
        public void MonoFontNamesOnlyMonospacedLatinFaces()
        {
            foreach (var language in Localizer.Languages)
            {
                var proportional = FacesOf(language.Code, "MonoFont")
                    .Where(f => Faces[f].Script == Latin && !Faces[f].Monospaced)
                    .ToList();

                Assert.True(proportional.Count == 0, $"MonoFont in Strings.{language.Code}.xaml names {string.Join(", ", proportional)}");
            }
        }

        /// <summary>
        /// The string dictionary is merged after the palette, so a font the palette declared as
        /// well would be dead, and would come back to life if the order ever changed.
        /// </summary>
        [Fact]
        public void OnlyTheStringDictionariesDeclareTheTextFonts()
        {
            var elsewhere = new List<string>();
            foreach (string file in Directory.EnumerateFiles(GuiRoot, "*.xaml", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(GuiRoot, file);
                if (relative.StartsWith("Localization", StringComparison.OrdinalIgnoreCase)
                    || relative.Split(Path.DirectorySeparatorChar).Any(p => p is "obj" or "bin"))
                {
                    continue;
                }

                var keys = XDocument.Load(file).Descendants()
                    .Select(e => (string?)e.Attribute(X + "Key"))
                    .Where(k => k != null && TextFonts.Contains(k))
                    .ToList();

                elsewhere.AddRange(keys.Select(k => $"{relative} declares {k}"));
            }

            Assert.True(elsewhere.Count == 0, string.Join("; ", elsewhere));
        }

        private static IEnumerable<(string Code, string Key, List<string> Faces)> AllLists() =>
            Localizer.Languages.SelectMany(l => TextFonts.Select(k => (l.Code, k, FacesOf(l.Code, k))));

        /// <summary>The face names of a font list, in order, as WPF reads them: comma-separated, trimmed.</summary>
        private static List<string> FacesOf(string code, string key) =>
            (Declaration(code, key)?.Value ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();

        private static XElement? Declaration(string code, string key) =>
            XDocument.Load(Path.Combine(GuiRoot, "Localization", $"Strings.{code}.xaml"))
                .Descendants()
                .FirstOrDefault(e => (string?)e.Attribute(X + "Key") == key);

        /// <summary>The WinSW.Gui project directory, found upwards from the test binary.</summary>
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

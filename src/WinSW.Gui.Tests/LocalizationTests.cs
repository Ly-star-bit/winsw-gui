using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using WinSW.Gui.Localization;
using Xunit;

namespace WinSW.Gui.Tests
{
    /// <summary>
    /// The four dictionaries have to carry the same keys.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A key present in one language and missing from another is invisible until somebody
    /// switches to that language and finds a raw <c>M.Dash.Something</c> on a button:
    /// <see cref="Localizer.Get"/> returns the key when the resource is not found, which is
    /// the right behaviour at runtime and the reason nothing else catches this.
    /// </para>
    /// <para>
    /// Read from the source files rather than from the compiled dictionaries: a
    /// <see cref="System.Windows.ResourceDictionary"/> wants an <see cref="System.Windows.Application"/>
    /// and an STA thread, and what is being checked here is the source anyway.
    /// </para>
    /// </remarks>
    public class LocalizationTests
    {
        private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

        [Fact]
        public void EveryLanguageIsShippedAsADictionary()
        {
            foreach (var language in Localizer.Languages)
            {
                Assert.True(File.Exists(PathFor(language.Code)), $"Strings.{language.Code}.xaml is missing");
            }
        }

        [Fact]
        public void EveryLanguageCarriesTheSameKeys()
        {
            var english = KeysOf("en");
            Assert.NotEmpty(english);

            foreach (var language in Localizer.Languages.Where(l => l.Code != "en"))
            {
                var keys = KeysOf(language.Code);

                var missing = english.Except(keys).OrderBy(k => k, StringComparer.Ordinal).ToList();
                var extra = keys.Except(english).OrderBy(k => k, StringComparer.Ordinal).ToList();

                Assert.True(
                    missing.Count == 0,
                    $"Strings.{language.Code}.xaml is missing: {string.Join(", ", missing)}");

                Assert.True(
                    extra.Count == 0,
                    $"Strings.{language.Code}.xaml has keys English does not: {string.Join(", ", extra)}");
            }
        }

        [Fact]
        public void NoLanguageDeclaresAKeyTwice()
        {
            foreach (var language in Localizer.Languages)
            {
                var all = Document(language.Code).Descendants()
                    .Select(e => (string?)e.Attribute(X + "Key"))
                    .Where(k => k != null)
                    .ToList();

                var duplicates = all.GroupBy(k => k, StringComparer.Ordinal)
                    .Where(g => g.Count() > 1)
                    .Select(g => g.Key)
                    .ToList();

                Assert.True(
                    duplicates.Count == 0,
                    $"Strings.{language.Code}.xaml declares twice: {string.Join(", ", duplicates!)}");
            }
        }

        /// <summary>
        /// Every key the code asks <see cref="Localizer.Get"/> or <see cref="Localizer.Format"/>
        /// for by a literal has to exist, or that call renders the key itself on screen.
        /// </summary>
        [Fact]
        public void EveryKeyTheCodeAsksForByNameExists()
        {
            var defined = KeysOf("en");
            var asked = new SortedSet<string>(StringComparer.Ordinal);

            var call = new Regex(@"Localizer\.(?:Get|Format)\(""([^""]+)""", RegexOptions.CultureInvariant);
            foreach (string file in Directory.EnumerateFiles(GuiRoot, "*.cs", SearchOption.AllDirectories))
            {
                foreach (Match match in call.Matches(File.ReadAllText(file)))
                {
                    asked.Add(match.Groups[1].Value);
                }
            }

            Assert.NotEmpty(asked);

            var undefined = asked.Except(defined).ToList();
            Assert.True(undefined.Count == 0, "Asked for but not defined: " + string.Join(", ", undefined));
        }

        /// <summary>
        /// A <c>{0}</c> that one language has and another does not is a
        /// <see cref="string.Format(IFormatProvider, string, object?[])"/> that either drops
        /// an argument or throws, depending on which way round it is.
        /// </summary>
        [Fact]
        public void EveryLanguageUsesTheSamePlaceholders()
        {
            var placeholder = new Regex(@"\{(\d+)[^}]*\}", RegexOptions.CultureInvariant);
            var english = ValuesOf("en");

            foreach (var language in Localizer.Languages.Where(l => l.Code != "en"))
            {
                var values = ValuesOf(language.Code);

                foreach (var (key, text) in english)
                {
                    if (!values.TryGetValue(key, out string? translated))
                    {
                        continue;
                    }

                    var expected = Indexes(placeholder, text);
                    var actual = Indexes(placeholder, translated);

                    Assert.True(
                        expected.SetEquals(actual),
                        $"{language.Code} '{key}' uses {{{string.Join(",", actual)}}} where English uses {{{string.Join(",", expected)}}}");
                }
            }

            static SortedSet<string> Indexes(Regex pattern, string text) =>
                new(pattern.Matches(text).Select(m => m.Groups[1].Value), StringComparer.Ordinal);
        }

        private static HashSet<string> KeysOf(string code) =>
            new(ValuesOf(code).Keys, StringComparer.Ordinal);

        private static Dictionary<string, string> ValuesOf(string code)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var element in Document(code).Descendants())
            {
                if ((string?)element.Attribute(X + "Key") is { } key)
                {
                    result[key] = element.Value;
                }
            }

            return result;
        }

        private static XDocument Document(string code) => XDocument.Load(PathFor(code));

        private static string PathFor(string code) =>
            Path.Combine(GuiRoot, "Localization", $"Strings.{code}.xaml");

        /// <summary>
        /// The WinSW.Gui project directory. The test binary lives under the artifacts folder,
        /// so the tree is walked upwards for the solution rather than guessed at by depth.
        /// </summary>
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

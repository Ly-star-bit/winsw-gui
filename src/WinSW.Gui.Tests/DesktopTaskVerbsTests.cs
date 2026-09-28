using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using WinSW.Gui.Localization;
using WinSW.Gui.Services;
using Xunit;

namespace WinSW.Gui.Tests
{
    /// <summary>
    /// The desktop-task page names what it is doing through the dictionaries, so a Chinese
    /// page says 已禁用“robot” rather than “robot”的 disable 已完成.
    /// </summary>
    /// <remarks>
    /// Read from the dictionary files, like <see cref="LocalizationTests"/>: loading them
    /// through <see cref="Localizer"/> needs WPF.
    /// </remarks>
    public class DesktopTaskVerbsTests
    {
        private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

        /// <summary>A key missing from a dictionary would put the key itself in the sentence.</summary>
        [Fact]
        public void EveryOperationIsNamedInEveryLanguage()
        {
            var keys = Enum.GetValues<DesktopTaskOperation>().Select(DesktopTasks.VerbKey).ToList();

            foreach (var language in Localizer.Languages)
            {
                var values = ValuesOf(language.Code);
                var missing = keys.Where(k => !values.TryGetValue(k, out string? text) || string.IsNullOrWhiteSpace(text)).ToList();
                Assert.True(missing.Count == 0, $"Strings.{language.Code}.xaml is missing: {string.Join(", ", missing)}");
            }
        }

        /// <summary>
        /// Two operations that read alike would report a stop as a disable. Close pairs exist:
        /// 停止 and 停用 differ by one character in Traditional Chinese.
        /// </summary>
        [Fact]
        public void NoTwoOperationsReadAlikeInAnyLanguage()
        {
            var operations = Enum.GetValues<DesktopTaskOperation>();

            foreach (var language in Localizer.Languages)
            {
                var values = ValuesOf(language.Code);
                var alike = operations
                    .GroupBy(o => values.GetValueOrDefault(DesktopTasks.VerbKey(o), string.Empty).Trim(), StringComparer.OrdinalIgnoreCase)
                    .Where(g => g.Count() > 1)
                    .Select(g => $"'{g.Key}' for {string.Join(" and ", g)}")
                    .ToList();

                Assert.True(alike.Count == 0, $"Strings.{language.Code}.xaml: {string.Join("; ", alike)}");
            }
        }

        private static Dictionary<string, string> ValuesOf(string code)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var element in XDocument.Load(Path.Combine(GuiRoot, "Localization", $"Strings.{code}.xaml")).Descendants())
            {
                if ((string?)element.Attribute(X + "Key") is { } key)
                {
                    result[key] = element.Value;
                }
            }

            return result;
        }

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

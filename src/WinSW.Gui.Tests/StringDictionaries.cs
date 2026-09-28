using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Linq;
using Xunit;

namespace WinSW.Gui.Tests
{
    /// <summary>
    /// The four string dictionaries read from the source, for tests of code that names its keys
    /// through a rule rather than by a literal <c>Localizer.Get("…")</c>, which
    /// <see cref="LocalizationTests"/> cannot see. Read from the files, as there: a
    /// <see cref="System.Windows.ResourceDictionary"/> wants WPF and an STA thread.
    /// </summary>
    internal static class StringDictionaries
    {
        private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

        /// <summary>Every key <c>Strings.&lt;code&gt;.xaml</c> declares, with its text.</summary>
        public static Dictionary<string, string> ValuesOf(string code)
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

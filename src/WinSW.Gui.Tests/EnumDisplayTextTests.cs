using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.ServiceProcess;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using WinSW.Gui.Localization;
using WinSW.Gui.Services;
using Xunit;

namespace WinSW.Gui.Tests
{
    /// <summary>
    /// An enumeration put on screen as it is shows its English member name whatever the
    /// language: StartPending in the middle of a Chinese page. What a view shows goes through
    /// a dictionary key instead.
    /// </summary>
    /// <remarks>
    /// Read from the source, like <see cref="LocalizationTests"/>: the views need WPF to load,
    /// and a binding is only resolved against its data context at run time. So this checks
    /// what can be checked cheaply — a view showing, by name and without a converter, a
    /// property the code declares with an enumeration type — and not a list of enumeration
    /// values handed to a ComboBox, or text a view model formats from one.
    /// </remarks>
    public class EnumDisplayTextTests
    {
        private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

        /// <summary>The attributes whose bound value is shown as text, bound by path.</summary>
        private static readonly Regex DisplayBinding = new(
            @"\b(?:Text|Content|Header|ToolTip|Title)=""\{Binding\s+(?:Path=)?(?<path>[\w.]+)(?<rest>[^""]*)\}""",
            RegexOptions.CultureInvariant);

        /// <summary>A public property and the type it is declared with, nullable or not.</summary>
        private static readonly Regex PublicProperty = new(
            @"\bpublic\s+(?:(?:static|virtual|override|new|required)\s+)*(?<type>[\w.]+)\??\s+(?<name>\w+)\s*(?:\{|=>)",
            RegexOptions.CultureInvariant);

        [Fact]
        public void NoViewShowsAnEnumerationByItsMemberName()
        {
            var properties = EnumProperties();

            // The scan has to see the properties it is there for, or it proves nothing.
            Assert.Contains("Status", properties);
            Assert.Contains("Health", properties);

            var offenders = new List<string>();
            foreach (string file in Directory.EnumerateFiles(GuiRoot, "*.xaml", SearchOption.AllDirectories))
            {
                string text = File.ReadAllText(file);
                foreach (Match match in DisplayBinding.Matches(text))
                {
                    if (match.Groups["rest"].Value.Contains("Converter=", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    string name = match.Groups["path"].Value.Split('.')[^1];
                    if (properties.Contains(name))
                    {
                        int line = text.Take(match.Index).Count(c => c == '\n') + 1;
                        offenders.Add($"{Path.GetFileName(file)}:{line} shows {name}");
                    }
                }
            }

            Assert.True(offenders.Count == 0, "Shown as the enumeration's own name: " + string.Join("; ", offenders));
        }

        /// <summary>
        /// The Remote page's status pill and its messages name statuses and actions through
        /// keys; a key missing from a dictionary would put the key itself on screen.
        /// </summary>
        [Fact]
        public void TheRemotePageNamesEveryStatusAndActionThroughTheDictionaries()
        {
            var keys = new SortedSet<string>(StringComparer.Ordinal) { RemoteServiceStatus.StatusKey(null) };
            foreach (var status in Enum.GetValues<ServiceControllerStatus>())
            {
                string key = RemoteServiceStatus.StatusKey(status);
                Assert.NotEqual(RemoteServiceStatus.StatusKey(null), key);
                keys.Add(key);
            }

            foreach (var action in Enum.GetValues<RemoteAction>())
            {
                keys.Add(RemoteMonitor.VerbKey(action));
            }

            foreach (var language in Localizer.Languages)
            {
                var defined = KeysOf(language.Code);
                var missing = keys.Where(k => !defined.Contains(k)).ToList();
                Assert.True(missing.Count == 0, $"Strings.{language.Code}.xaml is missing: {string.Join(", ", missing)}");
            }
        }

        /// <summary>
        /// Names of the public properties declared with an enumeration type: one the console
        /// declares, or one from the runtime or the service controller it binds to.
        /// </summary>
        private static HashSet<string> EnumProperties()
        {
            var sources = Directory.EnumerateFiles(GuiRoot, "*.cs", SearchOption.AllDirectories)
                .Select(File.ReadAllText)
                .ToList();

            var enums = new HashSet<string>(StringComparer.Ordinal);
            var declared = new Regex(@"\benum\s+(\w+)", RegexOptions.CultureInvariant);
            foreach (string source in sources)
            {
                foreach (Match match in declared.Matches(source))
                {
                    enums.Add(match.Groups[1].Value);
                }
            }

            foreach (var type in typeof(object).Assembly.GetExportedTypes().Concat(typeof(ServiceController).Assembly.GetExportedTypes()))
            {
                if (type.IsEnum)
                {
                    enums.Add(type.Name);
                }
            }

            var properties = new HashSet<string>(StringComparer.Ordinal);
            var text = new HashSet<string>(StringComparer.Ordinal);
            foreach (string source in sources)
            {
                foreach (Match match in PublicProperty.Matches(source))
                {
                    string type = match.Groups["type"].Value.Split('.')[^1];
                    if (enums.Contains(type))
                    {
                        properties.Add(match.Groups["name"].Value);
                    }
                    else if (type is "string" or "String")
                    {
                        text.Add(match.Groups["name"].Value);
                    }
                }
            }

            // A binding names a property, not the class it belongs to: a name one class declares
            // as text and another as an enumeration (ServiceEntry.Problem, a string, and
            // SelfUpdate's Problem) cannot be told apart from the view, so it is not judged.
            properties.ExceptWith(text);
            return properties;
        }

        private static HashSet<string> KeysOf(string code) =>
            new(
                XDocument.Load(Path.Combine(GuiRoot, "Localization", $"Strings.{code}.xaml"))
                    .Descendants()
                    .Select(e => (string?)e.Attribute(X + "Key"))
                    .OfType<string>(),
                StringComparer.Ordinal);

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

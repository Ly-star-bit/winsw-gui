using System;
using System.IO;
using WinSW.Gui.Model;
using Xunit;

namespace WinSW.Gui.Tests
{
    /// <summary>
    /// <c>&lt;prompt&gt;console&lt;/prompt&gt;</c>, which an install from this console can never
    /// answer: the wrapper runs elevated in a hidden window and waits for typing nobody can see,
    /// until the install times out. The editor no longer offers it, but a file that says it still
    /// opens showing it, and saves as it was.
    /// </summary>
    public class ConsolePromptTests : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "WinSW.Gui.Tests", Guid.NewGuid().ToString("N"));

        public ConsolePromptTests() => Directory.CreateDirectory(this.directory);

        public void Dispose() => Directory.Delete(this.directory, recursive: true);

        [Fact]
        public void TheWrapperUnderstandsDialogAndConsole()
        {
            Assert.Equal(new[] { "dialog", "console" }, ServiceConfigModel.Prompts);
        }

        [Fact]
        public void ANewConfigurationIsNotOfferedConsole()
        {
            Assert.False(ServiceConfigModel.CreateNew().OffersConsolePrompt);
        }

        [Fact]
        public void AFileThatSaysDialogIsNotOfferedConsole()
        {
            var model = ServiceConfigModel.FromXml(Xml("dialog"), null);

            Assert.Equal("dialog", model.ServiceAccountPrompt);
            Assert.False(model.OffersConsolePrompt);
        }

        /// <summary>
        /// The wrapper lower-cases the prompt before it compares, so all of these ask at the
        /// console. The box shows console for each, and the file keeps its own spelling.
        /// </summary>
        [Theory]
        [InlineData("console")]
        [InlineData("Console")]
        [InlineData("CONSOLE")]
        public void AFileThatSaysConsoleShowsItAndSavesAsItWas(string written)
        {
            string path = Path.Combine(this.directory, "service.xml");
            File.WriteAllText(path, Xml(written));

            var model = ServiceConfigModel.Load(path);
            Assert.Equal("console", model.ServiceAccountPrompt);
            Assert.True(model.OffersConsolePrompt);

            model.Description = "changed";
            model.Save(path);
            Assert.Contains($"<prompt>{written}</prompt>", File.ReadAllText(path), StringComparison.Ordinal);
        }

        /// <summary>
        /// Choosing dialog writes dialog, and console stays under the box: an item taken away
        /// under a bound selection is how a ComboBox comes to write null back.
        /// </summary>
        [Fact]
        public void ChoosingDialogReplacesConsoleAndKeepsItOnOffer()
        {
            var model = ServiceConfigModel.FromXml(Xml("console"), null);

            model.ServiceAccountPrompt = "dialog";

            Assert.True(model.OffersConsolePrompt);
            Assert.Contains("<prompt>dialog</prompt>", model.ToXmlString(), StringComparison.Ordinal);
            Assert.DoesNotContain("console", model.ToXmlString(), StringComparison.Ordinal);
        }

        [Fact]
        public void ADialogSpelledOtherwiseIsMatchedAndSavedAsItWas()
        {
            var model = ServiceConfigModel.FromXml(Xml("Dialog"), null);

            Assert.Equal("dialog", model.ServiceAccountPrompt);
            Assert.Contains("<prompt>Dialog</prompt>", model.ToXmlString(), StringComparison.Ordinal);
        }

        /// <summary>None is the box's first item and means no element.</summary>
        [Fact]
        public void ChoosingNoneRemovesThePrompt()
        {
            var model = ServiceConfigModel.FromXml(Xml("console"), null);

            model.ServiceAccountPrompt = null;

            Assert.DoesNotContain("<prompt>", model.ToXmlString(), StringComparison.Ordinal);
        }

        [Theory]
        [InlineData("console", true)]
        [InlineData("Console", true)]
        [InlineData(" console ", true)]
        [InlineData("dialog", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void OnlyAConsolePromptIsFlagged(string? prompt, bool console)
        {
            Assert.Equal(console, ServiceConfigModel.IsConsolePrompt(prompt));
        }

        /// <summary>
        /// A warning, not a problem: the file must still open and save. Windows only:
        /// <c>ValidateEnvironment</c> reads its messages through WPF.
        /// </summary>
        [Fact]
        public void ValidateEnvironmentWarnsAboutAConsolePromptOnly()
        {
            var model = ServiceConfigModel.FromXml(Xml("console"), null);
            Assert.Single(model.ValidateEnvironment());
            Assert.Empty(model.Validate());

            model.ServiceAccountPrompt = "dialog";
            Assert.Empty(model.ValidateEnvironment());
        }

        // No user name: an account the machine does not know would add a warning of its own.
        private static string Xml(string prompt) =>
            $"<service><id>demo</id><executable>demo.exe</executable><serviceaccount><prompt>{prompt}</prompt></serviceaccount></service>";
    }
}

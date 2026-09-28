using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using WinSW.Gui.Model;
using WinSW.Gui.Services;
using Xunit;

namespace WinSW.Gui.Tests
{
    /// <summary>
    /// <c>&lt;endProcessesWithWrapper&gt;</c>, which has the wrapper end everything the service
    /// started when its own process ends, so a wrapper that crashed leaves nothing holding the port.
    /// </summary>
    /// <remarks>
    /// The wrapper reads it with <c>bool.Parse</c> under this exact spelling and defaults to off,
    /// so the model reads it the same way and writes it only when it is on. An older wrapper skips
    /// it as an unknown element, which is why the editor says so beside the box.
    /// </remarks>
    public class EndProcessesWithWrapperTests : IDisposable
    {
        private const string Minimal = "<service><id>demo</id><executable>demo.exe</executable></service>";

        private readonly string directory = Path.Combine(Path.GetTempPath(), "WinSW.Gui.Tests", Guid.NewGuid().ToString("N"));

        public EndProcessesWithWrapperTests() => Directory.CreateDirectory(this.directory);

        public void Dispose() => Directory.Delete(this.directory, recursive: true);

        /// <summary>Off by default, as in the wrapper, and off is written as nothing at all.</summary>
        [Fact]
        public void OffIsTheDefaultAndWritesNothing()
        {
            Assert.False(ServiceConfigModel.CreateNew().EndProcessesWithWrapper);

            var model = ServiceConfigModel.FromXml(Minimal, null);

            Assert.False(model.EndProcessesWithWrapper);
            Assert.Empty(Elements(model.ToXmlString()));
        }

        /// <summary>Ticking the box writes the element once, however often the preview renders.</summary>
        [Fact]
        public void TickingItWritesTheElementOnce()
        {
            var model = ServiceConfigModel.FromXml(Minimal, null);

            model.EndProcessesWithWrapper = true;
            model.ToXmlString();

            Assert.Equal(new[] { "<endProcessesWithWrapper>true</endProcessesWithWrapper>" }, Elements(model.ToXmlString()));
        }

        /// <summary>Saved, read back, saved again: still on, still one element, in the place it had.</summary>
        [Fact]
        public void OnRoundTripsThroughTheFile()
        {
            string path = this.Write(@"<service>
  <id>demo</id>
  <executable>demo.exe</executable>
  <!-- end everything with the wrapper -->
  <endProcessesWithWrapper>true</endProcessesWithWrapper>
  <logpath>%BASE%\logs</logpath>
</service>");

            var model = ServiceConfigModel.Load(path);
            Assert.True(model.EndProcessesWithWrapper);

            model.Save(path);
            var again = ServiceConfigModel.Load(path);
            again.Save(path);

            string text = File.ReadAllText(path);
            Assert.True(ServiceConfigModel.Load(path).EndProcessesWithWrapper);
            Assert.Equal(new[] { "<endProcessesWithWrapper>true</endProcessesWithWrapper>" }, Elements(text));

            int comment = text.IndexOf("<!-- end everything with the wrapper -->", StringComparison.Ordinal);
            int element = text.IndexOf("<endProcessesWithWrapper>", StringComparison.Ordinal);
            int logPath = text.IndexOf("<logpath>", StringComparison.Ordinal);
            Assert.True(comment >= 0 && comment < element && element < logPath, text);
        }

        /// <summary>Unticking it removes the element, which the wrapper reads as off.</summary>
        [Fact]
        public void UntickingItRemovesTheElement()
        {
            string path = this.Write("<service><id>demo</id><executable>demo.exe</executable><endProcessesWithWrapper>true</endProcessesWithWrapper></service>");

            var model = ServiceConfigModel.Load(path);
            model.EndProcessesWithWrapper = false;
            model.Save(path);

            Assert.Empty(Elements(File.ReadAllText(path)));
            Assert.False(ServiceConfigModel.Load(path).EndProcessesWithWrapper);
        }

        /// <summary>
        /// A file that spells out <c>false</c> means what leaving it out means, so saving drops it,
        /// as it does for every other switch that is at its default.
        /// </summary>
        [Fact]
        public void AnExplicitFalseIsDroppedOnSave()
        {
            var model = ServiceConfigModel.FromXml("<service><id>demo</id><executable>demo.exe</executable><endProcessesWithWrapper>false</endProcessesWithWrapper></service>", null);

            Assert.False(model.EndProcessesWithWrapper);
            Assert.Empty(Elements(model.ToXmlString()));
        }

        /// <summary><c>bool.Parse</c> takes any casing of the value, and so does the model.</summary>
        [Theory]
        [InlineData("True")]
        [InlineData("TRUE")]
        [InlineData(" true ")]
        public void TheValueIsReadWithoutRegardToCase(string value)
        {
            var model = ServiceConfigModel.FromXml($"<service><id>demo</id><executable>demo.exe</executable><endProcessesWithWrapper>{value}</endProcessesWithWrapper></service>", null);

            Assert.True(model.EndProcessesWithWrapper);
        }

        /// <summary>
        /// The element's name is matched by XPath, case and all: the wrapper does not read
        /// <c>&lt;endprocesseswithwrapper&gt;</c>, so the box must not show it as on either. The
        /// file keeps it, as it keeps any element the model does not know.
        /// </summary>
        [Fact]
        public void AMisspelledElementIsNeitherReadNorLost()
        {
            var model = ServiceConfigModel.FromXml("<service><id>demo</id><executable>demo.exe</executable><endprocesseswithwrapper>true</endprocesseswithwrapper></service>", null);

            Assert.False(model.EndProcessesWithWrapper);

            string xml = model.ToXmlString();
            Assert.Empty(Elements(xml));
            Assert.Contains("<endprocesseswithwrapper>true</endprocesseswithwrapper>", xml, StringComparison.Ordinal);
        }

        /// <summary>
        /// The cheat sheet is also the AI prompt: it has to name the element where stopping is
        /// described, with the wrapper's own default, say which wrappers ignore it, and list its
        /// camelCase among the names to check.
        /// </summary>
        [Theory]
        [InlineData("en", "| `endProcessesWithWrapper` | ? | bool | `false` |", "older than the one the console bundles", "6. Element names match section 4")]
        [InlineData("zh-CN", "| `endProcessesWithWrapper` | ? | 布尔 | `false` |", "比控制台自带版本旧的包装器", "6. 元素名和第 4 节")]
        public void TheGuideDocumentsTheElement(string language, string row, string olderWrappers, string checklistItem)
        {
            var lines = Guide(language).Replace("\r\n", "\n").Split('\n');

            string line = Assert.Single(lines, l => l.StartsWith(row, StringComparison.Ordinal));
            Assert.Contains(olderWrappers, line, StringComparison.Ordinal);
            Assert.Contains("`CREATE_BREAKAWAY_FROM_JOB`", line, StringComparison.Ordinal);

            // In the stopping table, which the stoptimeout row opens.
            int rowAt = Array.IndexOf(lines, line);
            int stopTimeoutAt = Array.FindIndex(lines, l => l.StartsWith("| `stoptimeout` |", StringComparison.Ordinal));
            Assert.True(stopTimeoutAt >= 0 && stopTimeoutAt < rowAt, line);
            Assert.All(lines.Skip(stopTimeoutAt).Take(rowAt - stopTimeoutAt), l => Assert.StartsWith("|", l, StringComparison.Ordinal));

            string checklist = string.Join(
                " ",
                lines.SkipWhile(l => !l.StartsWith(checklistItem, StringComparison.Ordinal)).TakeWhile(l => !l.StartsWith("7. ", StringComparison.Ordinal)));
            Assert.Contains("`endProcessesWithWrapper`", checklist, StringComparison.Ordinal);
        }

        private static string[] Elements(string xml) =>
            Regex.Matches(xml, "<endProcessesWithWrapper>[^<]*</endProcessesWithWrapper>").Select(m => m.Value).ToArray();

        private static string Guide(string code)
        {
            using var stream = typeof(XmlGuide).Assembly.GetManifestResourceStream("WinSW.Gui.Guide." + code + ".md");
            Assert.NotNull(stream);
            return new StreamReader(stream!).ReadToEnd();
        }

        private string Write(string xml)
        {
            string path = Path.Combine(this.directory, "service.xml");
            File.WriteAllText(path, xml);
            return path;
        }
    }
}

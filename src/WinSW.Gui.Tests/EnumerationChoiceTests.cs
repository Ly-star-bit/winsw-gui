using System;
using System.IO;
using System.Linq;
using WinSW.Gui.Model;
using Xunit;

namespace WinSW.Gui.Tests
{
    /// <summary>
    /// The start mode, priority and download authentication boxes: what they offer, what they
    /// show for the file that was opened, and what they can never write.
    /// </summary>
    /// <remarks>
    /// Each is a ComboBox over an enumeration, the shape that wrote <c>&lt;log mode=""&gt;</c> on a
    /// Server 2012 R2 machine: an ItemsSource that resolves after the SelectedItem binding, and
    /// null written back into the model. None of this touches <c>Validate</c>, which needs the
    /// WPF dictionaries.
    /// </remarks>
    public class EnumerationChoiceTests : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "WinSW.Gui.Tests", Guid.NewGuid().ToString("N"));

        public EnumerationChoiceTests() => Directory.CreateDirectory(this.directory);

        public void Dispose() => Directory.Delete(this.directory, recursive: true);

        /// <summary>
        /// A blank start mode drops <c>&lt;startmode&gt;</c> and <c>&lt;delayedAutoStart&gt;</c>,
        /// and the next Save &amp; apply makes a Manual service Automatic without a word.
        /// </summary>
        [Fact]
        public void BlankStartModeIsNeverWritten()
        {
            var model = Demo();
            model.StartMode = "Manual";

            model.StartMode = string.Empty;
            Assert.Equal("Manual", model.StartMode);

            model.StartMode = null!;
            Assert.Equal("Manual", model.StartMode);
            Assert.Contains("<startmode>Manual</startmode>", model.ToXmlString(), StringComparison.Ordinal);
        }

        /// <summary>The same for the priority, where a blank quietly means Normal.</summary>
        [Fact]
        public void BlankPriorityIsNeverWritten()
        {
            var model = Demo();
            model.Priority = "BelowNormal";

            model.Priority = string.Empty;
            model.Priority = null!;

            Assert.Equal("BelowNormal", model.Priority);
            Assert.Contains("<priority>BelowNormal</priority>", model.ToXmlString(), StringComparison.Ordinal);
        }

        /// <summary>And for a download, where a blank drops the credentials from the request.</summary>
        [Fact]
        public void BlankDownloadAuthIsNeverWritten()
        {
            var model = Demo();
            model.Downloads.Add(new DownloadItem { From = "https://example/a", To = "a", Auth = "sspi" });

            model.Downloads[0].Auth = string.Empty;
            model.Downloads[0].Auth = null!;

            Assert.Equal("sspi", model.Downloads[0].Auth);
            Assert.Contains(@"auth=""sspi""", model.ToXmlString(), StringComparison.Ordinal);
        }

        /// <summary>
        /// Boot and System are for drivers, and install fails with error 87 for a service the
        /// wrapper registers; Disabled is how a looping service is parked.
        /// </summary>
        [Fact]
        public void DisabledIsOfferedAndTheDriverModesAreNot()
        {
            Assert.Equal(new[] { "Automatic", "Manual", "Disabled" }, ServiceConfigModel.StartModes);
            Assert.Same(ServiceConfigModel.StartModes, ServiceConfigModel.CreateNew().StartModeChoices);
        }

        [Fact]
        public void ChoosingDisabledWritesItAndLeavesDelayedStartOut()
        {
            var model = Demo();
            model.DelayedAutoStart = true;
            Assert.Contains("<delayedAutoStart>true</delayedAutoStart>", model.ToXmlString(), StringComparison.Ordinal);

            model.StartMode = "Disabled";

            string xml = model.ToXmlString();
            Assert.Contains("<startmode>Disabled</startmode>", xml, StringComparison.Ordinal);
            Assert.DoesNotContain("delayedAutoStart", xml, StringComparison.Ordinal);
            Assert.False(model.SupportsDelayedAutoStart);
        }

        /// <summary>
        /// The wrapper reads the start mode without regard to case, and the box matches by exact
        /// string: a file that says <c>manual</c> has to show Manual, not an empty box, and is
        /// written back as its author spelled it until somebody picks another mode.
        /// </summary>
        [Theory]
        [InlineData("manual", "Manual")]
        [InlineData("DISABLED", "Disabled")]
        public void StartModeIsMatchedWithoutRegardToCase(string written, string shown)
        {
            var model = ServiceConfigModel.FromXml($"<service><id>demo</id><executable>demo.exe</executable><startmode>{written}</startmode></service>", null);

            Assert.Equal(shown, model.StartMode);
            Assert.Same(ServiceConfigModel.StartModes, model.StartModeChoices);
            Assert.Contains($"<startmode>{written}</startmode>", model.ToXmlString(), StringComparison.Ordinal);
        }

        /// <summary>Automatic is the default, and is left out of the file however it was spelled.</summary>
        [Fact]
        public void AnyAutomaticIsShownAsAutomaticAndLeftOut()
        {
            var model = ServiceConfigModel.FromXml("<service><id>demo</id><executable>demo.exe</executable><startmode>automatic</startmode></service>", null);

            Assert.Equal("Automatic", model.StartMode);
            Assert.True(model.SupportsDelayedAutoStart);
            Assert.DoesNotContain("<startmode>", model.ToXmlString(), StringComparison.Ordinal);
        }

        [Fact]
        public void AStartModeSpelledOtherwiseIsStillReplacedWhenChanged()
        {
            var model = ServiceConfigModel.FromXml("<service><id>demo</id><executable>demo.exe</executable><startmode>manual</startmode></service>", null);

            model.StartMode = "Disabled";

            Assert.Contains("<startmode>Disabled</startmode>", model.ToXmlString(), StringComparison.Ordinal);
        }

        /// <summary>
        /// A file that says Boot or System is no longer something the editor offers, but it still
        /// opens showing what it says, and still saves as it was.
        /// </summary>
        [Theory]
        [InlineData("Boot")]
        [InlineData("System")]
        public void ADriverStartModeRoundTripsAndStaysOnOffer(string mode)
        {
            string path = this.Write($"<service><id>demo</id><executable>demo.exe</executable><startmode>{mode}</startmode></service>");

            var model = ServiceConfigModel.Load(path);
            Assert.Equal(mode, model.StartMode);
            Assert.Equal(ServiceConfigModel.StartModes.Append(mode), model.StartModeChoices);

            model.Description = "changed";
            model.Save(path);
            Assert.Contains($"<startmode>{mode}</startmode>", File.ReadAllText(path), StringComparison.Ordinal);

            // Choosing another mode does not take the file's own off the list under the box.
            model.StartMode = "Manual";
            Assert.Contains(mode, model.StartModeChoices);
            Assert.Contains("<startmode>Manual</startmode>", model.ToXmlString(), StringComparison.Ordinal);
        }

        [Fact]
        public void PriorityIsMatchedWithoutRegardToCaseAndWrittenAsItWas()
        {
            var model = ServiceConfigModel.FromXml("<service><id>demo</id><executable>demo.exe</executable><priority>high</priority></service>", null);

            Assert.Equal("High", model.Priority);
            Assert.Contains("<priority>high</priority>", model.ToXmlString(), StringComparison.Ordinal);

            model.Priority = "Idle";
            Assert.Contains("<priority>Idle</priority>", model.ToXmlString(), StringComparison.Ordinal);
        }

        [Fact]
        public void DownloadAuthIsMatchedWithoutRegardToCase()
        {
            var model = ServiceConfigModel.FromXml(@"<service><id>demo</id><executable>demo.exe</executable><download from=""https://example/a"" to=""a"" auth=""Basic"" user=""u"" password=""p"" /></service>", null);

            Assert.Equal("basic", model.Downloads.Single().Auth);
            Assert.Contains(model.Downloads.Single().Auth, DownloadItem.AuthTypes);
        }

        /// <summary>
        /// The editor has no box for either any more, since the wrapper uses neither, but a file
        /// that has them keeps them: the editor never drops what it was not asked to.
        /// </summary>
        [Fact]
        public void InteractiveAndAllowServiceLogonStillRoundTrip()
        {
            string path = this.Write(@"<service>
  <id>demo</id>
  <executable>demo.exe</executable>
  <interactive>true</interactive>
  <serviceaccount>
    <username>.\svc</username>
    <password>p</password>
    <allowservicelogon>true</allowservicelogon>
  </serviceaccount>
</service>");

            var model = ServiceConfigModel.Load(path);
            Assert.True(model.Interactive);
            Assert.True(model.AllowServiceLogon);

            model.Description = "changed";
            model.Save(path);

            string text = File.ReadAllText(path);
            Assert.Contains("<interactive>true</interactive>", text, StringComparison.Ordinal);
            Assert.Contains("<allowservicelogon>true</allowservicelogon>", text, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData("Boot", true)]
        [InlineData("system", true)]
        [InlineData(" SYSTEM ", true)]
        [InlineData("Automatic", false)]
        [InlineData("Manual", false)]
        [InlineData("Disabled", false)]
        [InlineData(null, false)]
        public void OnlyTheDriverModesAreFlagged(string? mode, bool driver)
        {
            Assert.Equal(driver, ServiceConfigModel.IsDriverStartMode(mode));
        }

        [Theory]
        [InlineData("manual", "Manual")]
        [InlineData(" Manual ", "Manual")]
        [InlineData("Boot", "Boot")]
        [InlineData("boot", "boot")]
        public void AChoiceTakesTheListsSpellingOrStaysAsWritten(string value, string expected)
        {
            Assert.Equal(expected, ServiceConfigModel.MatchChoice(ServiceConfigModel.StartModes, value));
        }

        private static ServiceConfigModel Demo()
        {
            var model = ServiceConfigModel.CreateNew();
            model.Id = "demo";
            model.Executable = "demo.exe";
            return model;
        }

        private string Write(string xml)
        {
            string path = Path.Combine(this.directory, "service.xml");
            File.WriteAllText(path, xml);
            return path;
        }
    }
}

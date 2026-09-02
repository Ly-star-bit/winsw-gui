using System;
using System.IO;
using WinSW.Gui.Model;
using WinSW.Gui.Services;
using Xunit;

namespace WinSW.Gui.Tests
{
    public class ConfigExtrasTests : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "WinSW.Gui.Tests", Guid.NewGuid().ToString("N"));

        public ConfigExtrasTests() => Directory.CreateDirectory(this.directory);

        public void Dispose() => Directory.Delete(this.directory, recursive: true);

        [Fact]
        public void Hooks_Mappings_And_Extensions_RoundTrip()
        {
            string path = Path.Combine(this.directory, "svc.xml");
            File.WriteAllText(path, @"<service>
  <id>svc</id>
  <executable>app.exe</executable>
  <prestart>
    <executable>warm.exe</executable>
    <arguments>--fast</arguments>
    <stdoutPath>%BASE%\warm.out</stdoutPath>
  </prestart>
  <sharedDirectoryMapping>
    <map label=""N:"" uncpath=""\\server\share"" />
  </sharedDirectoryMapping>
  <extensions>
    <extension enabled=""true"" id=""x"" className=""a.b.C""><foo>1</foo></extension>
  </extensions>
</service>");

            var model = ServiceConfigModel.Load(path);
            Assert.Equal("warm.exe", model.Prestart.Executable);
            Assert.Equal("--fast", model.Prestart.Arguments);
            Assert.True(model.Poststart.IsEmpty);
            Assert.Single(model.SharedDirectories);
            Assert.Contains("className=\"a.b.C\"", model.ExtensionsXml);

            model.Poststop.Executable = "cleanup.exe";
            model.SharedDirectories.Add(new DriveMapping { Label = "M:", UncPath = @"\\srv2\data" });
            model.Save(path);

            var again = ServiceConfigModel.Load(path);
            Assert.Equal("cleanup.exe", again.Poststop.Executable);
            Assert.Equal(2, again.SharedDirectories.Count);
            Assert.Contains("<foo>1</foo>", again.ExtensionsXml);

            again.Prestart.Clear();
            again.ExtensionsXml = null;
            again.SharedDirectories.Clear();
            again.Save(path);

            string text = File.ReadAllText(path);
            Assert.DoesNotContain("<prestart>", text);
            Assert.DoesNotContain("<extensions>", text);
            Assert.DoesNotContain("sharedDirectoryMapping", text);
            Assert.Contains("<poststop>", text);
        }

        [Fact]
        public void Validate_RejectsBadExtensionsAndMappings()
        {
            var model = ServiceConfigModel.CreateNew();
            model.Id = "x";
            model.Executable = "x.exe";
            Assert.Empty(model.Validate());

            model.ExtensionsXml = "<extensions><broken></extensions>";
            Assert.Single(model.Validate());

            model.ExtensionsXml = "<notextensions />";
            Assert.Single(model.Validate());

            model.ExtensionsXml = null;
            model.SharedDirectories.Add(new DriveMapping { Label = "NN", UncPath = "C:\\not-unc" });
            Assert.Equal(2, model.Validate().Count);
        }

        [Fact]
        public void FromXml_ParsesText_AndKeepsFilePath()
        {
            var model = ServiceConfigModel.FromXml("<service><id>a</id><executable>a.exe</executable></service>", @"C:\x\a.xml");
            Assert.Equal("a", model.Id);
            Assert.Equal(@"C:\x\a.xml", model.FilePath);

            Assert.Throws<InvalidDataException>(() => ServiceConfigModel.FromXml("<nope/>", null));
            Assert.Throws<InvalidDataException>(() => ServiceConfigModel.FromXml("<service", null));
        }

        [Fact]
        public void ValidateEnvironment_FlagsMissingPaths()
        {
            var model = ServiceConfigModel.CreateNew();
            model.Id = "x";
            model.Executable = Path.Combine(this.directory, "does-not-exist.exe");
            model.WorkingDirectory = Path.Combine(this.directory, "nowhere");

            var warnings = model.ValidateEnvironment();
            Assert.Equal(2, warnings.Count);
        }

        [Theory]
        [InlineData("3.0.1", "3.0.0", true)]
        [InlineData("3.0.0", "3.0.0", false)]
        [InlineData("2.12.0", "3.0.0-alpha.11", false)]
        [InlineData("0.4.0", "0.3.0", true)]
        [InlineData("garbage", "0.3.0", false)]
        public void IsNewer_ComparesNumericParts(string candidate, string current, bool expected)
        {
            Assert.Equal(expected, UpdateChecker.IsNewer(candidate, current));
        }

        [Fact]
        public void ReleaseInfo_StripsTagPrefixes()
        {
            var assets = new System.Collections.Generic.Dictionary<string, string>();
            Assert.Equal("3.0.0", new ReleaseInfo("v3.0.0", "", assets).Version);
            Assert.Equal("0.4.0", new ReleaseInfo("gui-v0.4.0", "", assets).Version);
        }
    }
}

using System;
using System.IO;
using System.Linq;
using WinSW.Gui.Model;
using Xunit;

namespace WinSW.Gui.Tests
{
    public class ServiceConfigModelTests : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "WinSW.Gui.Tests", Guid.NewGuid().ToString("N"));

        public ServiceConfigModelTests() => Directory.CreateDirectory(this.directory);

        public void Dispose() => Directory.Delete(this.directory, recursive: true);

        [Fact]
        public void RoundTrip_PreservesCommentsAndUnknownElements()
        {
            string path = this.Write(@"<?xml version=""1.0"" encoding=""utf-8""?>
<!-- top comment -->
<service>
  <!-- the id -->
  <id>myapp</id>
  <executable>%BASE%\myapp.exe</executable>
  <somethingNew>kept</somethingNew>
  <log mode=""roll-by-size"">
    <sizeThreshold>2048</sizeThreshold>
    <keepFiles>3</keepFiles>
  </log>
  <env name=""A"" value=""1"" />
  <onfailure action=""restart"" delay=""10 sec"" />
</service>");

            var model = ServiceConfigModel.Load(path);
            model.DisplayName = "My App";
            model.Save(path);

            string text = File.ReadAllText(path);
            Assert.Contains("<!-- top comment -->", text);
            Assert.Contains("<!-- the id -->", text);
            Assert.Contains("<somethingNew>kept</somethingNew>", text);
            Assert.Contains("<name>My App</name>", text);

            var again = ServiceConfigModel.Load(path);
            Assert.Equal("myapp", again.Id);
            Assert.Equal("My App", again.DisplayName);
            Assert.Equal("roll-by-size", again.LogMode);
            Assert.Equal("2048", again.SizeThreshold);
            Assert.Equal("3", again.KeepFiles);
            Assert.Single(again.EnvironmentVariables);
            Assert.Equal("restart", again.FailureActions.Single().Action);
        }

        [Fact]
        public void Save_DropsDefaultsAndLegacyLogMode()
        {
            string path = this.Write(@"<service><id>x</id><executable>x.exe</executable><logmode>append</logmode><priority>Normal</priority></service>");

            var model = ServiceConfigModel.Load(path);
            model.Save(path);

            string text = File.ReadAllText(path);
            Assert.DoesNotContain("<logmode>", text);
            Assert.DoesNotContain("<priority>", text);
            Assert.Contains("<log mode=\"append\"", text);
        }

        [Fact]
        public void Save_UsesUserAttributeForDownloads()
        {
            // Download.cs reads "user", not "username"; the sample in the repository is wrong.
            var model = ServiceConfigModel.CreateNew();
            model.Id = "x";
            model.Executable = "x.exe";
            model.Downloads.Add(new DownloadItem { From = "https://example/a", To = "a", Auth = "basic", User = "u", Password = "p" });

            string path = Path.Combine(this.directory, "d.xml");
            model.Save(path);

            string text = File.ReadAllText(path);
            Assert.Contains("user=\"u\"", text);
            Assert.DoesNotContain("username=", text);
        }

        [Fact]
        public void Validate_FlagsWhatTheWrapperWouldReject()
        {
            var model = ServiceConfigModel.CreateNew();
            Assert.Equal(2, model.Validate().Count); // id and executable

            model.Id = "has space";
            model.Executable = "x.exe";
            model.StopTimeout = "ten seconds";
            model.LogMode = "roll-by-time";
            Assert.Equal(3, model.Validate().Count); // id chars, bad time, missing pattern

            model.Id = "ok";
            model.StopTimeout = "15 sec";
            model.RollPattern = "yyyyMMdd";
            Assert.Empty(model.Validate());
        }

        [Theory]
        [InlineData("15 sec", 15_000)]
        [InlineData("2 min", 120_000)]
        [InlineData("1 hour", 3_600_000)]
        [InlineData("500", 500)]
        [InlineData("3 days", 259_200_000)]
        public void TryParseTime_MatchesWrapperSuffixes(string text, long expectedMilliseconds)
        {
            Assert.True(ServiceConfigModel.TryParseTime(text, out var result));
            Assert.Equal(expectedMilliseconds, (long)result.TotalMilliseconds);
        }

        [Theory]
        [InlineData("abc")]
        [InlineData("10 fortnights")]
        [InlineData("")]
        public void TryParseTime_RejectsGarbage(string text)
        {
            Assert.False(ServiceConfigModel.TryParseTime(text, out _));
        }

        private string Write(string xml)
        {
            string path = Path.Combine(this.directory, "service.xml");
            File.WriteAllText(path, xml);
            return path;
        }

        /// <summary>
        /// A blank log mode reaches the wrapper as "Undefined logging mode" and the service
        /// does not start. It came from a ComboBox writing null back into the model, so the
        /// model refuses it and the writer never emits an empty one.
        /// </summary>
        [Fact]
        public void BlankLogModeIsNeverWritten()
        {
            var model = ServiceConfigModel.CreateNew();
            model.Id = "demo";
            model.Executable = "demo.exe";

            model.LogMode = string.Empty;
            Assert.Equal("append", model.LogMode);

            model.LogMode = "roll-by-size";
            Assert.Equal("roll-by-size", model.LogMode);
            Assert.Contains(@"mode=""roll-by-size""", model.ToXmlString(), StringComparison.Ordinal);
        }

        /// <summary>The same for a failure action, whose attribute is mandatory.</summary>
        [Fact]
        public void FailureActionWithoutAnActionIsNeitherKeptNorWritten()
        {
            var model = ServiceConfigModel.CreateNew();
            model.Id = "demo";
            model.Executable = "demo.exe";
            model.FailureActions.Add(new FailureAction { Action = "restart", Delay = "10 sec" });
            model.FailureActions[0].Action = string.Empty;

            Assert.Equal("restart", model.FailureActions[0].Action);
            Assert.Contains(@"action=""restart""", model.ToXmlString(), StringComparison.Ordinal);
            Assert.DoesNotContain(@"action=""""", model.ToXmlString(), StringComparison.Ordinal);
        }
    }
}

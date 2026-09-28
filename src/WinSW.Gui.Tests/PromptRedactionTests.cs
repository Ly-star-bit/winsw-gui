using System;
using System.Linq;
using WinSW.Gui.Model;
using WinSW.Gui.Services;
using Xunit;

namespace WinSW.Gui.Tests
{
    /// <summary>
    /// "Copy as AI prompt" hands the configuration being edited to a third-party assistant, so
    /// it goes out with its secrets masked, and the answer pasted back into the editor gets the
    /// real values back wherever it kept the mask.
    /// </summary>
    /// <remarks>
    /// What matters on the way out is that no secret reaches the prompt; on the way back, that
    /// a password never becomes eight asterisks, and never moves to a place it was not taken
    /// from. A mask with nothing to give back is counted, so the editor can say so.
    /// </remarks>
    public class PromptRedactionTests
    {
        private const string Secrets = @"<service>
  <id>demo</id>
  <executable>java</executable>
  <arguments>-Ddb.password=dbpass -Dmq.password=mqpass -jar app.jar</arguments>
  <serviceaccount>
    <username>CORP\svc</username>
    <password>hunter2</password>
  </serviceaccount>
  <proxy>http://bob:proxypass@proxy.example.com:8080</proxy>
  <env name=""DB_PASSWORD"" value=""envpass"" />
  <env name=""JAVA_HOME"" value=""C:\jdk"" />
  <download from=""https://alice:frompass@files.example.com/a.zip"" to=""%BASE%\a.zip"" auth=""basic"" user=""alice"" password=""dlpass-a"" />
  <download from=""https://files.example.com/b.zip"" to=""%BASE%\b.zip"" auth=""basic"" user=""carol"" password=""dlpass-b"" />
  <prestart>
    <executable>prep.cmd</executable>
    <arguments>--token hooktoken</arguments>
  </prestart>
</service>";

        private static readonly string[] SecretValues =
        {
            "dbpass", "mqpass", "hunter2", "proxypass", "envpass", "frompass", "dlpass-a", "dlpass-b", "hooktoken",
        };

        private static string Mask => ConfigRedactor.Mask;

        [Fact]
        public void NoSecretReachesThePromptsConfiguration()
        {
            var model = ServiceConfigModel.FromXml(Secrets, null);

            string? configuration = XmlGuide.ConfigurationForPrompt(model.ToXmlString(), out var masked);

            Assert.NotNull(configuration);
            foreach (string secret in SecretValues)
            {
                Assert.DoesNotContain(secret, configuration, StringComparison.Ordinal);
            }

            // One report entry per place, and everything else survives to be worked on.
            Assert.Equal(8, masked.Count);
            Assert.Contains(@"CORP\svc", configuration, StringComparison.Ordinal);
            Assert.Contains(@"C:\jdk", configuration, StringComparison.Ordinal);
            Assert.Contains("-Ddb.password=" + Mask, configuration, StringComparison.Ordinal);
            Assert.Contains("proxy.example.com:8080", configuration, StringComparison.Ordinal);
        }

        [Fact]
        public void NothingToMaskIsNothingMasked()
        {
            string? configuration = XmlGuide.ConfigurationForPrompt("<service><id>demo</id><executable>demo.exe</executable></service>", out var masked);

            Assert.Equal("<service><id>demo</id><executable>demo.exe</executable></service>", configuration);
            Assert.Empty(masked);
        }

        /// <summary>
        /// The preview holds a note rather than XML when the form cannot be rendered. The note is
        /// left out of the prompt rather than passed on unread, and there is nothing to report.
        /// </summary>
        [Theory]
        [InlineData(null)]
        [InlineData("   ")]
        [InlineData("<!-- The configuration could not be rendered: boom -->")]
        [InlineData("<service><password>hunter2</password>")]
        public void TextThatIsNotAConfigurationIsLeftOut(string? text)
        {
            Assert.Null(XmlGuide.ConfigurationForPrompt(text, out var masked));
            Assert.Empty(masked);
        }

        /// <summary>
        /// The whole trip: out through the prompt, back unchanged from the assistant, and every
        /// secret is where it was.
        /// </summary>
        [Fact]
        public void AnAnswerThatKeepsTheMasksGetsEverySecretBack()
        {
            var form = ServiceConfigModel.FromXml(Secrets, null);
            var answer = ServiceConfigModel.FromXml(XmlGuide.ConfigurationForPrompt(form.ToXmlString(), out _)!, null);

            var (kept, left) = answer.KeepMaskedValues(form);

            Assert.Equal(SecretValues.Length, kept);
            Assert.Equal(0, left);
            Assert.Equal("hunter2", answer.ServiceAccountPassword);
            Assert.Equal("-Ddb.password=dbpass -Dmq.password=mqpass -jar app.jar", answer.Arguments);
            Assert.Equal("http://bob:proxypass@proxy.example.com:8080", answer.ProxyAddress);
            Assert.Equal("envpass", answer.EnvironmentVariables.Single(v => v.Name == "DB_PASSWORD").Value);
            Assert.Equal(@"C:\jdk", answer.EnvironmentVariables.Single(v => v.Name == "JAVA_HOME").Value);
            Assert.Equal("https://alice:frompass@files.example.com/a.zip", answer.Downloads[0].From);
            Assert.Equal("dlpass-a", answer.Downloads[0].Password);
            Assert.Equal("dlpass-b", answer.Downloads[1].Password);
            Assert.Equal("--token hooktoken", answer.Prestart.Arguments);

            string written = answer.ToXmlString();
            Assert.DoesNotContain(Mask, written, StringComparison.Ordinal);
            foreach (string secret in SecretValues)
            {
                Assert.Contains(secret, written, StringComparison.Ordinal);
            }
        }

        /// <summary>
        /// The command line is where an assistant is most likely to have changed something. The
        /// change stays, each secret goes back to its own argument, and two passwords on one
        /// line cannot swap when the answer reorders them.
        /// </summary>
        [Fact]
        public void AnEditedCommandLineKeepsTheEditAndGetsEachSecretBackInItsPlace()
        {
            var form = ServiceConfigModel.FromXml(Secrets, null);
            var answer = ServiceConfigModel.FromXml(
                $"<service><id>demo</id><executable>java</executable><arguments>-Xmx1g -Dmq.password={Mask} -jar app.jar -Ddb.password={Mask} --verbose</arguments></service>",
                null);

            var (kept, left) = answer.KeepMaskedValues(form);

            Assert.Equal("-Xmx1g -Dmq.password=mqpass -jar app.jar -Ddb.password=dbpass --verbose", answer.Arguments);
            Assert.Equal(2, kept);
            Assert.Equal(0, left);
        }

        /// <summary>
        /// A download is matched by where it is saved to, not by its position: an answer that
        /// puts a new download first must not hand it the first download's password.
        /// </summary>
        [Fact]
        public void DownloadsAreMatchedByDestinationNotPosition()
        {
            var form = ServiceConfigModel.FromXml(Secrets, null);
            var answer = ServiceConfigModel.FromXml(
                $@"<service><id>demo</id><executable>java</executable>
                  <download from=""https://other.example.com/c.zip"" to=""%BASE%\c.zip"" auth=""basic"" user=""dave"" password=""{Mask}"" />
                  <download from=""https://files.example.com/b.zip"" to=""%BASE%\b.zip"" auth=""basic"" user=""carol"" password=""{Mask}"" />
                  <download from=""https://{Mask}@files.example.com/a.zip"" to=""%BASE%\a.zip"" auth=""basic"" user=""alice"" password=""{Mask}"" />
                </service>",
                null);

            var (kept, left) = answer.KeepMaskedValues(form);

            Assert.Equal(Mask, answer.Downloads[0].Password);
            Assert.Equal("dlpass-b", answer.Downloads[1].Password);
            Assert.Equal("dlpass-a", answer.Downloads[2].Password);
            Assert.Equal("https://alice:frompass@files.example.com/a.zip", answer.Downloads[2].From);
            Assert.Equal(3, kept);
            Assert.Equal(1, left);
        }

        /// <summary>
        /// A variable is matched by name, as Windows reads it, without regard to case; one the
        /// answer renamed has no earlier value and keeps the mask, counted.
        /// </summary>
        [Fact]
        public void VariablesAreMatchedByName()
        {
            var form = ServiceConfigModel.FromXml(Secrets, null);
            var answer = ServiceConfigModel.FromXml(
                $@"<service><id>demo</id><executable>java</executable>
                  <env name=""NEW_TOKEN"" value=""{Mask}"" />
                  <env name=""db_password"" value=""{Mask}"" />
                </service>",
                null);

            var (kept, left) = answer.KeepMaskedValues(form);

            Assert.Equal(Mask, answer.EnvironmentVariables[0].Value);
            Assert.Equal("envpass", answer.EnvironmentVariables[1].Value);
            Assert.Equal(1, kept);
            Assert.Equal(1, left);
        }

        /// <summary>
        /// Credentials go back only in front of the host they were for. An answer that points the
        /// proxy somewhere else keeps the mask rather than sending the old password there.
        /// </summary>
        [Fact]
        public void CredentialsDoNotFollowAUrlToAnotherHost()
        {
            var form = ServiceConfigModel.FromXml(Secrets, null);
            var moved = ServiceConfigModel.FromXml(
                $"<service><id>demo</id><executable>java</executable><proxy>http://{Mask}@proxy.elsewhere.example:8080</proxy></service>",
                null);

            var (kept, left) = moved.KeepMaskedValues(form);

            Assert.Equal($"http://{Mask}@proxy.elsewhere.example:8080", moved.ProxyAddress);
            Assert.Equal(0, kept);
            Assert.Equal(1, left);
        }

        /// <summary>
        /// A value the assistant actually wrote is the answer, not a mask to undo; and a mask
        /// where the form had nothing stays, counted, so the editor can ask for it.
        /// </summary>
        [Fact]
        public void OnlyTheMaskIsReplacedAndOnlyWithARealValue()
        {
            var form = ServiceConfigModel.FromXml("<service><id>demo</id><executable>java</executable><serviceaccount><username>CORP\\svc</username><password>hunter2</password></serviceaccount></service>", null);

            var changed = ServiceConfigModel.FromXml("<service><id>demo</id><executable>java</executable><serviceaccount><username>CORP\\svc</username><password>n3w-pass</password></serviceaccount></service>", null);
            Assert.Equal((0, 0), changed.KeepMaskedValues(form));
            Assert.Equal("n3w-pass", changed.ServiceAccountPassword);

            var bare = ServiceConfigModel.FromXml($"<service><id>demo</id><executable>java</executable><serviceaccount><username>CORP\\svc</username><password>{Mask}</password></serviceaccount></service>", null);
            var (kept, left) = bare.KeepMaskedValues(ServiceConfigModel.FromXml("<service><id>demo</id><executable>java</executable></service>", null));
            Assert.Equal(Mask, bare.ServiceAccountPassword);
            Assert.Equal(0, kept);
            Assert.Equal(1, left);
        }

        /// <summary>The extensions are carried as text with nothing to match, so a mask there is only counted.</summary>
        [Fact]
        public void AMaskInTheExtensionsIsCounted()
        {
            var answer = ServiceConfigModel.FromXml(
                $"<service><id>demo</id><executable>java</executable><extensions><extension id=\"x\"><password>{Mask}</password></extension></extensions></service>",
                null);

            Assert.Equal((0, 1), answer.KeepMaskedValues(ServiceConfigModel.FromXml(Secrets, null)));
        }

        [Theory]
        [InlineData("********", "hunter2", "hunter2")]
        [InlineData(" ******** ", "hunter2", "hunter2")]
        [InlineData("********", "", "********")]
        [InlineData("********", null, "********")]
        [InlineData("********", "********", "********")]
        [InlineData("hunter3", "hunter2", "hunter3")]
        [InlineData("x********", "hunter2", "x********")]
        public void AWholeValueComesBackOnlyForTheMaskAlone(string pasted, string? earlier, string expected) =>
            Assert.Equal(expected, ConfigRedactor.Unmask(pasted, earlier));

        [Theory]
        [InlineData("http://********@proxy:8080", "http://bob:pw@proxy:8080", "http://bob:pw@proxy:8080")]
        [InlineData("http://********@proxy:8080/v2/", "http://bob:pw@proxy:8080/v1/", "http://bob:pw@proxy:8080/v2/")]
        [InlineData("http://********@PROXY:8080", "http://bob:pw@proxy:8080", "http://bob:pw@PROXY:8080")]
        [InlineData("http://********@proxy:9090", "http://bob:pw@proxy:8080", "http://********@proxy:9090")]
        [InlineData("https://********@proxy:8080", "http://bob:pw@proxy:8080", "https://********@proxy:8080")]
        [InlineData("http://********@proxy:8080", "http://proxy:8080", "http://********@proxy:8080")]
        [InlineData("http://proxy:8080", "http://bob:pw@proxy:8080", "http://proxy:8080")]
        public void CredentialsComeBackInFrontOfTheSameHostOnly(string pasted, string earlier, string expected) =>
            Assert.Equal(expected, ConfigRedactor.UnmaskUrl(pasted, earlier));

        [Theory]
        [InlineData("--token ********", "--token abc", "--token abc")]
        [InlineData("--secret ********", "--secret \"a b\"", "--secret \"a b\"")]
        [InlineData("--api-key ******** --api-key ********", "--api-key one --api-key two", "--api-key one --api-key two")]
        [InlineData("-Dpassword=******** --new", "-Dpassword=$1x --old", "-Dpassword=$1x --new")]
        [InlineData("--TOKEN=********", "--token=abc", "--TOKEN=abc")]
        [InlineData("--token ********", "--other abc", "--token ********")]
        [InlineData("--token ******** --token ********", "--token abc", "--token abc --token ********")]
        public void EachArgumentGetsItsOwnValueBack(string pasted, string earlier, string expected) =>
            Assert.Equal(expected, ConfigRedactor.UnmaskCommandLine(pasted, earlier));

        [Theory]
        [InlineData(null, 0)]
        [InlineData("", 0)]
        [InlineData("plain", 0)]
        [InlineData("********", 1)]
        [InlineData("a ******** b ********", 2)]
        public void MasksAreCounted(string? value, int count) =>
            Assert.Equal(count, ConfigRedactor.CountMasks(value));

        /// <summary>
        /// The prompt as a whole, instructions included. It needs the interface's dictionaries,
        /// so it runs only where WPF does.
        /// </summary>
        [Fact]
        public void ThePromptNeverCarriesASecret()
        {
            string prompt = XmlGuide.BuildPrompt(Secrets, out var masked);

            foreach (string secret in SecretValues)
            {
                Assert.DoesNotContain(secret, prompt, StringComparison.Ordinal);
            }

            Assert.NotEmpty(masked);
            Assert.Contains("CORP", prompt, StringComparison.Ordinal);
        }
    }
}

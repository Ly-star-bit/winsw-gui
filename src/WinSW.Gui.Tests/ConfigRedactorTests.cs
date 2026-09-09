using System.Linq;
using WinSW.Gui.Services;
using Xunit;

namespace WinSW.Gui.Tests
{
    /// <summary>
    /// A diagnostics bundle is collected in order to be sent to somebody, so what these test
    /// is not that the redaction is tidy but that the secret is gone from the output.
    /// </summary>
    public class ConfigRedactorTests
    {
        [Fact]
        public void TheServiceAccountPasswordDoesNotSurvive()
        {
            string xml = Redact(@"
                <service>
                  <id>demo</id>
                  <serviceaccount>
                    <username>CORP\svc</username>
                    <password>hunter2</password>
                  </serviceaccount>
                </service>", out var removed);

            Assert.DoesNotContain("hunter2", xml);
            Assert.Contains(ConfigRedactor.Mask, xml);
            Assert.Contains(removed, r => r.Contains("password"));

            // The rest of the element is what makes the bundle worth having.
            Assert.Contains(@"CORP\svc", xml);
            Assert.Contains("demo", xml);
        }

        [Fact]
        public void ADownloadPasswordAttributeDoesNotSurvive()
        {
            string xml = Redact(
                @"<service><download from=""https://example.com/a.zip"" to=""a.zip"" auth=""basic"" user=""bob"" password=""s3cret"" /></service>",
                out _);

            Assert.DoesNotContain("s3cret", xml);
            Assert.Contains(@"user=""bob""", xml);
        }

        [Fact]
        public void CredentialsInFrontOfAUrlHostDoNotSurvive()
        {
            string xml = Redact(
                @"<service><proxy>http://bob:s3cret@proxy.example.com:8080</proxy></service>",
                out var removed);

            Assert.DoesNotContain("s3cret", xml);
            Assert.DoesNotContain("bob", xml);

            // The host is the diagnostically interesting half and is kept.
            Assert.Contains("proxy.example.com:8080", xml);
            Assert.Contains(removed, r => r.Contains("URL"));
        }

        [Fact]
        public void AProxyWithNoCredentialsIsLeftAlone()
        {
            string xml = Redact(@"<service><proxy>http://proxy.example.com:8080</proxy></service>", out var removed);

            Assert.Contains("http://proxy.example.com:8080", xml);
            Assert.Empty(removed);
        }

        [Theory]
        [InlineData("DB_PASSWORD")]
        [InlineData("api_key")]
        [InlineData("GithubToken")]
        [InlineData("MY_SECRET")]
        [InlineData("AUTH_HEADER")]
        public void AnEnvironmentValueWhoseNameSaysSecretDoesNotSurvive(string name)
        {
            string xml = Redact($@"<service><env name=""{name}"" value=""s3cret"" /></service>", out var removed);

            Assert.DoesNotContain("s3cret", xml);
            Assert.Contains(removed, r => r.Contains(name));
        }

        /// <summary>
        /// The env values are usually the reason the bundle was collected. Blanking them all
        /// would be safe and useless, so only the ones whose name says so are masked.
        /// </summary>
        [Fact]
        public void AnOrdinaryEnvironmentValueIsKept()
        {
            string xml = Redact(@"<service><env name=""JAVA_HOME"" value=""C:\jdk"" /></service>", out var removed);

            Assert.Contains(@"C:\jdk", xml);
            Assert.Empty(removed);
        }

        [Theory]
        [InlineData("-Dpassword=hunter2")]
        [InlineData("--api-key hunter2")]
        [InlineData("-Dspring.datasource.password=hunter2")]
        [InlineData("/TOKEN:hunter2")]
        [InlineData("--secret \"hunter2\"")]
        public void AnArgumentThatNamesItselfASecretIsMasked(string argument)
        {
            string xml = Redact($"<service><arguments>-jar app.jar {argument} --verbose</arguments></service>", out var removed);

            Assert.DoesNotContain("hunter2", xml);

            // The rest of the command line is the most useful thing in the bundle.
            Assert.Contains("-jar app.jar", xml);
            Assert.Contains("--verbose", xml);
            Assert.NotEmpty(removed);
        }

        [Theory]
        [InlineData("startarguments")]
        [InlineData("stoparguments")]
        public void TheOtherArgumentElementsAreCheckedToo(string element)
        {
            string xml = Redact($"<service><{element}>--token=hunter2</{element}></service>", out _);

            Assert.DoesNotContain("hunter2", xml);
        }

        /// <summary>
        /// The masking keeps the argument's name, so the reader can still see which switch
        /// was passed — only its value is gone.
        /// </summary>
        [Fact]
        public void MaskingAnArgumentLeavesItRecognisable()
        {
            string xml = Redact("<service><arguments>-Dpassword=hunter2</arguments></service>", out _);

            Assert.Contains("-Dpassword=" + ConfigRedactor.Mask, xml);
        }

        [Fact]
        public void OrdinaryArgumentsAreLeftAlone()
        {
            string xml = Redact(@"<service><arguments>-classpath c:\app\out test.Main --port 8080</arguments></service>", out var removed);

            Assert.Contains("--port 8080", xml);
            Assert.Contains(@"-classpath c:\app\out", xml);
            Assert.Empty(removed);
        }

        /// <summary>
        /// A file this code cannot read is a file whose secrets it cannot find. Passing it
        /// through unread is the one outcome that must not happen.
        /// </summary>
        [Fact]
        public void AFileThatDoesNotParseIsNotPassedThrough()
        {
            string xml = Redact(@"<service><password>hunter2</password>", out var removed);

            Assert.DoesNotContain("hunter2", xml);
            Assert.NotEmpty(removed);
        }

        [Fact]
        public void CommentsAndUnknownElementsSurvive()
        {
            string xml = Redact(@"
                <service>
                  <!-- keep me -->
                  <somethingTheModelDoesNotKnow value=""42"" />
                  <password>hunter2</password>
                </service>", out _);

            Assert.Contains("keep me", xml);
            Assert.Contains("somethingTheModelDoesNotKnow", xml);
            Assert.DoesNotContain("hunter2", xml);
        }

        /// <summary>Nesting is no escape: the walk is recursive.</summary>
        [Fact]
        public void ASecretNestedSeveralElementsDeepDoesNotSurvive()
        {
            string xml = Redact(
                @"<service><extensions><extension><settings><password>hunter2</password></settings></extension></extensions></service>",
                out var removed);

            Assert.DoesNotContain("hunter2", xml);
            Assert.Contains(removed, r => r.Contains("extensions/extension/settings/password"));
        }

        private static string Redact(string xml, out System.Collections.Generic.IReadOnlyList<string> removed) =>
            ConfigRedactor.Redact(xml.Trim(), out removed);
    }
}

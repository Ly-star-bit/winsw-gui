using System;
using System.Collections.Generic;
using System.IO;
using WinSW.Configuration;
using Xunit;

namespace WinSW.Tests
{
    public class ProxyConfigTests
    {
        private const string Address = "http://proxy.test.invalid:8080";

        [Fact]
        public void ProxyReachesTheChildEnvironment()
        {
            var environment = Load($"<proxy>{Address}</proxy>");

            Assert.Equal(Address, environment["HTTP_PROXY"]);
            Assert.Equal(Address, environment["HTTPS_PROXY"]);
            Assert.False(environment.ContainsKey("NO_PROXY"));
        }

        /// <summary>
        /// The variables describe how the wrapped program reaches the network. The wrapper's own
        /// downloads have a proxy attribute of their own, and must not start following this one.
        /// </summary>
        [Fact]
        public void ProxyIsNotPublishedToTheWrapperItself()
        {
            _ = Load($"<proxy>{Address}</proxy>");

            Assert.NotEqual(Address, Environment.GetEnvironmentVariable("HTTP_PROXY"));
        }

        [Fact]
        public void DirectHostsAreNormalizedOntoOneList()
        {
            var environment = Load($@"<proxy noProxy="" localhost , 127.0.0.1 ,, .corp.example.com "">{Address}</proxy>");

            Assert.Equal("localhost,127.0.0.1,.corp.example.com", environment["NO_PROXY"]);
        }

        [Fact]
        public void AnEnvironmentEntryOutranksTheElement()
        {
            var environment = Configured("HTTP_PROXY", "http://explicit.test.invalid:3128");
            new ProxyConfig(Address).ApplyTo(environment);

            Assert.Equal("http://explicit.test.invalid:3128", environment["HTTP_PROXY"]);
            Assert.Equal(Address, environment["HTTPS_PROXY"]);
        }

        /// <summary>
        /// Windows resolves environment variables case-insensitively, so a lowercase entry is the
        /// same variable and has to win in the same way.
        /// </summary>
        [Fact]
        public void TheCaseOfAnEnvironmentEntryDoesNotMatter()
        {
            var environment = Configured("http_proxy", "http://explicit.test.invalid:3128");
            new ProxyConfig(Address).ApplyTo(environment);

            Assert.Equal("http://explicit.test.invalid:3128", environment["http_proxy"]);
            Assert.False(environment.ContainsKey("HTTP_PROXY"));

            // Only that one variable was spoken for; the rest of the element still applies.
            Assert.Equal(Address, environment["HTTPS_PROXY"]);
        }

        [Fact]
        public void JavaOptionsCarryTheProxyTheJvmWillNotReadFromTheEnvironment()
        {
            var environment = Load($@"<proxy java=""true"" noProxy=""localhost,.corp.example.com"">{Address}</proxy>");

            Assert.StartsWith(
                "-Dhttp.proxyHost=proxy.test.invalid -Dhttp.proxyPort=8080 " +
                "-Dhttps.proxyHost=proxy.test.invalid -Dhttps.proxyPort=8080 " +
                "-Dhttp.nonProxyHosts=localhost|*.corp.example.com",
                environment["JAVA_TOOL_OPTIONS"],
                StringComparison.Ordinal);

            // The variables are still set: java="true" adds to the element, it does not replace it.
            Assert.Equal(Address, environment["HTTP_PROXY"]);
        }

        [Fact]
        public void TheDefaultPortIsTheSchemeDefault()
        {
            var environment = Load(@"<proxy java=""true"">https://proxy.test.invalid</proxy>");

            Assert.Contains("-Dhttp.proxyPort=443", environment["JAVA_TOOL_OPTIONS"], StringComparison.Ordinal);
        }

        /// <summary>
        /// Built without going through the XML, whose %VAR% expansion would have an opinion about
        /// the percent signs: what is under test is the escaping the URL itself carries.
        /// </summary>
        [Fact]
        public void CredentialsAreDecodedAndQuoted()
        {
            var proxy = new ProxyConfig("http://a%20user:p%20ss%40word@proxy.test.invalid:8080", null, true);

            Assert.Contains(@"-Dhttp.proxyUser=""a user""", proxy.JavaOptions!, StringComparison.Ordinal);
            Assert.Contains(@"-Dhttp.proxyPassword=""p ss@word""", proxy.JavaOptions!, StringComparison.Ordinal);
        }

        /// <summary>
        /// The variable may already be carrying something else. The JVM takes the last -D of a
        /// kind, so ours goes in front and whatever was there still wins.
        /// </summary>
        [Fact]
        public void ExistingJavaToolOptionsSurviveInFront()
        {
            var environment = Configured("JAVA_TOOL_OPTIONS", "-Dfile.encoding=UTF-8");
            new ProxyConfig(Address, null, true).ApplyTo(environment);

            Assert.EndsWith(" -Dfile.encoding=UTF-8", environment["JAVA_TOOL_OPTIONS"], StringComparison.Ordinal);
            Assert.StartsWith("-Dhttp.proxyHost=", environment["JAVA_TOOL_OPTIONS"], StringComparison.Ordinal);
        }

        [Fact]
        public void ASocksProxyIsPassedOnButHasNoJavaEquivalent()
        {
            var environment = Load("<proxy>socks5://proxy.test.invalid:1080</proxy>");

            Assert.Equal("socks5://proxy.test.invalid:1080", environment["HTTP_PROXY"]);
            Assert.Throws<InvalidDataException>(() => new ProxyConfig("socks5://proxy.test.invalid:1080", null, true));
        }

        [Theory]
        [InlineData("proxy.test.invalid:8080")]
        [InlineData("//proxy.test.invalid:8080")]
        [InlineData("ftp://proxy.test.invalid")]
        [InlineData("   ")]
        public void AnAddressThatIsNotOneIsRefused(string address) =>
            Assert.Throws<InvalidDataException>(() => new ProxyConfig(address));

        [Fact]
        public void NothingIsAddedWithoutTheElement() =>
            Assert.Empty(Load(string.Empty));

        /// <summary>
        /// What the wrapper hands to <see cref="ProxyConfig.ApplyTo"/>: the configuration's own
        /// &lt;env&gt; entries and nothing else, in a dictionary with the ordinal comparer that
        /// <see cref="XmlServiceConfig"/> gives it.
        /// </summary>
        private static Dictionary<string, string> Configured(string name, string value) =>
            new() { [name] = value };

        /// <summary>
        /// Deliberately never given an &lt;env&gt; entry: loading one publishes it to this
        /// process, and an HTTP_PROXY here would send the download tests through a proxy that
        /// does not exist. Anything about &lt;env&gt; is tested against <see cref="Configured"/>.
        /// </summary>
        private static Dictionary<string, string> Load(string elements)
        {
            var config = XmlServiceConfig.FromXml(
                $@"<service><id>proxy</id><executable>node.exe</executable>{elements}</service>");

            return config.EnvironmentVariables;
        }
    }
}

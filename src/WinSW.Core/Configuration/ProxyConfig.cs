using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace WinSW.Configuration
{
    /// <summary>
    /// The <c>&lt;proxy&gt;</c> element: one proxy address, expanded into the settings a wrapped
    /// program is expected to read.
    /// </summary>
    /// <remarks>
    /// Nothing here forces a program through a proxy. The wrapper starts a child process; it
    /// cannot intercept that child's sockets, so all it can do is hand it the settings the
    /// common runtimes already look for. A program that reads neither the environment variables
    /// nor the JVM options is unaffected by this element, and has to be pointed at the proxy by
    /// whatever means it does understand.
    /// </remarks>
    public sealed class ProxyConfig
    {
        internal const string JavaToolOptions = "JAVA_TOOL_OPTIONS";

        private readonly Uri uri;

        /// <summary>
        /// Initializes a new instance of the <see cref="ProxyConfig"/> class.
        /// </summary>
        /// <param name="address">The proxy address, scheme included.</param>
        /// <param name="noProxy">Hosts to reach directly, separated by commas.</param>
        /// <param name="java">Whether to also express the proxy as JVM options.</param>
        /// <exception cref="InvalidDataException">
        /// The address is missing or malformed, or it cannot be expressed as JVM options.
        /// </exception>
        public ProxyConfig(string? address, string? noProxy = null, bool java = false)
        {
            address = address?.Trim() ?? string.Empty;
            if (address.Length == 0)
            {
                throw new InvalidDataException(
                    "<proxy> is empty: it needs a proxy address, such as 'http://proxy.example.com:8080'.");
            }

            // A scheme is required rather than assumed: 'proxy.example.com:8080' parses as a URI
            // whose scheme is the host name, and silently proxying to nowhere is worse than a
            // configuration error the moment the file is read.
            if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) ||
                !IsProxyScheme(uri.Scheme) ||
                uri.Host.Length == 0)
            {
                throw new InvalidDataException(
                    "'" + address + "' is not a proxy address. Expected a URL with its scheme spelled out, " +
                    "such as 'http://proxy.example.com:8080'.");
            }

            this.uri = uri;
            this.Address = address;
            this.NoProxy = NormalizeList(noProxy, ",");
            this.Java = java;

            // Built here rather than at start time so that a proxy the JVM cannot be given is
            // refused while the configuration is being written, not when the service first runs.
            this.JavaOptions = java ? this.BuildJavaOptions() : null;
        }

        /// <summary>
        /// The address as configured, which is what the environment variables carry.
        /// </summary>
        public string Address { get; }

        /// <summary>
        /// The hosts to reach directly, or <see langword="null"/> if none were named.
        /// </summary>
        public string? NoProxy { get; }

        /// <summary>
        /// Whether the proxy is also expressed as JVM options.
        /// </summary>
        public bool Java { get; }

        /// <summary>
        /// The JVM options this proxy amounts to, or <see langword="null"/> unless <see cref="Java"/>.
        /// </summary>
        public string? JavaOptions { get; }

        /// <summary>
        /// Windows resolves environment variables case-insensitively but a dictionary does not,
        /// so a variable is looked up the way the child process would see it, and written back
        /// under the name the configuration already spelled.
        /// </summary>
        private static string? ExistingName(IDictionary<string, string> environment, string variable)
        {
            foreach (string name in environment.Keys)
            {
                if (string.Equals(name, variable, StringComparison.OrdinalIgnoreCase))
                {
                    return name;
                }
            }

            return null;
        }

        private static bool IsProxyScheme(string scheme)
        {
            switch (scheme.ToLowerInvariant())
            {
                case "http":
                case "https":
                case "socks":
                case "socks4":
                case "socks4a":
                case "socks5":
                case "socks5h":
                    return true;

                default:
                    return false;
            }
        }

        private static string? NormalizeList(string? list, string separator)
        {
            var entries = Split(list);
            return entries.Count == 0 ? null : string.Join(separator, entries);
        }

        private static List<string> Split(string? list)
        {
            var entries = new List<string>();
            if (list is null)
            {
                return entries;
            }

            foreach (string entry in list.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string trimmed = entry.Trim();
                if (trimmed.Length != 0)
                {
                    entries.Add(trimmed);
                }
            }

            return entries;
        }

        /// <summary>
        /// Translates the direct-connection list into the syntax the JVM uses for it: hosts are
        /// separated by a bar, and a leading dot is written as a wildcard.
        /// </summary>
        private static string NonProxyHosts(string noProxy)
        {
            var hosts = Split(noProxy);
            for (int i = 0; i < hosts.Count; i++)
            {
                if (hosts[i].Length > 1 && hosts[i][0] == '.')
                {
                    hosts[i] = "*" + hosts[i];
                }
            }

            return string.Join("|", hosts);
        }

        /// <summary>
        /// The JVM splits JAVA_TOOL_OPTIONS on whitespace, so a value containing any has to be
        /// quoted. A value containing a quote of its own cannot be expressed at all.
        /// </summary>
        private static string Quote(string name, string value)
        {
            if (value.IndexOf('"') >= 0)
            {
                throw new InvalidDataException(
                    "The " + name + " value contains a double quote, which cannot be passed to the JVM this way.");
            }

            return value.IndexOf(' ') < 0 && value.IndexOf('\t') < 0 ? value : "\"" + value + "\"";
        }

        /// <summary>
        /// Adds the proxy settings to <paramref name="environment"/>, leaving alone any variable
        /// the configuration already sets: an explicit <c>&lt;env&gt;</c> entry outranks this element.
        /// </summary>
        /// <param name="environment">The environment being prepared for the child process.</param>
        public void ApplyTo(IDictionary<string, string> environment)
        {
            if (environment is null)
            {
                throw new ArgumentNullException(nameof(environment));
            }

            SetIfAbsent("HTTP_PROXY", this.Address);
            SetIfAbsent("HTTPS_PROXY", this.Address);

            string? direct = this.NoProxy;
            if (direct != null)
            {
                SetIfAbsent("NO_PROXY", direct);
            }

            string? options = this.JavaOptions;
            if (options is null)
            {
                return;
            }

            // JAVA_TOOL_OPTIONS is a single variable that a machine, or this configuration, may
            // already be using for something else. Ours goes in front rather than over it: the
            // JVM takes the last -D of a kind, so anything already there still wins.
            string name = ExistingName(environment, JavaToolOptions) ?? JavaToolOptions;
            string inherited = (environment.TryGetValue(name, out string? value)
                ? value
                : Environment.GetEnvironmentVariable(JavaToolOptions)) ?? string.Empty;

            inherited = inherited.Trim();
            environment[name] = inherited.Length == 0 ? options : options + " " + inherited;

            void SetIfAbsent(string variable, string value)
            {
                if (ExistingName(environment, variable) is null)
                {
                    environment[variable] = value;
                }
            }
        }

        private string BuildJavaOptions()
        {
            string scheme = this.uri.Scheme;
            if (!string.Equals(scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "A '" + scheme + "' proxy has no equivalent among the JVM's proxy options. Drop java=\"true\" " +
                    "and pass the options your program needs in <arguments>.");
            }

            var options = new StringBuilder();
            string host = this.uri.Host;
            string port = this.uri.Port.ToString(CultureInfo.InvariantCulture);

            // Both schemes point at the one proxy: https.proxyHost names the proxy's own address,
            // not the scheme it is reached over, so an http proxy belongs under both names.
            Append("http.proxyHost", host);
            Append("http.proxyPort", port);
            Append("https.proxyHost", host);
            Append("https.proxyPort", port);

            string userInfo = this.uri.UserInfo;
            if (userInfo.Length != 0)
            {
                int separator = userInfo.IndexOf(':');
                string user = Uri.UnescapeDataString(separator < 0 ? userInfo : userInfo.Substring(0, separator));
                Append("http.proxyUser", user);
                Append("https.proxyUser", user);

                if (separator >= 0)
                {
                    string password = Uri.UnescapeDataString(userInfo.Substring(separator + 1));
                    Append("http.proxyPassword", password);
                    Append("https.proxyPassword", password);
                }
            }

            string? direct = this.NoProxy;
            if (direct != null)
            {
                // One property serves both schemes; there is no https.nonProxyHosts.
                Append("http.nonProxyHosts", NonProxyHosts(direct));
            }

            return options.ToString();

            void Append(string name, string value)
            {
                if (options.Length != 0)
                {
                    options.Append(' ');
                }

                options.Append("-D").Append(name).Append('=').Append(Quote(name, value));
            }
        }
    }
}

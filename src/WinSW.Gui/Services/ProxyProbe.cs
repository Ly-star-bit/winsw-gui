using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using WinSW.Configuration;

namespace WinSW.Gui.Services
{
    /// <summary>How far a probe got before it stopped.</summary>
    public enum ProxyProbeOutcome
    {
        /// <summary>The proxy answered and the target replied through it.</summary>
        Ok,

        /// <summary>No address is configured, so there is nothing to test.</summary>
        NoAddress,

        /// <summary>The address is not one the wrapper would accept either.</summary>
        BadAddress,

        /// <summary>The target is not an http or https URL.</summary>
        BadTarget,

        /// <summary>Nothing accepted a connection at the proxy's host and port.</summary>
        ProxyUnreachable,

        /// <summary>The proxy is listening, but the request through it did not come back.</summary>
        RequestFailed,
    }

    /// <summary>What one probe found.</summary>
    public sealed class ProxyProbeResult
    {
        internal ProxyProbeResult(ProxyProbeOutcome outcome, string endpoint, string target, string detail = "", int status = 0, long elapsed = 0)
        {
            this.Outcome = outcome;
            this.Endpoint = endpoint;
            this.Target = target;
            this.Detail = detail;
            this.Status = status;
            this.Elapsed = elapsed;
        }

        public ProxyProbeOutcome Outcome { get; }

        /// <summary>The proxy as host:port, for a message that names what was tried.</summary>
        public string Endpoint { get; }

        public string Target { get; }

        /// <summary>The underlying reason, in the framework's words. Never localized.</summary>
        public string Detail { get; }

        /// <summary>The HTTP status the target replied with, when it replied.</summary>
        public int Status { get; }

        /// <summary>Milliseconds the request through the proxy took.</summary>
        public long Elapsed { get; }
    }

    /// <summary>
    /// Answers "does this proxy work" for the address in the editor, before a service is
    /// installed around it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two steps, because the two failures want different fixes. A proxy that accepts no
    /// connection is a process that is not running, a port that is wrong, or one bound to
    /// 127.0.0.1 on a machine that is not this one. A proxy that connects but cannot deliver
    /// the request is a rule, an upstream, or credentials.
    /// </para>
    /// <para>
    /// The probe runs as the logged-on user. A service usually does not, and the console says
    /// so beside the button: this answers whether the proxy is reachable and willing, not
    /// whether the service account may reach it.
    /// </para>
    /// </remarks>
    public static class ProxyProbe
    {
        /// <summary>A small, redirect-free document that exists to be fetched by connectivity checks.</summary>
        public const string DefaultTarget = "https://www.google.com/generate_204";

        /// <summary>Requires the proxy to answer quickly; it is normally on the same network.</summary>
        private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

        /// <summary>The whole round trip, which crosses whatever the proxy sits in front of.</summary>
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

        public static async Task<ProxyProbeResult> RunAsync(string? address, string? target, CancellationToken cancellation = default)
        {
            if (string.IsNullOrWhiteSpace(address))
            {
                return new ProxyProbeResult(ProxyProbeOutcome.NoAddress, string.Empty, string.Empty);
            }

            Uri proxyUri;
            try
            {
                // The wrapper's own parser, so a probe never passes an address the service
                // would then refuse to start on.
                _ = new ProxyConfig(address);
                proxyUri = new Uri(address.Trim());
            }
            catch (InvalidDataException e)
            {
                return new ProxyProbeResult(ProxyProbeOutcome.BadAddress, string.Empty, string.Empty, e.Message);
            }

            string endpoint = proxyUri.Host + ":" + proxyUri.Port;

            string url = string.IsNullOrWhiteSpace(target) ? DefaultTarget : target!.Trim();
            if (!Uri.TryCreate(url, UriKind.Absolute, out var targetUri) ||
                (targetUri.Scheme != Uri.UriSchemeHttp && targetUri.Scheme != Uri.UriSchemeHttps))
            {
                return new ProxyProbeResult(ProxyProbeOutcome.BadTarget, endpoint, url);
            }

            try
            {
                using var socket = new TcpClient();
                using var connecting = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                connecting.CancelAfter(ConnectTimeout);
                await socket.ConnectAsync(proxyUri.Host, proxyUri.Port, connecting.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
            {
                return new ProxyProbeResult(
                    ProxyProbeOutcome.ProxyUnreachable, endpoint, url, $"timed out after {ConnectTimeout.TotalSeconds:0} s");
            }
            catch (Exception e) when (e is SocketException or ArgumentException)
            {
                return new ProxyProbeResult(ProxyProbeOutcome.ProxyUnreachable, endpoint, url, e.Message);
            }

            // WebProxy is given the address without the credentials in it, which it would not
            // read from there anyway; they go on the proxy as credentials of their own.
            var endpointUri = new UriBuilder(proxyUri)
            {
                UserName = string.Empty,
                Password = string.Empty,
                Path = string.Empty,
                Query = string.Empty,
                Fragment = string.Empty,
            }.Uri;

            var proxy = new WebProxy(endpointUri);
            if (proxyUri.UserInfo.Length != 0)
            {
                int separator = proxyUri.UserInfo.IndexOf(':');
                proxy.Credentials = new NetworkCredential(
                    Uri.UnescapeDataString(separator < 0 ? proxyUri.UserInfo : proxyUri.UserInfo.Substring(0, separator)),
                    separator < 0 ? string.Empty : Uri.UnescapeDataString(proxyUri.UserInfo.Substring(separator + 1)));
            }

            // Redirects are not followed: what the target itself answered says more about the
            // path to it than what the second hop answered.
            using var handler = new HttpClientHandler { Proxy = proxy, UseProxy = true, AllowAutoRedirect = false };
            using var http = new HttpClient(handler) { Timeout = RequestTimeout };

            var clock = Stopwatch.StartNew();
            try
            {
                using var response = await http
                    .GetAsync(targetUri, HttpCompletionOption.ResponseHeadersRead, cancellation)
                    .ConfigureAwait(false);

                // 407 is the one status that is the proxy's own rather than the target's: on
                // an http target the proxy answers it directly instead of tunnelling, and
                // reporting that as a reachable target would be a green light for a request
                // that never left the building.
                if (response.StatusCode == HttpStatusCode.ProxyAuthenticationRequired)
                {
                    return new ProxyProbeResult(
                        ProxyProbeOutcome.RequestFailed, endpoint, url, $"HTTP 407 {response.ReasonPhrase}".Trim());
                }

                // Any other answer means the chain carried a request end to end. A 403 from
                // the far side is a different problem from a proxy that never delivered it.
                return new ProxyProbeResult(
                    ProxyProbeOutcome.Ok, endpoint, url, response.ReasonPhrase ?? string.Empty, (int)response.StatusCode, clock.ElapsedMilliseconds);
            }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
            {
                return new ProxyProbeResult(
                    ProxyProbeOutcome.RequestFailed, endpoint, url, $"timed out after {RequestTimeout.TotalSeconds:0} s");
            }
            // NotSupportedException is not hypothetical: the wrapper passes socks and socks5h
            // through because curl reads them, and HttpClient accepts neither.
            catch (Exception e) when (e is HttpRequestException or InvalidOperationException or NotSupportedException)
            {
                return new ProxyProbeResult(ProxyProbeOutcome.RequestFailed, endpoint, url, Innermost(e).Message);
            }
        }

        /// <summary>
        /// The reason a request failed is usually two or three exceptions down: the outer one
        /// says only that it failed.
        /// </summary>
        private static Exception Innermost(Exception e)
        {
            while (e.InnerException is { } inner)
            {
                e = inner;
            }

            return e;
        }
    }
}

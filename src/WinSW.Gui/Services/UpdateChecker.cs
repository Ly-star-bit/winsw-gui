using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

namespace WinSW.Gui.Services
{
    /// <summary>A published release with its downloadable assets.</summary>
    public sealed class ReleaseInfo
    {
        public ReleaseInfo(string tag, string url, IReadOnlyDictionary<string, string> assets)
        {
            this.Tag = tag;
            this.Url = url;
            this.Assets = assets;
        }

        public string Tag { get; }

        public string Url { get; }

        /// <summary>Asset file name → download URL.</summary>
        public IReadOnlyDictionary<string, string> Assets { get; }

        /// <summary>"3.0.0" from "v3.0.0" or "gui-v0.4.0".</summary>
        public string Version
        {
            get
            {
                string tag = this.Tag;
                int v = tag.LastIndexOf('v');
                return v >= 0 ? tag.Substring(v + 1) : tag;
            }
        }
    }

    /// <summary>
    /// Looks up the latest wrapper and GUI releases on GitHub. Network access is optional:
    /// every failure degrades to "unknown", never to an error the user has to dismiss.
    /// </summary>
    public static class UpdateChecker
    {
        private const string WrapperReleases = "https://api.github.com/repos/winsw/winsw/releases?per_page=10";
        private const string GuiReleases = "https://api.github.com/repos/Ly-star-bit/winsw-gui/releases?per_page=10";

        private static readonly HttpClient Http = CreateClient();

        /// <summary>The GUI's own version, as stamped by the build.</summary>
        public static string CurrentGuiVersion
        {
            get
            {
                string? informational = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
                if (string.IsNullOrEmpty(informational))
                {
                    return "0.0.0";
                }

                // Strip a source-link suffix such as "+abc123".
                int plus = informational.IndexOf('+');
                return plus > 0 ? informational.Substring(0, plus) : informational;
            }
        }

        /// <summary>Latest stable wrapper (v3 pre-releases count: v3 is the current line).</summary>
        public static Task<ReleaseInfo?> LatestWrapperAsync() => LatestAsync(WrapperReleases, static tag => tag.StartsWith("v", StringComparison.OrdinalIgnoreCase));

        public static Task<ReleaseInfo?> LatestGuiAsync() => LatestAsync(GuiReleases, static tag => tag.StartsWith("gui-v", StringComparison.OrdinalIgnoreCase));

        /// <summary>True when <paramref name="candidate"/> is a higher version than <paramref name="current"/>.</summary>
        public static bool IsNewer(string candidate, string current)
        {
            return Version.TryParse(Normalize(candidate), out var a) && Version.TryParse(Normalize(current), out var b) && a > b;

            static string Normalize(string value)
            {
                int dash = value.IndexOfAny(new[] { '-', '+' });
                string core = dash > 0 ? value.Substring(0, dash) : value;
                return core.Count(c => c == '.') == 0 ? core + ".0" : core;
            }
        }

        public static async Task<string?> DownloadAsync(string url, string destinationDirectory)
        {
            try
            {
                Directory.CreateDirectory(destinationDirectory);
                string file = Path.Combine(destinationDirectory, Path.GetFileName(new Uri(url).AbsolutePath));

                using var response = await Http.GetAsync(url).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                await using var output = new FileStream(file, FileMode.Create, FileAccess.Write);
                await response.Content.CopyToAsync(output).ConfigureAwait(false);
                return file;
            }
            catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException or UriFormatException)
            {
                return null;
            }
        }

        private static async Task<ReleaseInfo?> LatestAsync(string url, Func<string, bool> tagFilter)
        {
            try
            {
                using var response = await Http.GetAsync(url).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));

                // The API lists newest first; the first matching, non-draft release wins.
                foreach (var release in document.RootElement.EnumerateArray())
                {
                    if (release.TryGetProperty("draft", out var draft) && draft.GetBoolean())
                    {
                        continue;
                    }

                    string tag = release.GetProperty("tag_name").GetString() ?? string.Empty;
                    if (!tagFilter(tag))
                    {
                        continue;
                    }

                    var assets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    if (release.TryGetProperty("assets", out var list))
                    {
                        foreach (var asset in list.EnumerateArray())
                        {
                            string? name = asset.GetProperty("name").GetString();
                            string? download = asset.GetProperty("browser_download_url").GetString();
                            if (name != null && download != null)
                            {
                                assets[name] = download;
                            }
                        }
                    }

                    return new ReleaseInfo(tag, release.GetProperty("html_url").GetString() ?? string.Empty, assets);
                }

                return null;
            }
            catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException or KeyNotFoundException or InvalidOperationException)
            {
                return null;
            }
        }

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("WinSW-GUI/" + CurrentGuiVersion);
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return client;
        }
    }

    internal static class StringCountExtensions
    {
        public static int Count(this string value, Func<char, bool> predicate)
        {
            int count = 0;
            foreach (char c in value)
            {
                if (predicate(c))
                {
                    count++;
                }
            }

            return count;
        }
    }
}

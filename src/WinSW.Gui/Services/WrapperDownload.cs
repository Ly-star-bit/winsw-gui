using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace WinSW.Gui.Services
{
    /// <summary>
    /// Upstream's wrapper build for this machine, fetched by the wizard's Download button into a
    /// per-user cache. The wizard installs it from there the way it installs the bundled one:
    /// copied into <c>bin</c> under the install root, through the elevated copy when that needs one.
    /// </summary>
    /// <remarks>
    /// It used to be written next to the program, and a wrapper there took the service's
    /// configuration and logs into that folder with it — a virtual environment's Scripts
    /// folder, say, which the next rebuild of the environment deletes. Under Program Files it
    /// could not be written at all: the download has no elevated path, and needs none here.
    /// Download is the default way on a machine whose .NET Framework is too old for the
    /// bundled wrapper (see <see cref="NetFramework"/>), which is where both went wrong.
    /// </remarks>
    public static class WrapperDownload
    {
        /// <summary>Upstream's name for its .NET Framework build; see <see cref="WrapperKind.ReleaseAssetFor"/>.</summary>
        public const string FrameworkAsset = "WinSW-net461.exe";

        /// <summary>
        /// Where downloads are kept: a folder of its own beside the bundled wrapper's cache, which
        /// is <c>WinSW.exe</c> and is rewritten whenever its length differs from the one carried.
        /// </summary>
        public static string CacheDirectory { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinSW.Gui",
            "download");

        /// <summary>
        /// The release asset to fetch: the self-contained build for <paramref name="architecture"/>,
        /// or — from an older release that has none — the .NET Framework build, but not on a
        /// machine whose framework is too old to run it, where the download is the way round the
        /// bundled wrapper in the first place. Null when the release has nothing that fits.
        /// </summary>
        internal static string? AssetFor(Architecture architecture, bool frameworkTooOld, IReadOnlyDictionary<string, string> assets)
        {
            string native = architecture switch
            {
                Architecture.Arm64 => "WinSW-arm64.exe",
                Architecture.X64 => "WinSW-x64.exe",
                _ => "WinSW-x86.exe",
            };

            if (assets.ContainsKey(native))
            {
                return native;
            }

            return !frameworkTooOld && assets.ContainsKey(FrameworkAsset) ? FrameworkAsset : null;
        }

        /// <summary>
        /// Downloads <paramref name="url"/> into <see cref="CacheDirectory"/> under the asset's own
        /// name and returns the file, or null when the download failed. The file is only a
        /// source: the install copies it to where the service will run it from.
        /// </summary>
        /// <remarks>
        /// Fetched into a folder of its own and moved in whole, so that a download cut short
        /// never leaves half a file where an earlier, complete one was — the one the wizard may
        /// already be pointing at.
        /// </remarks>
        public static async Task<string?> FetchAsync(string url)
        {
            string? fetched = await UpdateChecker.DownloadAsync(url, Path.Combine(CacheDirectory, "partial")).ConfigureAwait(false);
            if (fetched is null)
            {
                return null;
            }

            string file = Path.Combine(CacheDirectory, Path.GetFileName(fetched));
            File.Move(fetched, file, overwrite: true);
            return file;
        }
    }
}

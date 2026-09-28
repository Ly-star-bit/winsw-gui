using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace WinSW.Gui.Services
{
    /// <summary>Why "Update now" has no file to restart into.</summary>
    public enum UpdateProblem
    {
        None,

        /// <summary>
        /// This console is not one of the executables a release carries: a build from source, or
        /// an arm64 build that depends on an installed runtime, which no release has.
        /// </summary>
        NotThisBuild,

        /// <summary>The release has no executable of this build's kind.</summary>
        NoAsset,

        /// <summary>The release has no SHA256SUMS.txt, or the file has no line for the executable.</summary>
        NoChecksum,

        /// <summary>
        /// The executable's folder cannot be written to, and the new file has to go beside the
        /// old one; or a file of the new one's name is there and cannot be deleted.
        /// </summary>
        FolderNotWritable,

        DownloadFailed,

        /// <summary>Nothing more of the file arrived for <see cref="UpdateChecker.StallTimeout"/>, and it was given up.</summary>
        DownloadStalled,

        /// <summary>The connection closed before the whole file had arrived.</summary>
        DownloadTruncated,

        /// <summary>What arrived is not the file the release lists. It has been deleted.</summary>
        ChecksumMismatch,
    }

    /// <summary>What "Update now" fetched: the checked file, or why there is none.</summary>
    public sealed class UpdateDownload
    {
        private UpdateDownload(string? file, UpdateProblem problem, string? detail)
        {
            this.File = file;
            this.Problem = problem;
            this.Detail = detail;
        }

        /// <summary>The release's executable beside the running one, its checksum verified; null on a problem.</summary>
        public string? File { get; }

        public UpdateProblem Problem { get; }

        /// <summary>
        /// The asset concerned, or what the network or the file system said. For a download that
        /// stalled or was cut short it is this console's own English, for the action log only:
        /// <see cref="Problem"/> alone says what to show.
        /// </summary>
        public string? Detail { get; }

        internal static UpdateDownload Ready(string file) => new(file, UpdateProblem.None, null);

        internal static UpdateDownload Failed(UpdateProblem problem, string? detail) => new(null, problem, detail);
    }

    /// <summary>
    /// "Update now": the new release's executable of this build's kind, checked against the
    /// release's <c>SHA256SUMS.txt</c> and put where the running one is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The console used to offer only the release page. On Windows Server 2012 R2, 2016 and 2019
    /// that opens in Internet Explorer under Enhanced Security Configuration, which blocks the
    /// download; the file then came over by some other route, under some other name, and the
    /// sign-in entry and the Explorer verb went on starting the old one.
    /// </para>
    /// <para>
    /// A running executable cannot be overwritten or deleted, but it can be renamed. The new file
    /// is downloaded beside it as <c>&lt;name&gt;.new</c> — on the same volume, so that moving it
    /// is a rename — and checked; then the running one becomes <c>&lt;name&gt;.old</c>, the new
    /// one takes its name, and the console restarts into it with
    /// <see cref="StartupArguments.ReplaceArgument"/>. Everything that starts the console by its
    /// path goes on working, and the next start deletes the <c>.old</c>; see
    /// <see cref="CleanUpAsync(string)"/>.
    /// </para>
    /// <para>
    /// The checksum is what makes the file safe to start. The download comes over HTTPS from
    /// GitHub's own hosts, but the release's checksum list is written by the same workflow that
    /// built the files, and a file that does not match it — a download cut short, a proxy that
    /// substituted an error page — is deleted rather than run.
    /// </para>
    /// </remarks>
    public static class SelfUpdate
    {
        /// <summary>The checksum list each release carries; see .github/workflows/gui.yml.</summary>
        public const string ChecksumsAsset = "SHA256SUMS.txt";

        /// <summary>What the running executable is renamed to while the new one takes its place.</summary>
        internal const string OldSuffix = ".old";

        /// <summary>What the new executable is downloaded as, beside the running one.</summary>
        internal const string NewSuffix = ".new";

        /// <summary>
        /// How often a start tries to delete what the last update left, <see cref="CleanUpRetryDelay"/>
        /// apart. The copy that restarted into this one lets go of the session a moment before its
        /// process ends, and its executable cannot be deleted until then.
        /// </summary>
        private const int CleanUpAttempts = 15;

        private const int BufferSize = 81920;

        private static readonly TimeSpan CleanUpRetryDelay = TimeSpan.FromSeconds(2);

        /// <summary>
        /// The release asset this console is a build of; null when it is none of them, and
        /// "Update now" is not offered.
        /// </summary>
        public static string? AssetForThisBuild { get; } = AssetFor(RuntimeInformation.ProcessArchitecture, IsSingleFile(), IsSelfContained(CoreLibraryLocation()));

        /// <summary>The executable a release carries for a build like this one; see the release step in gui.yml.</summary>
        /// <param name="architecture">The process's, which for an x64 build running on an arm64 machine is x64.</param>
        /// <param name="singleFile">
        /// Built as one executable. A build from source is an executable beside its assemblies,
        /// and swapping the executable alone would leave them behind.
        /// </param>
        /// <param name="selfContained">Carries its own .NET runtime, rather than using the one installed.</param>
        internal static string? AssetFor(Architecture architecture, bool singleFile, bool selfContained)
        {
            if (!singleFile)
            {
                return null;
            }

            return (architecture, selfContained) switch
            {
                (Architecture.X64, true) => "WinSW.Gui-win-x64.exe",
                (Architecture.X64, false) => "WinSW.Gui-win-x64-framework-dependent.exe",
                (Architecture.Arm64, true) => "WinSW.Gui-win-arm64.exe",
                _ => null,
            };
        }

        /// <summary>
        /// Whether the runtime came with the application. A console that uses the installed
        /// runtime loads its core library from the shared framework, under
        /// <c>shared\Microsoft.NETCore.App</c>; a self-contained single file carries that library
        /// inside itself, where it has no location at all.
        /// </summary>
        /// <param name="coreLibraryLocation">Where <c>System.Private.CoreLib</c> was loaded from; empty for one inside the executable.</param>
        internal static bool IsSelfContained(string coreLibraryLocation) =>
            coreLibraryLocation.Length == 0
            || coreLibraryLocation.IndexOf("Microsoft.NETCore.App", StringComparison.OrdinalIgnoreCase) < 0;

        /// <summary>
        /// Reads a checksum list as <c>sha256sum</c> writes it: 64 hexadecimal digits, white
        /// space, then the file name — after a <c>*</c> in binary mode. Keyed by file name without
        /// regard to case, with the checksum in lower case.
        /// </summary>
        /// <remarks>
        /// Forgiving about what does not matter — line endings, a byte-order mark, blank lines, a
        /// directory in front of the name — and strict about what does: a line whose checksum is
        /// not 64 hexadecimal digits is skipped, and a name listed twice with two different
        /// checksums is left out, so that it has none rather than a guessed one.
        /// </remarks>
        internal static IReadOnlyDictionary<string, string> ParseChecksums(string text)
        {
            var sums = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var conflicting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string raw in text.Split('\n'))
            {
                string line = raw.TrimStart('\uFEFF').Trim();
                if (line.Length < 66 || line[0] == '#' || !char.IsWhiteSpace(line[64]))
                {
                    continue;
                }

                string hash = line.Substring(0, 64);
                if (!IsHex(hash))
                {
                    continue;
                }

                string name = line.Substring(64).TrimStart();
                if (name.StartsWith('*'))
                {
                    name = name.Substring(1);
                }

                name = name.Substring(name.LastIndexOfAny(new[] { '/', '\\' }) + 1);
                if (name.Length == 0)
                {
                    continue;
                }

                hash = hash.ToLowerInvariant();
                if (sums.TryGetValue(name, out string? listed) && !string.Equals(listed, hash, StringComparison.Ordinal))
                {
                    conflicting.Add(name);
                }

                sums[name] = hash;
            }

            foreach (string name in conflicting)
            {
                sums.Remove(name);
            }

            return sums;
        }

        /// <summary>
        /// Downloads the release's executable for this build beside <paramref name="executable"/>
        /// and checks it. Never throws for the network or the disk: each failure is a
        /// <see cref="UpdateProblem"/>.
        /// </summary>
        /// <param name="progress">Told the percentage downloaded, each time it grows by one.</param>
        public static Task<UpdateDownload> DownloadAsync(ReleaseInfo release, string executable, IProgress<int>? progress, CancellationToken cancellationToken) =>
            DownloadAsync(release, AssetForThisBuild, executable, UpdateChecker.DownloadTextAsync, UpdateChecker.DownloadToAsync, progress, cancellationToken);

        /// <summary>The same, with the build and the network given, so that tests need neither.</summary>
        internal static async Task<UpdateDownload> DownloadAsync(
            ReleaseInfo release,
            string? asset,
            string executable,
            Func<string, CancellationToken, Task<string?>> fetchText,
            Func<string, Stream, IProgress<int>?, CancellationToken, Task> fetchFile,
            IProgress<int>? progress,
            CancellationToken cancellationToken)
        {
            if (asset is null)
            {
                return UpdateDownload.Failed(UpdateProblem.NotThisBuild, null);
            }

            if (!release.Assets.TryGetValue(asset, out string? url))
            {
                return UpdateDownload.Failed(UpdateProblem.NoAsset, asset);
            }

            // Asked before anything is downloaded: a release from before the list existed can
            // still be updated to from its page, just not from here.
            if (!release.Assets.TryGetValue(ChecksumsAsset, out string? checksumsUrl))
            {
                return UpdateDownload.Failed(UpdateProblem.NoChecksum, asset);
            }

            string? checksums = await fetchText(checksumsUrl, cancellationToken).ConfigureAwait(false);
            if (checksums is null)
            {
                return UpdateDownload.Failed(UpdateProblem.DownloadFailed, ChecksumsAsset);
            }

            if (!ParseChecksums(checksums).TryGetValue(asset, out string? expected))
            {
                return UpdateDownload.Failed(UpdateProblem.NoChecksum, asset);
            }

            // Opened before the download starts, so that a folder this account cannot write to —
            // Program Files, for a console not running as administrator — is said at once rather
            // than after seventy megabytes.
            //
            // Made new, never opened as it is. In a folder where other accounts can create files
            // — a folder under the root of drive C, some under ProgramData — one of them could put
            // a file of this name there first. Opened and overwritten, it would keep its owner and
            // its permissions, so that they could change it between the check and the swap, or
            // own the console's executable ever after; a hard link of this name would have the
            // download written into whatever file it links to. So whatever is there is deleted
            // (a link, not what it links to), and the file is created only if the name is still
            // free; one put back in between makes the creation fail rather than be used.
            string file = NewPath(executable);
            FileStream output;
            try
            {
                File.Delete(file);
                output = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, useAsync: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                return UpdateDownload.Failed(UpdateProblem.FolderNotWritable, e.Message);
            }

            try
            {
                await using (output.ConfigureAwait(false))
                {
                    await fetchFile(url, output, progress, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (DownloadStalledException e)
            {
                // This and the next come before the general case, which they are part of: they
                // are said in the console's own words, where anything else can only repeat
                // what .NET said.
                TryDelete(file);
                return UpdateDownload.Failed(UpdateProblem.DownloadStalled, e.Message);
            }
            catch (DownloadTruncatedException e)
            {
                TryDelete(file);
                return UpdateDownload.Failed(UpdateProblem.DownloadTruncated, e.Message);
            }
            catch (Exception e) when (e is HttpRequestException or IOException or TimeoutException or OperationCanceledException or InvalidOperationException or UriFormatException)
            {
                TryDelete(file);
                return UpdateDownload.Failed(UpdateProblem.DownloadFailed, e.Message);
            }

            string actual;
            try
            {
                actual = await Task.Run(() => HashAsync(file, cancellationToken), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or OperationCanceledException)
            {
                // Taken or removed between the download and the check: an antivirus quarantine
                // does that to a file it does not like.
                TryDelete(file);
                return UpdateDownload.Failed(UpdateProblem.DownloadFailed, e.Message);
            }

            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(file);
                return UpdateDownload.Failed(UpdateProblem.ChecksumMismatch, asset);
            }

            return UpdateDownload.Ready(file);
        }

        /// <summary>The SHA-256 of a file, in lower-case hexadecimal as <c>sha256sum</c> writes it.</summary>
        internal static async Task<string> HashAsync(string file, CancellationToken cancellationToken)
        {
            var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);
            await using (input.ConfigureAwait(false))
            {
                byte[] hash = await SHA256.HashDataAsync(input, cancellationToken).ConfigureAwait(false);
                return Convert.ToHexString(hash).ToLower(CultureInfo.InvariantCulture);
            }
        }

        /// <summary>
        /// Puts <paramref name="downloaded"/> where <paramref name="executable"/> is, and the
        /// running executable aside as its <c>.old</c>. Throws when either move fails, having put
        /// the running executable back.
        /// </summary>
        internal static void Swap(string executable, string downloaded)
        {
            string old = OldPath(executable);

            // Left by the last update, when the copy it restarted from had not ended in time for
            // the start after it to delete it. That copy has long ended since.
            File.Delete(old);
            File.Move(executable, old);
            try
            {
                File.Move(downloaded, executable);
            }
            catch
            {
                File.Move(old, executable);
                throw;
            }
        }

        /// <summary>
        /// Takes a <see cref="Swap"/> back: the new executable returns to where it was
        /// downloaded, and the running one to its own name. For when the new one did not start.
        /// </summary>
        internal static void Undo(string executable, string downloaded)
        {
            File.Move(executable, downloaded, overwrite: true);
            File.Move(OldPath(executable), executable);
        }

        /// <summary>
        /// Deletes what an update left beside <paramref name="executable"/>: the executable it
        /// replaced, and a download it did not finish. Tries for half a minute, in the background,
        /// while the copy that restarted into this one finishes ending.
        /// </summary>
        public static Task CleanUpAsync(string executable) => CleanUpAsync(executable, CleanUpAttempts, CleanUpRetryDelay);

        /// <summary>The same, with the attempts given. True once nothing is left.</summary>
        internal static async Task<bool> CleanUpAsync(string executable, int attempts, TimeSpan delay)
        {
            string[] leftovers = { OldPath(executable), NewPath(executable) };
            for (int attempt = 1; ; attempt++)
            {
                bool done = true;
                foreach (string file in leftovers)
                {
                    done &= TryDelete(file);
                }

                if (done)
                {
                    return true;
                }

                if (attempt >= attempts)
                {
                    return false;
                }

                await Task.Delay(delay).ConfigureAwait(false);
            }
        }

        internal static string OldPath(string executable) => executable + OldSuffix;

        internal static string NewPath(string executable) => executable + NewSuffix;

        /// <summary>
        /// Whether the application's own assembly is inside its executable. Its location is
        /// empty then, which is what is asked here.
        /// </summary>
        [UnconditionalSuppressMessage("SingleFile", "IL3000", Justification = "An empty location is the answer being looked for.")]
        private static bool IsSingleFile() => string.IsNullOrEmpty(typeof(SelfUpdate).Assembly.Location);

        [UnconditionalSuppressMessage("SingleFile", "IL3000", Justification = "An empty location is the answer being looked for.")]
        private static string CoreLibraryLocation() => typeof(object).Assembly.Location;

        /// <summary>True when the file is not there, whether it was deleted now or never existed.</summary>
        private static bool TryDelete(string file)
        {
            try
            {
                File.Delete(file);
                return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        private static bool IsHex(string text)
        {
            foreach (char c in text)
            {
                if (!char.IsAsciiHexDigit(c))
                {
                    return false;
                }
            }

            return true;
        }
    }
}

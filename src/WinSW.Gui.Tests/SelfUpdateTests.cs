using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WinSW.Gui.Services;
using Xunit;

namespace WinSW.Gui.Tests
{
    /// <summary>
    /// "Update now": which of a release's executables this build is, how the release's
    /// SHA256SUMS.txt is read, and what becomes of the file on the way — a file that does not
    /// match the list must never be left where it could be started.
    /// </summary>
    public sealed class SelfUpdateTests : IDisposable
    {
        private const string Asset = "WinSW.Gui-win-x64.exe";

        private const string ChecksumsUrl = "https://example.invalid/SHA256SUMS.txt";

        private const string AssetUrl = "https://example.invalid/" + Asset;

        private static readonly byte[] Release = Encoding.UTF8.GetBytes("the new console");

        private readonly string folder = Path.Combine(Path.GetTempPath(), "winsw-selfupdate-" + Guid.NewGuid().ToString("n").Substring(0, 12));

        public SelfUpdateTests()
        {
            Directory.CreateDirectory(this.folder);
        }

        private string Executable => Path.Combine(this.folder, "WinSW.Gui.exe");

        public void Dispose()
        {
            try
            {
                Directory.Delete(this.folder, recursive: true);
            }
            catch (IOException)
            {
            }
        }

        // Which asset -------------------------------------------------------------

        /// <summary>The three executables gui.yml puts in a release, each for the build it is.</summary>
        [Fact]
        public void EachReleasedBuildUpdatesToItsOwnKind()
        {
            Assert.Equal("WinSW.Gui-win-x64.exe", SelfUpdate.AssetFor(Architecture.X64, singleFile: true, selfContained: true));
            Assert.Equal("WinSW.Gui-win-x64-framework-dependent.exe", SelfUpdate.AssetFor(Architecture.X64, singleFile: true, selfContained: false));
            Assert.Equal("WinSW.Gui-win-arm64.exe", SelfUpdate.AssetFor(Architecture.Arm64, singleFile: true, selfContained: true));
        }

        /// <summary>No release carries these, and a guess would swap in a console that cannot run here.</summary>
        [Fact]
        public void ABuildNoReleaseCarriesHasNoAsset()
        {
            Assert.Null(SelfUpdate.AssetFor(Architecture.Arm64, singleFile: true, selfContained: false));
            Assert.Null(SelfUpdate.AssetFor(Architecture.X86, singleFile: true, selfContained: true));
            Assert.Null(SelfUpdate.AssetFor(Architecture.X86, singleFile: true, selfContained: false));
        }

        /// <summary>
        /// A build from source is an executable beside its assemblies. Swapping the executable
        /// alone would start a release's console against the old assemblies.
        /// </summary>
        [Fact]
        public void ABuildThatIsNotOneFileHasNoAsset()
        {
            Assert.Null(SelfUpdate.AssetFor(Architecture.X64, singleFile: false, selfContained: true));
            Assert.Null(SelfUpdate.AssetFor(Architecture.X64, singleFile: false, selfContained: false));
        }

        /// <summary>The tests run from a build output, which is not one file.</summary>
        [Fact]
        public void TheTestRunIsNotUpdatedInPlace()
        {
            Assert.Null(SelfUpdate.AssetForThisBuild);
        }

        [Fact]
        public void TheInstalledRuntimeGivesAFrameworkDependentBuildAway()
        {
            Assert.False(SelfUpdate.IsSelfContained(@"C:\Program Files\dotnet\shared\Microsoft.NETCore.App\8.0.8\System.Private.CoreLib.dll"));
            Assert.False(SelfUpdate.IsSelfContained("/usr/local/share/dotnet/shared/Microsoft.NETCore.App/8.0.8/System.Private.CoreLib.dll"));
        }

        /// <summary>Inside a single file the core library has no location; beside the executable, it is the application's own.</summary>
        [Fact]
        public void ACoreLibraryInsideTheExecutableOrBesideItIsSelfContained()
        {
            Assert.True(SelfUpdate.IsSelfContained(string.Empty));
            Assert.True(SelfUpdate.IsSelfContained(@"D:\tools\WinSW.Gui\System.Private.CoreLib.dll"));
        }

        // Reading SHA256SUMS.txt ------------------------------------------------------

        [Fact]
        public void ReadsTheListAsSha256sumWritesIt()
        {
            string x64 = Hex('a');
            string arm = Hex('b');

            var sums = SelfUpdate.ParseChecksums($"{x64}  WinSW.Gui-win-x64.exe\n{arm}  WinSW.Gui-win-arm64.exe\n");

            Assert.Equal(2, sums.Count);
            Assert.Equal(x64, sums["WinSW.Gui-win-x64.exe"]);
            Assert.Equal(arm, sums["WinSW.Gui-win-arm64.exe"]);
        }

        /// <summary>Written on Windows, the list may come with a byte-order mark and carriage returns.</summary>
        [Fact]
        public void ALineEndingOrAByteOrderMarkIsNotPartOfTheName()
        {
            var sums = SelfUpdate.ParseChecksums("\uFEFF" + Hex('c') + "  " + Asset + "\r\n\r\n");

            Assert.Equal(Hex('c'), sums[Asset]);
        }

        [Fact]
        public void TheBinaryMarkerAndAFolderInFrontOfTheNameAreDropped()
        {
            var sums = SelfUpdate.ParseChecksums(
                Hex('d') + " *WinSW.Gui-win-arm64.exe\n"
                + Hex('e') + "  release/WinSW.Gui-win-x64.exe\n"
                + Hex('f') + "  release\\WinSW.Gui-win-x64-framework-dependent.exe\n");

            Assert.Equal(Hex('d'), sums["WinSW.Gui-win-arm64.exe"]);
            Assert.Equal(Hex('e'), sums["WinSW.Gui-win-x64.exe"]);
            Assert.Equal(Hex('f'), sums["WinSW.Gui-win-x64-framework-dependent.exe"]);
        }

        /// <summary>PowerShell's Get-FileHash writes upper case; the comparison must not care.</summary>
        [Fact]
        public void AChecksumInUpperCaseIsKeptInLowerCaseAndNamesIgnoreCase()
        {
            var sums = SelfUpdate.ParseChecksums(Hex('A') + "  " + Asset + "\n");

            Assert.Equal(Hex('a'), sums["winsw.gui-win-x64.EXE"]);
        }

        [Fact]
        public void ALineThatIsNotAChecksumIsSkipped()
        {
            var sums = SelfUpdate.ParseChecksums(
                "# SHA-256\n"
                + new string('a', 63) + "  short.exe\n"
                + new string('a', 65) + "  long.exe\n"
                + new string('a', 63) + "g  nothex.exe\n"
                + Hex('a') + "\n"
                + Hex('a') + "  \n"
                + Hex('b') + "  " + Asset + "\n");

            Assert.Equal(new[] { Asset }, sums.Keys);
        }

        /// <summary>Two checksums for one file leave it with none, rather than with whichever came last.</summary>
        [Fact]
        public void AFileListedWithTwoDifferentChecksumsHasNone()
        {
            var sums = SelfUpdate.ParseChecksums(
                Hex('a') + "  " + Asset + "\n"
                + Hex('b') + "  " + Asset + "\n"
                + Hex('c') + "  other.exe\n"
                + Hex('C') + "  other.exe\n");

            Assert.False(sums.ContainsKey(Asset));
            Assert.Equal(Hex('c'), sums["other.exe"]);
        }

        [Fact]
        public async Task HashesAsSha256sumWrites()
        {
            string file = Path.Combine(this.folder, "abc.txt");
            await File.WriteAllTextAsync(file, "abc");

            Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", await SelfUpdate.HashAsync(file, CancellationToken.None));
        }

        // Fetching and checking ---------------------------------------------------------

        [Fact]
        public async Task AFileThatMatchesTheListIsLeftBesideTheExecutable()
        {
            var download = await this.DownloadAsync(Sha256(Release), SendRelease);

            Assert.Equal(UpdateProblem.None, download.Problem);
            Assert.Equal(SelfUpdate.NewPath(this.Executable), download.File);
            Assert.Equal(Release, await File.ReadAllBytesAsync(download.File!));
        }

        /// <summary>A download cut short or swapped by a proxy: deleted, and never started.</summary>
        [Fact]
        public async Task AFileThatDoesNotMatchTheListIsDeleted()
        {
            var download = await this.DownloadAsync(Hex('0'), SendRelease);

            Assert.Equal(UpdateProblem.ChecksumMismatch, download.Problem);
            Assert.Equal(Asset, download.Detail);
            Assert.Null(download.File);
            Assert.False(File.Exists(SelfUpdate.NewPath(this.Executable)));
        }

        [Fact]
        public async Task ADownloadThatFailsLeavesNothingBehind()
        {
            var download = await this.DownloadAsync(
                Sha256(Release),
                async (url, output, progress, cancellationToken) =>
                {
                    await output.WriteAsync(Release.AsMemory(0, 4), cancellationToken);
                    throw new HttpRequestException("connection reset");
                });

            Assert.Equal(UpdateProblem.DownloadFailed, download.Problem);
            Assert.Equal("connection reset", download.Detail);
            Assert.False(File.Exists(SelfUpdate.NewPath(this.Executable)));
        }

        /// <summary>
        /// A download that went silent is a problem of its own, which the console words from its
        /// dictionaries; the English detail is for the action log.
        /// </summary>
        [Fact]
        public async Task ADownloadThatStalledIsSaidAsSuchAndLeavesNothingBehind()
        {
            var download = await this.DownloadAsync(
                Sha256(Release),
                async (url, output, progress, cancellationToken) =>
                {
                    await output.WriteAsync(Release.AsMemory(0, 4), cancellationToken);
                    throw new DownloadStalledException(TimeSpan.FromSeconds(60));
                });

            Assert.Equal(UpdateProblem.DownloadStalled, download.Problem);
            Assert.Equal("Nothing arrived for 60 seconds.", download.Detail);
            Assert.Null(download.File);
            Assert.False(File.Exists(SelfUpdate.NewPath(this.Executable)));
        }

        [Fact]
        public async Task ADownloadCutShortIsSaidAsSuchAndLeavesNothingBehind()
        {
            var download = await this.DownloadAsync(
                Sha256(Release),
                async (url, output, progress, cancellationToken) =>
                {
                    await output.WriteAsync(Release.AsMemory(0, 4), cancellationToken);
                    throw new DownloadTruncatedException(4, Release.Length);
                });

            Assert.Equal(UpdateProblem.DownloadTruncated, download.Problem);
            Assert.Equal($"The download ended after 4 of {Release.Length} bytes.", download.Detail);
            Assert.Null(download.File);
            Assert.False(File.Exists(SelfUpdate.NewPath(this.Executable)));
        }

        /// <summary>
        /// Any other failure to write or wait is still a plain failed download: only the two the
        /// console words itself are told apart.
        /// </summary>
        [Fact]
        public async Task AnyOtherTimeoutOrFileErrorIsAFailedDownload()
        {
            var timedOut = await this.DownloadAsync(Sha256(Release), (url, output, progress, cancellationToken) => throw new TimeoutException("elsewhere"));
            var diskFull = await this.DownloadAsync(Sha256(Release), (url, output, progress, cancellationToken) => throw new IOException("There is not enough space on the disk."));

            Assert.Equal(UpdateProblem.DownloadFailed, timedOut.Problem);
            Assert.Equal(UpdateProblem.DownloadFailed, diskFull.Problem);
            Assert.Equal("There is not enough space on the disk.", diskFull.Detail);
        }

        /// <summary>A release from before the list existed is updated to from its page, not from here.</summary>
        [Fact]
        public async Task AReleaseWithoutAChecksumListIsNotFetched()
        {
            bool fetched = false;
            var release = new ReleaseInfo("gui-v9.0.0", "https://example.invalid/release", new Dictionary<string, string> { [Asset] = AssetUrl });

            var download = await SelfUpdate.DownloadAsync(
                release,
                Asset,
                this.Executable,
                Checksums(Sha256(Release)),
                (url, output, progress, cancellationToken) =>
                {
                    fetched = true;
                    return Task.CompletedTask;
                },
                null,
                CancellationToken.None);

            Assert.Equal(UpdateProblem.NoChecksum, download.Problem);
            Assert.False(fetched, "nothing should be downloaded without a list to check it against");
        }

        [Fact]
        public async Task AListWithoutALineForTheAssetIsNoChecksum()
        {
            string list = Hex('a') + "  WinSW.Gui-win-arm64.exe\n";

            var download = await SelfUpdate.DownloadAsync(
                ReleaseWithList(), Asset, this.Executable, (url, cancellationToken) => Task.FromResult<string?>(list), SendRelease, null, CancellationToken.None);

            Assert.Equal(UpdateProblem.NoChecksum, download.Problem);
            Assert.False(File.Exists(SelfUpdate.NewPath(this.Executable)));
        }

        [Fact]
        public async Task AChecksumListThatCannotBeFetchedIsAFailedDownload()
        {
            var download = await SelfUpdate.DownloadAsync(
                ReleaseWithList(), Asset, this.Executable, (url, cancellationToken) => Task.FromResult<string?>(null), SendRelease, null, CancellationToken.None);

            Assert.Equal(UpdateProblem.DownloadFailed, download.Problem);
            Assert.Equal(SelfUpdate.ChecksumsAsset, download.Detail);
        }

        [Fact]
        public async Task AReleaseWithoutThisBuildsExecutableHasNoAsset()
        {
            var release = new ReleaseInfo("gui-v9.0.0", "https://example.invalid/release", new Dictionary<string, string> { ["WinSW.Gui-win-arm64.exe"] = AssetUrl, [SelfUpdate.ChecksumsAsset] = ChecksumsUrl });

            var download = await SelfUpdate.DownloadAsync(release, Asset, this.Executable, Checksums(Sha256(Release)), SendRelease, null, CancellationToken.None);

            Assert.Equal(UpdateProblem.NoAsset, download.Problem);
            Assert.Equal(Asset, download.Detail);
        }

        [Fact]
        public async Task ABuildNoReleaseCarriesFetchesNothing()
        {
            var download = await SelfUpdate.DownloadAsync(
                ReleaseWithList(), null, this.Executable, Checksums(Sha256(Release)), SendRelease, null, CancellationToken.None);

            Assert.Equal(UpdateProblem.NotThisBuild, download.Problem);
        }

        /// <summary>Said before anything is downloaded: in Program Files, without administrator rights.</summary>
        [Fact]
        public async Task AFolderThatCannotBeWrittenToIsSaidBeforeTheDownload()
        {
            bool fetched = false;
            string executable = Path.Combine(this.folder, "missing", "WinSW.Gui.exe");

            var download = await SelfUpdate.DownloadAsync(
                ReleaseWithList(),
                Asset,
                executable,
                Checksums(Sha256(Release)),
                (url, output, progress, cancellationToken) =>
                {
                    fetched = true;
                    return Task.CompletedTask;
                },
                null,
                CancellationToken.None);

            Assert.Equal(UpdateProblem.FolderNotWritable, download.Problem);
            Assert.False(fetched);
        }

        /// <summary>
        /// A hard link put at the download's name by another account, to a file this one may
        /// write: the link is deleted, not written through, and the download is a file of its own.
        /// </summary>
        [Fact]
        public async Task AHardLinkAtTheDownloadsNameIsNotWrittenThrough()
        {
            string elsewhere = Path.Combine(this.folder, "elsewhere.txt");
            File.WriteAllText(elsewhere, "not the console");
            HardLink(elsewhere, SelfUpdate.NewPath(this.Executable));

            var download = await this.DownloadAsync(Sha256(Release), SendRelease);

            Assert.Equal(UpdateProblem.None, download.Problem);
            Assert.Equal(Release, await File.ReadAllBytesAsync(download.File!));
            Assert.Equal("not the console", File.ReadAllText(elsewhere));
        }

        /// <summary>
        /// Something at the download's name that cannot be deleted is never used in its place:
        /// nothing is fetched, as for a folder that cannot be written to.
        /// </summary>
        [Fact]
        public async Task WhatCannotBeDeletedFromTheDownloadsNameIsNotUsed()
        {
            bool fetched = false;
            Directory.CreateDirectory(SelfUpdate.NewPath(this.Executable));

            var download = await SelfUpdate.DownloadAsync(
                ReleaseWithList(),
                Asset,
                this.Executable,
                Checksums(Sha256(Release)),
                (url, output, progress, cancellationToken) =>
                {
                    fetched = true;
                    return Task.CompletedTask;
                },
                null,
                CancellationToken.None);

            Assert.Equal(UpdateProblem.FolderNotWritable, download.Problem);
            Assert.False(fetched);
            Assert.True(Directory.Exists(SelfUpdate.NewPath(this.Executable)));
        }

        // Putting it in place ----------------------------------------------------------

        [Fact]
        public void ASwapPutsTheNewFileInPlaceAndTheRunningOneAside()
        {
            File.WriteAllText(this.Executable, "old");
            string downloaded = SelfUpdate.NewPath(this.Executable);
            File.WriteAllText(downloaded, "new");

            SelfUpdate.Swap(this.Executable, downloaded);

            Assert.Equal("new", File.ReadAllText(this.Executable));
            Assert.Equal("old", File.ReadAllText(SelfUpdate.OldPath(this.Executable)));
            Assert.False(File.Exists(downloaded));
        }

        /// <summary>The last update's .old, which its start could not delete in time, does not stand in the way.</summary>
        [Fact]
        public void ASwapReplacesALeftoverFromTheLastUpdate()
        {
            File.WriteAllText(this.Executable, "old");
            File.WriteAllText(SelfUpdate.OldPath(this.Executable), "older");
            string downloaded = SelfUpdate.NewPath(this.Executable);
            File.WriteAllText(downloaded, "new");

            SelfUpdate.Swap(this.Executable, downloaded);

            Assert.Equal("new", File.ReadAllText(this.Executable));
            Assert.Equal("old", File.ReadAllText(SelfUpdate.OldPath(this.Executable)));
        }

        /// <summary>A swap that fails halfway puts the running executable back under its own name.</summary>
        [Fact]
        public void ASwapThatFailsLeavesTheExecutableWhereItWas()
        {
            File.WriteAllText(this.Executable, "old");

            Assert.ThrowsAny<IOException>(() => SelfUpdate.Swap(this.Executable, SelfUpdate.NewPath(this.Executable)));

            Assert.Equal("old", File.ReadAllText(this.Executable));
            Assert.False(File.Exists(SelfUpdate.OldPath(this.Executable)));
        }

        [Fact]
        public void UndoingASwapRestoresBothFiles()
        {
            File.WriteAllText(this.Executable, "old");
            string downloaded = SelfUpdate.NewPath(this.Executable);
            File.WriteAllText(downloaded, "new");

            SelfUpdate.Swap(this.Executable, downloaded);
            SelfUpdate.Undo(this.Executable, downloaded);

            Assert.Equal("old", File.ReadAllText(this.Executable));
            Assert.Equal("new", File.ReadAllText(downloaded));
            Assert.False(File.Exists(SelfUpdate.OldPath(this.Executable)));
        }

        [Fact]
        public async Task TheNextStartDeletesWhatTheUpdateLeft()
        {
            File.WriteAllText(this.Executable, "new");
            File.WriteAllText(SelfUpdate.OldPath(this.Executable), "old");
            File.WriteAllText(SelfUpdate.NewPath(this.Executable), "half");

            Assert.True(await SelfUpdate.CleanUpAsync(this.Executable, attempts: 1, delay: TimeSpan.Zero));

            Assert.True(File.Exists(this.Executable));
            Assert.False(File.Exists(SelfUpdate.OldPath(this.Executable)));
            Assert.False(File.Exists(SelfUpdate.NewPath(this.Executable)));
        }

        [Fact]
        public async Task WithNothingLeftTheCleanUpIsDoneAtOnce()
        {
            File.WriteAllText(this.Executable, "new");

            Assert.True(await SelfUpdate.CleanUpAsync(this.Executable, attempts: 1, delay: TimeSpan.FromHours(1)));
            Assert.True(File.Exists(this.Executable));
        }

        private static string Hex(char digit) => new string(digit, 64);

        /// <summary>Makes <paramref name="link"/> a second name for <paramref name="existing"/>; .NET has no call for it.</summary>
        private static void HardLink(string existing, string link)
        {
            bool made = OperatingSystem.IsWindows() ? CreateHardLink(link, existing, IntPtr.Zero) : Link(existing, link) == 0;
            Assert.True(made, "The test's hard link could not be made: error " + Marshal.GetLastPInvokeError());
        }

        [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);

        [DllImport("libc", EntryPoint = "link", SetLastError = true)]
        private static extern int Link([MarshalAs(UnmanagedType.LPUTF8Str)] string existing, [MarshalAs(UnmanagedType.LPUTF8Str)] string link);

        private static string Sha256(byte[] content) => Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

        private static async Task SendRelease(string url, Stream output, IProgress<int>? progress, CancellationToken cancellationToken)
        {
            Assert.Equal(AssetUrl, url);
            await output.WriteAsync(Release, cancellationToken);
            progress?.Report(100);
        }

        /// <summary>A release as gui.yml makes one: this build's executable, another, and the checksum list.</summary>
        private static ReleaseInfo ReleaseWithList() =>
            new("gui-v9.0.0", "https://example.invalid/release", new Dictionary<string, string>
            {
                [Asset] = AssetUrl,
                ["WinSW.Gui-win-arm64.exe"] = "https://example.invalid/WinSW.Gui-win-arm64.exe",
                [SelfUpdate.ChecksumsAsset] = ChecksumsUrl,
            });

        /// <summary>Serves a checksum list that gives this build's executable <paramref name="listed"/>.</summary>
        private static Func<string, CancellationToken, Task<string?>> Checksums(string listed) => (url, cancellationToken) =>
        {
            Assert.Equal(ChecksumsUrl, url);
            return Task.FromResult<string?>(listed + "  " + Asset + "\n" + Hex('b') + "  WinSW.Gui-win-arm64.exe\n");
        };

        /// <summary>Fetches from <see cref="ReleaseWithList"/>, whose list gives the executable <paramref name="listed"/>.</summary>
        private Task<UpdateDownload> DownloadAsync(string listed, Func<string, Stream, IProgress<int>?, CancellationToken, Task> fetchFile) =>
            SelfUpdate.DownloadAsync(ReleaseWithList(), Asset, this.Executable, Checksums(listed), fetchFile, null, CancellationToken.None);
    }
}

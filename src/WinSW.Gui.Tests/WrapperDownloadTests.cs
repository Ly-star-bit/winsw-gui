using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using WinSW.Gui.Services;
using WinSW.Gui.ViewModels;
using Xunit;

namespace WinSW.Gui.Tests
{
    /// <summary>
    /// The wizard's Download button, which is the way forward on a machine whose .NET Framework
    /// is too old for the bundled wrapper. It used to write WinSW.exe next to the program, and
    /// the service's configuration and logs followed it there — into a virtual environment's
    /// Scripts folder, which the next rebuild of the environment deletes; under Program Files it
    /// could not be written at all. It now goes into a per-user cache, and the service gets the
    /// install root's layout, as it does with the bundled wrapper.
    /// </summary>
    /// <remarks>
    /// In the collection of the other tests that point the install root somewhere of their own:
    /// it is one setting for the whole process, and they must not take turns at it mid-test.
    /// Messages are compared by key, which is what the localizer hands back outside the application.
    /// </remarks>
    [Collection("install root")]
    public sealed class WrapperDownloadTests : IDisposable
    {
        /// <summary>4.5.1, which Windows Server 2012 R2 ships with.</summary>
        private static readonly NetFrameworkInfo OldFramework = new(378675);

        private static readonly Dictionary<string, string> AllAssets = new()
        {
            ["WinSW-x64.exe"] = "https://example.invalid/WinSW-x64.exe",
            ["WinSW-x86.exe"] = "https://example.invalid/WinSW-x86.exe",
            ["WinSW-arm64.exe"] = "https://example.invalid/WinSW-arm64.exe",
            ["WinSW-net461.exe"] = "https://example.invalid/WinSW-net461.exe",
        };

        private readonly string directory = Path.Combine(Path.GetTempPath(), "winsw-download-" + Guid.NewGuid().ToString("N"));
        private readonly string root;
        private readonly string programDirectory;
        private readonly string program;

        /// <summary>Stands in for the cache: a self-contained build, tens of megabytes and no .NET Framework assembly.</summary>
        private readonly string downloaded;
        private readonly string? installRoot;

        public WrapperDownloadTests()
        {
            this.root = Path.Combine(this.directory, "root");
            this.programDirectory = Path.Combine(this.directory, "apps", "api", ".venv", "Scripts");
            this.program = Path.Combine(this.programDirectory, "uvicorn.exe");
            Directory.CreateDirectory(this.programDirectory);
            File.WriteAllBytes(this.program, Array.Empty<byte>());

            string cache = Path.Combine(this.directory, "cache");
            Directory.CreateDirectory(cache);
            this.downloaded = Path.Combine(cache, "WinSW-x64.exe");
            File.WriteAllBytes(this.downloaded, new byte[5 * 1024 * 1024]);

            // The install root is read from the settings, which nothing here saves.
            this.installRoot = AppSettings.Current.InstallRoot;
            AppSettings.Current.InstallRoot = this.root;
        }

        public void Dispose()
        {
            AppSettings.Current.InstallRoot = this.installRoot;
            Directory.Delete(this.directory, recursive: true);
        }

        [Theory]
        [InlineData(Architecture.X64, "WinSW-x64.exe")]
        [InlineData(Architecture.Arm64, "WinSW-arm64.exe")]
        [InlineData(Architecture.X86, "WinSW-x86.exe")]
        public void TheSelfContainedBuildForTheMachineIsFetched(Architecture architecture, string expected)
        {
            Assert.Equal(expected, WrapperDownload.AssetFor(architecture, frameworkTooOld: false, AllAssets));
            Assert.Equal(expected, WrapperDownload.AssetFor(architecture, frameworkTooOld: true, AllAssets));
        }

        /// <summary>
        /// A release without a build for the machine offers the .NET Framework one — except where
        /// the framework is too old, which is what the download was meant to get round.
        /// </summary>
        [Fact]
        public void TheFrameworkBuildIsTheFallbackOnlyWhereItCanRun()
        {
            var older = new Dictionary<string, string>
            {
                ["WinSW-x86.exe"] = "https://example.invalid/WinSW-x86.exe",
                ["WinSW-net461.exe"] = "https://example.invalid/WinSW-net461.exe",
            };

            Assert.Equal(WrapperDownload.FrameworkAsset, WrapperDownload.AssetFor(Architecture.Arm64, frameworkTooOld: false, older));
            Assert.Null(WrapperDownload.AssetFor(Architecture.Arm64, frameworkTooOld: true, older));
        }

        /// <summary>The bundled wrapper's cache is WinSW.exe, rewritten when its length differs; downloads keep out of its folder.</summary>
        [Fact]
        public void DownloadsHaveAFolderOfTheirOwn()
        {
            string applicationCache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinSW.Gui");

            Assert.Equal(applicationCache, Path.GetDirectoryName(WrapperDownload.CacheDirectory));
        }

        /// <summary>
        /// The layout the wizard exists to give: a folder of its own under the root and the wrapper
        /// shared from bin — not the program's folder, and not the cache the file was fetched into.
        /// </summary>
        [Fact]
        public void ADownloadedWrapperGetsTheRootsLayout()
        {
            var wizard = this.Wizard();

            wizard.UseDownloadedWrapper(this.downloaded);

            Assert.False(wizard.UseBundledWrapper);
            Assert.Equal(this.downloaded, wizard.WrapperPath);
            Assert.Equal(Path.Combine(this.root, "api"), wizard.InstallDirectory);
            Assert.Equal(Path.Combine(this.root, "api", "api.xml"), wizard.ConfigPath);
            Assert.Equal(Path.Combine(this.root, "bin", "WinSW.exe"), wizard.EffectiveWrapperPath);
            Assert.True(wizard.NextCommand.CanExecute(null));
        }

        /// <summary>Asked for, the program's folder gets the wrapper as WinSW.exe, not under the name it was released as.</summary>
        [Fact]
        public void NextToTheProgramItIsCalledWinSWexe()
        {
            var wizard = this.Wizard();
            wizard.UseDownloadedWrapper(this.downloaded);

            wizard.PlaceNextToProgram = true;

            Assert.Equal(this.programDirectory, wizard.InstallDirectory);
            Assert.Equal(Path.Combine(this.programDirectory, "WinSW.exe"), wizard.EffectiveWrapperPath);
        }

        [Fact]
        public void ABrandedCopyOfADownloadGoesInTheServicesOwnFolder()
        {
            var wizard = this.Wizard();
            wizard.UseDownloadedWrapper(this.downloaded);

            wizard.BrandWrapper = true;

            Assert.Equal(Path.Combine(this.root, "api", "api.exe"), wizard.EffectiveWrapperPath);
        }

        /// <summary>A wrapper the user keeps in a folder of their own still keeps that folder, as it always has.</summary>
        [Fact]
        public void PickingAnotherWrapperAfterwardsKeepsItsFolder()
        {
            string elsewhere = Path.Combine(this.directory, "tools");
            var wizard = this.Wizard();
            wizard.UseDownloadedWrapper(this.downloaded);

            wizard.WrapperPath = Path.Combine(elsewhere, "WinSW.exe");

            Assert.Equal(elsewhere, wizard.InstallDirectory);
            Assert.Equal(Path.Combine(elsewhere, "WinSW.exe"), wizard.EffectiveWrapperPath);
        }

        [Fact]
        public void StartingOverForgetsTheDownload()
        {
            var wizard = this.Wizard();
            wizard.UseDownloadedWrapper(this.downloaded);

            wizard.ResetCommand.Execute(null);
            wizard.TargetPath = this.program;
            wizard.ServiceId = "api";
            wizard.UseBundledWrapper = false;
            wizard.WrapperPath = this.downloaded;

            Assert.Equal(Path.GetDirectoryName(this.downloaded), wizard.InstallDirectory);
        }

        /// <summary>
        /// The shared wrapper picked for a second service — what a machine that cannot run the
        /// bundled one comes to after its first — is the root's layout too, not a folder of its
        /// own: the service's files used to land in bin.
        /// </summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void TheSharedWrapperPickedForAnotherServiceIsShared(bool spelledAnotherWay)
        {
            string shared = Path.Combine(this.root, "bin", "WinSW.exe");
            var wizard = this.Wizard();
            wizard.UseBundledWrapper = false;

            wizard.WrapperPath = spelledAnotherWay ? Path.Combine(this.root, "bin", "..", "BIN", "WinSW.exe") : shared;

            Assert.Equal(Path.Combine(this.root, "api"), wizard.InstallDirectory);
            Assert.Equal(Path.Combine(this.root, "api", "api.xml"), wizard.ConfigPath);
            Assert.Equal(shared, wizard.EffectiveWrapperPath);
        }

        /// <summary>
        /// The install keeps a shared wrapper already in place. Where that is the .NET Framework
        /// build, the downloaded one is not what the service runs, and downloading again would
        /// not help; the review step says so rather than send the user back to download.
        /// </summary>
        [Fact]
        public async Task AFrameworkBuildAlreadySharedIsReportedAsSuch()
        {
            // This application's own assembly carries the wrapper's product name and is a few
            // hundred kilobytes: to the checks, the .NET Framework build.
            string shared = Path.Combine(this.root, "bin", "WinSW.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(shared)!);
            File.Copy(typeof(WizardViewModel).Assembly.Location, shared);
            if (!ServiceDiscovery.IsWrapperExecutable(shared) || WrapperKind.ReleaseAssetFor(shared) != WrapperDownload.FrameworkAsset)
            {
                return;
            }

            var wizard = this.Wizard();
            wizard.UseDownloadedWrapper(this.downloaded);

            wizard.Step = WizardViewModel.LastStep;
            await wizard.MachineCheck;

            Assert.Contains("M.Wiz.NetFxWrapperInPlace", wizard.EnvironmentWarnings);
            Assert.DoesNotContain("M.Wiz.NetFxWrapper", wizard.EnvironmentWarnings);
        }

        [Fact]
        public async Task ASelfContainedDownloadOnAnOldFrameworkIsNotReported()
        {
            var wizard = this.Wizard();
            wizard.UseDownloadedWrapper(this.downloaded);

            wizard.Step = WizardViewModel.LastStep;
            await wizard.MachineCheck;

            Assert.DoesNotContain("M.Wiz.NetFxWrapper", wizard.EnvironmentWarnings);
            Assert.DoesNotContain("M.Wiz.NetFxWrapperInPlace", wizard.EnvironmentWarnings);
        }

        [Theory]
        [InlineData("", "", false)]
        [InlineData("   ", "   ", false)]
        [InlineData("a/WinSW.exe", "A/winsw.EXE", true)]
        [InlineData("a/b/../WinSW.exe", "a/WinSW.exe", true)]
        [InlineData("a/WinSW.exe", "b/WinSW.exe", false)]
        [InlineData("a\0/WinSW.exe", "a\0/WinSW.exe", false)]
        public void PathsAreComparedTheWayWindowsDoes(string path, string other, bool same)
        {
            Assert.Equal(same, WizardViewModel.SamePath(path, other));
        }

        private WizardViewModel Wizard() =>
            new(OldFramework, () => ServiceNames.None) { TargetPath = this.program, ServiceId = "api", DisplayName = "API" };
    }
}

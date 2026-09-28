using System;
using System.IO;
using System.Linq;
using WinSW.Gui.Model;
using WinSW.Gui.Services;
using WinSW.Gui.ViewModels;
using Xunit;

namespace WinSW.Gui.Tests
{
    /// <summary>
    /// Starting a new service from an installed one. The copy used to land next to the
    /// source's wrapper — in the shared <c>bin</c>, or in a branded source's own folder — and
    /// kept about fourteen fields: the environment, the account, the stop settings, the hooks,
    /// the dependencies, all but the first recovery action and the roll pattern were lost.
    /// </summary>
    /// <remarks>
    /// Messages are compared by key: outside the application no dictionary is loaded, and
    /// <see cref="Localization.Localizer"/> hands back the key itself.
    /// </remarks>
    [Collection("install root")]
    public sealed class WizardCloneTests : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "winsw-gui-" + Guid.NewGuid().ToString("n"));
        private readonly string root;
        private readonly string sourceDirectory;
        private readonly string? installRoot;

        public WizardCloneTests()
        {
            this.root = Path.Combine(this.directory, "root");
            this.sourceDirectory = Path.Combine(this.root, "api");
            Directory.CreateDirectory(this.sourceDirectory);

            // The install root is read from the settings, which nothing here saves.
            this.installRoot = AppSettings.Current.InstallRoot;
            AppSettings.Current.InstallRoot = this.root;
        }

        public void Dispose()
        {
            AppSettings.Current.InstallRoot = this.installRoot;
            Directory.Delete(this.directory, recursive: true);
        }

        /// <summary>Everything the wizard has no field for comes along, and so does everything it does.</summary>
        [Fact]
        public void ACopyKeepsTheWholeConfiguration()
        {
            var wizard = this.Clone(
                "<name>API</name><description>The public API</description>"
                + @"<executable>C:\apps\api\server.exe</executable><arguments>--workers 4</arguments>"
                + @"<workingdirectory>C:\apps\api</workingdirectory>"
                + "<priority>High</priority><stoptimeout>30 sec</stoptimeout>"
                + @"<stopexecutable>C:\apps\api\stop.exe</stopexecutable>"
                + "<startmode>Manual</startmode>"
                + "<depend>Tcpip</depend><depend>postgresql</depend>"
                + "<serviceaccount><username>CORP\\svc-api</username><password>secret</password><allowservicelogon>true</allowservicelogon></serviceaccount>"
                + @"<env name=""DATABASE_URL"" value=""postgres://db/api"" />"
                + @"<prestart><executable>C:\apps\api\migrate.exe</executable></prestart>"
                + @"<onfailure action=""restart"" delay=""10 sec"" /><onfailure action=""restart"" delay=""1 min"" /><onfailure action=""reboot"" delay=""5 min"" />"
                + "<resetfailure>2 hours</resetfailure>"
                + @"<log mode=""roll-by-time""><pattern>yyyy-MM-dd</pattern><period>2</period><keepFiles>14</keepFiles></log>"
                + "<outfilepattern>.stdout.log</outfilepattern>");

            var model = Written(wizard);

            Assert.Equal("api-2", model.Id);
            Assert.Equal("API (2)", model.DisplayName);
            Assert.Equal("The public API", model.Description);
            Assert.Equal(@"C:\apps\api\server.exe", model.Executable);
            Assert.Equal("--workers 4", model.Arguments);
            Assert.Equal(@"C:\apps\api", model.WorkingDirectory);
            Assert.Equal("High", model.Priority);
            Assert.Equal("30 sec", model.StopTimeout);
            Assert.Equal(@"C:\apps\api\stop.exe", model.StopExecutable);
            Assert.Equal("Manual", model.StartMode);
            Assert.Equal(new[] { "Tcpip", "postgresql" }, model.Dependencies.Select(d => d.ServiceName));
            Assert.Equal(@"CORP\svc-api", model.ServiceAccountUser);
            Assert.Equal("secret", model.ServiceAccountPassword);
            Assert.True(model.AllowServiceLogon);
            Assert.Equal("postgres://db/api", model.EnvironmentVariables.Single(v => v.Name == "DATABASE_URL").Value);
            Assert.Equal(@"C:\apps\api\migrate.exe", model.Prestart.Executable);
            Assert.Equal(new[] { "restart", "restart", "reboot" }, model.FailureActions.Select(a => a.Action));
            Assert.Equal(new[] { "10 sec", "1 min", "5 min" }, model.FailureActions.Select(a => a.Delay));
            Assert.Equal("2 hours", model.ResetFailureAfter);
            Assert.Equal("roll-by-time", model.LogMode);
            Assert.Equal("yyyy-MM-dd", model.RollPattern);
            Assert.Equal("2", model.RollPeriod);
            Assert.Equal("14", model.KeepFiles);
            Assert.Equal(".stdout.log", model.OutFilePattern);
        }

        /// <summary>
        /// The copy's file is written the way the wizard writes it and reads back whole — as
        /// UTF-8, whatever the source's own file was encoded in.
        /// </summary>
        [Fact]
        public void TheCopysFileReadsBack()
        {
            string config = Path.Combine(this.sourceDirectory, "api.xml");
            File.WriteAllBytes(
                config,
                System.Text.Encoding.Unicode.GetPreamble().Concat(System.Text.Encoding.Unicode.GetBytes(
                    "<?xml version=\"1.0\" encoding=\"utf-16\"?><service><id>api</id><description>接口服务</description>"
                    + "<executable>server.exe</executable><env name=\"GREETING\" value=\"你好\" /><depend>Tcpip</depend></service>")).ToArray());
            var wizard = new WizardViewModel { CloneSource = new ServiceEntry("api", "API", Path.Combine(this.root, "bin", "WinSW.exe"), config) };

            string written = Path.Combine(this.directory, "api-2.xml");
            wizard.BuildModel().Save(written);

            Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>", File.ReadAllText(written));
            var model = ServiceConfigModel.Load(written);
            Assert.Equal("api-2", model.Id);
            Assert.Equal("接口服务", model.Description);
            Assert.Equal("你好", model.EnvironmentVariables.Single().Value);
            Assert.Single(model.Dependencies);
        }

        /// <summary>
        /// The copy gets the layout any new service gets — its own folder under the root —
        /// and not the source's wrapper, which is the shared one in <c>bin</c> or a branded
        /// one in the source's folder.
        /// </summary>
        [Fact]
        public void ACopyGetsAFolderOfItsOwnUnderTheInstallRoot()
        {
            string bin = Path.Combine(this.root, "bin");
            Directory.CreateDirectory(bin);
            var wizard = this.Clone(@"<executable>C:\apps\api\server.exe</executable>", Path.Combine(bin, "WinSW.exe"));

            Assert.Equal(Path.Combine(this.root, "api-2"), wizard.InstallDirectory);
            Assert.Equal(Path.Combine(this.root, "api-2", "api-2.xml"), wizard.ConfigPath);
            Assert.Equal(BundledWrapper.IsAvailable, wizard.UseBundledWrapper);
            Assert.Equal(string.Empty, wizard.WrapperPath);
            Assert.False(wizard.PlaceNextToProgram);
        }

        [Fact]
        public void ABrandedSourcesCopyIsNotPutInTheSourcesFolder()
        {
            var wizard = this.Clone(@"<executable>C:\apps\api\server.exe</executable>", Path.Combine(this.sourceDirectory, "api.exe"));

            Assert.Equal(Path.Combine(this.root, "api-2", "api-2.xml"), wizard.ConfigPath);
            Assert.False(wizard.BrandWrapper);
        }

        /// <summary>
        /// %BASE% found the source's program in its own folder; the copy is pointed there, while
        /// its logs, which %BASE% also names, go to the copy's own folder.
        /// </summary>
        [Fact]
        public void BaseInTheProgramNamesTheSourcesFolderAndTheLogsStayTheCopys()
        {
            var wizard = this.Clone(
                @"<executable>%BASE%\server.exe</executable><arguments>--config %BASE%\app.ini</arguments>"
                + @"<workingdirectory>%BASE%</workingdirectory><logpath>%BASE%\logs</logpath>");

            Assert.Equal(this.sourceDirectory + @"\server.exe", wizard.TargetPath);
            Assert.Equal(@"--config " + this.sourceDirectory + @"\app.ini", wizard.Arguments);
            Assert.Equal(this.sourceDirectory, wizard.WorkingDirectory);
            Assert.Equal(@"%BASE%\logs", wizard.LogPath);

            var model = Written(wizard);
            Assert.Equal(this.sourceDirectory + @"\server.exe", model.Executable);
            Assert.Equal(@"%BASE%\logs", model.LogPath);
        }

        /// <summary>A source without a working directory ran in its own folder, and so does its copy.</summary>
        [Fact]
        public void ACopyOfASourceWithNoWorkingDirectoryRunsInTheSourcesFolder()
        {
            var wizard = this.Clone("<executable>server.exe</executable><arguments>main.py</arguments>");

            Assert.Equal(this.sourceDirectory, wizard.WorkingDirectory);
            Assert.Equal(this.sourceDirectory, wizard.BuildModel().WorkingDirectory);
        }

        /// <summary>The preview and the install each build the model; neither doubles a row.</summary>
        [Fact]
        public void BuildingTwiceDoublesNothing()
        {
            var wizard = this.Clone(
                @"<executable>server.exe</executable><env name=""A"" value=""1"" /><env name=""B"" value=""2"" />"
                + @"<onfailure action=""restart"" delay=""10 sec"" /><onfailure action=""reboot"" delay=""1 min"" /><depend>Tcpip</depend>");

            var first = wizard.BuildModel();
            var second = wizard.BuildModel();

            Assert.Equal(2, first.EnvironmentVariables.Count);
            Assert.Equal(2, second.EnvironmentVariables.Count);
            Assert.Equal(2, second.FailureActions.Count);
            Assert.Single(second.Dependencies);
            Assert.Equal(2, Written(wizard).EnvironmentVariables.Count);
        }

        [Fact]
        public void TheCopysVariablesAreTheWizardsToEdit()
        {
            var wizard = this.Clone(@"<executable>server.exe</executable><env name=""A"" value=""1"" /><env name=""B"" value=""2"" />");

            Assert.Equal(new[] { "A", "B" }, wizard.EnvironmentVariables.Select(v => v.Name));
            wizard.EnvironmentVariables.RemoveAt(0);
            wizard.EnvironmentVariables[0].Value = "3";

            var model = Written(wizard);
            Assert.Equal("B", model.EnvironmentVariables.Single().Name);
            Assert.Equal("3", model.EnvironmentVariables.Single().Value);
        }

        [Fact]
        public void TheSourcesRecoveryIsShownByItsFirstDelay()
        {
            var wizard = this.Clone(@"<executable>server.exe</executable><onfailure action=""restart"" delay=""30 sec"" /><onfailure action=""reboot"" delay=""5 min"" />");

            Assert.True(wizard.RestartOnFailure);
            Assert.Equal("30 sec", wizard.RestartDelay);
            Assert.Equal("M.Wiz.RecoveryHintCopied", wizard.RecoveryHint);
        }

        /// <summary>A different delay asks for the wizard's own ladder in place of the source's actions.</summary>
        [Fact]
        public void ChangingTheDelayReplacesTheSourcesRecovery()
        {
            var wizard = this.Clone(
                @"<executable>server.exe</executable><onfailure action=""restart"" delay=""10 sec"" /><onfailure action=""reboot"" delay=""5 min"" />"
                + "<resetfailure>1 day</resetfailure>");

            wizard.RestartDelay = "30 sec";

            var model = Written(wizard);
            Assert.Equal(new[] { "restart", "restart", "restart" }, model.FailureActions.Select(a => a.Action));
            Assert.Equal(WizardViewModel.RestartDelays("30 sec"), model.FailureActions.Select(a => a.Delay));
            Assert.Equal(WizardViewModel.FailureResetPeriod, model.ResetFailureAfter);
            Assert.Equal("M.Wiz.RecoveryHint", wizard.RecoveryHint);

            // Putting the delay back puts the source's actions back.
            wizard.RestartDelay = " 10 sec ";
            Assert.Equal(new[] { "restart", "reboot" }, Written(wizard).FailureActions.Select(a => a.Action));
            Assert.Equal("M.Wiz.RecoveryHintCopied", wizard.RecoveryHint);
        }

        /// <summary>
        /// The delay editor writes its own spelling back once its unit is picked; the same
        /// wait spelled another way is no change, and the source's actions stay.
        /// </summary>
        [Theory]
        [InlineData("30 secs", "30 sec")]
        [InlineData("30000", "30 sec")]
        [InlineData("1 mins", "60 sec")]
        public void TheSameDelaySpelledAnotherWayKeepsTheSourcesRecovery(string written, string edited)
        {
            var wizard = this.Clone($@"<executable>server.exe</executable><onfailure action=""restart"" delay=""{written}"" /><onfailure action=""reboot"" delay=""5 min"" />");

            wizard.RestartDelay = edited;

            Assert.Equal(new[] { "restart", "reboot" }, Written(wizard).FailureActions.Select(a => a.Action));
            Assert.Equal(written, Written(wizard).FailureActions[0].Delay);
            Assert.Equal("M.Wiz.RecoveryHintCopied", wizard.RecoveryHint);
        }

        [Fact]
        public void UntickingRecoveryWritesNone()
        {
            var wizard = this.Clone(@"<executable>server.exe</executable><onfailure action=""restart"" delay=""10 sec"" /><resetfailure>1 day</resetfailure>");

            wizard.RestartOnFailure = false;

            var model = Written(wizard);
            Assert.Empty(model.FailureActions);
            Assert.Null(model.ResetFailureAfter);
        }

        /// <summary>A source with no recovery is copied without; ticking it gives the wizard's own.</summary>
        [Fact]
        public void ASourceWithoutRecoveryGetsTheWizardsWhenAskedFor()
        {
            var wizard = this.Clone(@"<executable>server.exe</executable><onfailure action=""none"" />");

            Assert.False(wizard.RestartOnFailure);
            Assert.Empty(Written(wizard).FailureActions);

            wizard.RestartOnFailure = true;
            Assert.Equal(WizardViewModel.RestartDelays("10 sec"), Written(wizard).FailureActions.Select(a => a.Delay));
        }

        /// <summary>
        /// A none ahead of a restart waits for nothing: the copy is shown by its restart's delay,
        /// and written with both rows while that is left alone.
        /// </summary>
        [Fact]
        public void TheSourcesRecoveryIsShownByItsFirstRestart()
        {
            var wizard = this.Clone(@"<executable>server.exe</executable><onfailure action=""none"" /><onfailure action=""restart"" delay=""30 sec"" />");

            Assert.True(wizard.RestartOnFailure);
            Assert.Equal("30 sec", wizard.RestartDelay);
            Assert.Equal(new[] { "none", "restart" }, Written(wizard).FailureActions.Select(a => a.Action));
        }

        [Fact]
        public void ACopyRegisteredAsADesktopTaskLeavesTheRecoveryBehind()
        {
            var wizard = this.Clone(@"<executable>server.exe</executable><onfailure action=""restart"" delay=""10 sec"" /><resetfailure>1 day</resetfailure>");

            wizard.DesktopTask = true;

            var model = wizard.BuildModel();
            Assert.Empty(model.FailureActions);
            Assert.Null(model.ResetFailureAfter);
            Assert.True(model.HideWindow);
        }

        [Theory]
        [InlineData("restart", "10 sec", "10 sec")]
        [InlineData("restart", null, "0 sec")]
        [InlineData("reboot", "5 min", "reboot 5 min")]
        [InlineData("none", null, "none")]
        public void EachCopiedActionIsListedTheWayTheLadderIs(string action, string? delay, string expected)
        {
            Assert.Equal(expected, WizardViewModel.DescribeFailureAction((action, delay)));
        }

        /// <summary>
        /// A copy keeps the pattern its source rolled on — which need not be daily — so the
        /// count is of files; switching to another mode and back keeps it.
        /// </summary>
        [Fact]
        public void ATimeRolledCopyKeepsItsPatternAndCountsFiles()
        {
            var wizard = this.Clone(@"<executable>server.exe</executable><log mode=""roll-by-time""><pattern>yyyyMMddHH</pattern></log>");

            Assert.True(wizard.KeepsSourceRollPattern);
            Assert.Equal(string.Empty, wizard.KeepDays);
            Assert.Null(wizard.BuildModel().KeepFiles);

            wizard.LogMode = "roll-by-size";
            Assert.False(wizard.KeepsSourceRollPattern);
            Assert.Null(Written(wizard).RollPattern);

            wizard.LogMode = "roll-by-time";
            Assert.Equal("yyyyMMddHH", Written(wizard).RollPattern);
        }

        [Fact]
        public void ASizeRolledSourceSwitchedToTimeRollsDaily()
        {
            var wizard = this.Clone(@"<executable>server.exe</executable><log mode=""roll-by-size""><sizeThreshold>2048</sizeThreshold><keepFiles>3</keepFiles></log>");

            Assert.Equal("2048", wizard.SizeThresholdKb);
            Assert.Equal("3", wizard.KeepFiles);

            wizard.LogMode = "roll-by-time";
            Assert.False(wizard.KeepsSourceRollPattern);
            Assert.Equal(WizardViewModel.DailyRollPattern, Written(wizard).RollPattern);
        }

        /// <summary>A mode the wizard has no fields for is offered for the copy, and written with its settings.</summary>
        [Fact]
        public void AModeTheWizardDoesNotOfferIsKeptWithItsSettings()
        {
            var wizard = this.Clone(
                @"<executable>server.exe</executable><log mode=""roll-by-size-time"">"
                + "<sizeThreshold>1024</sizeThreshold><pattern>yyyyMMdd</pattern><autoRollAtTime>00:00:00</autoRollAtTime><zipOlderThanNumDays>5</zipOlderThanNumDays>"
                + "</log>");

            Assert.Contains("roll-by-size-time", wizard.LogModes);
            Assert.Equal("roll-by-size-time", wizard.LogMode);

            var model = Written(wizard);
            Assert.Equal("roll-by-size-time", model.LogMode);
            Assert.Equal("1024", model.SizeThreshold);
            Assert.Equal("yyyyMMdd", model.RollPattern);
            Assert.Equal("00:00:00", model.AutoRollAtTime);
            Assert.Equal("5", model.ZipOlderThanNumDays);

            wizard.ResetCommand.Execute(null);
            Assert.DoesNotContain("roll-by-size-time", wizard.LogModes);
            Assert.Equal("roll-by-size", wizard.LogMode);
        }

        [Fact]
        public void TheIdSkipsOnesAlreadyInUse()
        {
            var wizard = new WizardViewModel
            {
                Sources = new[]
                {
                    new ServiceEntry("api-2", "API (2)", Path.Combine(this.root, "bin", "WinSW.exe"), null),
                    new ServiceEntry("api-3", "API (3)", Path.Combine(this.root, "bin", "WinSW.exe"), null),
                },
            };

            wizard.CloneSource = this.Source("<name>API</name><executable>server.exe</executable>");

            Assert.Equal("api-4", wizard.ServiceId);
            Assert.Equal("API (4)", wizard.DisplayName);
            Assert.False(wizard.IdInUse);
        }

        /// <summary>
        /// The review step lists what the copy still shares with its source: the port it
        /// listens on, the password, and — for a source that ran in its own folder — the files.
        /// </summary>
        [Fact]
        public void TheReviewStepListsWhatProbablyNeedsChanging()
        {
            var wizard = this.Clone(
                @"<executable>%BASE%\server.exe</executable><arguments>main:app --port 8000</arguments>"
                + "<serviceaccount><username>CORP\\svc-api</username><password>secret</password></serviceaccount>");

            wizard.Step = WizardViewModel.LastStep;

            Assert.Contains("M.Wiz.ClonePort", wizard.Warnings);
            Assert.Contains("M.Wiz.ClonePassword", wizard.Warnings);
            Assert.Contains("M.Wiz.CloneRebased", wizard.Warnings);
            Assert.DoesNotContain("M.Wiz.CloneLogFiles", wizard.Warnings);
            Assert.Empty(wizard.Problems);
        }

        [Fact]
        public void AChangedPortIsNoLongerListed()
        {
            var wizard = this.Clone(@"<executable>C:\apps\api\server.exe</executable><workingdirectory>C:\apps\api</workingdirectory><arguments>main:app --port 8000</arguments>");

            wizard.Arguments = "main:app --port 8001";
            wizard.Step = WizardViewModel.LastStep;

            Assert.Empty(wizard.Warnings.Where(w => w.StartsWith("M.Wiz.Clone", StringComparison.Ordinal)));
        }

        [Fact]
        public void ACopyThatWouldWriteTheSourcesLogFilesIsTold()
        {
            string logs = Path.Combine(this.directory, "logs");
            var wizard = this.Clone($@"<executable>C:\apps\api\server.exe</executable><workingdirectory>C:\apps\api</workingdirectory><logpath>{logs}</logpath><logname>api</logname>");

            wizard.Step = WizardViewModel.LastStep;
            Assert.Contains("M.Wiz.CloneLogFiles", wizard.Warnings);

            wizard.Step = 3;
            wizard.LogPath = @"%BASE%\logs";
            wizard.Step = WizardViewModel.LastStep;
            Assert.DoesNotContain("M.Wiz.CloneLogFiles", wizard.Warnings);
        }

        /// <summary>A second source replaces the first; nothing of the first is left to be written.</summary>
        [Fact]
        public void ASecondSourceReplacesTheFirst()
        {
            var wizard = this.Clone(
                @"<executable>server.exe</executable><env name=""FIRST"" value=""1"" />"
                + "<serviceaccount><username>CORP\\first</username><password>secret</password></serviceaccount>");

            string other = Path.Combine(this.root, "worker");
            Directory.CreateDirectory(other);
            string config = Path.Combine(other, "worker.xml");
            File.WriteAllText(config, @"<service><id>worker</id><executable>worker.exe</executable><env name=""SECOND"" value=""2"" /></service>");
            wizard.CloneSource = new ServiceEntry("worker", "worker", Path.Combine(this.root, "bin", "WinSW.exe"), config);

            Assert.Equal(new[] { "SECOND" }, wizard.EnvironmentVariables.Select(v => v.Name));
            var model = Written(wizard);
            Assert.Equal("worker-2", model.Id);
            Assert.Null(model.ServiceAccountUser);
            Assert.Equal(new[] { "SECOND" }, model.EnvironmentVariables.Select(v => v.Name));
        }

        [Fact]
        public void StartingOverForgetsTheSource()
        {
            var wizard = this.Clone(
                "<executable>server.exe</executable><depend>Tcpip</depend><stoptimeout>30 sec</stoptimeout>"
                + "<serviceaccount><username>CORP\\svc-api</username><password>secret</password></serviceaccount>");

            wizard.ResetCommand.Execute(null);
            wizard.TargetPath = @"C:\apps\other\other.exe";

            var model = wizard.BuildModel();
            Assert.Null(model.ServiceAccountUser);
            Assert.Empty(model.Dependencies);
            Assert.Null(model.StopTimeout);
            Assert.Equal(WizardViewModel.RestartDelays("10 sec"), model.FailureActions.Select(a => a.Delay));
            Assert.Equal("M.Wiz.RecoveryHint", wizard.RecoveryHint);
        }

        /// <summary>
        /// The picker writes null back when the service it shows leaves its list; what was
        /// filled in from it is still what gets written.
        /// </summary>
        [Fact]
        public void ThePickerLettingGoKeepsTheCopy()
        {
            var wizard = this.Clone("<executable>server.exe</executable><depend>Tcpip</depend>");

            wizard.CloneSource = null;

            Assert.Single(wizard.BuildModel().Dependencies);
        }

        [Fact]
        public void AnUnreadableSourceChangesNothing()
        {
            string config = Path.Combine(this.sourceDirectory, "broken.xml");
            File.WriteAllText(config, "<service><id>broken");
            var wizard = new WizardViewModel { TargetPath = @"C:\apps\mine.exe", ServiceId = "mine" };

            wizard.CloneSource = new ServiceEntry("broken", "broken", Path.Combine(this.root, "bin", "WinSW.exe"), config);

            Assert.Equal("mine", wizard.ServiceId);
            Assert.Equal(@"C:\apps\mine.exe", wizard.TargetPath);
            Assert.Equal("M.Wiz.CloneFailed", wizard.StatusMessage);
        }

        /// <summary>The model as the file will have it: written, and read back.</summary>
        private static ServiceConfigModel Written(WizardViewModel wizard) =>
            ServiceConfigModel.FromXml(wizard.BuildModel().ToXmlString(), null);

        private WizardViewModel Clone(string body, string? wrapper = null) =>
            new() { CloneSource = this.Source(body, wrapper) };

        private ServiceEntry Source(string body, string? wrapper = null)
        {
            string config = Path.Combine(this.sourceDirectory, "api.xml");
            File.WriteAllText(config, "<service><id>api</id>" + body + "</service>");
            return new ServiceEntry("api", "API", wrapper ?? Path.Combine(this.root, "bin", "WinSW.exe"), config);
        }
    }
}

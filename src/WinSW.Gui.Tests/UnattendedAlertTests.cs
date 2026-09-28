using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using WinSW.Gui.Services;
using Xunit;

namespace WinSW.Gui.Tests
{
    /// <summary>
    /// The unattended alert's task definition, the command lines that start its headless runs,
    /// and what it keeps on disk. Reading the event back, the elevation and the folder
    /// permissions need Windows and are not tested here.
    /// </summary>
    public class UnattendedAlertTests
    {
        private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

        private const string Runner = @"C:\ProgramData\WinSW.Gui\Alert\WinSW.Gui.exe";

        private const string Extraction = @"C:\ProgramData\WinSW.Gui\Alert\runtime";

        private static XElement Task() => XDocument.Parse(UnattendedAlert.BuildXml(Runner, Extraction)).Root!;

        // Headless command lines ---------------------------------------------------------

        [Fact]
        public void TheTasksRunIsHeadlessWithItsRecordNumber()
        {
            Assert.Equal(new HeadlessCommand(HeadlessAction.Alert, "4711"), UnattendedAlert.ParseHeadless(new[] { "--alert", "4711" }));
        }

        [Fact]
        public void TheElevatedStepsAreHeadless()
        {
            Assert.Equal(new HeadlessCommand(HeadlessAction.SetUp, "AQID"), UnattendedAlert.ParseHeadless(new[] { "--alert-setup", "AQID" }));
            Assert.Equal(new HeadlessCommand(HeadlessAction.Remove, null), UnattendedAlert.ParseHeadless(new[] { "--alert-remove" }));
            Assert.Equal(new HeadlessCommand(HeadlessAction.Alert, null), UnattendedAlert.ParseHeadless(new[] { "--ALERT" }));
        }

        /// <summary>A console launch never turns into a headless run, whatever else it carries.</summary>
        [Theory]
        [InlineData()]
        [InlineData(@"C:\svc\myapp.xml")]
        [InlineData("--tray")]
        [InlineData("--tray", "--alert", "1")]
        [InlineData("--replace", @"C:\svc\--alert.xml")]
        public void AConsoleLaunchIsNotHeadless(params string[] args)
        {
            Assert.Null(UnattendedAlert.ParseHeadless(args));
        }

        // The task -------------------------------------------------------------------------

        [Fact]
        public void TheTaskRunsAsSystemAndAnySignedInAccountMayReadIt()
        {
            var task = Task();

            Assert.Equal("S-1-5-18", task.Descendants(Ns + "UserId").Single().Value);
            Assert.Equal("HighestAvailable", task.Descendants(Ns + "RunLevel").Single().Value);
            Assert.Equal(ScheduledRestart.SecurityDescriptor, task.Descendants(Ns + "SecurityDescriptor").Single().Value);
        }

        [Fact]
        public void ItStartsOnTheServiceControlManagersSixFailuresFromAnyService()
        {
            var subscription = XDocument.Parse(Task().Descendants(Ns + "Subscription").Single().Value);
            var select = subscription.Descendants("Select").Single();
            Assert.Equal("System", select.Attribute("Path")!.Value);

            string xpath = select.Value;
            Assert.Contains("Provider[@Name='Service Control Manager']", xpath, StringComparison.Ordinal);
            foreach (int id in new[] { 7000, 7009, 7023, 7024, 7031, 7034 })
            {
                Assert.Contains("EventID=" + id, xpath, StringComparison.Ordinal);
            }

            // No service is named: the query would have to change with every install.
            Assert.DoesNotContain("EventData", xpath, StringComparison.Ordinal);
            Assert.Single(Regex.Matches(xpath, "@Name="));
        }

        /// <summary>The task scheduler's schema is a sequence: the trigger's children come in its order.</summary>
        [Fact]
        public void TheTriggerIsWrittenInTheSchemasOrder()
        {
            var trigger = Task().Descendants(Ns + "EventTrigger").Single();

            Assert.Equal(new[] { "Enabled", "Subscription", "ValueQueries" }, trigger.Elements().Select(e => e.Name.LocalName));
            var value = trigger.Descendants(Ns + "Value").Single();
            Assert.Equal("EventRecordID", value.Attribute("name")!.Value);
            Assert.Equal("Event/System/EventRecordID", value.Value);
        }

        /// <summary>
        /// The action goes through cmd, to point the .NET host at the private folder, and cmd
        /// reads what is substituted into it as commands: only the record number is.
        /// </summary>
        [Fact]
        public void OnlyTheRecordNumberIsPutOnTheCommandLine()
        {
            var exec = Task().Descendants(Ns + "Exec").Single();
            string arguments = exec.Element(Ns + "Arguments")!.Value;

            Assert.Equal(@"%SystemRoot%\System32\cmd.exe", exec.Element(Ns + "Command")!.Value);
            Assert.Equal(new[] { "EventRecordID" }, Regex.Matches(arguments, @"\$\(([^)]*)\)").Select(m => m.Groups[1].Value));
            Assert.Equal(
                "/d /c \"set \"DOTNET_BUNDLE_EXTRACT_BASE_DIR=" + Extraction + "\" && start \"\" /wait \"" + Runner + "\" --alert $(EventRecordID)\"",
                arguments);
        }

        [Fact]
        public void FailuresQueueRatherThanBeingDroppedOrRunTogether()
        {
            var settings = Task().Element(Ns + "Settings")!;

            Assert.Equal("Queue", settings.Element(Ns + "MultipleInstancesPolicy")!.Value);
            Assert.Equal("false", settings.Element(Ns + "DisallowStartIfOnBatteries")!.Value);
            Assert.NotNull(settings.Element(Ns + "ExecutionTimeLimit"));
        }

        [Fact]
        public void ItsOwnDefinitionIsRecognisedAndADesktopTaskIsNot()
        {
            Assert.True(UnattendedAlert.IsOurs(UnattendedAlert.BuildXml(Runner, Extraction)));

            string desktopTask =
                "<Task xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\"><Actions><Exec>"
                + "<Command>C:\\tools\\WinSW.exe</Command><Arguments>console \"C:\\tools\\Alert.xml\"</Arguments>"
                + "</Exec></Actions></Task>";
            Assert.False(UnattendedAlert.IsOurs(desktopTask));
            Assert.False(UnattendedAlert.IsOurs("not xml"));
        }

        // Which service, and which stops --------------------------------------------------------

        [Fact]
        public void TheLoggedDisplayNameFindsTheWinSwService()
        {
            var services = new[] { ("myapp", "My App"), ("jenkins", "Jenkins Agent") };

            Assert.Equal("myapp", UnattendedAlert.FindService(services, "My App"));
            Assert.Equal("jenkins", UnattendedAlert.FindService(services, "jenkins agent"));
        }

        [Fact]
        public void AServiceLoggedUnderItsOwnNameIsFoundToo()
        {
            Assert.Equal("myapp", UnattendedAlert.FindService(new[] { ("myapp", "My App") }, "MYAPP"));
        }

        /// <summary>Display names are unique among services, and a display name is what the log holds.</summary>
        [Fact]
        public void ADisplayNameIsMatchedBeforeAServiceName()
        {
            var services = new[] { ("web", "api"), ("api", "Public API") };

            Assert.Equal("web", UnattendedAlert.FindService(services, "api"));
        }

        [Fact]
        public void AnyOtherServiceIsNotFound()
        {
            Assert.Null(UnattendedAlert.FindService(new[] { ("myapp", "My App") }, "Windows Update"));
            Assert.Null(UnattendedAlert.FindService(Array.Empty<(string, string)>(), "My App"));
        }

        [Theory]
        [InlineData(1067, true)]
        [InlineData(1, true)]
        [InlineData(-1, true)]
        [InlineData(0, false)]
        [InlineData(null, false)]
        public void OnlyAStopWithAnExitCodeIsLeftToTheTask(int? lastExitCode, bool covered)
        {
            Assert.Equal(covered, UnattendedAlert.CoversStop(lastExitCode));
        }

        // What it keeps ------------------------------------------------------------------------

        [Fact]
        public void TheFingerprintFollowsTheAddressAndSecretAndHidesThem()
        {
            string url = "https://qyapi.weixin.qq.com/cgi-bin/webhook/send?key=0123456789";
            string fingerprint = UnattendedAlert.Fingerprint(url, "s");

            Assert.Matches("^[0-9a-f]{16}$", fingerprint);
            Assert.Equal(fingerprint, UnattendedAlert.Fingerprint(" " + url + " ", "s "));
            Assert.NotEqual(fingerprint, UnattendedAlert.Fingerprint(url, "t"));
            Assert.NotEqual(fingerprint, UnattendedAlert.Fingerprint(url + "0", "s"));
            Assert.DoesNotContain("0123456789", fingerprint, StringComparison.Ordinal);
        }

        /// <summary>
        /// The console leaves a failure to the task only when the task posts to its own webhook:
        /// one set up with another robot — since deleted, or another administrator's — would
        /// otherwise take the console's alerts where nobody reads them.
        /// </summary>
        [Fact]
        public void TheConsoleLeavesAFailureOnlyToATaskThatPostsToItsOwnWebhook()
        {
            string robotA = "https://qyapi.weixin.qq.com/cgi-bin/webhook/send?key=aaaa";
            string robotB = "https://qyapi.weixin.qq.com/cgi-bin/webhook/send?key=bbbb";
            var setUpWithA = new AlertManifest { Version = "0.14.0", Fingerprint = UnattendedAlert.Fingerprint(robotA, "s") };

            Assert.True(UnattendedAlert.SameWebhook(setUpWithA, robotA, "s"));
            Assert.True(UnattendedAlert.SameWebhook(setUpWithA, " " + robotA + " ", "s "));
            Assert.False(UnattendedAlert.SameWebhook(setUpWithA, robotB, "s"));
            Assert.False(UnattendedAlert.SameWebhook(setUpWithA, robotA, "rotated"));
        }

        /// <summary>A task whose manifest is missing or unreadable is not known to post anywhere in particular.</summary>
        [Fact]
        public void WithoutAManifestTheConsolePostsItself()
        {
            string url = "https://open.feishu.cn/open-apis/bot/v2/hook/x";

            Assert.False(UnattendedAlert.SameWebhook(null, url, string.Empty));
            Assert.False(UnattendedAlert.SameWebhook(new AlertManifest(), url, string.Empty));
        }

        [Fact]
        public void TheWebhookCopyReadsBackAsWritten()
        {
            var copy = new WebhookCopy { Url = "https://oapi.dingtalk.com/robot/send?access_token=t", Secret = "SEC1", Language = "zh-CN" };

            var read = UnattendedAlert.CopyFromJson(UnattendedAlert.CopyToJson(copy));

            Assert.NotNull(read);
            Assert.Equal(copy.Url, read!.Url);
            Assert.Equal(copy.Secret, read.Secret);
            Assert.Equal("zh-CN", read.Language);
        }

        [Theory]
        [InlineData("{\"url\":\"\",\"secret\":\"s\"}")]
        [InlineData("{}")]
        [InlineData("not json")]
        public void ACopyWithoutAnAddressIsNoCopy(string json)
        {
            Assert.Null(UnattendedAlert.CopyFromJson(json));
        }

        [Fact]
        public void TheManifestReadsBackAsWritten()
        {
            string folder = Path.Combine(Path.GetTempPath(), "winsw-gui-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                string path = Path.Combine(folder, "alert.json");
                var at = new DateTimeOffset(2026, 9, 24, 3, 0, 0, TimeSpan.FromHours(8));
                File.WriteAllText(path, UnattendedAlert.ManifestToJson(new AlertManifest
                {
                    Version = "0.14.0",
                    Fingerprint = "0011223344556677",
                    Language = "ja",
                    InstalledAt = at,
                    InstalledBy = @"HOST\ops",
                }));

                var read = UnattendedAlert.ReadJson<AlertManifest>(path);

                Assert.NotNull(read);
                Assert.Equal("0.14.0", read!.Version);
                Assert.Equal("0011223344556677", read.Fingerprint);
                Assert.Equal("ja", read.Language);
                Assert.Equal(at, read.InstalledAt);
                Assert.Equal(@"HOST\ops", read.InstalledBy);
                Assert.Null(UnattendedAlert.ReadJson<AlertManifest>(Path.Combine(folder, "missing.json")));
            }
            finally
            {
                Directory.Delete(folder, recursive: true);
            }
        }

        /// <summary>The console reads the state, the log and the manifest; the copies are in the folder it cannot open.</summary>
        [Fact]
        public void TheSecretsLiveInTheirOwnFolder()
        {
            Assert.Equal(UnattendedAlert.MachineFolder, Path.GetDirectoryName(UnattendedAlert.StatePath));
            Assert.Equal(UnattendedAlert.MachineFolder, Path.GetDirectoryName(UnattendedAlert.LogPath));
            Assert.Equal(UnattendedAlert.MachineFolder, Path.GetDirectoryName(UnattendedAlert.ManifestPath));

            Assert.Equal(UnattendedAlert.PrivateFolder, Path.GetDirectoryName(UnattendedAlert.CopyPath));
            Assert.Equal(UnattendedAlert.PrivateFolder, Path.GetDirectoryName(UnattendedAlert.RunnerPath));
            Assert.Equal(UnattendedAlert.PrivateFolder, Path.GetDirectoryName(UnattendedAlert.ExtractionBase));
            Assert.Equal(@"\WinSW\Alert", UnattendedAlert.TaskPath);
        }
    }
}

using System;
using System.IO;
using System.Linq;
using WinSW.Gui.Model;
using WinSW.Gui.Services;
using Xunit;

namespace WinSW.Gui.Tests
{
    /// <summary>
    /// What a copy takes from the service it is started from. It used to take about fourteen
    /// fields and leave behind the environment, the account, the stop settings, the hooks and
    /// the roll pattern; and a %BASE% that found the source's program pointed the copy at its
    /// own, empty folder.
    /// </summary>
    public sealed class ServiceCloneTests : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "winsw-gui-" + Guid.NewGuid().ToString("n"));
        private readonly string sourceDirectory;

        public ServiceCloneTests()
        {
            this.sourceDirectory = Path.Combine(this.directory, "api");
            Directory.CreateDirectory(this.sourceDirectory);
        }

        public void Dispose() => Directory.Delete(this.directory, recursive: true);

        [Theory]
        [InlineData("main:app --host 0.0.0.0 --port 8000", new[] { 8000 })]
        [InlineData("main:app --port=8001", new[] { 8001 })]
        [InlineData("--port:8002", new[] { 8002 })]
        [InlineData("-Dserver.port=8080 -jar app.jar", new[] { 8080 })]
        [InlineData("-jar app.jar --server.port=8081", new[] { 8081 })]
        [InlineData("serve --http-port 81", new[] { 81 })]
        [InlineData("-b 0.0.0.0:5000 app:app", new[] { 5000 })]
        [InlineData("--bind=127.0.0.1:9000 app:app", new[] { 9000 })]
        [InlineData("--urls \"http://*:5000;http://*:5001\"", new[] { 5000, 5001 })]
        [InlineData("-p 3000", new[] { 3000 })]
        [InlineData("-S 0.0.0.0:8888 -t public", new[] { 8888 })]
        [InlineData("serve http://0.0.0.0:8000/", new[] { 8000 })]
        [InlineData("--port 8000 --admin-port 8001 --port 8000", new[] { 8000, 8001 })]
        [InlineData("--port 8000 --db-port 5432 --metrics-port 9100", new[] { 8000, 9100 })]
        [InlineData("-Dserver.port=8080 -Dspring.redis.port=6379 -jar app.jar", new[] { 8080 })]
        public void FindsThePortsACommandLineListensOn(string arguments, int[] expected)
        {
            Assert.Equal(expected, ServiceClone.FindPorts(arguments, Array.Empty<EnvironmentVariable>()));
        }

        /// <summary>
        /// Numbers that are not ports: a worker count, an option that only contains the
        /// letters, a path, a time of day, and addresses and ports the program connects to
        /// rather than listens on.
        /// </summary>
        [Theory]
        [InlineData("main:app --workers 4")]
        [InlineData("--report 5 --portal 6")]
        [InlineData(@"C:\apps\api\main.py")]
        [InlineData("--at 10:30")]
        [InlineData("--redis localhost:6379 --db postgres://user:secret@db:5432/app")]
        [InlineData("--port 0 --port 70000")]
        [InlineData("--db-port 5432 --redis-port=6379 --smtp-port 25")]
        [InlineData("-Ddb.port=5432 -Dspring.datasource.mysql.port=3306 -Dmail.smtp.port=587")]
        [InlineData("--upstream-port 80 --proxy_port 3128 --backend.port 9000 --pg-port 5432")]
        [InlineData("")]
        public void FindsNoPortWhereThereIsNone(string arguments)
        {
            Assert.Empty(ServiceClone.FindPorts(arguments, Array.Empty<EnvironmentVariable>()));
        }

        [Fact]
        public void FindsPortsInTheEnvironment()
        {
            var environment = new[]
            {
                new EnvironmentVariable { Name = "PORT", Value = " 8000 " },
                new EnvironmentVariable { Name = "ASPNETCORE_URLS", Value = "http://*:5000" },
                new EnvironmentVariable { Name = "GUNICORN_CMD_ARGS", Value = "--bind=0.0.0.0:7000 --workers 2" },
                new EnvironmentVariable { Name = "admin_port", Value = "9001" },
                new EnvironmentVariable { Name = "REDIS_URL", Value = "redis://localhost:6379/0" },
                new EnvironmentVariable { Name = "SUPPORT", Value = "1234" },

                // Ports it connects to, named after the server at the other end.
                new EnvironmentVariable { Name = "DB_PORT", Value = "5432" },
                new EnvironmentVariable { Name = "REDIS_PORT", Value = "6379" },
                new EnvironmentVariable { Name = "SPRING_RABBITMQ_PORT", Value = "5672" },
                new EnvironmentVariable { Name = "mail.port", Value = "25" },
                new EnvironmentVariable { Name = "METRICS_PORT", Value = "9100" },
            };

            Assert.Equal(new[] { 8000, 5000, 7000, 9001, 9100 }, ServiceClone.FindPorts(null, environment));
        }

        [Theory]
        [InlineData(@"%BASE%\server.exe", @"{0}\server.exe")]
        [InlineData(@"--config %base%\app.ini --data ""%Base%\data""", @"--config {0}\app.ini --data ""{0}\data""")]
        [InlineData(@"%ProgramData%\app\%SERVICE_ID%.ini", @"%ProgramData%\app\%SERVICE_ID%.ini")]
        public void RebaseNamesTheSourcesFolderAndNothingElse(string value, string expected)
        {
            Assert.Equal(string.Format(expected, this.sourceDirectory), ServiceClone.Rebase(value, this.sourceDirectory));
        }

        [Theory]
        [InlineData("logs", @"%BASE%\logs")]
        [InlineData(@"out\hook.txt", @"%BASE%\out\hook.txt")]
        [InlineData(@"%BASE%\logs", @"%BASE%\logs")]
        [InlineData(@"%TEMP%\logs", @"%TEMP%\logs")]
        [InlineData(@"D:\logs", @"D:\logs")]
        [InlineData(@"\\server\share\logs", @"\\server\share\logs")]
        [InlineData(@"\logs", @"\logs")]
        [InlineData(" ", " ")]
        [InlineData(null, null)]
        public void AnchorToBaseTakesOnlyRelativePaths(string? path, string? expected)
        {
            Assert.Equal(expected, ServiceClone.AnchorToBase(path));
        }

        /// <summary>
        /// What the service reads or runs follows the source; what it writes stays with the
        /// copy: its logs, and a hook's captured output.
        /// </summary>
        [Fact]
        public void BaseInWhatTheServiceRunsNamesTheSourcesFolder()
        {
            var clone = this.Load(
                @"<executable>%BASE%\server.exe</executable>"
                + @"<arguments>--config %BASE%\app.ini</arguments>"
                + @"<startarguments>--start %BASE%\start.ini</startarguments>"
                + @"<stopexecutable>%BASE%\stop.exe</stopexecutable>"
                + @"<stoparguments>--pid %BASE%\pid</stoparguments>"
                + @"<workingdirectory>%BASE%\work</workingdirectory>"
                + @"<prestart><executable>%BASE%\migrate.exe</executable><arguments>%BASE%\db</arguments><stdoutPath>%BASE%\migrate.log</stdoutPath></prestart>"
                + @"<env name=""APP_HOME"" value=""%BASE%\home"" />"
                + @"<env name=""MODE"" value=""production"" />"
                + @"<logpath>%BASE%\logs</logpath>");
            var model = clone.NewModel();
            string source = this.sourceDirectory;

            Assert.Equal(source + @"\server.exe", model.Executable);
            Assert.Equal(@"--config " + source + @"\app.ini", model.Arguments);
            Assert.Equal(@"--start " + source + @"\start.ini", model.StartArguments);
            Assert.Equal(source + @"\stop.exe", model.StopExecutable);
            Assert.Equal(@"--pid " + source + @"\pid", model.StopArguments);
            Assert.Equal(source + @"\work", model.WorkingDirectory);
            Assert.Equal(source + @"\migrate.exe", model.Prestart.Executable);
            Assert.Equal(source + @"\db", model.Prestart.Arguments);
            Assert.Equal(source + @"\home", model.EnvironmentVariables.Single(v => v.Name == "APP_HOME").Value);
            Assert.Equal("production", model.EnvironmentVariables.Single(v => v.Name == "MODE").Value);

            Assert.Equal(@"%BASE%\logs", model.LogPath);
            Assert.Equal(@"%BASE%\migrate.log", model.Prestart.StdoutPath);

            Assert.Equal(
                new[] { "<executable>", "<arguments>", "<startarguments>", "<stopexecutable>", "<stoparguments>", "<workingdirectory>", "<prestart>", "<env name=\"APP_HOME\">" },
                clone.PointingIntoSource(model));
        }

        /// <summary>
        /// A configuration without a working directory runs in its own folder. The copy is
        /// given the source's outright, and what it writes by a relative path is anchored to
        /// its own folder, where it went before.
        /// </summary>
        [Fact]
        public void ASourceWithoutAWorkingDirectoryIsGivenItsFolder()
        {
            var clone = this.Load(
                "<executable>server.exe</executable><arguments>main.py</arguments>"
                + "<logpath>logs</logpath>"
                + @"<poststart><executable>notify.exe</executable><stderrPath>notify.err</stderrPath></poststart>"
                + @"<download from=""https://example.com/app.zip"" to=""app.zip"" />");
            var model = clone.NewModel();

            Assert.Equal(this.sourceDirectory, model.WorkingDirectory);
            Assert.Equal("server.exe", model.Executable);
            Assert.Equal("main.py", model.Arguments);
            Assert.Equal(@"%BASE%\logs", model.LogPath);
            Assert.Equal(@"%BASE%\notify.err", model.Poststart.StderrPath);
            Assert.Equal(@"%BASE%\app.zip", model.Downloads.Single().To);
            Assert.Equal(new[] { "<workingdirectory>" }, clone.PointingIntoSource(model));
        }

        [Fact]
        public void ASettingChangedAwayFromTheSourcesFolderIsNoLongerListed()
        {
            var clone = this.Load(@"<executable>%BASE%\server.exe</executable><workingdirectory>%BASE%</workingdirectory>");
            var model = clone.NewModel();

            model.WorkingDirectory = Path.Combine(this.directory, "elsewhere");

            Assert.Equal(new[] { "<executable>" }, clone.PointingIntoSource(model));
        }

        [Fact]
        public void NothingIsListedWhenNothingNamedTheSourcesFolder()
        {
            var clone = this.Load(@"<executable>C:\apps\api\server.exe</executable><workingdirectory>C:\apps\api</workingdirectory><logpath>%BASE%\logs</logpath>");

            Assert.Empty(clone.PointingIntoSource(clone.NewModel()));
        }

        /// <summary>The wizard builds its model more than once; rows added to one are not in the next.</summary>
        [Fact]
        public void EveryModelIsFresh()
        {
            var clone = this.Load(@"<executable>server.exe</executable><env name=""A"" value=""1"" /><onfailure action=""restart"" />");

            var first = clone.NewModel();
            first.EnvironmentVariables.Add(new EnvironmentVariable { Name = "B", Value = "2" });
            first.FailureActions.Clear();
            first.Id = "changed";

            var second = clone.NewModel();
            Assert.Single(second.EnvironmentVariables);
            Assert.Single(second.FailureActions);
            Assert.Equal("api", second.Id);
        }

        [Fact]
        public void KeepsTheSourcesRecoveryAndRollPattern()
        {
            var clone = this.Load(
                "<executable>server.exe</executable>"
                + @"<onfailure action=""restart"" delay=""10 sec"" /><onfailure action=""restart"" delay=""1 min"" /><onfailure action=""reboot"" delay=""5 min"" />"
                + "<resetfailure>2 hours</resetfailure>"
                + @"<log mode=""roll-by-time""><pattern>yyyy-MM-dd</pattern><period>2</period><keepFiles>14</keepFiles></log>");

            Assert.Equal(new[] { ("restart", (string?)"10 sec"), ("restart", "1 min"), ("reboot", "5 min") }, clone.Recovery);
            Assert.True(clone.HasRecovery);
            Assert.Equal("2 hours", clone.ResetFailureAfter);
            Assert.Equal("roll-by-time", clone.LogMode);
            Assert.Equal("yyyy-MM-dd", clone.RollPattern);

            var model = clone.NewModel();
            Assert.Equal("2", model.RollPeriod);
            Assert.Equal("14", model.KeepFiles);
        }

        [Fact]
        public void AnActionOfNoneIsNoRecovery()
        {
            Assert.False(this.Load(@"<executable>server.exe</executable><onfailure action=""none"" />").HasRecovery);
            Assert.False(this.Load("<executable>server.exe</executable>").HasRecovery);
        }

        [Fact]
        public void ThePortsAreTheSourcesArgumentsAndEnvironment()
        {
            var clone = this.Load(@"<executable>server.exe</executable><arguments>main:app --port 8000</arguments><startarguments>--admin-port=8100</startarguments><env name=""METRICS_PORT"" value=""9100"" />");

            Assert.Equal(new[] { 8000, 8100, 9100 }, clone.Ports);
        }

        [Theory]
        [InlineData(@"CORP\svc-api", "secret", true)]
        [InlineData(@".\apiuser", "secret", true)]
        [InlineData(@"CORP\svc-api", null, false)]
        [InlineData(@"CORP\gmsa-api$", null, false)]
        [InlineData(@"CORP\gmsa-api$", "ignored", false)]
        [InlineData(@"NT AUTHORITY\NetworkService", "", false)]
        [InlineData(@"NT SERVICE\api", "x", false)]
        [InlineData("LocalSystem", "x", false)]
        [InlineData(null, null, false)]
        public void OnlyANamedAccountCarriesAPassword(string? user, string? password, bool expected)
        {
            var model = ServiceConfigModel.CreateNew();
            model.ServiceAccountUser = user;
            model.ServiceAccountPassword = password;

            Assert.Equal(expected, ServiceClone.CarriesPassword(model));
        }

        /// <summary>
        /// A named log in a directory given in full is the one case where the copy writes into
        /// the source's very files. %BASE% moves the directory with the copy, and without a
        /// log name the files are named after each service's own configuration.
        /// </summary>
        [Fact]
        public void TellsWhenTheCopyWouldWriteTheSourcesLogFiles()
        {
            string logs = Path.Combine(this.directory, "logs");
            var clone = this.Load($"<executable>server.exe</executable><logpath>{logs}</logpath><logname>api</logname>");
            string copyConfig = Path.Combine(this.directory, "api-2", "api-2.xml");

            var model = clone.NewModel();
            Assert.True(clone.SharesLogFilesWith(model, copyConfig));
            Assert.False(clone.SharesLogFilesWith(model, string.Empty));

            model.LogName = null;
            Assert.False(clone.SharesLogFilesWith(model, copyConfig));

            model.LogName = "api";
            model.LogPath = @"%BASE%\logs";
            Assert.False(clone.SharesLogFilesWith(model, copyConfig));

            model.LogPath = logs;
            model.LogMode = "none";
            Assert.False(clone.SharesLogFilesWith(model, copyConfig));
        }

        [Fact]
        public void ASourceThatDoesNotLogSharesNoLogFiles()
        {
            string logs = Path.Combine(this.directory, "logs");
            var clone = this.Load($@"<executable>server.exe</executable><logpath>{logs}</logpath><logname>api</logname><log mode=""none"" />");
            var model = clone.NewModel();
            model.LogMode = "append";

            Assert.False(clone.SharesLogFilesWith(model, Path.Combine(this.directory, "api-2", "api-2.xml")));
        }

        [Fact]
        public void AFileThatIsNotAConfigurationIsRefused()
        {
            string path = Path.Combine(this.sourceDirectory, "api.xml");
            File.WriteAllText(path, "<nothing />");

            Assert.Throws<InvalidDataException>(() => ServiceClone.Load("api", path));
        }

        private ServiceClone Load(string body)
        {
            string path = Path.Combine(this.sourceDirectory, "api.xml");
            File.WriteAllText(path, "<service><id>api</id>" + body + "</service>");
            return ServiceClone.Load("api", path);
        }
    }
}

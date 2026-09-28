using System;
using System.Diagnostics;
using System.IO;
using System.ServiceProcess;
using WinSW.Native;
using WinSW.Tests.Util;
using Xunit;
using Helper = WinSW.Tests.Util.CommandLineTestHelper;

namespace WinSW.Tests
{
    public class CommandLineTests
    {
        [ElevatedFact]
        public void Install_Start_Stop_Uninstall_Console_App()
        {
            using var config = Helper.TestXmlServiceConfig.FromXml(Helper.SeedXml);

            try
            {
                _ = Helper.Test(new[] { "install", config.FullPath }, config);

                using var controller = new ServiceController(config.Name);
                Assert.Equal(config.DisplayName, controller.DisplayName);
                Assert.False(controller.CanStop);
                Assert.False(controller.CanShutdown);
                Assert.False(controller.CanPauseAndContinue);
                Assert.Equal(ServiceControllerStatus.Stopped, controller.Status);
                Assert.Equal(ServiceType.Win32OwnProcess, controller.ServiceType);

#if NET
                InterProcessCodeCoverageSession session = null;
                try
                {
                    try
                    {
                        _ = Helper.Test(new[] { "start", config.FullPath }, config);
                        controller.Refresh();
                        Assert.Equal(ServiceControllerStatus.Running, controller.Status);
                        Assert.True(controller.CanStop);

                        Assert.EndsWith(
                            ServiceMessages.StartedSuccessfully + Environment.NewLine,
                            File.ReadAllText(Path.ChangeExtension(config.FullPath, ".wrapper.log")));

                        if (Environment.GetEnvironmentVariable("System.DefinitionId") != null)
                        {
                            session = new InterProcessCodeCoverageSession(config.Name);
                        }
                    }
                    finally
                    {
                        _ = Helper.Test(new[] { "stop", config.FullPath }, config);
                        controller.Refresh();
                        Assert.Equal(ServiceControllerStatus.Stopped, controller.Status);

                        Assert.EndsWith(
                            ServiceMessages.StoppedSuccessfully + Environment.NewLine,
                            File.ReadAllText(Path.ChangeExtension(config.FullPath, ".wrapper.log")));
                    }
                }
                finally
                {
                    session?.Wait();
                }
#endif
            }
            finally
            {
                _ = Helper.Test(new[] { "uninstall", config.FullPath }, config);
            }
        }

        /// <summary>
        /// Refresh takes delayed start from the file both ways, as it does the start mode, but
        /// replaces failure actions only when the file declares some: no <c>&lt;onfailure&gt;</c>
        /// leaves them alone, and <c>&lt;onfailure action="none"/&gt;</c> is how a file clears them.
        /// It sits in this class rather than its own because <see cref="Program.TestConfig"/> is
        /// static, and xunit runs test classes in parallel.
        /// </summary>
        [ElevatedFact]
        public void Refresh_Follows_The_File_For_Delayed_Start_And_Declared_Failure_Actions()
        {
            using var config = Helper.TestXmlServiceConfig.FromXml(
$@"<service>
  <id>{Helper.Name}</id>
  <name>{Helper.DisplayName}</name>
  <executable>cmd.exe</executable>
  <arguments>/c timeout /t -1 /nobreak</arguments>
  <startmode>Automatic</startmode>
  <delayedAutoStart>true</delayedAutoStart>
  <onfailure action=""restart"" delay=""10 sec""/>
  <resetfailure>1 hour</resetfailure>
</service>");

            // Later versions of the same file. TestXmlServiceConfig would give each a new
            // service ID, and refresh needs the one that was installed.
            XmlServiceConfig Revision(string entries) => XmlServiceConfig.FromXml(
$@"<service>
  <id>{config.Name}</id>
  <name>{config.DisplayName}</name>
  <executable>cmd.exe</executable>
  <arguments>/c timeout /t -1 /nobreak</arguments>
  <startmode>Automatic</startmode>
  {entries}
</service>");

            try
            {
                _ = Helper.Test(new[] { "install", config.FullPath }, config);

                Assert.True(ServiceConfigQuery.DelayedAutoStart(config.Name));
                var action = Assert.Single(ServiceConfigQuery.FailureActions(config.Name, out var resetPeriod));
                Assert.Equal(SC_ACTION_TYPE.SC_ACTION_RESTART, action.Type);
                Assert.Equal(10_000, action.Delay);
                Assert.Equal(TimeSpan.FromHours(1), resetPeriod);

                // Neither element: delayed start goes, and recovery stays as it was, reset
                // period included.
                _ = Helper.Test(new[] { "refresh", config.FullPath }, Revision(string.Empty));

                Assert.False(ServiceConfigQuery.DelayedAutoStart(config.Name));
                action = Assert.Single(ServiceConfigQuery.FailureActions(config.Name, out resetPeriod));
                Assert.Equal(SC_ACTION_TYPE.SC_ACTION_RESTART, action.Type);
                Assert.Equal(10_000, action.Delay);
                Assert.Equal(TimeSpan.FromHours(1), resetPeriod);

                // A single action that does nothing replaces the restart, and the reset period
                // is the file's too, here its default.
                _ = Helper.Test(new[] { "refresh", config.FullPath }, Revision(@"<onfailure action=""none""/>"));

                action = Assert.Single(ServiceConfigQuery.FailureActions(config.Name, out resetPeriod));
                Assert.Equal(SC_ACTION_TYPE.SC_ACTION_NONE, action.Type);
                Assert.Equal(0, action.Delay);
                Assert.Equal(TimeSpan.FromDays(1), resetPeriod);

                _ = Helper.Test(new[] { "refresh", config.FullPath }, Revision("<delayedAutoStart>true</delayedAutoStart>"));

                Assert.True(ServiceConfigQuery.DelayedAutoStart(config.Name));
            }
            finally
            {
                _ = Helper.Test(new[] { "uninstall", config.FullPath }, config);
            }
        }

        [Fact]
        public void FailOnUnknownCommand()
        {
            const string commandName = "unknown";

            var result = Helper.ErrorTest(new[] { commandName });

            Assert.Equal($"Unrecognized command or argument '{commandName}'.\r\n\r\n", result.Error);
        }

        /// <summary>
        /// https://github.com/kohsuke/winsw/issues/206
        /// </summary>
        [Fact(Skip = "unknown")]
        public void ShouldNotPrintLogsForStatusCommand()
        {
            string cliOut = Helper.Test(new[] { "status" });
            Assert.Equal("NonExistent" + Environment.NewLine, cliOut);
        }

        [Fact]
        public void Customize()
        {
            const string OldCompanyName = "CloudBees, Inc.";
            const string NewCompanyName = "CLOUDBEES, INC.";

            string inputPath = Layout.WinSWExe;

            Assert.Equal(OldCompanyName, FileVersionInfo.GetVersionInfo(inputPath).CompanyName);

            // deny write access
            using var file = File.OpenRead(inputPath);

            string outputPath = Path.GetTempFileName();
            Program.TestExecutablePath = inputPath;
            try
            {
                _ = Helper.Test(new[] { "customize", "-o", outputPath, "--manufacturer", NewCompanyName });

                Assert.Equal(NewCompanyName, FileVersionInfo.GetVersionInfo(outputPath).CompanyName);
            }
            finally
            {
                Program.TestExecutablePath = null;
                File.Delete(outputPath);
            }
        }
    }
}

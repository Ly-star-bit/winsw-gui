using System;
using WinSW.Gui.Services;
using Xunit;

namespace WinSW.Gui.Tests
{
    public class DesktopTasksTests
    {
        /// <summary>
        /// The wrapper publishes this name and the console opens it; the two implementations
        /// are in different assemblies that do not reference each other, so both are pinned to
        /// the same literals. <c>WinSW.Tests.ConsoleSessionTests</c> asserts the other half.
        /// A change here without one there does not fail to compile: it silently turns every
        /// clean stop into a hard termination.
        /// </summary>
        [Theory]
        [InlineData("my-app", @"Local\WinSW.Console.my-app.ef484009")]
        [InlineData("ocr_server", @"Local\WinSW.Console.ocr_server.6291a5fb")]
        [InlineData("ocr.server", @"Local\WinSW.Console.ocr.server.5aff84f0")]
        public void StopEventNameIsStable(string serviceId, string expected)
        {
            Assert.Equal(expected, DesktopTasks.StopEventName(serviceId));
        }

        [Fact]
        public void StopEventNameReplacesCharactersAKernelObjectCannotHold()
        {
            // A backslash would start a new namespace, so it cannot survive into the name.
            string name = DesktopTasks.StopEventName(@"a\b c");
            Assert.StartsWith(@"Local\WinSW.Console.a_b_c.", name, StringComparison.Ordinal);
            Assert.Equal(1, CountBackslashes(name));

            static int CountBackslashes(string value)
            {
                int count = 0;
                foreach (char c in value)
                {
                    if (c == '\\')
                    {
                        count++;
                    }
                }

                return count;
            }
        }

        /// <summary>Two IDs that sanitise to the same text must still get different names.</summary>
        [Fact]
        public void StopEventNameKeepsPunctuationApart()
        {
            Assert.NotEqual(DesktopTasks.StopEventName("a b"), DesktopTasks.StopEventName("a.b"));
        }

        [Fact]
        public void ArgumentsRoundTripThroughTheConfigurationPath()
        {
            const string config = @"C:\Users\bot\AppData\Local\WinSW\ocr\ocr.xml";

            string arguments = DesktopTasks.BuildArguments(config);
            Assert.Equal(@"console ""C:\Users\bot\AppData\Local\WinSW\ocr\ocr.xml""", arguments);
            Assert.Equal(config, DesktopTasks.ConfigPathFromArguments(arguments));
        }

        [Fact]
        public void ConfigPathIsRecoveredFromATaskSomeoneEditedByHand()
        {
            Assert.Equal(@"C:\a b\svc.xml", DesktopTasks.ConfigPathFromArguments(@"console ""C:\a b\svc.xml"""));
            Assert.Equal(@"C:\a\svc.xml", DesktopTasks.ConfigPathFromArguments(@"console C:\a\svc.xml"));
            Assert.Null(DesktopTasks.ConfigPathFromArguments("console"));
            Assert.Null(DesktopTasks.ConfigPathFromArguments(null));
        }

        [Theory]
        [InlineData(0, "PT0S")]
        [InlineData(-5, "PT0S")]
        [InlineData(30, "PT30S")]
        [InlineData(60, "PT1M")]
        [InlineData(90, "PT1M30S")]
        [InlineData(3600, "PT1H")]
        [InlineData(90000, "P1DT1H")]
        public void DurationIsAnIso8601Period(int seconds, string expected)
        {
            Assert.Equal(expected, DesktopTasks.Duration(TimeSpan.FromSeconds(seconds)));
        }

        [Fact]
        public void DefinitionIsReadBackFromTheTaskDocument()
        {
            const string xml = @"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo>
    <Author>WinSW</Author>
    <Description>Screen robot</Description>
  </RegistrationInfo>
  <Principals>
    <Principal id=""Author"">
      <UserId>DESK\bot</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Actions Context=""Author"">
    <Exec>
      <Command>C:\WinSW\bin\WinSW.exe</Command>
      <Arguments>console ""C:\WinSW\ocr\ocr.xml""</Arguments>
      <WorkingDirectory>C:\WinSW\ocr</WorkingDirectory>
    </Exec>
  </Actions>
</Task>";

            var parsed = DesktopTasks.ParseDefinition(xml);

            Assert.Equal(@"C:\WinSW\bin\WinSW.exe", parsed.Command);
            Assert.Equal(@"C:\WinSW\ocr\ocr.xml", parsed.ConfigPath);
            Assert.Equal("Screen robot", parsed.Description);
            Assert.Equal(@"DESK\bot", parsed.UserId);
            Assert.True(parsed.RunElevated);
        }

        [Fact]
        public void AnUnreadableDefinitionYieldsEmptyStringsRatherThanNulls()
        {
            var parsed = DesktopTasks.ParseDefinition("not xml at all");

            Assert.Equal(string.Empty, parsed.Command);
            Assert.Equal(string.Empty, parsed.Description);
            Assert.Equal(string.Empty, parsed.UserId);
            Assert.Null(parsed.ConfigPath);
            Assert.False(parsed.RunElevated);
        }

        /// <summary>A task without the elevation flag must not be reported as elevated.</summary>
        [Fact]
        public void LeastPrivilegeIsNotMistakenForElevation()
        {
            const string xml = @"<Task xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <Principals><Principal><UserId>DESK\bot</UserId></Principal></Principals>
  <Actions><Exec><Command>w.exe</Command><Arguments>console ""c:\a.xml""</Arguments></Exec></Actions>
</Task>";

            Assert.False(DesktopTasks.ParseDefinition(xml).RunElevated);
        }
    }
}

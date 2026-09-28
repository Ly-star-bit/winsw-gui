using WinSW.Gui.Services;
using Xunit;

namespace WinSW.Gui.Tests
{
    /// <summary>
    /// Telling a WinSW service on another machine from the rest, by the command line it was
    /// registered with: the only thing the Remote page reads about it besides its status.
    /// </summary>
    public class RemoteWrapperCheckTests
    {
        /// <summary>What the wrapper's own install writes, and what the console's installs look like.</summary>
        [Theory]
        [InlineData(@"""C:\ProgramData\WinSW\bin\WinSW.exe"" ""C:\ProgramData\WinSW\api\api.xml""")]
        [InlineData(@"""D:\Program Files\WinSW\WinSW-x64.exe"" ""D:\services\my app\my app.XML""")]
        public void TheWrapperWithItsConfigurationIsAWrapper(string binaryPath)
        {
            Assert.True(RemoteMonitor.LooksLikeWrapper(binaryPath));
        }

        /// <summary>The bundled form: the wrapper renamed, its configuration beside it.</summary>
        [Theory]
        [InlineData(@"""D:\apps\api\api.exe""")]
        [InlineData(@"C:\jenkins\jenkins.exe")]
        public void AWrapperRegisteredAloneIsAWrapper(string binaryPath)
        {
            Assert.True(RemoteMonitor.LooksLikeWrapper(binaryPath));
        }

        /// <summary>Windows' own services are the bulk of the bare programs on a server.</summary>
        [Theory]
        [InlineData(@"C:\Windows\system32\lsass.exe")]
        [InlineData(@"C:\WINDOWS\System32\spoolsv.exe")]
        [InlineData(@"""C:\Windows\servicing\TrustedInstaller.exe""")]
        [InlineData(@"%SystemRoot%\system32\msdtc.exe")]
        [InlineData(@"C:\Windows\system32\svchost.exe -k netsvcs -p")]
        public void WindowsOwnServicesAreNot(string binaryPath)
        {
            Assert.False(RemoteMonitor.LooksLikeWrapper(binaryPath));
        }

        [Theory]
        [InlineData(@"""C:\Program Files\Agent\agent.exe"" --service")]
        [InlineData(@"""C:\apps\x.exe"" ""C:\apps\x.config""")]
        [InlineData(@"""C:\apps\x.exe"" ""C:\apps\x.xml"" extra")]
        [InlineData(@"C:\apps\run.cmd")]
        [InlineData(@"agent.exe")]
        [InlineData(@"\\fileserver\share\agent.exe")]
        [InlineData("")]
        [InlineData("   ")]
        public void OtherShapesAreNot(string binaryPath)
        {
            Assert.False(RemoteMonitor.LooksLikeWrapper(binaryPath));
        }

        /// <summary>
        /// The limit, stated: another program registered bare outside the Windows directory has
        /// the wrapper's shape, and only its version information could say otherwise. The page
        /// does not read that over the network, so it passes, as it would pass the shape locally.
        /// </summary>
        [Fact]
        public void AnotherBareProgramOutsideWindowsCannotBeToldApart()
        {
            Assert.True(RemoteMonitor.LooksLikeWrapper(@"""C:\Program Files\VMware\VMware Tools\vmtoolsd.exe"""));
        }
    }
}

using WinSW.Gui.Services;
using Xunit;

namespace WinSW.Gui.Tests
{
    public class ServiceDiscoveryTests
    {
        [Fact]
        public void SplitCommandLine_HandlesQuotedAndBarePaths()
        {
            var tokens = ServiceDiscovery.SplitCommandLine(@"""C:\Program Files\App\WinSW.exe"" ""C:\Program Files\App\svc.xml""");
            Assert.Equal(2, tokens.Count);
            Assert.Equal(@"C:\Program Files\App\WinSW.exe", tokens[0]);
            Assert.Equal(@"C:\Program Files\App\svc.xml", tokens[1]);

            tokens = ServiceDiscovery.SplitCommandLine(@"C:\app\myapp.exe");
            Assert.Single(tokens);

            tokens = ServiceDiscovery.SplitCommandLine(@"  ""C:\a b\x.exe""   -flag   ");
            Assert.Equal(new[] { @"C:\a b\x.exe", "-flag" }, tokens);
        }

        [Fact]
        public void SameShape_ComparesStructureNotIdentity()
        {
            var a = new ProcessNode(1, "winsw.exe");
            a.Children.Add(new ProcessNode(2, "java.exe"));

            var b = new ProcessNode(1, "winsw.exe");
            b.Children.Add(new ProcessNode(2, "java.exe"));

            Assert.True(ProcessTreeProvider.SameShape(a, b));

            b.Children.Add(new ProcessNode(3, "cmd.exe"));
            Assert.False(ProcessTreeProvider.SameShape(a, b));
            Assert.False(ProcessTreeProvider.SameShape(a, null));
            Assert.True(ProcessTreeProvider.SameShape(null, null));
        }
    }
}

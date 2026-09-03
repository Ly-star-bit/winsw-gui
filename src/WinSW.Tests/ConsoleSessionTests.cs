using WinSW;
using Xunit;

namespace WinSW.Tests
{
    public sealed class ConsoleSessionTests
    {
        /// <summary>
        /// The console front end reimplements this name — it does not reference this assembly —
        /// so both sides are pinned to the same literals. <c>WinSW.Gui.Tests.DesktopTasksTests</c>
        /// asserts the other half. Changing one without the other still compiles; what it
        /// breaks is every clean stop, silently, in favour of a hard termination.
        /// </summary>
        [Theory]
        [InlineData("my-app", @"Local\WinSW.Console.my-app.ef484009")]
        [InlineData("ocr_server", @"Local\WinSW.Console.ocr_server.6291a5fb")]
        [InlineData("ocr.server", @"Local\WinSW.Console.ocr.server.5aff84f0")]
        public void EventNameIsStable(string serviceId, string expected)
        {
            Assert.Equal(expected, ConsoleSession.EventName(serviceId));
        }

        [Fact]
        public void EventNameReplacesCharactersAKernelObjectCannotHold()
        {
            Assert.StartsWith(@"Local\WinSW.Console.a_b_c.", ConsoleSession.EventName(@"a\b c"));
        }

        [Fact]
        public void EventNameKeepsPunctuationApart()
        {
            Assert.NotEqual(ConsoleSession.EventName("a b"), ConsoleSession.EventName("a.b"));
        }

        [Fact]
        public void NothingIsListeningForAServiceThatIsNotRunning()
        {
            Assert.False(ConsoleSession.RequestStop("winsw-tests-no-such-console-wrapper"));
        }
    }
}

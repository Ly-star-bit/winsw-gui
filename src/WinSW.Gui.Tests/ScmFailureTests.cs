using System;
using WinSW.Gui.Services;
using Xunit;

namespace WinSW.Gui.Tests
{
    /// <summary>
    /// Reading the service control manager's failure events, as the unattended alert gets them:
    /// the values in the order the event holds them, error codes as <c>%%</c> references.
    /// </summary>
    public class ScmFailureTests
    {
        [Fact]
        public void AnUnexpectedStopWithRecoveryCarriesItsCount()
        {
            // "The My App service terminated unexpectedly. It has done this 3 time(s). The
            // following corrective action will be taken in 10000 milliseconds: Restart the service."
            var failure = ScmFailure.From(7031, new[] { "My App", "3", "10000", "1", "Restart the service" });

            Assert.Equal(new ScmFailure(ScmFailureKind.Crashed, "My App", 3, "3"), failure);
        }

        [Fact]
        public void AnUnexpectedStopWithoutRecoveryIsTheSameKind()
        {
            Assert.Equal(new ScmFailure(ScmFailureKind.Crashed, "My App", 1, "1"), ScmFailure.From(7034, new[] { "My App", "1" }));
        }

        [Fact]
        public void AFailedStartCarriesItsErrorCode()
        {
            Assert.Equal(new ScmFailure(ScmFailureKind.StartFailed, "My App", 1053, "%%1053"), ScmFailure.From(7000, new[] { "My App", "%%1053" }));
        }

        /// <summary>"A timeout was reached (30000 milliseconds) while waiting for the My App service to connect."</summary>
        [Fact]
        public void AStartTimeoutNamesTheServiceSecond()
        {
            Assert.Equal(new ScmFailure(ScmFailureKind.StartTimedOut, "My App", 30000, "30000"), ScmFailure.From(7009, new[] { "30000", "My App" }));
        }

        [Fact]
        public void AStopWithAnErrorCarriesItsCode()
        {
            Assert.Equal(new ScmFailure(ScmFailureKind.EndedWithError, "My App", 1067, "%%1067"), ScmFailure.From(7023, new[] { "My App", "%%1067" }));
            Assert.Equal(new ScmFailure(ScmFailureKind.EndedWithCode, "My App", -1, "%%-1"), ScmFailure.From(7024, new[] { "My App", "%%-1" }));
        }

        [Theory]
        [InlineData(7036)]
        [InlineData(7040)]
        [InlineData(0)]
        public void AnyOtherEventIsNotAFailure(int eventId)
        {
            Assert.Null(ScmFailure.From(eventId, new[] { "My App", "stopped" }));
        }

        [Fact]
        public void AnEventWithoutAServiceNameIsNotAFailure()
        {
            Assert.Null(ScmFailure.From(7031, Array.Empty<string>()));
            Assert.Null(ScmFailure.From(7009, new[] { "30000" }));
            Assert.Null(ScmFailure.From(7000, new[] { "  ", "%%1053" }));
        }

        [Fact]
        public void AValueThatIsNoNumberIsKeptAsRecorded()
        {
            var failure = ScmFailure.From(7000, new[] { "My App", "%%not a code" })!;

            Assert.Null(failure.Number);
            Assert.Equal("%%not a code", failure.Recorded);

            var (_, args) = failure.Describe("HOST", "myapp", code => "should not be asked");
            Assert.Equal("%%not a code", args[2]);
        }

        [Fact]
        public void EachKindHasItsOwnMessage()
        {
            static string KeyOf(int id, params string[] values) => ScmFailure.From(id, values)!.Describe("HOST", "myapp", c => "e" + c).Key;

            Assert.Equal("M.Alert.Crashed", KeyOf(7031, "My App", "2"));
            Assert.Equal("M.Alert.Crashed", KeyOf(7034, "My App", "2"));
            Assert.Equal("M.Alert.StartFailed", KeyOf(7000, "My App", "%%2"));
            Assert.Equal("M.Alert.StartTimedOut", KeyOf(7009, "30000", "My App"));
            Assert.Equal("M.Alert.EndedWithError", KeyOf(7023, "My App", "%%2"));
            Assert.Equal("M.Alert.EndedWithCode", KeyOf(7024, "My App", "%%2"));
        }

        /// <summary>The message names the machine and the service by its own name; the detail is worded per kind.</summary>
        [Fact]
        public void TheMessageSaysWhatWentWrongInWords()
        {
            var (_, crashed) = ScmFailure.From(7031, new[] { "My App", "3" })!.Describe("HOST", "myapp", c => "error " + c);
            Assert.Equal(new object[] { "HOST", "myapp", "3" }, crashed);

            var (_, failed) = ScmFailure.From(7000, new[] { "My App", "%%1053" })!.Describe("HOST", "myapp", c => "error " + c);
            Assert.Equal(new object[] { "HOST", "myapp", "error 1053" }, failed);

            var (_, timedOut) = ScmFailure.From(7009, new[] { "30000", "My App" })!.Describe("HOST", "myapp", c => "error " + c);
            Assert.Equal(new object[] { "HOST", "myapp", "30" }, timedOut);

            var (_, code) = ScmFailure.From(7024, new[] { "My App", "%%42" })!.Describe("HOST", "myapp", c => "error " + c);
            Assert.Equal(new object[] { "HOST", "myapp", "42" }, code);
        }

        [Theory]
        [InlineData("%%1053", 1053)]
        [InlineData("1053", 1053)]
        [InlineData("%%-2147467259", -2147467259)]
        [InlineData("%%", null)]
        [InlineData("", null)]
        [InlineData("1,053", null)]
        public void NumbersAreReadWithOrWithoutTheMessageReference(string value, int? expected)
        {
            Assert.Equal(expected, ScmFailure.NumberIn(value));
        }
    }
}

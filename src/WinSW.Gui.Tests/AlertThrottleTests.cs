using System;
using System.IO;
using WinSW.Gui.Services;
using Xunit;

namespace WinSW.Gui.Tests
{
    /// <summary>
    /// The unattended alert's throttle, the state file it keeps between runs, and choosing which
    /// last alert the settings page shows.
    /// </summary>
    public class AlertThrottleTests
    {
        private static readonly DateTimeOffset T0 = new(2026, 9, 24, 3, 0, 0, TimeSpan.FromHours(8));

        [Fact]
        public void TheFirstFailureIsPostedAtOnce()
        {
            Assert.Equal(0, AlertThrottle.Admit(new AlertState(), "myapp", T0));
        }

        [Fact]
        public void ACrashLoopIsOneMessagePerWindowCarryingTheCount()
        {
            var state = new AlertState();
            Assert.Equal(0, AlertThrottle.Admit(state, "myapp", T0));
            AlertThrottle.Record(state, "myapp", T0, sent: true);

            // Restarted and failing every ten seconds for the rest of the window.
            for (int i = 1; i < 30; i++)
            {
                Assert.Null(AlertThrottle.Admit(state, "myapp", T0.AddSeconds(10 * i)));
            }

            Assert.Equal(29, AlertThrottle.Admit(state, "myapp", T0 + AlertThrottle.Window));
            AlertThrottle.Record(state, "myapp", T0 + AlertThrottle.Window, sent: true);

            Assert.Null(AlertThrottle.Admit(state, "myapp", T0 + AlertThrottle.Window + TimeSpan.FromSeconds(10)));
            Assert.Equal(1, AlertThrottle.Admit(state, "myapp", T0 + AlertThrottle.Window + AlertThrottle.Window));
        }

        /// <summary>
        /// A failed send starts the window as well, or a crash loop on a machine without network
        /// would queue a minute of retries per restart; the failure it could not announce is
        /// counted into the next message.
        /// </summary>
        [Fact]
        public void AMessageThatWasNotSentIsCountedIntoTheNext()
        {
            var state = new AlertState();
            AlertThrottle.Admit(state, "myapp", T0);
            AlertThrottle.Record(state, "myapp", T0, sent: false);

            Assert.Null(AlertThrottle.Admit(state, "myapp", T0.AddMinutes(1)));
            Assert.Equal(2, AlertThrottle.Admit(state, "myapp", T0.AddMinutes(6)));
        }

        [Fact]
        public void EachServiceHasItsOwnWindowWhateverTheCase()
        {
            var state = new AlertState();
            AlertThrottle.Record(state, "myapp", T0, sent: true);

            Assert.Null(AlertThrottle.Admit(state, "MyApp", T0.AddMinutes(1)));
            Assert.Equal(0, AlertThrottle.Admit(state, "other", T0.AddMinutes(1)));
        }

        [Fact]
        public void AClockSetBackDoesNotSilenceTheService()
        {
            var state = new AlertState();
            AlertThrottle.Record(state, "myapp", T0, sent: true);

            Assert.Equal(0, AlertThrottle.Admit(state, "myapp", T0.AddHours(-2)));
        }

        [Fact]
        public void AServiceNotHeardFromForADayIsForgotten()
        {
            var state = new AlertState();
            AlertThrottle.Record(state, "old", T0, sent: false);
            AlertThrottle.Record(state, "recent", T0.AddHours(20), sent: true);

            AlertThrottle.Prune(state, T0.AddHours(25));

            Assert.False(state.Services.ContainsKey("old"));
            Assert.True(state.Services.ContainsKey("recent"));
        }

        [Fact]
        public void TheStateReadsBackAsWritten()
        {
            string folder = NewFolder();
            try
            {
                string path = Path.Combine(folder, "alert-state.json");
                var state = new AlertState { Last = new AlertOutcome { At = T0, Service = "myapp", Error = "HTTP 502 Bad Gateway" } };
                AlertThrottle.Record(state, "myapp", T0, sent: false);
                state.Save(path);
                state.Save(path);

                var read = AlertState.Load(path);

                Assert.Equal(T0, read.Last!.At);
                Assert.Equal("myapp", read.Last.Service);
                Assert.Equal("HTTP 502 Bad Gateway", read.Last.Error);
                Assert.Equal(1, read.Services["MYAPP"].Held);
                Assert.Equal(T0, read.Services["myapp"].LastAttempt);
                Assert.False(File.Exists(path + ".tmp"));
            }
            finally
            {
                Directory.Delete(folder, recursive: true);
            }
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("{ not json")]
        [InlineData("null")]
        public void AMissingOrBrokenStateStartsAfresh(string? content)
        {
            string folder = NewFolder();
            try
            {
                string path = Path.Combine(folder, "alert-state.json");
                if (content != null)
                {
                    File.WriteAllText(path, content);
                }

                var state = AlertState.Load(path);

                Assert.Empty(state.Services);
                Assert.Null(state.Last);
                Assert.Equal(0, AlertThrottle.Admit(state, "myapp", T0));
            }
            finally
            {
                Directory.Delete(folder, recursive: true);
            }
        }

        [Fact]
        public void TheLaterOfTheTwoLastAlertsIsShown()
        {
            var console = new AlertOutcome { At = T0, Service = "a" };
            var task = new AlertOutcome { At = T0.AddMinutes(1), Service = "b" };

            Assert.Equal((task, true), AlertOutcome.Latest(console, task));
            Assert.Equal((console, false), AlertOutcome.Latest(console, new AlertOutcome { At = T0.AddMinutes(-1) }));
            Assert.Equal((console, false), AlertOutcome.Latest(console, null));
            Assert.Equal((task, true), AlertOutcome.Latest(null, task));
            Assert.Equal(((AlertOutcome?)null, false), AlertOutcome.Latest(null, null));
        }

        private static string NewFolder()
        {
            string folder = Path.Combine(Path.GetTempPath(), "winsw-gui-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            return folder;
        }
    }
}

using System;
using WinSW.Gui.Services;
using Xunit;

namespace WinSW.Gui.Tests
{
    /// <summary>
    /// The events tab said "no events" after reading only the newest 4000 records, which on a
    /// System log full of Schannel or DCOM noise can be a few hours. The logs are now queried for
    /// the few sources that can be about the service, and a search that is still cut short says
    /// how far back it went.
    /// </summary>
    public class EventLogSearchTests
    {
        private static readonly DateTime Monday = new(2026, 9, 21, 8, 0, 0);
        private static readonly DateTime Tuesday = new(2026, 9, 22, 8, 0, 0);

        [Fact]
        public void TheQueryAsksForTheSourcesTheWayEventViewerDoes()
        {
            Assert.Equal(
                "*[System[Provider[@Name='myapp' or @Name='Windows Service Wrapper']]]",
                EventLogReader.SourceQuery(new[] { "myapp", "Windows Service Wrapper" }));
            Assert.Equal(
                "*[System[Provider[@Name='Service Control Manager']]]",
                EventLogReader.SourceQuery(new[] { "Service Control Manager" }));
        }

        /// <summary>An XPath string has no escapes: a quote is put in a string of the other kind.</summary>
        [Fact]
        public void ANameWithAQuoteGoesInTheOtherKind()
        {
            Assert.Equal(
                "*[System[Provider[@Name=\"henry's app\"]]]",
                EventLogReader.SourceQuery(new[] { "henry's app" }));
            Assert.Equal(
                "*[System[Provider[@Name='the \"app\"']]]",
                EventLogReader.SourceQuery(new[] { "the \"app\"" }));
        }

        /// <summary>No query at all, rather than a broken one: the log is then read record by record.</summary>
        [Fact]
        public void ANameWithBothQuotesCannotBeQueried()
        {
            Assert.Null(EventLogReader.SourceQuery(new[] { "myapp", "it's \"odd\"" }));
        }

        [Fact]
        public void TheWrappersOwnSourceIsAboutTheService()
        {
            Assert.True(EventLogReader.IsFromWrapper("myapp", new[] { "Service started successfully." }, "myapp"));
            Assert.True(EventLogReader.IsFromWrapper("MyApp", Array.Empty<string?>(), "myapp"));
        }

        [Fact]
        public void TheSharedSourceIsAboutTheServiceOnlyWhenItNamesIt()
        {
            Assert.True(EventLogReader.IsFromWrapper("Windows Service Wrapper", new[] { "Starting the service with id 'myapp'" }, "myapp"));
            Assert.False(EventLogReader.IsFromWrapper("Windows Service Wrapper", new[] { "Starting the service with id 'other'" }, "myapp"));
            Assert.False(EventLogReader.IsFromWrapper("Windows Service Wrapper", new string?[] { null }, "myapp"));
        }

        [Fact]
        public void AnotherSourceInTheApplicationLogIsNot()
        {
            Assert.False(EventLogReader.IsFromWrapper("Application Error", new[] { "myapp.exe", "1.0.0.0" }, "myapp"));
        }

        /// <summary>The manager's records carry the display name whole: "The {0} service entered the {1} state."</summary>
        [Fact]
        public void TheManagersRecordNamingTheServiceIsAboutIt()
        {
            Assert.True(EventLogReader.IsFromScm("Service Control Manager", new[] { "My App", "stopped" }, "myapp", "My App"));
            Assert.True(EventLogReader.IsFromScm("Service Control Manager", new[] { "30000", "my app " }, "myapp", "My App"));

            // The start-type change carries the service's name as well as its display name.
            Assert.True(EventLogReader.IsFromScm("Service Control Manager", new[] { "Renamed", "demand start", "auto start", "myapp" }, "myapp", "Renamed Since"));
        }

        /// <summary>Found anywhere in the text, "api" was every service with "api" in its name.</summary>
        [Fact]
        public void ANameInsideAnotherIsNotTheService()
        {
            Assert.False(EventLogReader.IsFromScm("Service Control Manager", new[] { "Windows Update API", "running" }, "api", "api"));
        }

        /// <summary>An empty display name, a desktop task's say, matched every record the manager wrote.</summary>
        [Fact]
        public void AnEmptyDisplayNameMatchesNothing()
        {
            Assert.False(EventLogReader.IsFromScm("Service Control Manager", new[] { string.Empty, "running" }, "myapp", string.Empty));
            Assert.False(EventLogReader.IsFromScm("Service Control Manager", new[] { "  ", "running" }, "myapp", " "));
        }

        [Fact]
        public void AnotherSourceInTheSystemLogIsNot()
        {
            Assert.False(EventLogReader.IsFromScm("Schannel", new[] { "My App" }, "myapp", "My App"));
            Assert.False(EventLogReader.IsFromScm("Service Control Manager", new string?[] { null }, "myapp", "My App"));
        }

        [Fact]
        public void TwoLogsSearchedToTheirStartAreSearchedToTheirStart()
        {
            var both = EventScan.Combine(new EventScan(120, null), new EventScan(900, null));

            Assert.Null(both.CutShortAt);
            Assert.Equal(1020, both.Examined);
        }

        /// <summary>A log searched to its start covers any time; the other one's cut is the one that counts.</summary>
        [Fact]
        public void OneLogCutShortIsWhereTheSearchWasCutShort()
        {
            Assert.Equal(Monday, EventScan.Combine(new EventScan(120, null), new EventScan(4000, Monday)).CutShortAt);
            Assert.Equal(Monday, EventScan.Combine(new EventScan(4000, Monday), new EventScan(120, null)).CutShortAt);
        }

        /// <summary>Nothing was passed over in either log from the later of the two times on.</summary>
        [Fact]
        public void BothCutShortIsTheLaterOfTheTwo()
        {
            var both = EventScan.Combine(new EventScan(4000, Tuesday), new EventScan(4000, Monday));

            Assert.Equal(Tuesday, both.CutShortAt);
            Assert.Equal(8000, both.Examined);
            Assert.Equal(Tuesday, EventScan.Combine(new EventScan(4000, Monday), new EventScan(4000, Tuesday)).CutShortAt);
        }
    }
}

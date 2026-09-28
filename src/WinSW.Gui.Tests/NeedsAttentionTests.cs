using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel;
using System.Globalization;
using System.ServiceProcess;
using WinSW.Gui.Localization;
using WinSW.Gui.Model;
using WinSW.Gui.Services;
using Xunit;

namespace WinSW.Gui.Tests
{
    /// <summary>
    /// Which services the Needs attention card counts, where "sort by status" puts them, and what
    /// the row's tooltip says, in the real dictionaries; and that the row colour, which is by
    /// <see cref="ServiceEntry.Health"/>, is not changed by any of it.
    /// </summary>
    public class NeedsAttentionTests
    {
        private static readonly DateTime T0 = new(2026, 9, 24, 10, 0, 0, DateTimeKind.Utc);

        private static readonly RecoverySettings RestartAfterTenSeconds =
            new(ImmutableArray.Create(new RecoveryAction(RecoveryActionKind.Restart, TimeSpan.FromSeconds(10))), TimeSpan.FromDays(1), false);

        private static readonly RecoverySettings NoRecovery =
            new(ImmutableArray<RecoveryAction>.Empty, TimeSpan.FromDays(1), false);

        // Each reason -------------------------------------------------------------------------

        /// <summary>What the card counted before, still counted, and said as the detail panel's banner says it.</summary>
        [Fact]
        public void AnUnusableConfigurationNeedsAttention()
        {
            var entry = Service(ServiceStartMode.Manual);
            Read(entry, ServiceControllerStatus.Running, 0);
            entry.Problem = "The configuration file 'C:/svc/api.xml' does not exist.";

            Assert.Equal(AttentionReasons.ConfigProblem, entry.Attention);
            Assert.Equal(ServiceHealth.Broken, entry.Health);
            Assert.Equal("The configuration file 'C:/svc/api.xml' does not exist.", Describe(entry, "en"));
        }

        /// <summary>A service running beside what an earlier run left, or with its port held, reads as plain Running.</summary>
        [Fact]
        public void AProgramLeftRunningNeedsAttentionWhileTheServiceRuns()
        {
            var entry = Service(ServiceStartMode.Automatic);
            Read(entry, ServiceControllerStatus.Running, 0);

            // Something outside every wrapper on the service's port is shown at once.
            entry.NoteStray(new StrayFinding(new ProcessMark(4312, T0, "python.exe"), null, 8000), T0);

            Assert.Equal(AttentionReasons.StrayProcess, entry.Attention);
            Assert.Equal(ServiceHealth.Running, entry.Health);
            Assert.Equal("Port 8000 is held by python.exe (PID 4312).", Describe(entry, "en"));

            entry.NoteStray(null, T0.AddSeconds(2));
            Assert.False(entry.NeedsAttention);
        }

        /// <summary>The crash is counted, as the dashboard hands it over; its exit code and start type are not said again.</summary>
        [Fact]
        public void AServiceWindowsIsAboutToRestartIsNotAlsoFlaggedForItsStop()
        {
            var entry = Service(ServiceStartMode.Automatic, RestartAfterTenSeconds);
            Read(entry, ServiceControllerStatus.Running, 0, T0);
            Read(entry, ServiceControllerStatus.Stopped, RecoverySettings.ProcessAborted, T0.AddSeconds(2), crashCount: 1);
            entry.RecentStops = 1;
            Assert.True(entry.IsRestartingByRecovery);

            // Its exit code and its start type describe the stop the restart is about to end.
            Assert.Equal(AttentionReasons.RestartingByRecovery | AttentionReasons.RecentStops, entry.Attention);
            Assert.Equal(ServiceHealth.Pending, entry.Health);
            Assert.Equal("出错停止，Windows 服务恢复即将再次启动它。\n最近几分钟内意外停止了 1 次。", Describe(entry, "zh-CN"));
        }

        /// <summary>A crash that Windows restarted from reads as Running; the count says it happened.</summary>
        [Fact]
        public void RecentStopsNeedAttentionWhileTheServiceRunsAgain()
        {
            var entry = Service(ServiceStartMode.Automatic);
            Read(entry, ServiceControllerStatus.Running, 0);

            entry.RecentStops = 3;

            Assert.Equal(AttentionReasons.RecentStops, entry.Attention);
            Assert.Equal(ServiceHealth.Running, entry.Health);
            Assert.Equal("Stopped unexpectedly 3 time(s) in the last few minutes.", Describe(entry, "en"));
            Assert.Equal("最近几分钟内意外停止了 3 次。", Describe(entry, "zh-CN"));

            entry.RecentStops = 0;
            Assert.False(entry.NeedsAttention);
        }

        [Fact]
        public void AManualServiceStoppedWithAFailureCodeNeedsAttention()
        {
            var entry = Service(ServiceStartMode.Manual, NoRecovery);
            Read(entry, ServiceControllerStatus.Running, 0, T0);
            Read(entry, ServiceControllerStatus.Stopped, RecoverySettings.ProcessAborted, T0.AddSeconds(2));

            Assert.Equal(AttentionReasons.StoppedWithError, entry.Attention);
            Assert.Equal(ServiceHealth.Stopped, entry.Health);
            Assert.Equal("Stopped with exit code 1067. <text for 1067>", Describe(entry, "en"));
            Assert.Equal("已停止，退出码 1067。<text for 1067>", Describe(entry, "zh-CN"));
        }

        /// <summary>A code Windows has no text for leaves no space dangling at the end of the line.</summary>
        [Fact]
        public void AnExitCodeWithoutWindowsTextEndsTheLine()
        {
            var entry = Service(ServiceStartMode.Manual, NoRecovery);
            Read(entry, ServiceControllerStatus.Stopped, 3);

            string line = entry.DescribeAttention(FormatIn("en"), _ => string.Empty);

            Assert.Equal("Stopped with exit code 3.", line);
        }

        /// <summary>1077 is what a service nobody has started since the machine booted reports: nothing wrong with a manual one.</summary>
        [Fact]
        public void AManualServiceNeverStartedSinceBootIsLeftAlone()
        {
            var entry = Service(ServiceStartMode.Manual);
            Read(entry, ServiceControllerStatus.Stopped, 1077);

            Assert.False(entry.NeedsAttention);
            Assert.Null(entry.AttentionText);
            Assert.Equal(2, entry.SortRank);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AnAutomaticServiceThatIsStoppedNeedsAttention(bool delayed)
        {
            var entry = Service(ServiceStartMode.Automatic);
            entry.DelayedAutoStart = delayed;
            Read(entry, ServiceControllerStatus.Stopped, 0);

            Assert.Equal(AttentionReasons.AutomaticButStopped, entry.Attention);
            Assert.Equal(ServiceHealth.Stopped, entry.Health);
            Assert.Equal("启动类型为自动，但当前已停止。", Describe(entry, "zh-CN"));
        }

        /// <summary>Stopped until somebody wants it, or stopped on purpose: nothing to look at.</summary>
        [Theory]
        [InlineData(ServiceStartMode.Manual)]
        [InlineData(ServiceStartMode.Disabled)]
        public void AManualOrDisabledServiceStoppedCleanlyIsLeftAlone(ServiceStartMode startType)
        {
            var entry = Service(startType);
            Read(entry, ServiceControllerStatus.Stopped, 0);

            Assert.Equal(AttentionReasons.None, entry.Attention);
            Assert.Null(entry.AttentionText);
        }

        [Theory]
        [InlineData(ServiceControllerStatus.Running)]
        [InlineData(ServiceControllerStatus.StartPending)]
        [InlineData(ServiceControllerStatus.StopPending)]
        public void AnAutomaticServiceThatIsNotStoppedIsLeftAlone(ServiceControllerStatus status)
        {
            var entry = Service(ServiceStartMode.Automatic);

            // The exit code of an earlier stop stays with a service Windows is starting again.
            Read(entry, status, RecoverySettings.ProcessAborted);

            Assert.Equal(AttentionReasons.None, entry.Attention);
        }

        [Fact]
        public void AServiceThatCouldNotBeReadIsLeftAlone()
        {
            var entry = Service(ServiceStartMode.Automatic);

            ServiceDiscovery.ApplyStatus(entry, new ServiceSample { Queried = false });

            Assert.Null(entry.Status);
            Assert.False(entry.NeedsAttention);
        }

        /// <summary>Every reason that holds is said, one line each, in a fixed order.</summary>
        [Fact]
        public void EveryReasonIsSaidOnALineOfItsOwn()
        {
            var entry = Service(ServiceStartMode.Automatic, NoRecovery);
            Read(entry, ServiceControllerStatus.Running, 0, T0);
            Read(entry, ServiceControllerStatus.Stopped, RecoverySettings.ProcessAborted, T0.AddSeconds(2));
            entry.RecentStops = 1;

            Assert.Equal(
                AttentionReasons.RecentStops | AttentionReasons.StoppedWithError | AttentionReasons.AutomaticButStopped,
                entry.Attention);
            Assert.Equal(
                "Stopped unexpectedly 1 time(s) in the last few minutes.\nStopped with exit code 1067. <text for 1067>\nSet to start automatically, but stopped.",
                Describe(entry, "en"));
        }

        // Where it is counted and sorted ------------------------------------------------------

        /// <summary>What needs a look comes first whatever its state; the rest keep the order they had.</summary>
        [Fact]
        public void SortingByStatusPutsWhatNeedsAttentionFirst()
        {
            var looping = Service(ServiceStartMode.Automatic);
            Read(looping, ServiceControllerStatus.Running, 0);
            looping.RecentStops = 4;

            var starting = Service(ServiceStartMode.Manual);
            Read(starting, ServiceControllerStatus.StartPending, 0);

            var stopped = Service(ServiceStartMode.Manual);
            Read(stopped, ServiceControllerStatus.Stopped, 0);

            var running = Service(ServiceStartMode.Automatic);
            Read(running, ServiceControllerStatus.Running, 0);

            var unread = Service(ServiceStartMode.Automatic);

            var broken = Service(ServiceStartMode.Manual);
            broken.Problem = "No configuration.";

            Assert.Equal(0, looping.SortRank);
            Assert.Equal(0, broken.SortRank);
            Assert.Equal(1, starting.SortRank);
            Assert.Equal(2, stopped.SortRank);
            Assert.Equal(3, running.SortRank);
            Assert.Equal(4, unread.SortRank);
        }

        /// <summary>The count, the row and live sorting all hear of a change in anything the answer is made from.</summary>
        [Fact]
        public void EveryChangeTheAnswerIsMadeFromIsAnnounced()
        {
            AssertAnnounced(e => e.Problem = "No configuration.");
            AssertAnnounced(e => e.RecentStops = 2);
            AssertAnnounced(e => e.StartType = ServiceStartMode.Automatic);
            AssertAnnounced(e => e.LastExitCode = 5);
            AssertAnnounced(e => e.Status = ServiceControllerStatus.Running);
            AssertAnnounced(e => e.NoteStray(new StrayFinding(new ProcessMark(4312, T0, "python.exe"), null, 8000), T0));
            AssertAnnounced(e =>
            {
                // Windows' recovery taking the stop on: the one reason set by the entry itself.
                e.Recovery = RestartAfterTenSeconds;
                Read(e, ServiceControllerStatus.Running, 0, T0);
                Read(e, ServiceControllerStatus.Stopped, RecoverySettings.ProcessAborted, T0.AddSeconds(2), crashCount: 1);
                Assert.True(e.IsRestartingByRecovery);
            });

            static void AssertAnnounced(Action<ServiceEntry> change)
            {
                var entry = Service(ServiceStartMode.Manual);
                Read(entry, ServiceControllerStatus.Stopped, 0);

                var raised = new List<string?>();
                ((INotifyPropertyChanged)entry).PropertyChanged += (_, e) => raised.Add(e.PropertyName);
                change(entry);

                Assert.Contains(nameof(ServiceEntry.NeedsAttention), raised);
                Assert.Contains(nameof(ServiceEntry.AttentionText), raised);
                Assert.Contains(nameof(ServiceEntry.SortRank), raised);
            }
        }

        // The words ----------------------------------------------------------------------------

        /// <summary>
        /// The keys are chosen by the reasons, not named in a <c>Localizer.Get("…")</c> that
        /// <see cref="LocalizationTests"/> would find, so each one is looked for here, and filled in
        /// with the placeholders the code hands it.
        /// </summary>
        [Fact]
        public void EveryReasonIsWordedInEveryLanguage()
        {
            var own = new[]
            {
                AttentionReasons.RestartingByRecovery,
                AttentionReasons.RecentStops,
                AttentionReasons.StoppedWithError,
                AttentionReasons.AutomaticButStopped,
            };

            foreach (var language in Localizer.Languages)
            {
                foreach (var reason in own)
                {
                    string line = DescribeFlag(reason, language.Code);
                    Assert.False(string.IsNullOrWhiteSpace(line));
                    Assert.DoesNotContain("{", line);
                }
            }
        }

        // Helpers ------------------------------------------------------------------------------

        private static ServiceEntry Service(ServiceStartMode startType, RecoverySettings? recovery = null) =>
            new("api", "API", "C:/bin/WinSW.exe", "C:/svc/api.xml")
            {
                StartType = startType,
                Recovery = recovery ?? NoRecovery,
            };

        /// <summary>One reading, as the dashboard hands it over: the state, then the crash count, then the time.</summary>
        private static void Read(ServiceEntry entry, ServiceControllerStatus status, int exitCode, DateTime? now = null, int crashCount = 0)
        {
            ServiceDiscovery.ApplyStatus(entry, new ServiceSample { Queried = true, Status = status, LastExitCode = exitCode });
            entry.CrashCount = crashCount;
            entry.NoteRecovery(now ?? T0);
        }

        private static string Describe(ServiceEntry entry, string language) =>
            entry.DescribeAttention(FormatIn(language), code => $"<text for {code}>");

        /// <summary>The line for <paramref name="reason"/> alone, from a service in the state that gives it.</summary>
        private static string DescribeFlag(AttentionReasons reason, string language)
        {
            var entry = Service(reason == AttentionReasons.AutomaticButStopped ? ServiceStartMode.Automatic : ServiceStartMode.Manual);
            switch (reason)
            {
                case AttentionReasons.RestartingByRecovery:
                    entry.Recovery = RestartAfterTenSeconds;
                    Read(entry, ServiceControllerStatus.Running, 0, T0);
                    Read(entry, ServiceControllerStatus.Stopped, RecoverySettings.ProcessAborted, T0.AddSeconds(2), crashCount: 1);
                    break;
                case AttentionReasons.RecentStops:
                    Read(entry, ServiceControllerStatus.Running, 0);
                    entry.RecentStops = 2;
                    break;
                case AttentionReasons.StoppedWithError:
                    Read(entry, ServiceControllerStatus.Stopped, RecoverySettings.ProcessAborted);
                    break;
                case AttentionReasons.AutomaticButStopped:
                    Read(entry, ServiceControllerStatus.Stopped, 0);
                    break;
            }

            Assert.Equal(reason, entry.Attention);
            return Describe(entry, language);
        }

        private static Func<string, object?[], string> FormatIn(string code)
        {
            var values = StringDictionaries.ValuesOf(code);
            return (key, args) => string.Format(CultureInfo.InvariantCulture, values[key], args);
        }
    }
}

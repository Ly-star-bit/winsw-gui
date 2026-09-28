using System.Collections.Generic;
using System.ServiceProcess;
using WinSW.Gui.Model;
using Xunit;

namespace WinSW.Gui.Tests
{
    /// <summary>
    /// A disabled service is not offered Start or Restart: Windows refuses to start it with
    /// error 1058 whoever asks, and a restart would stop it first.
    /// </summary>
    public class DisabledServiceStartTests
    {
        [Fact]
        public void AStoppedDisabledServiceCannotBeStarted()
        {
            var entry = Entry(ServiceControllerStatus.Stopped, ServiceStartMode.Disabled);

            Assert.False(entry.IsStartable);
            Assert.False(entry.CanStart);
        }

        [Theory]
        [InlineData(ServiceStartMode.Automatic)]
        [InlineData(ServiceStartMode.Manual)]
        [InlineData(null)]
        public void AStoppedServiceOfAnyOtherStartTypeCanBeStarted(ServiceStartMode? startType)
        {
            var entry = Entry(ServiceControllerStatus.Stopped, startType);

            Assert.True(entry.IsStartable);
            Assert.True(entry.CanStart);
        }

        /// <summary>Running, Start was never offered; being disabled keeps Restart from it as well.</summary>
        [Fact]
        public void ARunningDisabledServiceIsNotStartable()
        {
            var entry = Entry(ServiceControllerStatus.Running, ServiceStartMode.Disabled);

            Assert.False(entry.CanStart);
            Assert.False(entry.IsStartable);
            Assert.True(entry.CanStop);
        }

        /// <summary>No tooltip at all, rather than an empty one, while nothing stands in the way.</summary>
        [Theory]
        [InlineData(ServiceStartMode.Automatic)]
        [InlineData(ServiceStartMode.Manual)]
        [InlineData(null)]
        public void AStartableServiceHasNoReasonGiven(ServiceStartMode? startType)
        {
            Assert.Null(Entry(ServiceControllerStatus.Stopped, startType).StartUnavailableTip);
        }

        /// <summary>
        /// The start type changes only with a rescan, which merges into the row on screen: the
        /// button and its tooltip have to hear about it then, not at the next status change.
        /// </summary>
        [Fact]
        public void ARescanThatDisablesTheServiceTellsTheButtons()
        {
            var entry = Entry(ServiceControllerStatus.Stopped, ServiceStartMode.Automatic);
            var raised = new List<string?>();
            entry.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            entry.MergeMetadataFrom(new ServiceEntry("demo", "demo", @"C:\svc\winsw.exe", @"C:\svc\demo.xml") { StartType = ServiceStartMode.Disabled });

            Assert.False(entry.CanStart);
            Assert.Contains(nameof(ServiceEntry.CanStart), raised);
            Assert.Contains(nameof(ServiceEntry.IsStartable), raised);
            Assert.Contains(nameof(ServiceEntry.StartUnavailableTip), raised);
        }

        private static ServiceEntry Entry(ServiceControllerStatus status, ServiceStartMode? startType) =>
            new("demo", "demo", @"C:\svc\winsw.exe", @"C:\svc\demo.xml")
            {
                Status = status,
                StartType = startType,
            };
    }
}

using System;
using System.IO;
using WinSW.Gui.Model;
using WinSW.Gui.ViewModels;
using Xunit;

namespace WinSW.Gui.Tests
{
    /// <summary>
    /// Every log mode the wizard offers has to come out as a configuration the wrapper will
    /// start from. roll-by-time used not to: the wrapper refuses it without a pattern, the
    /// wizard had no field for one, and choosing it left a wizard that could not be finished.
    /// </summary>
    [Collection("install root")]
    public sealed class WizardLogModesTests : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "winsw-gui-" + Guid.NewGuid().ToString("n"));
        private readonly string program;

        public WizardLogModesTests()
        {
            Directory.CreateDirectory(this.directory);
            this.program = Path.Combine(this.directory, "server.exe");
            File.WriteAllText(this.program, string.Empty);
        }

        public void Dispose() => Directory.Delete(this.directory, recursive: true);

        /// <summary>The review step refuses to install while this list has anything in it.</summary>
        [Fact]
        public void EveryOfferedLogModeValidates()
        {
            var wizard = this.NewWizard();

            foreach (string mode in wizard.LogModes)
            {
                wizard.LogMode = mode;
                var problems = wizard.BuildModel().Validate();
                Assert.True(problems.Count == 0, mode + ": " + string.Join(" | ", problems));
            }
        }

        /// <summary>
        /// What each mode needs is filled in, and nothing another mode needs comes with it: a
        /// pattern for roll-by-time, a threshold for roll-by-size, a count for both.
        /// </summary>
        [Fact]
        public void EachOfferedLogModeCarriesWhatItNeeds()
        {
            var wizard = this.NewWizard();

            foreach (string mode in wizard.LogModes)
            {
                wizard.LogMode = mode;
                var model = wizard.BuildModel();

                Assert.Equal(mode, model.LogMode);
                Assert.Equal(mode == "roll-by-time" ? WizardViewModel.DailyRollPattern : null, model.RollPattern);
                Assert.Equal(mode == "roll-by-size" ? "10240" : null, model.SizeThreshold);
                Assert.Equal(mode switch { "roll-by-time" => "30", "roll-by-size" => "8", _ => null }, model.KeepFiles);
                Assert.True(!model.UsesTimePattern || !string.IsNullOrWhiteSpace(model.RollPattern), mode);
            }
        }

        /// <summary>
        /// roll-by-time is written as one file a day and a month of them: the pattern the
        /// wrapper rolls on and the count it deletes beyond, read back from the file itself.
        /// </summary>
        [Fact]
        public void RollByTimeWritesADailyFileAndKeepsAMonth()
        {
            var wizard = this.NewWizard();
            wizard.LogMode = "roll-by-time";

            var written = ServiceConfigModel.FromXml(wizard.BuildModel().ToXmlString(), null);

            Assert.Equal("roll-by-time", written.LogMode);
            Assert.Equal("yyyyMMdd", written.RollPattern);
            Assert.Equal("30", written.KeepFiles);
            Assert.Null(written.RollPeriod);
            Assert.Null(written.SizeThreshold);
        }

        /// <summary>
        /// Days and files are two fields: switching the mode back and forth keeps what was
        /// typed into each, and each mode writes its own.
        /// </summary>
        [Fact]
        public void DaysToKeepAndFilesToKeepAreSeparate()
        {
            var wizard = this.NewWizard();
            wizard.KeepFiles = "5";
            wizard.LogMode = "roll-by-time";
            wizard.KeepDays = "14";

            Assert.Equal("14", wizard.BuildModel().KeepFiles);

            wizard.LogMode = "roll-by-size";
            Assert.Equal("5", wizard.BuildModel().KeepFiles);
            Assert.Null(wizard.BuildModel().RollPattern);

            wizard.LogMode = "roll-by-time";
            Assert.Equal("14", wizard.KeepDays);
            Assert.Equal("5", wizard.KeepFiles);
        }

        /// <summary>An empty count is the wrapper's own default, which keeps every file; it is still a valid pattern.</summary>
        [Fact]
        public void AnEmptyDaysToKeepStillWritesThePattern()
        {
            var wizard = this.NewWizard();
            wizard.LogMode = "roll-by-time";
            wizard.KeepDays = " ";

            var model = wizard.BuildModel();
            Assert.Equal(WizardViewModel.DailyRollPattern, model.RollPattern);
            Assert.Null(model.KeepFiles);
        }

        /// <summary>
        /// Cloning a time-rolled service carries its count into the days field and keeps the
        /// pattern it rolled on, so the count means what it meant, and leaves the size fields
        /// at their own defaults.
        /// </summary>
        [Fact]
        public void CloningATimeRolledServiceCarriesItsCountAsDays()
        {
            string config = Path.Combine(this.directory, "source.xml");
            File.WriteAllText(
                config,
                "<service><id>source</id><executable>" + this.program + "</executable>"
                + "<log mode=\"roll-by-time\"><pattern>yyyy-MM-dd</pattern><keepFiles>90</keepFiles></log></service>");

            var wizard = new WizardViewModel
            {
                CloneSource = new ServiceEntry("source", "Source", Path.Combine(this.directory, "WinSW.exe"), config),
            };

            Assert.Equal("roll-by-time", wizard.LogMode);
            Assert.Equal("90", wizard.KeepDays);
            Assert.Equal("8", wizard.KeepFiles);

            var model = wizard.BuildModel();
            Assert.Equal("yyyy-MM-dd", model.RollPattern);
            Assert.Equal("90", model.KeepFiles);
        }

        private WizardViewModel NewWizard() => new() { TargetPath = this.program, ServiceId = "demo" };
    }
}

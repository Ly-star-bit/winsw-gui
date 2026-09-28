using System;
using System.Globalization;
using WinSW.Gui.Model;
using Xunit;

namespace WinSW.Gui.Tests
{
    /// <summary>
    /// The date patterns the wrapper cannot name its log files with. It reads any pattern without
    /// a word and applies it only once the service is running, in the task that copies the
    /// program's output: a pattern that fails there leaves nothing reading the output, and the
    /// program hangs on a full pipe while the service still shows Running.
    /// </summary>
    /// <remarks>
    /// All but the last test ask <see cref="ServiceConfigModel.CheckRollPattern"/> directly, since
    /// <c>Validate</c> reads its messages from the WPF dictionaries.
    /// </remarks>
    public class RollPatternTests
    {
        [Theory]
        [InlineData("yyyyMMdd")]
        [InlineData("yyyy-MM-dd")]
        [InlineData("yyyy-MM-dd_HH-mm")]
        [InlineData("'app'-yyyyMMdd")]
        public void ADailyPatternThatMakesAFileNameIsFine(string pattern)
        {
            Assert.Equal(RollPatternFault.None, ServiceConfigModel.CheckRollPattern(pattern, rollsByTime: true, out _));
            Assert.Equal(RollPatternFault.None, ServiceConfigModel.CheckRollPattern(pattern, rollsByTime: false, out _));
        }

        /// <summary>
        /// The wrapper's calendar tries a millisecond, a second, a minute, an hour and a day, and
        /// takes the first step that changes the name: an hour of day on its own is an hourly roll,
        /// not a mistake, and the editor must not refuse what the wrapper accepts.
        /// </summary>
        [Theory]
        [InlineData("HH")]
        [InlineData("yyyyMMddHH")]
        [InlineData("dd")]
        public void APatternTheWrapperRollsHourlyOrDailyIsAccepted(string pattern)
        {
            Assert.Equal(RollPatternFault.None, ServiceConfigModel.CheckRollPattern(pattern, rollsByTime: true, out _));
        }

        /// <summary>
        /// A monthly pattern, or one with no date in it at all, never changes from one day to the
        /// next, and the wrapper's calendar throws on it at start.
        /// </summary>
        [Theory]
        [InlineData("yyyyMM")]
        [InlineData("yyyy")]
        [InlineData("MMMM")]
        [InlineData("'log'")]
        public void RollByTimeRefusesAPatternThatChangesLessThanDaily(string pattern)
        {
            Assert.Equal(RollPatternFault.ChangesTooRarely, ServiceConfigModel.CheckRollPattern(pattern, rollsByTime: true, out _));
        }

        /// <summary>Roll-by-size-time rolls on size and only names the files by the date.</summary>
        [Theory]
        [InlineData("yyyyMM")]
        [InlineData("yyyy")]
        public void RollBySizeTimeTakesAMonthlyPattern(string pattern)
        {
            Assert.Equal(RollPatternFault.None, ServiceConfigModel.CheckRollPattern(pattern, rollsByTime: false, out _));
        }

        /// <summary>
        /// The formatted date goes into the file name, where Windows refuses these characters. The
        /// single letter <c>d</c> is the short date, which has slashes in it.
        /// </summary>
        [Theory]
        [InlineData("yyyy/MM/dd", '/')]
        [InlineData("yyyyMMdd HH:mm", ':')]
        [InlineData("'logs'\\\\yyyyMMdd", '\\')]
        [InlineData("yyyyMMdd'?'", '?')]
        [InlineData("yyyyMMdd'*'", '*')]
        [InlineData("yyyyMMdd'<'", '<')]
        [InlineData("yyyyMMdd'|'", '|')]
        [InlineData("d", '/')]
        public void APatternThatMakesNoFileNameIsRefusedInEitherMode(string pattern, char offending)
        {
            Assert.Equal(RollPatternFault.NotAFileName, ServiceConfigModel.CheckRollPattern(pattern, rollsByTime: true, out string? name));
            Assert.NotNull(name);
            Assert.Contains(offending, name!);

            Assert.Equal(RollPatternFault.NotAFileName, ServiceConfigModel.CheckRollPattern(pattern, rollsByTime: false, out _));
        }

        /// <summary>
        /// In a custom format '/' and ':' stand for the culture's separators, which are '.' in
        /// German. The wrapper formats in the service account's culture, which need not be the
        /// desktop's, so a slash is judged as a slash whatever the console runs in.
        /// </summary>
        [Fact]
        public void SeparatorsAreJudgedAsWrittenWhateverTheCulture()
        {
            var saved = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
                Assert.Equal(RollPatternFault.NotAFileName, ServiceConfigModel.CheckRollPattern("yyyy/MM/dd", rollsByTime: true, out string? name));
                Assert.NotNull(name);
                Assert.Contains('/', name!);
            }
            finally
            {
                CultureInfo.CurrentCulture = saved;
            }
        }

        [Theory]
        [InlineData("%")]
        [InlineData("k")]
        [InlineData("yyyyMMdd\\")]
        [InlineData("yyyyMMdd'")]
        public void APatternThatIsNoDateFormatIsRefusedInEitherMode(string pattern)
        {
            Assert.Equal(RollPatternFault.NotADateFormat, ServiceConfigModel.CheckRollPattern(pattern, rollsByTime: true, out string? name));
            Assert.Null(name);

            Assert.Equal(RollPatternFault.NotADateFormat, ServiceConfigModel.CheckRollPattern(pattern, rollsByTime: false, out _));
        }

        /// <summary>A pattern with both faults is reported for the file name, the one it names.</summary>
        [Fact]
        public void AMonthlyPatternWithSlashesIsReportedForTheSlashes()
        {
            Assert.Equal(RollPatternFault.NotAFileName, ServiceConfigModel.CheckRollPattern("yyyy/MM", rollsByTime: true, out _));
        }

        [Fact]
        public void TheNameIsThePatternAppliedToNow()
        {
            Assert.Equal(RollPatternFault.None, ServiceConfigModel.CheckRollPattern("yyyy", rollsByTime: false, out string? name));
            Assert.Equal(DateTime.Now.Year.ToString(CultureInfo.InvariantCulture), name);
        }

        /// <summary>
        /// The check reaches the date pattern box, for the modes that use a pattern. Windows only:
        /// <c>Validate</c> reads its messages through WPF.
        /// </summary>
        [Fact]
        public void ValidateMarksThePatternForTheModesThatUseIt()
        {
            var model = ServiceConfigModel.CreateNew();
            model.Id = "demo";
            model.Executable = "demo.exe";
            model.LogMode = "roll-by-time";
            model.RollPattern = "yyyyMM";

            Assert.Single(model.Validate());
            Assert.NotNull(model.FieldErrors[nameof(ServiceConfigModel.RollPattern)]);

            model.LogMode = "roll-by-size-time";
            Assert.Empty(model.Validate());

            model.RollPattern = "yyyy/MM";
            Assert.Single(model.Validate());
            Assert.NotNull(model.FieldErrors[nameof(ServiceConfigModel.RollPattern)]);

            // A mode without a pattern leaves whatever is in the box alone.
            model.LogMode = "append";
            Assert.Empty(model.Validate());
        }
    }
}

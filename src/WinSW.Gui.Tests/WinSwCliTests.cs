using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using WinSW.Gui.Services;
using Xunit;

namespace WinSW.Gui.Tests
{
    /// <summary>
    /// The command-building half of <see cref="WinSwCli"/>. What it does with the command
    /// afterwards needs an elevation prompt and a service, so it is not tested here.
    /// </summary>
    public class WinSwCliTests
    {
        [Fact]
        public void NothingToDoIsOneEmptyResultAndNoScript()
        {
            Assert.Empty(WinSwCli.Chunk(new List<string>()));
        }

        [Fact]
        public void AShortBatchIsOneScriptAndSoOnePrompt()
        {
            var steps = Enumerable.Range(0, 5).Select(i => $@"""C:\winsw.exe"" stop ""C:\svc{i}.xml"" --no-elevate").ToList();

            var scripts = WinSwCli.Chunk(steps);

            Assert.Single(scripts);
            foreach (string step in steps)
            {
                Assert.Contains(step, scripts[0]);
            }
        }

        /// <summary>
        /// cmd refuses a command line past 8191 characters, and does not say so in a way the
        /// exit code carries. Thirty services under one install root is an ordinary number,
        /// and stop-copy-start makes two steps of each.
        /// </summary>
        [Fact]
        public void ALongBatchIsSplitBelowWhatCmdWillAccept()
        {
            var steps = Enumerable.Range(0, 200)
                .Select(i => $@"""C:\Program Files\WinSW\bin\WinSW.exe"" stop ""C:\ProgramData\WinSW\service-{i:D4}\service-{i:D4}.xml"" --no-elevate")
                .ToList();

            var scripts = WinSwCli.Chunk(steps);

            Assert.True(scripts.Count > 1);
            Assert.All(scripts, s => Assert.True(s.Length <= WinSwCli.MaxScriptLength, $"script of {s.Length} characters"));
        }

        /// <summary>
        /// Splitting must not drop or reorder a step: for an upgrade the copy has to stay
        /// between every stop and every start.
        /// </summary>
        [Fact]
        public void SplittingKeepsEveryStepAndItsOrder()
        {
            var steps = Enumerable.Range(0, 300).Select(i => $"step-{i:D4} " + new string('x', 40)).ToList();

            var rejoined = string.Join(" & ", WinSwCli.Chunk(steps));

            Assert.Equal(string.Join(" & ", steps), rejoined);
        }

        [Fact]
        public void OneStepTooLongToFitIsStillEmittedRatherThanCutInHalf()
        {
            var steps = new List<string> { new('x', WinSwCli.MaxScriptLength * 2) };

            var scripts = WinSwCli.Chunk(steps);

            Assert.Single(scripts);
            Assert.Equal(steps[0], scripts[0]);
        }

        [Fact]
        public void ThePromptCountIsTheNumberOfScripts()
        {
            var many = Enumerable.Range(0, 200)
                .Select(i => (Wrapper: @"C:\Program Files\WinSW\bin\WinSW.exe", ConfigPath: $@"C:\ProgramData\WinSW\service-{i:D4}\service-{i:D4}.xml"))
                .ToList();

            Assert.Equal(1, WinSwCli.PromptCountFor("stop", many.Take(3)));
            Assert.True(WinSwCli.PromptCountFor("stop", many) > 1);
        }

        /// <summary>
        /// cmd substitutes %NAME% even inside double quotes, and a command line offers no way
        /// to escape it. Running the command against a path that is not the one the user chose
        /// is the outcome being avoided.
        /// </summary>
        [Theory]
        [InlineData(@"C:\svc\%TEMP%\my.xml")]
        [InlineData(@"C:\%ProgramData%\my.xml")]
        [InlineData(@"C:\a%x%b\my.xml")]
        public void APathCmdWouldRewriteIsRefused(string configPath)
        {
            var refusal = WinSwCli.RejectExpandablePaths(new[] { configPath });

            Assert.NotNull(refusal);
            Assert.False(refusal!.Succeeded);
        }

        /// <summary>
        /// A lone percent, and a pair around something that is not a variable name, are left
        /// alone by cmd — and so must be left alone here, or ordinary paths get refused.
        /// </summary>
        [Theory]
        [InlineData(@"C:\100% done\my.xml")]
        [InlineData(@"C:\50%\my.xml")]
        [InlineData(@"C:\a%-%b\my.xml")]
        [InlineData(@"C:\Program Files\WinSW\my.xml")]
        public void APathCmdWouldLeaveAloneIsNotRefused(string configPath)
        {
            Assert.Null(WinSwCli.RejectExpandablePaths(new[] { configPath }));
        }

        /// <summary>
        /// Nothing to run means nothing to prompt for. Asserted because the alternative is an
        /// elevation prompt raised over an empty selection.
        /// </summary>
        [Fact]
        public async Task AnEmptyBatchSucceedsWithoutPrompting()
        {
            var result = await WinSwCli.RunOnManyAsync("stop", System.Array.Empty<(string, string)>());

            Assert.True(result.Succeeded);
        }

        [Fact]
        public async Task ACommandAgainstAMissingWrapperFailsBeforeAnyPrompt()
        {
            var result = await WinSwCli.StartAsync(@"C:\definitely\not\here\WinSW.exe", @"C:\svc.xml");

            Assert.False(result.Succeeded);
            Assert.False(result.Cancelled);
        }
    }
}

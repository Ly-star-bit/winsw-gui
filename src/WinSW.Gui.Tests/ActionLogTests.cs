using System;
using System.IO;
using WinSW.Gui.Services;
using Xunit;

namespace WinSW.Gui.Tests
{
    /// <summary>
    /// The action log is read by people and loaded by spreadsheets, so its one promise is
    /// shape: one operation, one line, five tab-separated fields.
    /// </summary>
    public class ActionLogTests
    {
        [Fact]
        public void OneOperationIsOneLineOfFiveFields()
        {
            string line = ActionLog.Format(
                new DateTime(2026, 9, 22, 20, 15, 3),
                @"CORP\henry",
                "stop",
                "demo",
                "failed: the service did not respond\r\nin time\tat all");

            Assert.DoesNotContain('\n', line);
            Assert.DoesNotContain('\r', line);

            string[] fields = line.Split('\t');
            Assert.Equal(5, fields.Length);
            Assert.Equal("2026-09-22 20:15:03", fields[0]);
            Assert.Equal(@"CORP\henry", fields[1]);
            Assert.Equal("stop", fields[2]);
            Assert.Equal("demo", fields[3]);
            Assert.StartsWith("failed: the service did not respond", fields[4], StringComparison.Ordinal);
        }

        [Fact]
        public void OutcomesAreWordedTheSameInEveryLanguage()
        {
            Assert.Equal("ok", ActionLog.Describe(CommandResult.Ok()));
            Assert.Equal("declined", ActionLog.Describe(new CommandResult(1223, true, false, null)));
            Assert.Equal("timed out", ActionLog.Describe(new CommandResult(-1, false, true, "slow")));
            Assert.Equal("failed: boom", ActionLog.Describe(CommandResult.Failed("boom")));
            Assert.Equal("failed: exit code 5", ActionLog.Describe(new CommandResult(5, false, false, null)));
        }

        /// <summary>Past the size limit the file is set aside, once, and a new one begun.</summary>
        [Fact]
        public void AFullLogIsSetAsideAndBegunAgain()
        {
            string directory = Path.Combine(Path.GetTempPath(), "winsw-gui-" + Guid.NewGuid().ToString("n"));
            string path = Path.Combine(directory, "actions.log");
            string previous = Path.Combine(directory, "actions.1.log");

            try
            {
                ActionLog.Append(path, "first");
                Assert.Equal("first" + Environment.NewLine, File.ReadAllText(path));

                File.WriteAllText(path, new string('x', (int)ActionLog.MaxBytes));
                ActionLog.Append(path, "second");

                Assert.Equal("second" + Environment.NewLine, File.ReadAllText(path));
                Assert.Equal(ActionLog.MaxBytes, new FileInfo(previous).Length);
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}

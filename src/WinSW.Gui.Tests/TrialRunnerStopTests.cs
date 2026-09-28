using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using WinSW.Gui.Model;
using WinSW.Gui.Services;
using Xunit;

namespace WinSW.Gui.Tests
{
    /// <summary>
    /// The wait Install and Apply do before going ahead over a try run: they may only go on
    /// once the program is gone, because a service started while it is still there binds the
    /// same port and fails. Real processes, since what is being tested is how long they take to
    /// go; each one is a harmless sleeper on the machine the tests run on.
    /// </summary>
    public class TrialRunnerStopTests
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

        [Fact]
        public async Task ARunThatNeverStartedHasNothingToWaitFor()
        {
            using var runner = new TrialRunner();

            Assert.True(await runner.StopAsync(Timeout));
        }

        [Fact]
        public async Task StoppingWaitsUntilTheProgramIsGone()
        {
            using var runner = new TrialRunner();
            runner.Start(Sleeper(), null);
            int pid = runner.ProcessId!.Value;

            Assert.True(await runner.StopAsync(Timeout));

            Assert.False(runner.IsRunning);
            Assert.Null(runner.ProcessId);
            Assert.True(IsGone(pid));
        }

        /// <summary>
        /// A program started through a shell, or one that starts workers, is several processes,
        /// and the one holding the port need not be the first. Every process that shares the
        /// run's output has to be gone before the wait is over.
        /// </summary>
        [Fact]
        public async Task StoppingWaitsForWhatTheProgramStartedToo()
        {
            using var runner = new TrialRunner();
            runner.Start(SleeperBehindAShell(), null);

            // Long enough for the shell to have started the program it runs.
            await Task.Delay(500);

            Assert.True(await runner.StopAsync(Timeout));
            Assert.False(runner.IsRunning);
        }

        [Fact]
        public async Task ARunThatEndedByItselfIsAlreadyGone()
        {
            using var runner = new TrialRunner();
            var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            runner.Exited += code => exited.TrySetResult(code);
            runner.Start(QuickExit(), null);

            await exited.Task.WaitAsync(Timeout);

            Assert.True(await runner.StopAsync(Timeout));
            Assert.False(runner.IsRunning);
        }

        private static ServiceConfigModel Sleeper()
        {
            var model = ServiceConfigModel.CreateNew();
            if (OperatingSystem.IsWindows())
            {
                model.Executable = Path.Combine(Environment.SystemDirectory, "PING.EXE");
                model.Arguments = "-n 60 127.0.0.1";
            }
            else
            {
                model.Executable = "/bin/sleep";
                model.Arguments = "60";
            }

            return model;
        }

        private static ServiceConfigModel SleeperBehindAShell()
        {
            var model = ServiceConfigModel.CreateNew();
            if (OperatingSystem.IsWindows())
            {
                model.Executable = Path.Combine(Environment.SystemDirectory, "cmd.exe");
                model.Arguments = "/c ping -n 60 127.0.0.1";
            }
            else
            {
                // Two sleepers, so the shell has something to wait on after starting the first.
                model.Executable = "/bin/sh";
                model.Arguments = "-c \"/bin/sleep 60 & /bin/sleep 60\"";
            }

            return model;
        }

        private static ServiceConfigModel QuickExit()
        {
            var model = ServiceConfigModel.CreateNew();
            if (OperatingSystem.IsWindows())
            {
                model.Executable = Path.Combine(Environment.SystemDirectory, "cmd.exe");
                model.Arguments = "/c exit 0";
            }
            else
            {
                model.Executable = "/bin/sh";
                model.Arguments = "-c \"exit 0\"";
            }

            return model;
        }

        private static bool IsGone(int pid)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                return process.HasExited;
            }
            catch (ArgumentException)
            {
                return true;
            }
        }
    }
}

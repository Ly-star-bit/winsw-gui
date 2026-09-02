using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using WinSW.Gui.Localization;

namespace WinSW.Gui.Services
{
    /// <summary>The outcome of one elevated wrapper invocation.</summary>
    public sealed class CommandResult
    {
        public CommandResult(int exitCode, bool cancelled, string? error)
        {
            this.ExitCode = exitCode;
            this.Cancelled = cancelled;
            this.Error = error;
        }

        public int ExitCode { get; }

        /// <summary>True when the user dismissed the UAC prompt.</summary>
        public bool Cancelled { get; }

        public string? Error { get; }

        public bool Succeeded => this.Error is null && !this.Cancelled && this.ExitCode == 0;

        public static CommandResult Ok() => new(0, false, null);

        public static CommandResult Failed(string error) => new(-1, false, error);
    }

    /// <summary>
    /// Runs the wrapper's own commands, elevated.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Installing, starting, stopping and removing a service all need administrator rights.
    /// Rather than marking the whole GUI <c>requireAdministrator</c>, each mutating command
    /// re-launches the wrapper through ShellExecute with the <c>runas</c> verb, so the user
    /// sees one UAC prompt per action and browsing stays prompt-free.
    /// </para>
    /// <para>
    /// <c>--no-elevate</c> is always passed: the wrapper would otherwise notice it is not
    /// elevated and try to elevate itself, producing a second prompt.
    /// </para>
    /// <para>
    /// ShellExecute cannot redirect standard output, so results are read from the exit code.
    /// The caller is expected to re-read the service state afterwards rather than parse text.
    /// </para>
    /// </remarks>
    public static class WinSwCli
    {
        public static Task<CommandResult> InstallAsync(string wrapper, string configPath) =>
            RunAsync(wrapper, "install", Quote(configPath));

        public static Task<CommandResult> UninstallAsync(string wrapper, string configPath) =>
            RunAsync(wrapper, "uninstall", Quote(configPath));

        public static Task<CommandResult> StartAsync(string wrapper, string configPath) =>
            RunAsync(wrapper, "start", Quote(configPath));

        public static Task<CommandResult> StopAsync(string wrapper, string configPath, bool force) =>
            RunAsync(wrapper, "stop", Quote(configPath) + (force ? " --force" : string.Empty));

        public static Task<CommandResult> RestartAsync(string wrapper, string configPath, bool force) =>
            RunAsync(wrapper, "restart", Quote(configPath) + (force ? " --force" : string.Empty));

        /// <summary>Re-applies configuration to an installed service without reinstalling it.</summary>
        public static Task<CommandResult> RefreshAsync(string wrapper, string configPath) =>
            RunAsync(wrapper, "refresh", Quote(configPath));

        /// <summary>Terminates a service whose process has stopped responding.</summary>
        public static Task<CommandResult> KillAsync(string wrapper, string configPath) =>
            RunAsync(wrapper, "dev kill", Quote(configPath));

        private static async Task<CommandResult> RunAsync(string wrapper, string command, string arguments)
        {
            if (!File.Exists(wrapper))
            {
                return CommandResult.Failed(Localizer.Format("M.Cli.WrapperMissing", wrapper));
            }

            var startInfo = new ProcessStartInfo(wrapper)
            {
                // Required for the runas verb; it also rules out output redirection.
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
                Arguments = $"{command} {arguments} --no-elevate".Trim(),
                WorkingDirectory = Path.GetDirectoryName(wrapper) ?? Environment.CurrentDirectory,
            };

            try
            {
                using var process = Process.Start(startInfo);
                if (process is null)
                {
                    return CommandResult.Failed(Localizer.Get("M.Cli.CannotStart"));
                }

                await process.WaitForExitAsync().ConfigureAwait(false);
                return process.ExitCode == 0
                    ? CommandResult.Ok()
                    : new CommandResult(process.ExitCode, false, DescribeExitCode(command, process.ExitCode));
            }
            catch (Win32Exception e) when (e.NativeErrorCode == NativeMethods.ERROR_CANCELLED)
            {
                return new CommandResult(NativeMethods.ERROR_CANCELLED, true, null);
            }
            catch (Exception e) when (e is Win32Exception or InvalidOperationException or IOException)
            {
                return CommandResult.Failed(e.Message);
            }
        }

        private static string DescribeExitCode(string command, int exitCode) => exitCode switch
        {
            1056 => Localizer.Get("M.Cli.AlreadyRunning"),
            1060 => Localizer.Get("M.Cli.NotInstalled"),
            1062 => Localizer.Get("M.Cli.NotRunning"),
            1073 => Localizer.Get("M.Cli.AlreadyExists"),
            _ => Localizer.Format("M.Cli.Failed", command, exitCode),
        };

        private static string Quote(string value) => $"\"{value}\"";
    }
}

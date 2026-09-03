using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WinSW.Gui.Localization;

namespace WinSW.Gui.Services
{
    /// <summary>The outcome of one elevated invocation.</summary>
    public sealed class CommandResult
    {
        /// <summary>ERROR_DEPENDENT_SERVICES_RUNNING: a stop was refused because other services depend on this one.</summary>
        public const int DependentServicesRunning = 1051;

        public CommandResult(int exitCode, bool cancelled, bool timedOut, string? error)
        {
            this.ExitCode = exitCode;
            this.Cancelled = cancelled;
            this.TimedOut = timedOut;
            this.Error = error;
        }

        public int ExitCode { get; }

        /// <summary>True when the user dismissed the UAC prompt.</summary>
        public bool Cancelled { get; }

        /// <summary>True when the wrapper did not finish within the allowed time. It may still be running.</summary>
        public bool TimedOut { get; }

        public string? Error { get; }

        public bool Succeeded => this.Error is null && !this.Cancelled && !this.TimedOut && this.ExitCode == 0;

        public bool HasDependents => this.ExitCode == DependentServicesRunning;

        public static CommandResult Ok() => new(0, false, false, null);

        public static CommandResult Failed(string error) => new(-1, false, false, error);
    }

    /// <summary>
    /// Runs the wrapper's own commands, elevated.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Installing, starting, stopping and removing a service all need administrator rights.
    /// Rather than marking the whole GUI <c>requireAdministrator</c>, each mutating command
    /// re-launches the wrapper through ShellExecute with the <c>runas</c> verb. When the GUI
    /// itself is already elevated the same call simply runs without a prompt.
    /// </para>
    /// <para>
    /// <c>--no-elevate</c> is always passed: the wrapper would otherwise notice it is not
    /// elevated and try to elevate itself, producing a second prompt.
    /// </para>
    /// <para>
    /// ShellExecute cannot redirect standard output, so results are read from the exit code.
    /// Several commands can be chained through <c>cmd.exe</c> so that a sequence such as
    /// install-then-start costs a single prompt.
    /// </para>
    /// </remarks>
    public static class WinSwCli
    {
        /// <summary>Generous enough for a service with a long <c>stoptimeout</c>; the caller may override.</summary>
        public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(3);

        private static readonly TimeSpan QuickTimeout = TimeSpan.FromMinutes(1);

        public static Task<CommandResult> InstallAsync(string wrapper, string configPath) =>
            RunAsync(wrapper, "install", Line("install", configPath), QuickTimeout);

        public static Task<CommandResult> UninstallAsync(string wrapper, string configPath) =>
            RunAsync(wrapper, "uninstall", Line("uninstall", configPath), DefaultTimeout);

        public static Task<CommandResult> StartAsync(string wrapper, string configPath, TimeSpan? timeout = null) =>
            RunAsync(wrapper, "start", Line("start", configPath), timeout ?? DefaultTimeout);

        /// <param name="force">Stop even if other services depend on this one. Off by default; see <see cref="CommandResult.HasDependents"/>.</param>
        public static Task<CommandResult> StopAsync(string wrapper, string configPath, bool force = false, TimeSpan? timeout = null) =>
            RunAsync(wrapper, "stop", Line("stop", configPath, force ? "--force" : null), timeout ?? DefaultTimeout);

        public static Task<CommandResult> RestartAsync(string wrapper, string configPath, bool force = false, TimeSpan? timeout = null) =>
            RunAsync(wrapper, "restart", Line("restart", configPath, force ? "--force" : null), timeout ?? DefaultTimeout);

        /// <summary>Re-applies configuration to an installed service without reinstalling it.</summary>
        public static Task<CommandResult> RefreshAsync(string wrapper, string configPath) =>
            RunAsync(wrapper, "refresh", Line("refresh", configPath), QuickTimeout);

        /// <summary>Terminates a service whose process has stopped responding.</summary>
        public static Task<CommandResult> KillAsync(string wrapper, string configPath) =>
            RunAsync(wrapper, "dev kill", Line("dev kill", configPath), QuickTimeout);

        /// <summary>Installs and starts under one elevation prompt.</summary>
        public static Task<CommandResult> InstallAndStartAsync(string wrapper, string configPath) =>
            RunBatchAsync(DefaultTimeout, (wrapper, Line("install", configPath)), (wrapper, Line("start", configPath)));

        /// <summary>
        /// Runs one command on each of several services under a single prompt. Unlike
        /// <see cref="InstallAndStartAsync"/> the chain continues past failures — the user
        /// asked for all of them, and the status refresh afterwards shows which ones took.
        /// </summary>
        public static Task<CommandResult> RunOnManyAsync(string command, IEnumerable<(string Wrapper, string ConfigPath)> services)
        {
            var steps = services.Select(s => $"{Quote(s.Wrapper)} {command} {Quote(s.ConfigPath)} --no-elevate").ToList();
            if (steps.Count == 0)
            {
                return Task.FromResult(CommandResult.Ok());
            }

            string script = string.Join(" & ", steps);
            return RunElevatedAsync("cmd.exe", $"/d /c \"{script}\"", null, DefaultTimeout, command);
        }

        /// <summary>
        /// Replaces a wrapper executable with a newer build under one prompt: stop (failure
        /// tolerated: it may not be running), copy over, start.
        /// </summary>
        /// <summary>
        /// Replaces a wrapper executable and brings back the services that run from it. Under
        /// the install root that is every service at once, because they share one file.
        /// </summary>
        /// <remarks>
        /// The steps are chained unconditionally. A running process locks its own image, so a
        /// copy can fail — and a chain that skipped the restart on failure would leave the
        /// services stopped, which is worse than not upgrading. Whether the file was actually
        /// replaced is judged afterwards from its own version, not from an exit code that the
        /// trailing starts would have overwritten anyway.
        /// </remarks>
        public static Task<CommandResult> UpgradeWrapperAsync(string wrapper, string newExecutable, IReadOnlyList<(string ConfigPath, bool WasRunning)> services)
        {
            var steps = new List<string>(services.Count * 2 + 1);

            foreach (var service in services)
            {
                steps.Add($"{Quote(wrapper)} stop {Quote(service.ConfigPath)} --no-elevate");
            }

            steps.Add($"copy /y {Quote(newExecutable)} {Quote(wrapper)}");

            foreach (var service in services)
            {
                if (service.WasRunning)
                {
                    steps.Add($"{Quote(wrapper)} start {Quote(service.ConfigPath)} --no-elevate");
                }
            }

            return RunElevatedAsync("cmd.exe", $"/d /c \"{string.Join(" & ", steps)}\"", Path.GetDirectoryName(wrapper), DefaultTimeout, "upgrade");
        }

        /// <summary>
        /// <c>winsw customize</c>: writes a copy of the wrapper with a different company name in
        /// its version information. Runs unelevated with output captured; no service is touched.
        /// </summary>
        public static async Task<CommandResult> CustomizeAsync(string wrapper, string output, string manufacturer)
        {
            if (!File.Exists(wrapper))
            {
                return CommandResult.Failed(Localizer.Format("M.Cli.WrapperMissing", wrapper));
            }

            var startInfo = new ProcessStartInfo(wrapper)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(wrapper) ?? Environment.CurrentDirectory,
            };
            startInfo.ArgumentList.Add("customize");
            startInfo.ArgumentList.Add("-o");
            startInfo.ArgumentList.Add(output);
            startInfo.ArgumentList.Add("--manufacturer");
            startInfo.ArgumentList.Add(manufacturer);

            try
            {
                using var process = Process.Start(startInfo);
                if (process is null)
                {
                    return CommandResult.Failed(Localizer.Get("M.Cli.CannotStart"));
                }

                string error = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
                await process.WaitForExitAsync().ConfigureAwait(false);

                return process.ExitCode == 0
                    ? CommandResult.Ok()
                    : new CommandResult(process.ExitCode, false, false, string.IsNullOrWhiteSpace(error) ? DescribeExitCode("customize", process.ExitCode) : error.Trim());
            }
            catch (Exception e) when (e is Win32Exception or InvalidOperationException or IOException)
            {
                return CommandResult.Failed(e.Message);
            }
        }

        /// <summary>
        /// Copies a file with administrator rights, for configurations that live in a directory
        /// a standard user cannot write to (Program Files, typically).
        /// </summary>
        public static Task<CommandResult> CopyElevatedAsync(string source, string destination)
        {
            string script = $"copy /y {Quote(source)} {Quote(destination)}";
            return RunElevatedAsync("cmd.exe", $"/d /c \"{script}\"", Path.GetDirectoryName(destination), QuickTimeout, "copy");
        }

        private static string Line(string command, string configPath, string? extra = null) =>
            extra is null ? $"{command} {Quote(configPath)}" : $"{command} {Quote(configPath)} {extra}";

        private static Task<CommandResult> RunAsync(string wrapper, string label, string commandLine, TimeSpan timeout)
        {
            if (!File.Exists(wrapper))
            {
                return Task.FromResult(CommandResult.Failed(Localizer.Format("M.Cli.WrapperMissing", wrapper)));
            }

            return RunElevatedAsync(wrapper, $"{commandLine} --no-elevate", Path.GetDirectoryName(wrapper), timeout, label);
        }

        /// <summary>
        /// Runs several wrapper commands under one prompt: <c>cmd /c "a &amp;&amp; b"</c>, so the
        /// chain stops at the first failure and its exit code is reported.
        /// </summary>
        private static Task<CommandResult> RunBatchAsync(TimeSpan timeout, params (string Wrapper, string CommandLine)[] steps)
        {
            foreach (var step in steps)
            {
                if (!File.Exists(step.Wrapper))
                {
                    return Task.FromResult(CommandResult.Failed(Localizer.Format("M.Cli.WrapperMissing", step.Wrapper)));
                }
            }

            string script = string.Join(" && ", steps.Select(s => $"{Quote(s.Wrapper)} {s.CommandLine} --no-elevate"));
            string label = string.Join(" + ", steps.Select(s => s.CommandLine.Split(' ')[0]));

            // The outer quotes are consumed by cmd itself; everything inside keeps its own quoting.
            return RunElevatedAsync("cmd.exe", $"/d /c \"{script}\"", Path.GetDirectoryName(steps[0].Wrapper), timeout, label);
        }

        private static async Task<CommandResult> RunElevatedAsync(string file, string arguments, string? workingDirectory, TimeSpan timeout, string label)
        {
            var startInfo = new ProcessStartInfo(file)
            {
                // Required for the runas verb; it also rules out output redirection.
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
                Arguments = arguments,
                WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
            };

            try
            {
                using var process = Process.Start(startInfo);
                if (process is null)
                {
                    return CommandResult.Failed(Localizer.Get("M.Cli.CannotStart"));
                }

                using var cancellation = new CancellationTokenSource(timeout);
                try
                {
                    await process.WaitForExitAsync(cancellation.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // An elevated process cannot be killed from here; report and let the user
                    // decide whether to terminate the service itself.
                    return new CommandResult(-1, false, true, Localizer.Format("M.Cli.TimedOut", label, (int)timeout.TotalSeconds));
                }

                return process.ExitCode == 0
                    ? CommandResult.Ok()
                    : new CommandResult(process.ExitCode, false, false, DescribeExitCode(label, process.ExitCode));
            }
            catch (Win32Exception e) when (e.NativeErrorCode == NativeMethods.ERROR_CANCELLED)
            {
                return new CommandResult(NativeMethods.ERROR_CANCELLED, true, false, null);
            }
            catch (Exception e) when (e is Win32Exception or InvalidOperationException or IOException)
            {
                return CommandResult.Failed(e.Message);
            }
        }

        private static string DescribeExitCode(string command, int exitCode) => exitCode switch
        {
            CommandResult.DependentServicesRunning => Localizer.Get("M.Cli.HasDependents"),
            1056 => Localizer.Get("M.Cli.AlreadyRunning"),
            1060 => Localizer.Get("M.Cli.NotInstalled"),
            1062 => Localizer.Get("M.Cli.NotRunning"),
            1073 => Localizer.Get("M.Cli.AlreadyExists"),
            _ => Localizer.Format("M.Cli.Failed", command, exitCode),
        };

        private static string Quote(string value) => $"\"{value}\"";
    }
}

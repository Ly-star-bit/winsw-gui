using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
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
            var list = services.ToList();
            if (list.Count == 0)
            {
                return Task.FromResult(CommandResult.Ok());
            }

            if (RejectExpandablePaths(list.SelectMany(s => new[] { s.Wrapper, s.ConfigPath })) is { } refusal)
            {
                return Task.FromResult(refusal);
            }

            var steps = list.Select(s => $"{Quote(s.Wrapper)} {command} {Quote(s.ConfigPath)} --no-elevate").ToList();
            return RunElevatedScriptAsync(steps, null, DefaultTimeout, command);
        }

        /// <summary>
        /// How many elevation prompts <see cref="RunOnManyAsync"/> will raise for this many
        /// services, so the confirmation can say so rather than surprising the user with a
        /// second one halfway through.
        /// </summary>
        public static int PromptCountFor(string command, IEnumerable<(string Wrapper, string ConfigPath)> services) =>
            Chunk(services.Select(s => $"{Quote(s.Wrapper)} {command} {Quote(s.ConfigPath)} --no-elevate").ToList()).Count;

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
            if (RejectExpandablePaths(services.Select(s => s.ConfigPath).Concat(new[] { wrapper, newExecutable })) is { } refusal)
            {
                return Task.FromResult(refusal);
            }

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

            // Chunks run in order, so the copy still falls between every stop and every start.
            return RunElevatedScriptAsync(steps, Path.GetDirectoryName(wrapper), DefaultTimeout, "upgrade");
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

                // Not redirected. A redirected pipe that nobody drains fills after a few
                // kilobytes and blocks the child inside its own write, and this method reads
                // only standard error — so redirecting output as well would be a deadlock
                // waiting for a chatty build of the wrapper. Where both streams are wanted,
                // they have to be read as they arrive: see TrialRunner.
                RedirectStandardOutput = false,
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

                using var cancellation = new CancellationTokenSource(QuickTimeout);

                string error;
                try
                {
                    error = await process.StandardError.ReadToEndAsync(cancellation.Token).ConfigureAwait(false);
                    await process.WaitForExitAsync(cancellation.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Unelevated and ours, so unlike the elevated commands this one can be
                    // stopped rather than merely reported.
                    Terminate(process);
                    return new CommandResult(-1, false, true, Localizer.Format("M.Cli.TimedOut", "customize", (int)QuickTimeout.TotalSeconds));
                }

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
            if (RejectExpandablePaths(new[] { source, destination }) is { } refusal)
            {
                return Task.FromResult(refusal);
            }

            string script = $"copy /y {Quote(source)} {Quote(destination)}";
            return RunElevatedAsync("cmd.exe", $"/d /c \"{script}\"", Path.GetDirectoryName(destination), QuickTimeout, "copy");
        }

        /// <summary>
        /// Ends a process and its children with administrator rights. Filtered on the image name
        /// as well as the ID, so that an ID the system has handed to another program since the
        /// process was found matches nothing.
        /// </summary>
        public static Task<CommandResult> KillProcessElevatedAsync(int processId, string imageName) =>
            RunElevatedAsync(
                "taskkill.exe",
                $"/F /T /PID {processId.ToString(System.Globalization.CultureInfo.InvariantCulture)} /FI {Quote("IMAGENAME eq " + imageName)}",
                null,
                QuickTimeout,
                "taskkill");

        /// <summary>
        /// Deletes files with administrator rights, in as few prompts as cmd allows: a
        /// service's logs are written by its own account, often where the user may only read.
        /// </summary>
        public static Task<CommandResult> DeleteElevatedAsync(IReadOnlyList<string> paths)
        {
            if (RejectExpandablePaths(paths) is { } refusal)
            {
                return Task.FromResult(refusal);
            }

            var steps = paths.Select(p => $"del /f /q {Quote(p)}").ToList();
            return RunElevatedScriptAsync(steps, null, QuickTimeout, "del");
        }

        /// <summary>
        /// Registers a task from a definition file, replacing one of the same name, with
        /// administrator rights: a task that runs as SYSTEM cannot be registered without them.
        /// </summary>
        public static Task<CommandResult> ScheduleTaskAsync(string taskPath, string definitionPath) =>
            RunElevatedAsync("schtasks.exe", $"/Create /TN {Quote(taskPath)} /XML {Quote(definitionPath)} /F", null, QuickTimeout, "schtasks");

        /// <summary>Deletes a task with administrator rights.</summary>
        public static Task<CommandResult> UnscheduleTaskAsync(string taskPath) =>
            RunElevatedAsync("schtasks.exe", $"/Delete /TN {Quote(taskPath)} /F", null, QuickTimeout, "schtasks");

        /// <summary>
        /// The most a chained script may be, in characters. cmd refuses a command line longer
        /// than 8191 and does not say so usefully; the margin covers <c>cmd.exe /d /c ""</c>
        /// and leaves room for one more step than fits exactly.
        /// </summary>
        internal const int MaxScriptLength = 7500;

        /// <summary>
        /// A <c>%NAME%</c> pair, which cmd substitutes even inside double quotes. Percent is a
        /// legal character in a Windows path, and there is no way to escape it on a command
        /// line — only inside a batch file, which is not something to write and then run
        /// elevated on a standard user's behalf.
        /// </summary>
        private static readonly Regex ExpandablePath =
            new(@"%[A-Za-z_][A-Za-z0-9_]*%", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>
        /// Splits chained steps into scripts short enough for cmd to accept. Every batch here
        /// grows with the number of services, and thirty of them under one install root is an
        /// ordinary amount, so the limit is reachable.
        /// </summary>
        /// <remarks>
        /// A single step longer than the budget is still emitted on its own: cmd will refuse
        /// it, but splitting a command in half would be worse than letting it be refused.
        /// </remarks>
        internal static IReadOnlyList<string> Chunk(IReadOnlyList<string> steps)
        {
            var scripts = new List<string>();
            var current = new StringBuilder();

            foreach (string step in steps)
            {
                if (current.Length > 0 && current.Length + Separator.Length + step.Length > MaxScriptLength)
                {
                    scripts.Add(current.ToString());
                    current.Clear();
                }

                if (current.Length > 0)
                {
                    current.Append(Separator);
                }

                current.Append(step);
            }

            if (current.Length > 0)
            {
                scripts.Add(current.ToString());
            }

            return scripts;
        }

        /// <summary>
        /// Refuses paths cmd would rewrite, rather than running a command against a path that
        /// is not the one the user chose.
        /// </summary>
        internal static CommandResult? RejectExpandablePaths(IEnumerable<string> paths)
        {
            foreach (string path in paths)
            {
                if (ExpandablePath.IsMatch(path))
                {
                    return CommandResult.Failed(Localizer.Format("M.Cli.PathNotUsable", path));
                }
            }

            return null;
        }

        /// <summary>
        /// Runs chained steps elevated, in as few prompts as cmd's command line allows.
        /// </summary>
        private static async Task<CommandResult> RunElevatedScriptAsync(
            IReadOnlyList<string> steps, string? workingDirectory, TimeSpan timeout, string label)
        {
            CommandResult? firstFailure = null;

            foreach (string script in Chunk(steps))
            {
                var result = await RunElevatedAsync("cmd.exe", $"/d /c \"{script}\"", workingDirectory, timeout, label).ConfigureAwait(false);

                // A dismissed prompt means the user has changed their mind about the whole
                // operation, not just about this chunk of it.
                if (result.Cancelled)
                {
                    return result;
                }

                // The remaining chunks still run — the steps were chained with '&' precisely
                // so that one failure does not strand the rest — but it is the first failure
                // that gets reported, not whatever the last chunk happened to return.
                firstFailure ??= result.Succeeded ? null : result;
            }

            return firstFailure ?? CommandResult.Ok();
        }

        /// <summary>Kills a process and the tree under it, ignoring one that has already gone.</summary>
        private static void Terminate(Process process)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception e) when (e is InvalidOperationException or Win32Exception or NotSupportedException)
            {
            }
        }

        private const string Separator = " & ";

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

            // The command line already carries the quoted configuration path, so it is checked
            // alongside the executable rather than the caller having to pass the path twice.
            if (RejectExpandablePaths(steps.SelectMany(s => new[] { s.Wrapper, s.CommandLine })) is { } refusal)
            {
                return Task.FromResult(refusal);
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

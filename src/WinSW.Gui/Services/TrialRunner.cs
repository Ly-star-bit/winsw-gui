using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using WinSW.Configuration;
using WinSW.Gui.Model;

namespace WinSW.Gui.Services
{
    /// <summary>
    /// Runs a configuration's program directly, without installing a service, so a bad
    /// path, argument or environment shows up in a console-style panel instead of as a
    /// silent failure in the service control manager.
    /// </summary>
    /// <remarks>
    /// The process runs as the current user, not as the service account, and there is no
    /// wrapper in front of it. That is the point: what is being checked is the command line
    /// and environment, and those are the same either way.
    /// </remarks>
    public sealed class TrialRunner : IDisposable
    {
        private Process? process;

        /// <summary>One line of output; the flag is true for stderr.</summary>
        public event Action<string, bool>? Output;

        /// <summary>Raised on the calling thread's pool when the process ends, with its exit code.</summary>
        public event Action<int>? Exited;

        public bool IsRunning => this.process is { HasExited: false };

        public int? ProcessId => this.IsRunning ? this.process!.Id : null;

        /// <exception cref="InvalidOperationException">The configuration has no executable, or a run is already active.</exception>
        /// <exception cref="Win32Exception">The program could not be started.</exception>
        public void Start(ServiceConfigModel model, string? configPath)
        {
            if (this.IsRunning)
            {
                throw new InvalidOperationException("A trial run is already active.");
            }

            string? Expand(string? value) =>
                string.IsNullOrWhiteSpace(value) ? null
                : configPath is null ? Environment.ExpandEnvironmentVariables(value!)
                : ConfigPaths.Expand(value!, configPath);

            string executable = Expand(model.Executable) ?? throw new InvalidOperationException("No executable is configured.");

            // The wrapper prefers startarguments over arguments for the start action.
            string arguments = Expand(model.StartArguments) ?? Expand(model.Arguments) ?? string.Empty;

            string workingDirectory = Expand(model.WorkingDirectory)
                ?? (configPath != null ? Path.GetDirectoryName(configPath) : null)
                ?? Environment.CurrentDirectory;

            var startInfo = new ProcessStartInfo(executable, arguments)
            {
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            // Assembled on the side rather than straight into startInfo.Environment, which
            // already carries this process's own variables: <proxy> has to lose to an <env>
            // entry and win over whatever the machine happens to have set, and only a
            // dictionary holding the configuration alone can tell those two apart.
            var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var variable in model.EnvironmentVariables)
            {
                if (!string.IsNullOrWhiteSpace(variable.Name))
                {
                    environment[variable.Name] = Expand(variable.Value) ?? string.Empty;
                }
            }

            if (!string.IsNullOrWhiteSpace(model.ProxyAddress))
            {
                try
                {
                    new ProxyConfig(Expand(model.ProxyAddress), Expand(model.ProxyNoProxy), model.ProxyJava)
                        .ApplyTo(environment);
                }
                catch (InvalidDataException e)
                {
                    throw new InvalidOperationException(e.Message, e);
                }
            }

            foreach (var pair in environment)
            {
                startInfo.Environment[pair.Key] = pair.Value;
            }

            if (configPath != null)
            {
                // The same variables the wrapper publishes to the child.
                startInfo.Environment["BASE"] = Path.GetDirectoryName(configPath)!;
                startInfo.Environment["SERVICE_ID"] = model.Id;
            }

            var started = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            started.OutputDataReceived += (_, e) =>
            {
                if (e.Data != null)
                {
                    this.Output?.Invoke(e.Data, false);
                }
            };
            started.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null)
                {
                    this.Output?.Invoke(e.Data, true);
                }
            };
            started.Exited += (_, _) =>
            {
                int code;
                try
                {
                    code = started.ExitCode;
                }
                catch (InvalidOperationException)
                {
                    code = -1;
                }

                this.Exited?.Invoke(code);
            };

            started.Start();
            started.BeginOutputReadLine();
            started.BeginErrorReadLine();
            this.process = started;
        }

        /// <summary>Ends the run, taking any children with it, the way the wrapper's stop would.</summary>
        public void Stop()
        {
            if (!this.IsRunning)
            {
                return;
            }

            try
            {
                this.process!.Kill(entireProcessTree: true);
            }
            catch (Exception e) when (e is InvalidOperationException or Win32Exception)
            {
                // Already gone.
            }
        }

        public void Dispose()
        {
            this.Stop();
            this.process?.Dispose();
            this.process = null;
        }
    }
}

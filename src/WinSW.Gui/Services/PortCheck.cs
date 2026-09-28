using System;
using System.Collections.Generic;
using WinSW.Gui.Model;

namespace WinSW.Gui.Services
{
    /// <summary>A port a configuration names, and the process already listening on it.</summary>
    /// <param name="Port">The port the configuration names.</param>
    /// <param name="ProcessName">The listening process's image name, such as <c>python.exe</c>.</param>
    /// <param name="ProcessId">Its PID, as Task Manager shows it.</param>
    /// <param name="Service">
    /// The service the process runs under, when it is the one the check was asked about (see
    /// <see cref="PortCheck.Find"/>): its own program, which is not a process to end but a service
    /// to stop. Null for any other holder.
    /// </param>
    public readonly record struct PortInUse(int Port, string ProcessName, int ProcessId, string? Service = null);

    /// <summary>
    /// Whether the ports a configuration names are free, asked once before it runs: on the
    /// wizard's review step, and before the editor's Install and Try run.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A port already taken is the commonest reason a web service here fails its first start and
    /// every restart after it, and the program's own words for it name neither the port nor who
    /// has it. Asked before, the answer names both — often a copy of the same program, started by
    /// hand to try it and never ended.
    /// </para>
    /// <para>
    /// The ports are those written in the configuration (see <see cref="ServiceClone.FindPorts"/>):
    /// a program's default port, not written anywhere, is not looked for. Any listener on the port
    /// counts, on whatever address and whoever it belongs to, another service included: the new
    /// program cannot have the port either way. A finding is a warning, never a refusal; the
    /// holder may be gone by the time the service starts.
    /// </para>
    /// </remarks>
    public static class PortCheck
    {
        /// <summary>
        /// Wrapper image names; none here, so that only the one process <see cref="StrayProcesses.IsOwned"/>
        /// is given, this console or a service's wrapper, counts as an owner.
        /// </summary>
        private static readonly ISet<string> NoWrappers = new HashSet<string>();

        /// <summary>
        /// The ports <paramref name="model"/> tells its program to listen on: in the arguments the
        /// wrapper starts it with — <c>&lt;startarguments&gt;</c> when there are any, as the wrapper
        /// has it — and in its <c>&lt;env&gt;</c> variables.
        /// </summary>
        public static IReadOnlyList<int> PortsOf(ServiceConfigModel model) =>
            ServiceClone.FindPorts(
                string.IsNullOrWhiteSpace(model.StartArguments) ? model.Arguments : model.StartArguments,
                model.EnvironmentVariables);

        /// <summary>
        /// Reads the machine's listeners and processes, and says which of <paramref name="ports"/>
        /// are held and by whom. Off the UI thread. Empty when there is nothing to look for, and
        /// when the machine cannot be read: no warning is better than a made-up one.
        /// </summary>
        /// <param name="ports">The ports to look for; see <see cref="PortsOf"/>.</param>
        /// <param name="consoleProcessId">
        /// This console, when its own try run is not to count: the editor's Install, which has
        /// asked for the try run to be ended already. Null to count every listener.
        /// </param>
        /// <param name="serviceName">
        /// The installed service the configuration belongs to, when there is one: the editor's
        /// Try run of a service's own file. A holder running under that service's wrapper is
        /// named as the service's (see <see cref="PortInUse.Service"/>) rather than as a process
        /// to end. Null when the configuration is no service's yet.
        /// </param>
        public static IReadOnlyList<PortInUse> Find(IReadOnlyList<int> ports, int? consoleProcessId, string? serviceName = null)
        {
            if (ports.Count == 0)
            {
                return Array.Empty<PortInUse>();
            }

            // The service's wrapper as it runs now, asked of the service control manager rather
            // than taken from the editor's entry for it, which after an Install is a copy made
            // once and keeps the PID of that moment. Read first: a wrapper that restarts after
            // this is not the one the snapshot finds above the holder, and the holder is named
            // plainly, which is no worse than before.
            (string Name, int WrapperProcessId)? service = null;
            if (serviceName is not null)
            {
                using var reading = new StatusReading(withProcesses: false);
                int wrapper = reading.Sample(serviceName).ProcessId;
                if (wrapper > 0)
                {
                    service = (serviceName, wrapper);
                }
            }

            // The snapshot is taken after the table, so that a listener missing from it has
            // exited in between, and taken its socket with it.
            if (PortTable.Read() is not { } table || ProcessSnapshot.Take() is not { } snapshot)
            {
                return Array.Empty<PortInUse>();
            }

            return Holders(ports, table, snapshot, consoleProcessId, service);
        }

        /// <summary>
        /// <see cref="Find"/> over a table and a snapshot already read: every process listening on
        /// each port, port by port in the order given and lowest PID first.
        /// </summary>
        /// <param name="service">The service asked about and its wrapper's PID, as <see cref="Find"/> read them; null for none.</param>
        internal static IReadOnlyList<PortInUse> Holders(
            IReadOnlyList<int> ports,
            PortTable table,
            ProcessSnapshot snapshot,
            int? consoleProcessId,
            (string Name, int WrapperProcessId)? service = null)
        {
            var found = new List<PortInUse>();
            foreach (int port in ports)
            {
                foreach (int holder in table.HoldersOf(port))
                {
                    // PID 0 is the kernel's idle process, never a holder to name; one missing from
                    // the snapshot has exited since the table was read.
                    if (holder <= 0 || !snapshot.TryGet(holder, out var record))
                    {
                        continue;
                    }

                    if (consoleProcessId is { } console
                        && (holder == console || StrayProcesses.IsOwned(snapshot, record, NoWrappers, console)))
                    {
                        continue;
                    }

                    // Under the service's own wrapper: the service is running, and a try run of its
                    // file cannot have the port while it does. Asked the same way as for this
                    // console, with the wrapper's PID as the one ancestor that counts.
                    string? owner = service is { } asked
                        && (holder == asked.WrapperProcessId || StrayProcesses.IsOwned(snapshot, record, NoWrappers, asked.WrapperProcessId))
                        ? asked.Name
                        : null;

                    found.Add(new PortInUse(port, record.Name, holder, owner));
                }
            }

            return found;
        }
    }
}

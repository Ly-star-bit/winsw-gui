using System;
using System.Collections.Generic;
using WinSW.Gui.Model;

namespace WinSW.Gui.Services
{
    /// <summary>A port a configuration names, and the process already listening on it.</summary>
    /// <param name="Port">The port the configuration names.</param>
    /// <param name="ProcessName">The listening process's image name, such as <c>python.exe</c>.</param>
    /// <param name="ProcessId">Its PID, as Task Manager shows it.</param>
    public readonly record struct PortInUse(int Port, string ProcessName, int ProcessId);

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
        /// <summary>Wrapper image names; none here, so that only this console counts as an owner.</summary>
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
        public static IReadOnlyList<PortInUse> Find(IReadOnlyList<int> ports, int? consoleProcessId)
        {
            if (ports.Count == 0)
            {
                return Array.Empty<PortInUse>();
            }

            // The snapshot is taken after the table, so that a listener missing from it has
            // exited in between, and taken its socket with it.
            if (PortTable.Read() is not { } table || ProcessSnapshot.Take() is not { } snapshot)
            {
                return Array.Empty<PortInUse>();
            }

            return Holders(ports, table, snapshot, consoleProcessId);
        }

        /// <summary>
        /// <see cref="Find"/> over a table and a snapshot already read: every process listening on
        /// each port, port by port in the order given and lowest PID first.
        /// </summary>
        internal static IReadOnlyList<PortInUse> Holders(IReadOnlyList<int> ports, PortTable table, ProcessSnapshot snapshot, int? consoleProcessId)
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

                    found.Add(new PortInUse(port, record.Name, holder));
                }
            }

            return found;
        }
    }
}

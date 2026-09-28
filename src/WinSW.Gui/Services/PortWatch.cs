using System;
using System.Collections.Generic;
using System.Linq;
using System.ServiceProcess;

namespace WinSW.Gui.Services
{
    /// <summary>
    /// The ports half of a status poll, done on the poll's worker once the samples are taken: what
    /// each running service listens on, noted into <see cref="RememberedRuns"/>, and what holds a
    /// port a stopped one last listened on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The machine's listeners are read at most once a poll, and only when a service needs them:
    /// the selected one is running, and its ports are on screen; a running one has not been looked
    /// at yet in this run, or is young and has listened on nothing so far (see
    /// <see cref="RememberedRuns.WantsPortsOf"/>); or a stopped one has ports to look for. A
    /// console watching a dozen services that all run and have all been seen reads nothing.
    /// </para>
    /// <para>
    /// Once read, the table answers every service: each running one has its ports taken from it
    /// and noted, whichever service it was read for, since that is free.
    /// </para>
    /// <para>
    /// A port holder already on a service's banner is looked for again while the service runs.
    /// Between two failed starts of a restart loop the service is running for a second or two,
    /// failing on the very port the banner names; clearing the banner for those seconds would have
    /// it blink at every restart. What holds the port then is outside every wrapper, so it cannot
    /// be the service's own; once the service holds its port itself, nothing is found and the
    /// banner goes.
    /// </para>
    /// </remarks>
    public static class PortWatch
    {
        /// <summary>
        /// Takes the ports into the samples: <see cref="ServiceSample.Listening"/> for every running
        /// service, and a port holder as <see cref="ServiceSample.Stray"/> for a stopped one, in place
        /// of what <see cref="StrayProcesses.Find"/> found — the port names what the next start fails
        /// on. Returns whether the table was read, which is only when something needed it.
        /// </summary>
        /// <param name="samples">This poll's samples, with their descendants and strays filled in; updated in place.</param>
        /// <param name="services">Each sample's service, as the UI thread read it before the poll.</param>
        /// <param name="snapshot">The snapshot the samples were taken against.</param>
        /// <param name="readTable">Reads the machine's listeners: <see cref="PortTable.Read"/>.</param>
        /// <param name="runs">Where ports are remembered from one run to the next.</param>
        /// <param name="wrapperNames">Image names of the WinSW wrappers on this machine.</param>
        /// <param name="consoleProcessId">This console, whose try runs are its own business.</param>
        /// <param name="now">Local time, as a process's start is kept.</param>
        public static bool Look(
            ServiceSample[] samples,
            IReadOnlyList<PolledService> services,
            ProcessSnapshot snapshot,
            Func<PortTable?> readTable,
            RememberedRuns runs,
            ISet<string> wrapperNames,
            int consoleProcessId,
            DateTime now)
        {
            if (!Wanted(samples, services, runs, now))
            {
                return false;
            }

            if (readTable() is not { } table)
            {
                return true;
            }

            for (int i = 0; i < samples.Length; i++)
            {
                var sample = samples[i];
                string name = services[i].Name;

                if (sample.HasProcess)
                {
                    var tree = new HashSet<int> { sample.ProcessId };
                    if (!sample.Descendants.IsDefault)
                    {
                        tree.UnionWith(sample.Descendants.Select(process => process.ProcessId));
                    }

                    var held = table.HeldBy(tree);
                    runs.NotePorts(name, WrapperOf(sample), held.Select(listener => listener.Port), now);
                    sample = sample with { Listening = held };
                }

                // After the noting: a running service that has moved to another port has just
                // stopped being looked for on the old one.
                if (LooksForHolder(sample, services[i])
                    && runs.PortsOf(name) is { Length: > 0 } remembered
                    && StrayProcesses.FindPortHolder(snapshot, table, remembered, wrapperNames, consoleProcessId) is { } holder)
                {
                    sample = sample with { Stray = holder };
                }

                samples[i] = sample;
            }

            return true;
        }

        /// <summary>Whether any service needs the table at this reading; see the remarks.</summary>
        internal static bool Wanted(ServiceSample[] samples, IReadOnlyList<PolledService> services, RememberedRuns runs, DateTime now)
        {
            for (int i = 0; i < samples.Length; i++)
            {
                var sample = samples[i];
                if (sample.HasProcess && (services[i].Selected || runs.WantsPortsOf(services[i].Name, WrapperOf(sample), now)))
                {
                    return true;
                }

                if (LooksForHolder(sample, services[i]) && runs.PortsOf(services[i].Name).Length > 0)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Stopped, or running with a holder already on its banner; see the remarks.</summary>
        private static bool LooksForHolder(in ServiceSample sample, PolledService service) =>
            sample.Queried && (sample.Status == ServiceControllerStatus.Stopped || service.HolderShown);

        /// <summary>The wrapper as a run is told by: its ID and its start. The name is not identity.</summary>
        private static ProcessMark WrapperOf(in ServiceSample sample) => new(sample.ProcessId, sample.StartedAt, string.Empty);
    }

    /// <summary>
    /// A service as a status poll hands it to its worker: plain values, read on the UI thread before
    /// the poll, since the entry itself may only be touched there.
    /// </summary>
    /// <param name="Name">The service's name.</param>
    /// <param name="Selected">Its details are on screen.</param>
    /// <param name="HolderShown">Its banner names a process holding its port; see <see cref="PortWatch"/>.</param>
    public readonly record struct PolledService(string Name, bool Selected, bool HolderShown);
}

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.ServiceProcess;

namespace WinSW.Gui.Services
{
    /// <summary>
    /// The leftovers half of a status poll, done per service on the poll's worker: what the
    /// service's runs have left running, remembered from one run to the next and from one start of
    /// the console to the next, and looked for in every state rather than only while it is stopped.
    /// </summary>
    /// <remarks>
    /// <para>
    /// What is remembered is every process seen under the service's wrapper, for as long as it
    /// runs — the same ID with the same start — whether or not it is under a wrapper still. A new
    /// wrapper used to wipe what the last one had, and that is the moment a leftover matters:
    /// Windows' recovery starts the next wrapper seconds after the last one went, the program that
    /// one left still holds the port, and a bare <c>python</c> or a venv's base interpreter has no
    /// path by which to be recognised again. Nor is a process forgotten for leaving the tree while
    /// its run goes on, as a program started through a launcher that exits does. What was found by
    /// its path while the service was stopped is remembered too, so that it is still told once the
    /// service is starting again, which is when the banner used to go.
    /// </para>
    /// <para>
    /// When a remembered process counts as left behind depends on the state. Stopped, or starting:
    /// whatever of it still runs, since nothing of a run that has not begun has been noted yet.
    /// Running, or stopping: what started before the wrapper now running did, which no process of
    /// that wrapper's can have; what started after it is this run's, under the wrapper or not. A
    /// reused ID passes for neither, since a remembered process is known by its start as well.
    /// </para>
    /// <para>
    /// The list goes into <see cref="RememberedRuns"/> too, so that a restart of the console does
    /// not forget it either, and what has exited is dropped from the file as it is from the list.
    /// Only what has run for <see cref="WorthKeeping"/> is written: a program's short-lived helpers
    /// would otherwise have the file written at every reading, and a process that came and went
    /// within a minute is not the one to look for after a restart.
    /// </para>
    /// </remarks>
    public static class StrayWatch
    {
        /// <summary>How long a process has to have run before it is kept in the file; see the remarks.</summary>
        internal static readonly TimeSpan WorthKeeping = TimeSpan.FromMinutes(1);

        /// <summary>
        /// Takes one service's leftovers into its sample: <see cref="ServiceSample.Remembered"/>, and
        /// whatever counts as left behind in the state it is in as <see cref="ServiceSample.Stray"/>.
        /// A port holder, looked for afterwards by <see cref="PortWatch"/>, takes the place of what
        /// is found here.
        /// </summary>
        /// <param name="sample">The service's sample, with what is under its wrapper filled in while it runs.</param>
        /// <param name="service">The service as the UI thread read it before the poll.</param>
        /// <param name="snapshot">The snapshot the sample was taken against.</param>
        /// <param name="wrapperNames">Image names of the WinSW wrappers on this machine.</param>
        /// <param name="consoleProcessId">This console, whose try runs are its own business.</param>
        /// <param name="imagePathOf">Reads a process's full image path; null when it cannot.</param>
        public static ServiceSample Look(
            ServiceSample sample,
            PolledService service,
            ProcessSnapshot snapshot,
            ISet<string> wrapperNames,
            int consoleProcessId,
            Func<int, string?> imagePathOf)
        {
            if (!sample.Queried)
            {
                return sample;
            }

            var under = sample.Descendants.IsDefault ? ImmutableArray<ProcessMark>.Empty : sample.Descendants;
            var underIds = new HashSet<int>(under.Select(process => process.ProcessId));

            // Out of the same snapshot, so the same ID is the same process. Oldest first: the banner
            // should name the same process from one reading to the next, and the oldest is the one
            // left longest, the top of whatever it left under it.
            var outside = StrayProcesses.StillRunning(snapshot, service.Remembered.IsDefault ? ImmutableArray<ProcessMark>.Empty : service.Remembered)
                .Where(record => !underIds.Contains(record.ProcessId))
                .GroupBy(record => record.ProcessId)
                .Select(same => same.First())
                .OrderBy(record => record.StartedAt ?? DateTime.MaxValue)
                .ThenBy(record => record.ProcessId)
                .ToList();

            StrayFinding? stray;
            switch (sample.Status)
            {
                case ServiceControllerStatus.Stopped:
                    stray = StrayProcesses.Find(snapshot, outside.Select(MarkOf).ToList(), service.Executable, wrapperNames, consoleProcessId, imagePathOf);
                    break;

                // Nothing of the run starting has been noted, so all that is remembered is an
                // earlier run's. Its program is not looked for by path: the new wrapper may be
                // starting it this moment, under a launcher the ancestry walk cannot yet see.
                case ServiceControllerStatus.StartPending:
                    stray = outside.Count > 0 ? StrayProcesses.FindingFor(snapshot, outside[0]) : null;
                    break;

                default:
                    stray = EarlierRun(snapshot, outside, RunStartOf(sample, service));
                    break;
            }

            var remembered = ImmutableArray.CreateBuilder<ProcessMark>(outside.Count + under.Length + 1);
            remembered.AddRange(outside.Select(MarkOf));
            remembered.AddRange(under);
            if (stray is { HoldsPort: false, Process: var found } && !remembered.Any(process => process.IsSameProcessAs(found)))
            {
                remembered.Add(found);
            }

            return sample with { Remembered = remembered.ToImmutable(), Stray = stray };
        }

        /// <summary>
        /// Writes what is worth keeping of <paramref name="remembered"/> for <paramref name="serviceName"/>
        /// into <paramref name="runs"/>: what has run for <see cref="WorthKeeping"/>, oldest first, so
        /// that the file's own cap keeps what was left longest. True when that changed the file.
        /// </summary>
        /// <param name="now">Local time, as a process's start is kept.</param>
        public static bool Keep(RememberedRuns runs, string serviceName, ImmutableArray<ProcessMark> remembered, DateTime now)
        {
            if (remembered.IsDefault)
            {
                return false;
            }

            var worth = remembered
                .Where(process => process.StartedAt is DateTime started && now - started >= WorthKeeping)
                .OrderBy(process => process.StartedAt)
                .ThenBy(process => process.ProcessId);

            return runs.NoteProcesses(serviceName, worth, now);
        }

        /// <summary>
        /// Where the run now going on began, for a service running or on its way to stopping: what
        /// started before it is an earlier run's. Null when that cannot be told, which finds nothing.
        /// </summary>
        private static DateTime? RunStartOf(in ServiceSample sample, PolledService service)
        {
            if (sample.HasProcess)
            {
                return sample.StartedAt;
            }

            // Running, with a wrapper the snapshot missed: it started after the snapshot was taken,
            // so everything in the snapshot started before it did.
            if (sample.Status == ServiceControllerStatus.Running && sample.ProcessId > 0)
            {
                return DateTime.MaxValue;
            }

            // Stopping, or paused: the wrapper is still the one last seen running, and the state
            // does not carry its ID. Its start was noted while it ran.
            return service.RunStartedAt;
        }

        /// <summary>The oldest of <paramref name="outside"/> that started before <paramref name="runStartedAt"/>, as an earlier run's.</summary>
        private static StrayFinding? EarlierRun(ProcessSnapshot snapshot, IReadOnlyList<ProcessRecord> outside, DateTime? runStartedAt)
        {
            if (runStartedAt is not DateTime before)
            {
                return null;
            }

            foreach (var record in outside)
            {
                if (record.StartedAt is DateTime started && started < before)
                {
                    return StrayProcesses.FindingFor(snapshot, record) with { EarlierRun = true };
                }
            }

            return null;
        }

        private static ProcessMark MarkOf(ProcessRecord record) => new(record.ProcessId, record.StartedAt, record.Name);
    }
}

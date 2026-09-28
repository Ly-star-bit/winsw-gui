using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace WinSW.Gui.Services
{
    /// <summary>
    /// What the console remembers of each service's runs from one start of the console to the next:
    /// the ports the service listened on, and the processes it had running. Kept per user as JSON
    /// under <c>%LOCALAPPDATA%\WinSW.Gui</c>, beside the settings.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both are facts that can only be read while the service runs, and are wanted when it does not:
    /// a service that cannot start because its port is taken never listens on anything, so the port
    /// to look for has to come from an earlier run — often from before the console itself was last
    /// started, or before the machine was.
    /// </para>
    /// <para>
    /// Ports are remembered per run, a run being one wrapper process, told apart from a later one
    /// given the same ID by its start. The first ports seen in a run replace what was remembered, so
    /// that a port the configuration has since moved off is not looked for forever; ports seen later
    /// in the same run are added, since a program can open its listeners one at a time as it starts.
    /// Nothing seen is never remembered as nothing: a service still starting listens on no port yet,
    /// and one failing on its port never does. Ports in the dynamic range are not remembered at all —
    /// they are the ones the system hands out at random, a JMX or debugger port, and one remembered
    /// today is some other program's tomorrow.
    /// </para>
    /// <para>
    /// Capped, since services come and go and nothing here forgets them otherwise: so many ports and
    /// processes a service, and so many services, the ones noted longest ago dropped first.
    /// </para>
    /// <para>
    /// The console's own bookkeeping, like the groups and the remembered start types: nothing is
    /// written to the service or its configuration. Read and written by the status poll's worker,
    /// and two polls can overlap, so every member takes a lock; the file is written only when what
    /// it holds changed, which is once or twice per run of a service, not once per poll.
    /// </para>
    /// </remarks>
    public sealed class RememberedRuns
    {
        internal const int MaxServices = 200;
        internal const int MaxPorts = 16;
        internal const int MaxProcesses = 32;

        /// <summary>Where the dynamic port range starts, the default since Windows Vista; see the remarks.</summary>
        internal const int DynamicPortsFrom = 49152;

        /// <summary>
        /// How long a run that has listened on nothing yet is looked at again at every reading; see
        /// <see cref="WantsPortsOf"/>. A server that has not opened its port two minutes after its
        /// wrapper started has either failed or is not a server.
        /// </summary>
        internal static readonly TimeSpan LearningPeriod = TimeSpan.FromMinutes(2);

        private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

        private static readonly Lazy<RememberedRuns> Shared = new(() => new RememberedRuns(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinSW.Gui",
            "remembered-runs.json")));

        private readonly object gate = new();
        private readonly string filePath;

        /// <summary>
        /// The run each service's ports were last looked at in, and whether any were seen in it: what
        /// tells a new run from the same one, and a run with something left to learn from one without.
        /// Not kept in the file: after a restart of the console the first ports seen replace what was
        /// remembered, which by then is the whole of a run's ports anyway.
        /// </summary>
        private readonly Dictionary<string, (ProcessMark Wrapper, bool PortsSeen)> lookedAt = new(StringComparer.OrdinalIgnoreCase);

        private Dictionary<string, Run>? runs;

        public RememberedRuns(string filePath) => this.filePath = filePath;

        /// <summary>The one instance the dashboard uses, over the file beside the settings.</summary>
        public static RememberedRuns Current => Shared.Value;

        /// <summary>The ports <paramref name="serviceName"/> last listened on, lowest first; empty when none are remembered.</summary>
        public ImmutableArray<int> PortsOf(string serviceName)
        {
            lock (this.gate)
            {
                return this.Load().TryGetValue(serviceName, out var run) ? run.Ports.ToImmutableArray() : ImmutableArray<int>.Empty;
            }
        }

        /// <summary>
        /// Whether the run under <paramref name="wrapper"/> has something to teach yet: it has not been
        /// looked at by this console, or it is young and has listened on nothing so far. Once its ports
        /// are seen, or it has run for <see cref="LearningPeriod"/> without any, it is not looked at for
        /// their sake again; a service on screen has its ports read regardless.
        /// </summary>
        /// <param name="serviceName">The service the run is of.</param>
        /// <param name="wrapper">The wrapper process, whose start tells one run from the next.</param>
        /// <param name="now">Local time, as a process's start is kept.</param>
        public bool WantsPortsOf(string serviceName, ProcessMark wrapper, DateTime now)
        {
            lock (this.gate)
            {
                if (!this.lookedAt.TryGetValue(serviceName, out var run) || !run.Wrapper.IsSameProcessAs(wrapper))
                {
                    return true;
                }

                return !run.PortsSeen && wrapper.StartedAt is DateTime started && now - started < LearningPeriod;
            }
        }

        /// <summary>
        /// Notes what the run under <paramref name="wrapper"/> listens on; see the remarks. True when
        /// that changed what is remembered, which is then saved.
        /// </summary>
        /// <param name="serviceName">The service the run is of.</param>
        /// <param name="wrapper">The wrapper process, whose start tells one run from the next.</param>
        /// <param name="ports">What the wrapper and everything under it listen on; none is noted as looked at.</param>
        /// <param name="now">When, for dropping the entries noted longest ago once there are too many.</param>
        public bool NotePorts(string serviceName, ProcessMark wrapper, IEnumerable<int> ports, DateTime now)
        {
            if (string.IsNullOrEmpty(serviceName))
            {
                return false;
            }

            var seen = ports.Where(IsWorthRemembering).Distinct().ToList();

            lock (this.gate)
            {
                // Ports seen earlier in this run are added to; a new run's first ports replace the
                // last run's, including when the new run listened on nothing when first looked at.
                bool addTo = this.lookedAt.TryGetValue(serviceName, out var run) && run.Wrapper.IsSameProcessAs(wrapper) && run.PortsSeen;
                this.lookedAt[serviceName] = (wrapper, addTo || seen.Count > 0);
                if (seen.Count == 0)
                {
                    return false;
                }

                var known = this.Load();
                known.TryGetValue(serviceName, out var remembered);
                var before = remembered?.Ports ?? new List<int>();
                var after = (addTo ? before.Concat(seen) : seen).Distinct().OrderBy(port => port).Take(MaxPorts).ToList();
                if (after.SequenceEqual(before))
                {
                    return false;
                }

                remembered ??= new Run();
                remembered.Ports = after;
                remembered.Noted = now;
                known[serviceName] = remembered;
                this.Save();
                return true;
            }
        }

        /// <summary>The processes remembered for <paramref name="serviceName"/>; empty when none are.</summary>
        public ImmutableArray<ProcessMark> ProcessesOf(string serviceName)
        {
            lock (this.gate)
            {
                return this.Load().TryGetValue(serviceName, out var run)
                    ? run.Processes.Select(process => new ProcessMark(process.Id, process.StartedAt, process.Name)).ToImmutableArray()
                    : ImmutableArray<ProcessMark>.Empty;
            }
        }

        /// <summary>
        /// Replaces the processes remembered for <paramref name="serviceName"/> with the first
        /// <see cref="MaxProcesses"/> of <paramref name="processes"/>; none forgets them. True when
        /// that changed what is remembered, which is then saved.
        /// </summary>
        public bool NoteProcesses(string serviceName, IEnumerable<ProcessMark> processes, DateTime now)
        {
            if (string.IsNullOrEmpty(serviceName))
            {
                return false;
            }

            var after = processes
                .Where(process => process.ProcessId > 0 && !string.IsNullOrEmpty(process.Name))
                .Take(MaxProcesses)
                .Select(process => new RememberedProcess { Id = process.ProcessId, StartedAt = process.StartedAt, Name = process.Name })
                .ToList();

            lock (this.gate)
            {
                var known = this.Load();
                known.TryGetValue(serviceName, out var run);
                var before = run?.Processes ?? new List<RememberedProcess>();
                if (after.SequenceEqual(before))
                {
                    return false;
                }

                run ??= new Run();
                run.Processes = after;
                run.Noted = now;

                // An entry with nothing left in it is dropped rather than kept empty, as reading
                // the file back would drop it.
                if (run.Ports.Count == 0 && run.Processes.Count == 0)
                {
                    known.Remove(serviceName);
                }
                else
                {
                    known[serviceName] = run;
                }

                this.Save();
                return true;
            }
        }

        /// <summary>A port a later run of the service can be expected to listen on again; see the remarks.</summary>
        private static bool IsWorthRemembering(int port) => port > 0 && port < DynamicPortsFrom;

        /// <summary>
        /// What of an entry read from the file is kept: it goes back through the limits it was
        /// written under, since the file is anyone's to edit, and an entry left with nothing in it
        /// is not kept at all.
        /// </summary>
        private static Run? Sanitized(Run? read)
        {
            if (read is null)
            {
                return null;
            }

            var run = new Run
            {
                Ports = (read.Ports ?? new List<int>())
                    .Where(IsWorthRemembering)
                    .Distinct()
                    .OrderBy(port => port)
                    .Take(MaxPorts)
                    .ToList(),
                Processes = (read.Processes ?? new List<RememberedProcess>())
                    .Where(process => process != null && process.Id > 0 && !string.IsNullOrEmpty(process.Name))
                    .Take(MaxProcesses)
                    .ToList(),
                Noted = read.Noted,
            };

            return run.Ports.Count > 0 || run.Processes.Count > 0 ? run : null;
        }

        /// <summary>Called under the lock.</summary>
        private Dictionary<string, Run> Load()
        {
            if (this.runs != null)
            {
                return this.runs;
            }

            // Service names are not case-sensitive; what the reader builds is.
            var loaded = new Dictionary<string, Run>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (File.Exists(this.filePath)
                    && JsonSerializer.Deserialize<Dictionary<string, Run?>>(File.ReadAllText(this.filePath)) is { } read)
                {
                    foreach (var (name, run) in read.OrderByDescending(pair => pair.Value?.Noted ?? default).Take(MaxServices))
                    {
                        if (!string.IsNullOrEmpty(name) && Sanitized(run) is { } kept)
                        {
                            loaded[name] = kept;
                        }
                    }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            {
                // Unreadable is the same as empty: the worst it costs is a port not looked for
                // until the service has run again.
            }

            return this.runs = loaded;
        }

        /// <summary>
        /// Called under the lock. Written beside the file and moved over it, so that a console
        /// ending halfway through a write leaves the last whole file rather than half of one.
        /// </summary>
        private void Save()
        {
            var known = this.runs!;
            while (known.Count > MaxServices)
            {
                known.Remove(known.MinBy(pair => pair.Value.Noted).Key);
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(this.filePath)!);
                string staging = this.filePath + ".tmp";
                File.WriteAllText(staging, JsonSerializer.Serialize(known, Options));
                File.Move(staging, this.filePath, overwrite: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Kept for this session all the same; only a restart of the console forgets it.
            }
        }

        /// <summary>One service's entry in the file.</summary>
        internal sealed class Run
        {
            public List<int> Ports { get; set; } = new();

            public List<RememberedProcess> Processes { get; set; } = new();

            /// <summary>When the entry last changed, for dropping the oldest once there are too many.</summary>
            public DateTime Noted { get; set; }
        }

        /// <summary>A <see cref="ProcessMark"/> as the file keeps it.</summary>
        internal sealed record RememberedProcess
        {
            public int Id { get; init; }

            public DateTime? StartedAt { get; init; }

            public string Name { get; init; } = string.Empty;
        }
    }
}

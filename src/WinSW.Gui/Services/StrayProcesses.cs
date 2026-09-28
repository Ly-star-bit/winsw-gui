using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using WinSW.Gui.Localization;

namespace WinSW.Gui.Services
{
    /// <summary>One process, told apart from a later one given the same ID by its start time.</summary>
    public readonly record struct ProcessMark(int ProcessId, DateTime? StartedAt, string Name)
    {
        /// <summary>The same process: the same ID and the same start. The name is not identity.</summary>
        public bool IsSameProcessAs(ProcessMark other) => this.ProcessId == other.ProcessId && this.StartedAt == other.StartedAt;
    }

    /// <summary>
    /// A process left running, and the process that started it when that is still alive: what
    /// keeps bringing back a program that has been ended, if something does.
    /// </summary>
    /// <param name="Process">The process found.</param>
    /// <param name="Parent">What started it, while that still runs.</param>
    /// <param name="Port">
    /// The port the service last listened on that the process now holds, when that is how it was
    /// found; 0 when it was found as the service's own program. See <see cref="StrayProcesses.FindPortHolder"/>.
    /// </param>
    /// <param name="EarlierRun">
    /// Found while the service runs, or is stopping, as what an earlier run of it left: the service
    /// has a program of its own again, and this one runs beside it. See <see cref="StrayWatch"/>.
    /// </param>
    public readonly record struct StrayFinding(ProcessMark Process, ProcessMark? Parent, int Port = 0, bool EarlierRun = false)
    {
        /// <summary>Found holding the service's port rather than as its program.</summary>
        public bool HoldsPort => this.Port != 0;

        /// <summary>
        /// The finding in words, for the banner: what was found, who started it, and what to make
        /// of it.
        /// </summary>
        /// <param name="serviceName">The service the finding is about.</param>
        /// <param name="format">Looks a phrase up by key and fills it in: <c>Localizer.Format</c>.</param>
        public StrayText Describe(string serviceName, Func<string, object?[], string> format)
        {
            var process = this.Process;
            if (!this.HoldsPort)
            {
                // Beside a running service the leftover is not what the next start fails on — the
                // start has happened — but a second copy of the program, working next to the first.
                return new StrayText(
                    format(this.EarlierRun ? "M.Dash.StrayEarlierRun" : "M.Dash.StrayBanner", new object?[] { process.Name, process.ProcessId }),
                    this.Parent is { } parent
                        ? format("M.Dash.StrayParent", new object?[] { parent.Name, parent.ProcessId })
                        : format("M.Dash.StrayOrphan", Array.Empty<object?>()),
                    format(this.EarlierRun ? "M.Dash.StrayEarlierRunHint" : "M.Dash.StrayHint", Array.Empty<object?>()));
            }

            string banner = this.Parent is { } starter
                ? format("M.Dash.PortHeldBy", new object?[] { this.Port, process.Name, process.ProcessId, starter.Name })
                : format("M.Dash.PortHeld", new object?[] { this.Port, process.Name, process.ProcessId });

            // What is not to be ended is said so, in place of who started it: there is no button
            // for it, and an explanation of what would restart it would only suggest there were.
            string about = !StrayProcesses.MayEnd(process)
                ? process.ProcessId == 4
                    ? format("M.Dash.PortHeldBySystem", Array.Empty<object?>())
                    : format("M.Dash.PortHeldByWindows", new object?[] { process.Name })
                : this.Parent is { } parentOfHolder
                    ? format("M.Dash.PortParent", new object?[] { parentOfHolder.Name, parentOfHolder.ProcessId, process.Name })
                    : format("M.Dash.StrayOrphan", Array.Empty<object?>());

            return new StrayText(banner, about, format("M.Dash.PortHint", new object?[] { serviceName }));
        }
    }

    /// <summary>A <see cref="StrayFinding"/> in words: the banner's three lines.</summary>
    /// <param name="Banner">What was found.</param>
    /// <param name="Parent">Who started it, or why it is not to be ended.</param>
    /// <param name="Hint">What to make of it.</param>
    public sealed record StrayText(string Banner, string Parent, string Hint);

    /// <summary>
    /// The program of a stopped service, still running outside it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A service reads as stopped when its wrapper has gone, whatever became of the program the
    /// wrapper started. A wrapper that crashed, or was ended from Task Manager, leaves that
    /// program running with nobody watching it: the dashboard says Stopped, the program is
    /// plainly up, and starting the service tends to fail on the port or the files the orphan
    /// still holds.
    /// </para>
    /// <para>
    /// Two signs, the first exact and the second careful. While a service runs, the processes
    /// under its wrapper are noted; one still alive after the service stops — the same ID with
    /// the same start time — was left behind. That needs no permissions and no guessing, but
    /// only covers what this console saw start. For the rest, a process running the service's
    /// executable counts only when its full path matches, read from the process itself; when
    /// that cannot be read the process is not claimed. An executable named bare, <c>java</c> or
    /// <c>python</c>, has no path to match and is left to the first sign: half the machine runs
    /// java.exe. Nor is a process claimed whose ancestry leads to a WinSW wrapper — another
    /// service or a desktop task running the same program — or to this console, which is a
    /// try run.
    /// </para>
    /// <para>
    /// A third sign covers what neither recognises — a venv's base interpreter, a copy started by
    /// hand: the port. What holds a port the service listened on when it last ran, outside every
    /// wrapper, is what its next start will fail on, whoever it is; see <see cref="FindPortHolder"/>.
    /// </para>
    /// <para>
    /// What was noted is remembered past the next start, and past a restart of the console, and
    /// looked for while the service runs as well: a leftover matters most when the service has
    /// started again beside it, which under Windows' recovery is seconds after the wrapper went.
    /// See <see cref="StrayWatch"/>.
    /// </para>
    /// </remarks>
    public static class StrayProcesses
    {
        /// <summary>Everything under <paramref name="processId"/>, for noticing later what outlived it.</summary>
        /// <remarks>
        /// A parent link is a process ID, and IDs are reused: a wrapper given the ID of a launcher
        /// that has exited would otherwise count the launcher's orphans as its own, and have them
        /// remembered, found left behind and offered for ending as the service's. A child that
        /// started before its supposed parent was started by an earlier holder of the ID, and is
        /// left out with everything under it, as <see cref="ProcessTreeProvider"/> leaves it out.
        /// </remarks>
        public static ImmutableArray<ProcessMark> DescendantsOf(ProcessSnapshot snapshot, int processId)
        {
            var found = ImmutableArray.CreateBuilder<ProcessMark>();
            if (!snapshot.TryGet(processId, out var root))
            {
                return found.ToImmutable();
            }

            var pending = new Stack<(int Child, DateTime? ParentStartedAt)>();
            var seen = new HashSet<int> { processId };
            PushChildren(root);

            while (pending.Count > 0)
            {
                var (child, parentStartedAt) = pending.Pop();
                if (!seen.Add(child)
                    || !snapshot.TryGet(child, out var record)
                    || (record.StartedAt is DateTime childStart && parentStartedAt is DateTime parentStart && childStart < parentStart))
                {
                    continue;
                }

                found.Add(new ProcessMark(record.ProcessId, record.StartedAt, record.Name));
                PushChildren(record);
            }

            return found.ToImmutable();

            void PushChildren(in ProcessRecord parent)
            {
                foreach (int childId in snapshot.ChildrenOf(parent.ProcessId))
                {
                    pending.Push((childId, parent.StartedAt));
                }
            }
        }

        /// <summary>
        /// A process a stopped service has left running, or null.
        /// </summary>
        /// <param name="remembered">What was under the service's wrapper while it last ran.</param>
        /// <param name="executablePath">The service's program, full path; null when it is named bare.</param>
        /// <param name="wrapperNames">Image names of the WinSW wrappers on this machine.</param>
        /// <param name="consoleProcessId">This console, whose try runs are its own business.</param>
        /// <param name="imagePathOf">Reads a process's full image path; null when it cannot.</param>
        public static StrayFinding? Find(
            ProcessSnapshot snapshot,
            IReadOnlyList<ProcessMark> remembered,
            string? executablePath,
            ISet<string> wrapperNames,
            int consoleProcessId,
            Func<int, string?> imagePathOf)
        {
            foreach (var mark in remembered)
            {
                if (snapshot.TryGet(mark.ProcessId, out var record) && SameStart(record.StartedAt, mark.StartedAt))
                {
                    return FindingFor(snapshot, record);
                }
            }

            if (executablePath is null)
            {
                return null;
            }

            string name = Path.GetFileName(executablePath);
            foreach (var record in snapshot.All)
            {
                if (!string.Equals(record.Name, name, StringComparison.OrdinalIgnoreCase)
                    || IsOwned(snapshot, record, wrapperNames, consoleProcessId))
                {
                    continue;
                }

                // Only now is the process opened, and only for its name's sake: a handful of
                // candidates a poll, not every process on the machine.
                if (string.Equals(imagePathOf(record.ProcessId), executablePath, StringComparison.OrdinalIgnoreCase))
                {
                    return FindingFor(snapshot, record);
                }
            }

            return null;
        }

        /// <summary>
        /// The processes among <paramref name="marks"/> that still run, in the order given: the same
        /// ID, started at the same moment. One that has exited, or whose ID the system has since
        /// given to another process, is left out.
        /// </summary>
        internal static IEnumerable<ProcessRecord> StillRunning(ProcessSnapshot snapshot, IEnumerable<ProcessMark> marks)
        {
            foreach (var mark in marks)
            {
                if (snapshot.TryGet(mark.ProcessId, out var record) && SameStart(record.StartedAt, mark.StartedAt))
                {
                    yield return record;
                }
            }
        }

        /// <summary>
        /// A process outside every wrapper that holds one of the ports a service last listened on,
        /// or null: what keeps the service from starting, whether or not it is the service's own
        /// program. See <see cref="RememberedRuns"/> for where the ports come from, and
        /// <see cref="PortWatch"/> for when they are looked for.
        /// </summary>
        /// <remarks>
        /// A holder under a wrapper — another service, a desktop task, a try run from this console —
        /// is left alone, as <see cref="Find"/> leaves it: it is somebody's, and ending it from this
        /// service's banner would be the wrong place. The kernel is never a holder to name by PID 0;
        /// PID 4 is named, since HTTP.sys takes its ports in System's name, but is not offered for
        /// ending, which <see cref="MayEnd"/> sees to.
        /// </remarks>
        /// <param name="ports">The machine's listeners, read once for the poll.</param>
        /// <param name="remembered">The ports the service listened on in its last run, lowest first.</param>
        /// <param name="wrapperNames">Image names of the WinSW wrappers on this machine.</param>
        /// <param name="consoleProcessId">This console, whose try runs are its own business.</param>
        public static StrayFinding? FindPortHolder(
            ProcessSnapshot snapshot,
            PortTable ports,
            IReadOnlyList<int> remembered,
            ISet<string> wrapperNames,
            int consoleProcessId)
        {
            foreach (int port in remembered)
            {
                foreach (int holder in ports.HoldersOf(port))
                {
                    // Not in the snapshot: started after it was taken, and named at the next poll.
                    if (holder <= 0
                        || holder == consoleProcessId
                        || !snapshot.TryGet(holder, out var record)
                        || wrapperNames.Contains(record.Name)
                        || IsOwned(snapshot, record, wrapperNames, consoleProcessId))
                    {
                        continue;
                    }

                    // System's parent is Idle, and nothing is "started by Idle".
                    var finding = FindingFor(snapshot, record);
                    return finding with { Parent = finding.Parent is { ProcessId: > 4 } ? finding.Parent : null, Port = port };
                }
            }

            return null;
        }

        /// <summary>
        /// The process, with its parent when that is still running. A parent that started after
        /// its child holds a reused ID and is not the parent; one that has exited is none.
        /// </summary>
        internal static StrayFinding FindingFor(ProcessSnapshot snapshot, ProcessRecord record)
        {
            var process = new ProcessMark(record.ProcessId, record.StartedAt, record.Name);

            if (record.ParentProcessId != record.ProcessId
                && snapshot.TryGet(record.ParentProcessId, out var parent)
                && !(parent.StartedAt is DateTime parentStart && record.StartedAt is DateTime childStart && parentStart > childStart))
            {
                return new StrayFinding(process, new ProcessMark(parent.ProcessId, parent.StartedAt, parent.Name));
            }

            return new StrayFinding(process, null);
        }

        /// <summary>
        /// Whether a wrapper or this console is among the process's ancestors. The walk stops at a
        /// parent that is gone, or that started after its child — a process ID the system has
        /// since given to something else.
        /// </summary>
        internal static bool IsOwned(ProcessSnapshot snapshot, ProcessRecord record, ISet<string> wrapperNames, int consoleProcessId)
        {
            var current = record;
            for (int depth = 0; depth < 64; depth++)
            {
                if (current.ParentProcessId == current.ProcessId
                    || !snapshot.TryGet(current.ParentProcessId, out var parent)
                    || (parent.StartedAt is DateTime parentStart && current.StartedAt is DateTime childStart && parentStart > childStart))
                {
                    return false;
                }

                if (parent.ProcessId == consoleProcessId || wrapperNames.Contains(parent.Name))
                {
                    return true;
                }

                current = parent;
            }

            return false;
        }

        /// <summary>
        /// Windows' own processes, which are never offered for ending however a stray program came
        /// to be under one of them: the shell, the service host, the session manager and the rest.
        /// A command prompt or PowerShell is not among them — a script looping over the program is
        /// exactly the parent worth ending, and the confirmation names it before anything happens.
        /// </summary>
        private static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
        {
            "System", "Registry", "Idle", "smss.exe", "csrss.exe", "wininit.exe", "winlogon.exe",
            "services.exe", "lsass.exe", "lsaiso.exe", "svchost.exe", "explorer.exe", "taskhostw.exe",
            "taskeng.exe", "dwm.exe", "sihost.exe", "fontdrvhost.exe", "userinit.exe", "spoolsv.exe",
            "wmiprvse.exe", "dllhost.exe", "conhost.exe", "runtimebroker.exe", "ctfmon.exe",
            "searchhost.exe", "startmenuexperiencehost.exe", "shellexperiencehost.exe", "mmc.exe",
        };

        /// <summary>Whether a process may be offered for ending at all; see <see cref="Protected"/>.</summary>
        public static bool MayEnd(ProcessMark process) =>
            process.ProcessId > 4 && process.ProcessId != Environment.ProcessId && !Protected.Contains(process.Name);

        /// <summary>
        /// Ends the process and everything it started. Checked first to still be the process that
        /// was found — an ID is reused once its process has gone — and done with administrator
        /// rights when the program runs as an account this user may not end.
        /// </summary>
        public static async Task<CommandResult> TerminateAsync(ProcessMark mark)
        {
            try
            {
                using var process = Process.GetProcessById(mark.ProcessId);
                if (mark.StartedAt is DateTime started && Math.Abs((process.StartTime - started).TotalSeconds) > 1)
                {
                    return CommandResult.Failed(Localizer.Get("M.Dash.StrayGone"));
                }

                bool refused = await Task.Run(() => EndTree(process)).ConfigureAwait(false);
                return refused ? await KillElevatedAsync(mark).ConfigureAwait(false) : CommandResult.Ok();
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException)
            {
                // Not running any more, or it exited between being found and being ended: the
                // outcome that was wanted, and no reason to raise a prompt over it.
                return CommandResult.Ok();
            }
            catch (Win32Exception)
            {
                // Its start could not even be read: the program runs as an account this user may
                // not look into, let alone end.
                return await KillElevatedAsync(mark).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Whether ending a process failed on a refusal — access denied, most likely — rather than
        /// on the process having gone. <see cref="Process.Kill(bool)"/> with the whole tree does not
        /// stop at a refusal: it ends what it may and throws what it was refused together, as an
        /// <see cref="AggregateException"/> of <see cref="Win32Exception"/>s.
        /// </summary>
        internal static bool IsRefusal(Exception e) => e switch
        {
            Win32Exception => true,
            AggregateException all => all.InnerExceptions.Count > 0 && all.InnerExceptions.All(IsRefusal),
            _ => false,
        };

        /// <summary>
        /// Ends the process and everything under it, on a worker. True when the process itself was
        /// refused and still runs, which is for the elevated taskkill to end with its tree.
        /// </summary>
        /// <remarks>
        /// Refused only something under it, the process itself has been ended: taskkill's /T walks
        /// the tree from the process it is given, and would have nothing to walk from. What is left
        /// is found again at the next reading — among what was noted under the wrapper, or holding
        /// the port — and offered on its own, where ending it meets the refusal head on.
        /// </remarks>
        private static bool EndTree(Process process)
        {
            try
            {
                process.Kill(entireProcessTree: true);
                return false;
            }
            catch (Exception e) when (IsRefusal(e))
            {
                return !HasEnded(process);
            }
        }

        /// <summary>
        /// Whether the process has gone, given the moment ending it takes: TerminateProcess returns
        /// before the process is over. One that cannot even be waited on is taken to be running.
        /// </summary>
        private static bool HasEnded(Process process)
        {
            try
            {
                return process.WaitForExit(500);
            }
            catch (Exception e) when (e is Win32Exception or InvalidOperationException)
            {
                return false;
            }
        }

        /// <summary>
        /// Ends the process and its tree with administrator rights. Filtered on the image name, so
        /// that an ID reused by now cannot take something else down with it.
        /// </summary>
        private static Task<CommandResult> KillElevatedAsync(ProcessMark mark) =>
            WinSwCli.KillProcessElevatedAsync(mark.ProcessId, mark.Name);

        /// <summary>
        /// Within a second, the tolerance <see cref="TerminateAsync"/> uses too. Two processes
        /// never hold one ID at once, so a later one given it cannot have started within a second
        /// of the one noted — which was alive then.
        /// </summary>
        private static bool SameStart(DateTime? a, DateTime? b) =>
            a is DateTime x && b is DateTime y ? Math.Abs((x - y).TotalSeconds) < 1 : a is null && b is null;
    }
}

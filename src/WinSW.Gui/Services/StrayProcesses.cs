using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using WinSW.Gui.Localization;

namespace WinSW.Gui.Services
{
    /// <summary>One process, told apart from a later one given the same ID by its start time.</summary>
    public readonly record struct ProcessMark(int ProcessId, DateTime? StartedAt, string Name);

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
    /// </remarks>
    public static class StrayProcesses
    {
        /// <summary>Everything under <paramref name="processId"/>, for noticing later what outlived it.</summary>
        public static ImmutableArray<ProcessMark> DescendantsOf(ProcessSnapshot snapshot, int processId)
        {
            var found = ImmutableArray.CreateBuilder<ProcessMark>();
            var pending = new Stack<int>(snapshot.ChildrenOf(processId));
            var seen = new HashSet<int> { processId };

            while (pending.Count > 0)
            {
                int child = pending.Pop();
                if (!seen.Add(child) || !snapshot.TryGet(child, out var record))
                {
                    continue;
                }

                found.Add(new ProcessMark(record.ProcessId, record.StartedAt, record.Name));
                foreach (int grandchild in snapshot.ChildrenOf(child))
                {
                    pending.Push(grandchild);
                }
            }

            return found.ToImmutable();
        }

        /// <summary>
        /// A process a stopped service has left running, or null.
        /// </summary>
        /// <param name="remembered">What was under the service's wrapper while it last ran.</param>
        /// <param name="executablePath">The service's program, full path; null when it is named bare.</param>
        /// <param name="wrapperNames">Image names of the WinSW wrappers on this machine.</param>
        /// <param name="consoleProcessId">This console, whose try runs are its own business.</param>
        /// <param name="imagePathOf">Reads a process's full image path; null when it cannot.</param>
        public static ProcessMark? Find(
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
                    return mark;
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
                    return new ProcessMark(record.ProcessId, record.StartedAt, record.Name);
                }
            }

            return null;
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

                await Task.Run(() => process.Kill(entireProcessTree: true)).ConfigureAwait(false);
                return CommandResult.Ok();
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException)
            {
                // Not running any more, or it exited between being found and being ended: the
                // outcome that was wanted, and no reason to raise a prompt over it.
                return CommandResult.Ok();
            }
            catch (Win32Exception)
            {
                // Access denied, most likely: the program runs as the service's account. The
                // elevated kill is filtered on the image name, so an ID reused by now cannot take
                // something else down with it.
                return await WinSwCli.KillProcessElevatedAsync(mark.ProcessId, mark.Name).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Within a second, the tolerance <see cref="TerminateAsync"/> uses too. Two processes
        /// never hold one ID at once, so a later one given it cannot have started within a second
        /// of the one noted — which was alive then.
        /// </summary>
        private static bool SameStart(DateTime? a, DateTime? b) =>
            a is DateTime x && b is DateTime y ? Math.Abs((x - y).TotalSeconds) < 1 : a is null && b is null;
    }
}

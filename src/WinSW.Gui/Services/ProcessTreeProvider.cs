using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace WinSW.Gui.Services
{
    /// <summary>A process and everything it spawned.</summary>
    public sealed class ProcessNode
    {
        public ProcessNode(int processId, string name)
        {
            this.ProcessId = processId;
            this.Name = name;
        }

        public int ProcessId { get; }

        public string Name { get; }

        public ObservableCollection<ProcessNode> Children { get; } = new();

        public string Caption => $"{this.Name}  ·  {this.ProcessId}";
    }

    /// <summary>
    /// Builds the descendant tree of a process out of a <see cref="ProcessSnapshot"/>.
    /// </summary>
    /// <remarks>
    /// This is what <c>winsw dev ps</c> prints, computed without elevation. The snapshot
    /// gives every process's parent in one pass, so the tree is consistent rather than
    /// stitched together from several point-in-time queries — and when the dashboard has
    /// already taken one for the status poll, the tree comes out of that same reading.
    /// </remarks>
    public static class ProcessTreeProvider
    {
        /// <summary>
        /// True when two trees have the same processes in the same places. The dashboard
        /// rebuilds the tree from a fresh snapshot every poll and only pushes it to the view
        /// when this says something changed; replacing the TreeView's source each tick would
        /// flicker and drop the user's selection.
        /// </summary>
        public static bool SameShape(ProcessNode? a, ProcessNode? b)
        {
            if (a is null || b is null)
            {
                return a is null && b is null;
            }

            if (a.ProcessId != b.ProcessId || a.Name != b.Name || a.Children.Count != b.Children.Count)
            {
                return false;
            }

            for (int i = 0; i < a.Children.Count; i++)
            {
                if (!SameShape(a.Children[i], b.Children[i]))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>Takes a snapshot of its own and builds the tree from it.</summary>
        public static ProcessNode? Build(int rootProcessId) =>
            rootProcessId > 0 && ProcessSnapshot.Take() is { } snapshot ? Build(snapshot, rootProcessId) : null;

        /// <summary>
        /// Builds the tree under <paramref name="rootProcessId"/> from a snapshot already
        /// taken; null when the process is not in it.
        /// </summary>
        public static ProcessNode? Build(ProcessSnapshot snapshot, int rootProcessId)
        {
            if (rootProcessId <= 0 || !snapshot.TryGet(rootProcessId, out var root))
            {
                return null;
            }

            // Process IDs are recycled, so a parent link can point at a process that started
            // later. Two guards: a child that is older than its supposed parent was spawned
            // by a previous holder of that ID and is left out, and what has already been
            // visited is not visited again, which keeps a stale link from looping forever.
            var visited = new HashSet<int>();
            return Expand(root);

            ProcessNode? Expand(ProcessRecord record)
            {
                if (!visited.Add(record.ProcessId))
                {
                    return null;
                }

                var node = new ProcessNode(record.ProcessId, record.Name);

                foreach (int childId in snapshot.ChildrenOf(record.ProcessId))
                {
                    if (!snapshot.TryGet(childId, out var child) || StartedBefore(child, record))
                    {
                        continue;
                    }

                    if (Expand(child) is { } childNode)
                    {
                        node.Children.Add(childNode);
                    }
                }

                return node;
            }

            static bool StartedBefore(in ProcessRecord child, in ProcessRecord parent) =>
                child.StartedAt is { } childStart && parent.StartedAt is { } parentStart && childStart < parentStart;
        }
    }
}

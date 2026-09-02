using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Runtime.InteropServices;

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
    /// Builds the descendant tree of a process from a single Toolhelp32 snapshot.
    /// </summary>
    /// <remarks>
    /// This is what <c>winsw dev ps</c> prints, computed without elevation. The snapshot
    /// gives every process's parent in one pass, so the tree is consistent rather than
    /// stitched together from several point-in-time queries.
    /// </remarks>
    public static class ProcessTreeProvider
    {
        public static ProcessNode? Build(int rootProcessId)
        {
            if (rootProcessId <= 0)
            {
                return null;
            }

            var names = new Dictionary<int, string>();
            var childrenByParent = new Dictionary<int, List<int>>();

            IntPtr snapshot = NativeMethods.CreateToolhelp32Snapshot(NativeMethods.TH32CS_SNAPPROCESS, 0);
            if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1))
            {
                return null;
            }

            try
            {
                var entry = default(NativeMethods.PROCESSENTRY32);
                entry.Size = Marshal.SizeOf<NativeMethods.PROCESSENTRY32>();

                if (!NativeMethods.Process32FirstW(snapshot, ref entry))
                {
                    return null;
                }

                do
                {
                    names[entry.ProcessId] = entry.ExeFile;

                    if (!childrenByParent.TryGetValue(entry.ParentProcessId, out var siblings))
                    {
                        siblings = new List<int>();
                        childrenByParent[entry.ParentProcessId] = siblings;
                    }

                    siblings.Add(entry.ProcessId);
                }
                while (NativeMethods.Process32NextW(snapshot, ref entry));
            }
            finally
            {
                NativeMethods.CloseHandle(snapshot);
            }

            if (!names.ContainsKey(rootProcessId))
            {
                return null;
            }

            // Process IDs are recycled, so a parent link can point at a process that started
            // later. Tracking what has been visited keeps a stale link from looping forever.
            var visited = new HashSet<int>();
            return Expand(rootProcessId);

            ProcessNode? Expand(int processId)
            {
                if (!visited.Add(processId))
                {
                    return null;
                }

                var node = new ProcessNode(processId, names.TryGetValue(processId, out string? name) ? name : "(exited)");

                if (childrenByParent.TryGetValue(processId, out var children))
                {
                    children.Sort();
                    foreach (int child in children)
                    {
                        if (Expand(child) is { } childNode)
                        {
                            node.Children.Add(childNode);
                        }
                    }
                }

                return node;
            }
        }
    }
}

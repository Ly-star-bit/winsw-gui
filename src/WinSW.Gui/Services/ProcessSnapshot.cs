using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;

namespace WinSW.Gui.Services
{
    /// <summary>One process as the snapshot saw it.</summary>
    public readonly struct ProcessRecord
    {
        public ProcessRecord(int processId, int parentProcessId, string name, DateTime? startedAt, TimeSpan processorTime, long workingSetBytes, int handleCount)
        {
            this.ProcessId = processId;
            this.ParentProcessId = parentProcessId;
            this.Name = name;
            this.StartedAt = startedAt;
            this.ProcessorTime = processorTime;
            this.WorkingSetBytes = workingSetBytes;
            this.HandleCount = handleCount;
        }

        public int ProcessId { get; }

        /// <summary>
        /// The process that started this one. A link, not a fact: process IDs are recycled,
        /// and the parent may since have exited and had its number given to something else.
        /// </summary>
        public int ParentProcessId { get; }

        /// <summary>The image file's name, "WinSW.exe".</summary>
        public string Name { get; }

        /// <summary>Local time; null for the two kernel processes that have none.</summary>
        public DateTime? StartedAt { get; }

        /// <summary>Kernel and user time together.</summary>
        public TimeSpan ProcessorTime { get; }

        public long WorkingSetBytes { get; }

        public int HandleCount { get; }
    }

    /// <summary>
    /// Every process on the machine at one instant, from a single
    /// <c>NtQuerySystemInformation(SystemProcessInformation)</c> call.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the reading underneath every other way of asking. The runtime's
    /// <see cref="System.Diagnostics.Process"/> takes it in full to answer its first counter —
    /// <c>WorkingSet64</c>, <c>HandleCount</c> — and keeps the one entry it was asked about, so
    /// a poll that opened a <c>Process</c> per running service was taking the whole snapshot
    /// once per service. Toolhelp, which the process tree used, takes it once more and copies
    /// it into a heap. Taking it once here and looking things up in it is the same information
    /// at a fraction of the cost.
    /// </para>
    /// <para>
    /// It also asks for nothing. Reading a counter through <c>Process</c> means opening the
    /// process, and a standard user cannot open a LocalSystem service's process for much;
    /// the snapshot carries every process's times and counters regardless of who is asking,
    /// which is how a start time is available for a service this user could not otherwise
    /// look into.
    /// </para>
    /// </remarks>
    public sealed class ProcessSnapshot
    {
        /// <summary>
        /// The first size tried. Half a megabyte holds a few hundred processes with their
        /// threads; what a machine actually needed is remembered for the next call.
        /// </summary>
        private const int InitialBufferSize = 512 * 1024;

        /// <summary>
        /// Room for the processes that start between the call that reports the size and the
        /// call that uses it. The runtime allows ten kilobytes; a busy machine spawning a
        /// build's worth of compilers has outrun that.
        /// </summary>
        private const int Slack = 64 * 1024;

        private static int lastBufferSize = InitialBufferSize;

        private readonly Dictionary<int, ProcessRecord> byId;
        private readonly Dictionary<int, List<int>> childrenByParent;

        /// <summary>Builds a snapshot from records already in hand; the tests use it to describe a machine.</summary>
        internal ProcessSnapshot(IEnumerable<ProcessRecord> records)
        {
            this.byId = new Dictionary<int, ProcessRecord>();
            this.childrenByParent = new Dictionary<int, List<int>>();

            foreach (var record in records)
            {
                this.byId[record.ProcessId] = record;

                if (!this.childrenByParent.TryGetValue(record.ParentProcessId, out var siblings))
                {
                    siblings = new List<int>();
                    this.childrenByParent[record.ParentProcessId] = siblings;
                }

                siblings.Add(record.ProcessId);
            }

            foreach (var siblings in this.childrenByParent.Values)
            {
                siblings.Sort();
            }
        }

        public int Count => this.byId.Count;

        /// <summary>
        /// Reads the machine. Null when the call fails, which on a supported Windows it does
        /// not: the runtime's own process class would be failing the same way.
        /// </summary>
        public static ProcessSnapshot? Take()
        {
            int size = lastBufferSize;

            // A bounded number of tries. The size the call reports is what was needed at the
            // moment it looked; the buffer allocated to it can be too small again if the
            // machine is spawning faster than the slack covers, but not indefinitely.
            for (int attempt = 0; attempt < 8; attempt++)
            {
                IntPtr buffer = Marshal.AllocHGlobal(size);
                try
                {
                    int status = NativeMethods.NtQuerySystemInformation(NativeMethods.SystemProcessInformation, buffer, size, out int needed);

                    if (status == unchecked((int)NativeMethods.STATUS_INFO_LENGTH_MISMATCH))
                    {
                        size = needed + Slack;
                        continue;
                    }

                    if (status < 0 || needed <= 0)
                    {
                        return null;
                    }

                    lastBufferSize = Math.Max(size, needed + Slack);
                    return new ProcessSnapshot(Parse(buffer, needed));
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }

            return null;
        }

        public bool TryGet(int processId, out ProcessRecord record) => this.byId.TryGetValue(processId, out record);

        /// <summary>The processes whose parent link names <paramref name="processId"/>, lowest ID first.</summary>
        public IReadOnlyList<int> ChildrenOf(int processId) =>
            this.childrenByParent.TryGetValue(processId, out var children) ? children : Array.Empty<int>();

        private static List<ProcessRecord> Parse(IntPtr buffer, int length)
        {
            var records = new List<ProcessRecord>(256);
            int headSize = Marshal.SizeOf<NativeMethods.SYSTEM_PROCESS_INFORMATION>();
            int offset = 0;

            while (offset >= 0 && offset + headSize <= length)
            {
                var entry = Marshal.PtrToStructure<NativeMethods.SYSTEM_PROCESS_INFORMATION>(buffer + offset);

                int processId = unchecked((int)entry.UniqueProcessId.ToInt64());
                int parentId = unchecked((int)entry.InheritedFromUniqueProcessId.ToInt64());

                records.Add(new ProcessRecord(
                    processId,
                    parentId,
                    NameOf(entry, processId),
                    entry.CreateTime > 0 ? DateTime.FromFileTimeUtc(entry.CreateTime).ToLocalTime() : null,
                    TimeSpan.FromTicks(entry.KernelTime + entry.UserTime),
                    unchecked((long)entry.WorkingSetSize.ToUInt64()),
                    unchecked((int)entry.HandleCount)));

                if (entry.NextEntryOffset == 0)
                {
                    break;
                }

                offset += unchecked((int)entry.NextEntryOffset);
            }

            return records;
        }

        /// <summary>
        /// The image name, or the names Task Manager gives the two processes that have none.
        /// Read here, while the buffer the string lives in is still allocated.
        /// </summary>
        private static string NameOf(in NativeMethods.SYSTEM_PROCESS_INFORMATION entry, int processId)
        {
            if (entry.ImageName.Buffer != IntPtr.Zero && entry.ImageName.Length >= sizeof(char))
            {
                return Marshal.PtrToStringUni(entry.ImageName.Buffer, entry.ImageName.Length / sizeof(char));
            }

            return processId switch
            {
                0 => "Idle",
                4 => "System",
                _ => processId.ToString(CultureInfo.InvariantCulture),
            };
        }
    }
}

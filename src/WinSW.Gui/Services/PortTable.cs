using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Net;

namespace WinSW.Gui.Services
{
    /// <summary>
    /// One listening TCP socket: the address it accepts on, its port, and the process that opened
    /// it. Plain values, so that a reading can carry it back from the poll's worker.
    /// </summary>
    public readonly record struct ListeningPort(string Address, int Port, int ProcessId)
    {
        /// <summary>An IPv6 address, which alone has colons in it.</summary>
        public bool IsIPv6 => this.Address.Contains(':', StringComparison.Ordinal);

        /// <summary>"0.0.0.0:8000" or "[::]:8000", the way netstat and a URL write it.</summary>
        public string Endpoint => this.IsIPv6
            ? "[" + this.Address + "]:" + this.Port.ToString(CultureInfo.InvariantCulture)
            : this.Address + ":" + this.Port.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Every TCP port listened on on this machine, with the process listening on it: what
    /// <c>netstat -ano</c> prints for the LISTENING rows, from
    /// <c>GetExtendedTcpTable(TCP_TABLE_OWNER_PID_LISTENER)</c> for IPv4 and for IPv6.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A port already taken is the commonest reason a web service here fails to start and then
    /// fails again at every restart, and the program's own words for it — "address already in
    /// use", "error while attempting to bind" — name neither the port nor who has it. The table
    /// names both. It asks for no rights: the owner of a listener is readable by a standard user
    /// whatever account the owner runs as.
    /// </para>
    /// <para>
    /// The owner is the process that opened the socket. A listener handed down to child
    /// processes, as a server with several workers does, is owned by the parent that opened it;
    /// one opened through HTTP.sys — IIS, or a program using HttpListener — is owned by System,
    /// PID 4, whichever program asked for it.
    /// </para>
    /// </remarks>
    public sealed class PortTable
    {
        /// <summary>dwNumEntries, ahead of the rows.</summary>
        internal const int HeadSize = 4;

        /// <summary>MIB_TCPROW_OWNER_PID: state, local address, local port, remote address, remote port, owner.</summary>
        internal const int IPv4RowSize = 24;

        /// <summary>
        /// MIB_TCP6ROW_OWNER_PID: local address (16 bytes), local scope, local port, remote address
        /// (16 bytes), remote scope, remote port, state, owner.
        /// </summary>
        internal const int IPv6RowSize = 56;

        /// <summary>MIB_TCP_STATE_LISTEN. The listener table holds nothing else; a row that says otherwise is skipped.</summary>
        internal const int ListenState = 2;

        /// <summary>
        /// The first size tried: some three hundred IPv4 listeners or a hundred and forty IPv6
        /// ones, more than a server has. What a machine needed is remembered for the next call.
        /// </summary>
        private const int InitialBufferSize = 8 * 1024;

        /// <summary>Room for the listeners opened between the call that reports the size and the one that uses it.</summary>
        private const int Slack = 16 * IPv6RowSize;

        private static int lastBufferSize = InitialBufferSize;

        /// <summary>Builds a table from listeners already in hand; the tests use it to describe a machine.</summary>
        internal PortTable(IEnumerable<ListeningPort> listeners) => this.All = listeners.ToImmutableArray();

        /// <summary>Every listener, IPv4 first, in the order the system gave them.</summary>
        public ImmutableArray<ListeningPort> All { get; }

        /// <summary>
        /// Reads the machine's listeners. Null when neither table could be read; a machine with no
        /// IPv6 stack still answers for IPv4.
        /// </summary>
        public static PortTable? Read()
        {
            byte[]? ipv4 = ReadTable(NativeMethods.AF_INET);
            byte[]? ipv6 = ReadTable(NativeMethods.AF_INET6);
            if (ipv4 is null && ipv6 is null)
            {
                return null;
            }

            var listeners = new List<ListeningPort>();
            if (ipv4 != null)
            {
                listeners.AddRange(ParseIPv4(ipv4));
            }

            if (ipv6 != null)
            {
                listeners.AddRange(ParseIPv6(ipv6));
            }

            return new PortTable(listeners);
        }

        /// <summary>What the given processes listen on, in the order <see cref="Describe"/> writes it.</summary>
        public ImmutableArray<ListeningPort> HeldBy(ICollection<int> processIds) =>
            Ordered(this.All.Where(listener => processIds.Contains(listener.ProcessId)));

        /// <summary>The processes listening on <paramref name="port"/>, on any address, each once and lowest ID first.</summary>
        public IEnumerable<int> HoldersOf(int port) =>
            this.All.Where(listener => listener.Port == port).Select(listener => listener.ProcessId).Distinct().OrderBy(id => id);

        /// <summary>"0.0.0.0:8000, [::]:8000"; empty for no listeners.</summary>
        public static string Describe(IEnumerable<ListeningPort> listeners) =>
            string.Join(", ", listeners.Select(listener => listener.Endpoint));

        /// <summary>
        /// Each address once — two sockets can listen on the same one — lowest port first, and for
        /// one port IPv4 before IPv6: the order a reader looks for a port in.
        /// </summary>
        internal static ImmutableArray<ListeningPort> Ordered(IEnumerable<ListeningPort> listeners) =>
            listeners
                .GroupBy(listener => (listener.Address, listener.Port))
                .Select(same => same.First())
                .OrderBy(listener => listener.Port)
                .ThenBy(listener => listener.IsIPv6)
                .ThenBy(listener => listener.Address, StringComparer.Ordinal)
                .ToImmutableArray();

        /// <summary>A MIB_TCPTABLE_OWNER_PID, as the system wrote it: little-endian counts, addresses and ports in network order.</summary>
        internal static List<ListeningPort> ParseIPv4(ReadOnlySpan<byte> table)
        {
            var listeners = new List<ListeningPort>();
            foreach (var row in Rows(table, IPv4RowSize))
            {
                var bytes = table.Slice(row, IPv4RowSize);
                if (BinaryPrimitives.ReadInt32LittleEndian(bytes) != ListenState)
                {
                    continue;
                }

                listeners.Add(new ListeningPort(
                    new IPAddress(bytes.Slice(4, 4)).ToString(),
                    PortOf(bytes.Slice(8, 2)),
                    BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(20, 4))));
            }

            return listeners;
        }

        /// <summary>
        /// A MIB_TCP6TABLE_OWNER_PID. The scope of a link-local address is left out: a server listens
        /// on any address, on loopback or on a routable one, and the documentation and the runtime's
        /// own reader disagree about the byte order the scope is in.
        /// </summary>
        internal static List<ListeningPort> ParseIPv6(ReadOnlySpan<byte> table)
        {
            var listeners = new List<ListeningPort>();
            foreach (var row in Rows(table, IPv6RowSize))
            {
                var bytes = table.Slice(row, IPv6RowSize);
                if (BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(48, 4)) != ListenState)
                {
                    continue;
                }

                listeners.Add(new ListeningPort(
                    new IPAddress(bytes.Slice(0, 16)).ToString(),
                    PortOf(bytes.Slice(20, 2)),
                    BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(52, 4))));
            }

            return listeners;
        }

        /// <summary>
        /// The offset of each row the table says it has and actually holds. A count larger than the
        /// buffer — a table cut short — yields the rows that are there.
        /// </summary>
        private static List<int> Rows(ReadOnlySpan<byte> table, int rowSize)
        {
            var offsets = new List<int>();
            if (table.Length < HeadSize)
            {
                return offsets;
            }

            uint count = BinaryPrimitives.ReadUInt32LittleEndian(table);
            for (long offset = HeadSize, row = 0; row < count && offset + rowSize <= table.Length; row++, offset += rowSize)
            {
                offsets.Add((int)offset);
            }

            return offsets;
        }

        /// <summary>
        /// A port as the table keeps it: in network order in the low two bytes of a DWORD, whose
        /// upper two bytes are not to be read. 8000 is stored as 1F 40.
        /// </summary>
        private static int PortOf(ReadOnlySpan<byte> bytes) => (bytes[0] << 8) | bytes[1];

        /// <summary>One family's table, or null when it could not be read.</summary>
        private static byte[]? ReadTable(int family)
        {
            int size = lastBufferSize;

            // A bounded number of tries, as for the process snapshot: the size reported is what
            // was needed at that moment, and listeners opened since can outgrow it again.
            for (int attempt = 0; attempt < 4; attempt++)
            {
                byte[] buffer = new byte[size];
                int given = size;
                int result = NativeMethods.GetExtendedTcpTable(buffer, ref given, false, family, NativeMethods.TCP_TABLE_OWNER_PID_LISTENER, 0);

                if (result == 0)
                {
                    return buffer;
                }

                if (result != NativeMethods.ERROR_INSUFFICIENT_BUFFER || given <= 0)
                {
                    return null;
                }

                size = given + Slack;
                lastBufferSize = Math.Max(lastBufferSize, size);
            }

            return null;
        }
    }
}

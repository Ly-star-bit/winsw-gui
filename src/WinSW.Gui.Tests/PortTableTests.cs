using System;
using System.Buffers.Binary;
using System.Linq;
using System.Net;
using WinSW.Gui.Services;
using Xunit;

namespace WinSW.Gui.Tests
{
    /// <summary>
    /// Reading the machine's listening ports out of the tables <c>GetExtendedTcpTable</c> fills, built
    /// here byte by byte the way the system lays them out. The call itself needs Windows; what is
    /// made of its answer does not.
    /// </summary>
    public class PortTableTests
    {
        private const int Listen = 2;
        private const int Established = 5;

        [Fact]
        public void AnIPv4ListenerIsReadWithItsAddressPortAndOwner()
        {
            var table = IPv4Table((Listen, "0.0.0.0", 8000, 4312), (Listen, "127.0.0.1", 5432, 900));

            var listeners = PortTable.ParseIPv4(table);

            Assert.Equal(
                new[] { new ListeningPort("0.0.0.0", 8000, 4312), new ListeningPort("127.0.0.1", 5432, 900) },
                listeners);
        }

        /// <summary>
        /// The port is in network order in the low two bytes of its DWORD, and the other two are not
        /// the port's: the rows here carry noise there, and a port above 32767 must not turn negative.
        /// </summary>
        [Theory]
        [InlineData(80)]
        [InlineData(8000)]
        [InlineData(50000)]
        [InlineData(65535)]
        public void APortIsReadInNetworkOrderFromItsLowTwoBytes(int port)
        {
            var table = IPv4Table((Listen, "0.0.0.0", port, 1234));

            Assert.Equal(port, Assert.Single(PortTable.ParseIPv4(table)).Port);
            Assert.Equal(port, Assert.Single(PortTable.ParseIPv6(IPv6Table((Listen, "::", 0, port, 1234)))).Port);
        }

        [Fact]
        public void AnIPv6ListenerIsReadWithItsAddressPortAndOwner()
        {
            var table = IPv6Table((Listen, "::", 0, 8000, 4312), (Listen, "::1", 0, 9000, 77));

            var listeners = PortTable.ParseIPv6(table);

            Assert.Equal(
                new[] { new ListeningPort("::", 8000, 4312), new ListeningPort("::1", 9000, 77) },
                listeners);
        }

        /// <summary>A link-local address is written without its scope, which the panel has no use for.</summary>
        [Fact]
        public void ALinkLocalAddressLosesItsScope()
        {
            var table = IPv6Table((Listen, "fe80::1", 3, 80, 5));

            Assert.Equal("fe80::1", Assert.Single(PortTable.ParseIPv6(table)).Address);
        }

        /// <summary>The listener table holds nothing else; a row that says otherwise is not taken for one.</summary>
        [Fact]
        public void ARowInAnotherStateIsSkipped()
        {
            Assert.Equal(new[] { 4312 }, PortTable.ParseIPv4(IPv4Table((Established, "10.0.0.5", 8000, 1), (Listen, "0.0.0.0", 8000, 4312))).Select(l => l.ProcessId));
            Assert.Equal(new[] { 4312 }, PortTable.ParseIPv6(IPv6Table((Established, "::1", 0, 8000, 1), (Listen, "::", 0, 8000, 4312))).Select(l => l.ProcessId));
        }

        /// <summary>A count larger than the buffer, a table cut short, yields the rows that are there.</summary>
        [Fact]
        public void ACountBeyondTheBufferYieldsTheRowsThatAreThere()
        {
            var table = IPv4Table((Listen, "0.0.0.0", 8000, 4312), (Listen, "0.0.0.0", 9000, 4313));
            BinaryPrimitives.WriteUInt32LittleEndian(table, 1000);

            Assert.Equal(new[] { 8000, 9000 }, PortTable.ParseIPv4(table).Select(l => l.Port));
            Assert.Single(PortTable.ParseIPv4(table.AsSpan(0, table.Length - 1)));
        }

        /// <summary>The rows the count names, and not what follows them in a buffer larger than the table.</summary>
        [Fact]
        public void OnlyTheCountedRowsAreRead()
        {
            var table = IPv4Table((Listen, "0.0.0.0", 8000, 4312), (Listen, "0.0.0.0", 9000, 4313));
            BinaryPrimitives.WriteUInt32LittleEndian(table, 1);

            Assert.Equal(new[] { 8000 }, PortTable.ParseIPv4(table).Select(l => l.Port));
        }

        [Fact]
        public void AnEmptyOrTruncatedTableHasNoListeners()
        {
            Assert.Empty(PortTable.ParseIPv4(Array.Empty<byte>()));
            Assert.Empty(PortTable.ParseIPv4(new byte[3]));
            Assert.Empty(PortTable.ParseIPv6(new byte[4]));
        }

        /// <summary>The panel's line: lowest port first, IPv4 before IPv6 for one port, each address once.</summary>
        [Fact]
        public void AServicesPortsAreWrittenAsNetstatWouldWriteThem()
        {
            var table = new PortTable(new[]
            {
                new ListeningPort("::", 8000, 4312),
                new ListeningPort("0.0.0.0", 9001, 4313),
                new ListeningPort("0.0.0.0", 8000, 4312),
                new ListeningPort("0.0.0.0", 8000, 4313),
                new ListeningPort("0.0.0.0", 443, 999),
            });

            var held = table.HeldBy(new[] { 4312, 4313 });

            Assert.Equal("0.0.0.0:8000, [::]:8000, 0.0.0.0:9001", PortTable.Describe(held));
        }

        [Fact]
        public void NothingHeldIsWrittenAsNothing()
        {
            var table = new PortTable(new[] { new ListeningPort("0.0.0.0", 443, 999) });

            Assert.Equal(string.Empty, PortTable.Describe(table.HeldBy(new[] { 4312 })));
        }

        /// <summary>Every process on a port, on any address, once each and lowest first.</summary>
        [Fact]
        public void AllTheHoldersOfAPortAreFound()
        {
            var table = new PortTable(new[]
            {
                new ListeningPort("::", 8000, 4312),
                new ListeningPort("127.0.0.1", 8000, 77),
                new ListeningPort("0.0.0.0", 8000, 4312),
                new ListeningPort("0.0.0.0", 8001, 5),
            });

            Assert.Equal(new[] { 77, 4312 }, table.HoldersOf(8000));
            Assert.Empty(table.HoldersOf(9000));
        }

        private static byte[] IPv4Table(params (int State, string Address, int Port, int ProcessId)[] rows)
        {
            var table = new byte[PortTable.HeadSize + (rows.Length * PortTable.IPv4RowSize)];
            BinaryPrimitives.WriteUInt32LittleEndian(table, (uint)rows.Length);
            for (int i = 0; i < rows.Length; i++)
            {
                var row = table.AsSpan(PortTable.HeadSize + (i * PortTable.IPv4RowSize), PortTable.IPv4RowSize);
                BinaryPrimitives.WriteInt32LittleEndian(row, rows[i].State);
                IPAddress.Parse(rows[i].Address).GetAddressBytes().CopyTo(row.Slice(4));
                WritePort(row.Slice(8, 4), rows[i].Port);
                IPAddress.Parse("192.0.2.1").GetAddressBytes().CopyTo(row.Slice(12));
                WritePort(row.Slice(16, 4), 51000);
                BinaryPrimitives.WriteInt32LittleEndian(row.Slice(20), rows[i].ProcessId);
            }

            return table;
        }

        private static byte[] IPv6Table(params (int State, string Address, uint Scope, int Port, int ProcessId)[] rows)
        {
            var table = new byte[PortTable.HeadSize + (rows.Length * PortTable.IPv6RowSize)];
            BinaryPrimitives.WriteUInt32LittleEndian(table, (uint)rows.Length);
            for (int i = 0; i < rows.Length; i++)
            {
                var row = table.AsSpan(PortTable.HeadSize + (i * PortTable.IPv6RowSize), PortTable.IPv6RowSize);
                IPAddress.Parse(rows[i].Address).GetAddressBytes().CopyTo(row);
                BinaryPrimitives.WriteUInt32LittleEndian(row.Slice(16), rows[i].Scope);
                WritePort(row.Slice(20, 4), rows[i].Port);
                IPAddress.Parse("2001:db8::1").GetAddressBytes().CopyTo(row.Slice(24));
                WritePort(row.Slice(44, 4), 51000);
                BinaryPrimitives.WriteInt32LittleEndian(row.Slice(48), rows[i].State);
                BinaryPrimitives.WriteInt32LittleEndian(row.Slice(52), rows[i].ProcessId);
            }

            return table;
        }

        /// <summary>As the system writes it: network order in the low two bytes, and whatever in the other two.</summary>
        private static void WritePort(Span<byte> field, int port)
        {
            BinaryPrimitives.WriteUInt16BigEndian(field, (ushort)port);
            field[2] = 0xAB;
            field[3] = 0xCD;
        }
    }
}

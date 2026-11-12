using System.Buffers.Binary;
using System.Text;

namespace MetroDom.Core.Lighting;

/// <summary>One E1.31 data packet, as read back (for tests and tools).</summary>
public sealed record E131Packet(Guid Cid, string SourceName, int Priority, byte Sequence, bool Terminated, int Universe, byte[] Data);

/// <summary>
/// sACN (ANSI E1.31-2018) data packets: DMX512 universes over UDP. Pure bytes, no sockets.
/// A data packet is a root layer, a framing layer and a DMP layer, all big-endian, carrying
/// start code 0 and 512 slots. See <a href="../../../docs/references/light-profile.md">light profile</a>.
/// </summary>
public static class E131
{
    /// <summary>sACN's UDP port.</summary>
    public const int Port = 5568;

    /// <summary>Bytes in a data packet with all 512 slots.</summary>
    public const int PacketLength = 126 + LightRenderer.SlotsPerUniverse;

    public const int MaxUniverse = 63_999;

    private const int MaxSourceNameBytes = 63; // 64 with the terminating zero
    private const byte OptionTerminated = 0x40;

    private static readonly byte[] AcnPacketIdentifier = "ASC-E1.17\0\0\0"u8.ToArray();

    /// <summary>The multicast group for <paramref name="universe"/>: 239.255.hi.lo.</summary>
    public static string MulticastAddress(int universe)
    {
        CheckUniverse(universe);
        return $"239.255.{universe >> 8}.{universe & 0xFF}";
    }

    /// <summary>
    /// Builds a data packet. <paramref name="terminated"/> marks the stream's end: receivers stop
    /// waiting for this source at once instead of after their 2.5 s timeout.
    /// </summary>
    public static byte[] DataPacket(Guid cid, string sourceName, int universe, byte sequence, ReadOnlySpan<byte> slots, int priority = LightOutput.DefaultPriority, bool terminated = false)
    {
        ArgumentNullException.ThrowIfNull(sourceName);
        CheckUniverse(universe);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(slots.Length, LightRenderer.SlotsPerUniverse, nameof(slots));
        ArgumentOutOfRangeException.ThrowIfNegative(priority);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(priority, 200);

        var p = new byte[PacketLength];
        var span = p.AsSpan();

        // Root layer.
        BinaryPrimitives.WriteUInt16BigEndian(span[0..], 0x0010); // preamble size
        BinaryPrimitives.WriteUInt16BigEndian(span[2..], 0x0000); // postamble size
        AcnPacketIdentifier.CopyTo(span[4..]);
        FlagsAndLength(span[16..], PacketLength - 16);
        BinaryPrimitives.WriteUInt32BigEndian(span[18..], 0x00000004); // VECTOR_ROOT_E131_DATA
        cid.TryWriteBytes(span[22..38], bigEndian: true, out _);

        // Framing layer.
        FlagsAndLength(span[38..], PacketLength - 38);
        BinaryPrimitives.WriteUInt32BigEndian(span[40..], 0x00000002); // VECTOR_E131_DATA_PACKET
        var name = Encoding.UTF8.GetBytes(sourceName);
        name.AsSpan(0, Math.Min(name.Length, MaxSourceNameBytes)).CopyTo(span[44..]);
        p[108] = (byte)priority;
        BinaryPrimitives.WriteUInt16BigEndian(span[109..], 0); // no synchronization universe
        p[111] = sequence;
        p[112] = terminated ? OptionTerminated : (byte)0;
        BinaryPrimitives.WriteUInt16BigEndian(span[113..], (ushort)universe);

        // DMP layer.
        FlagsAndLength(span[115..], PacketLength - 115);
        p[117] = 0x02; // VECTOR_DMP_SET_PROPERTY
        p[118] = 0xA1; // address and data type
        BinaryPrimitives.WriteUInt16BigEndian(span[119..], 0x0000); // first property address
        BinaryPrimitives.WriteUInt16BigEndian(span[121..], 0x0001); // address increment
        BinaryPrimitives.WriteUInt16BigEndian(span[123..], LightRenderer.SlotsPerUniverse + 1); // start code + slots
        p[125] = 0x00; // DMX start code: dimmer data
        slots.CopyTo(span[126..]);
        return p;
    }

    /// <summary>Reads a data packet back, or returns false if it isn't one.</summary>
    public static bool TryParse(ReadOnlySpan<byte> p, out E131Packet packet)
    {
        packet = null!;
        if (p.Length < 126
            || BinaryPrimitives.ReadUInt16BigEndian(p) != 0x0010
            || !p[4..16].SequenceEqual(AcnPacketIdentifier)
            || BinaryPrimitives.ReadUInt32BigEndian(p[18..]) != 4
            || BinaryPrimitives.ReadUInt32BigEndian(p[40..]) != 2
            || p[117] != 0x02 || p[125] != 0x00)
        {
            return false;
        }

        var count = BinaryPrimitives.ReadUInt16BigEndian(p[123..]) - 1;
        if (count < 0 || 126 + count > p.Length)
        {
            return false;
        }

        var nameBytes = p[44..108];
        var end = nameBytes.IndexOf((byte)0);
        packet = new E131Packet(
            new Guid(p[22..38], bigEndian: true),
            Encoding.UTF8.GetString(end < 0 ? nameBytes : nameBytes[..end]),
            p[108],
            p[111],
            (p[112] & OptionTerminated) != 0,
            BinaryPrimitives.ReadUInt16BigEndian(p[113..]),
            p.Slice(126, count).ToArray());
        return true;
    }

    private static void FlagsAndLength(Span<byte> at, int length) =>
        BinaryPrimitives.WriteUInt16BigEndian(at, (ushort)(0x7000 | length));

    private static void CheckUniverse(int universe)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(universe, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(universe, MaxUniverse);
    }
}

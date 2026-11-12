using System.Text;
using Melanchall.DryWetMidi.Core;
using MetroDom.Core.Discovery;

namespace MetroDom.Core.Bodies;

/// <summary>Body Write Reply status. The numbers are the firmware's <c>WriteStatus</c>: never renumber.</summary>
public enum BodyWriteStatus
{
    /// <summary>Stored and read back. The brain restarts as the new body.</summary>
    Written = 0,

    /// <summary>A chunk was out of order, not hex, or too long. Nothing was stored.</summary>
    BadMessage = 1,

    /// <summary>The record didn't pass the brain's checks; the detail says which. Nothing was stored.</summary>
    Invalid = 2,

    /// <summary>The EEPROM didn't take it (write-protected, missing, or the read-back differed).</summary>
    Failed = 3,

    /// <summary>This brain has nowhere to store a body.</summary>
    NoStore = 4,
}

/// <summary>What a brain said to a Body Write.</summary>
public sealed record BodyWriteReply(BodyWriteStatus Status, int Detail)
{
    /// <summary>One line for the person who ran <c>metrodom body write</c>.</summary>
    public string Describe() => Status switch
    {
        BodyWriteStatus.Written => "written and verified; the brain is restarting as the new body",
        BodyWriteStatus.BadMessage => "the brain got the record garbled (a chunk out of order or not hex); nothing was stored",
        BodyWriteStatus.Invalid when Detail == 0 => "the brain refused the record; nothing was stored",
        BodyWriteStatus.Invalid => $"the brain refused the record: {BodyWrite.ErrorText(Detail)}; nothing was stored",
        BodyWriteStatus.Failed => "the EEPROM didn't keep it: is the body's write-protect jumper on, or the EEPROM missing?",
        BodyWriteStatus.NoStore => "this brain can't store a body",
        _ => $"unknown status {(int)Status}",
    };
}

/// <summary>
/// Body Write over MetroDom SysEx (docs/references/midi-profile-exchange.md):
/// <c>F0 7D 4D 44 04 &lt;index&gt; &lt;count&gt; &lt;hex…&gt; F7</c> chunks, in order, answered by
/// <c>F0 7D 4D 44 05 &lt;status&gt; &lt;detail&gt; F7</c>. Send only to a port that identified as MetroDom.
/// </summary>
public static class BodyWrite
{
    /// <summary>Hex characters per chunk (64 record bytes).</summary>
    public const int ChunkHex = 128;

    private const byte NonCommercial = 0x7D;
    private const byte FamilyLsb = DeviceIdentity.MetroDomFamily & 0x7F;
    private const byte FamilyMsb = (DeviceIdentity.MetroDomFamily >> 7) & 0x7F;
    private const byte WriteId = 0x04;
    private const byte ReplyId = 0x05;
    private const byte EndOfExclusive = 0xF7;

    // The firmware's BodyError, by number (Body.h). Never renumber.
    private static readonly string[] Errors =
    [
        "ok", "blank", "not a body record", "unknown version", "bad length", "bad CRC", "bad driver",
        "bad actuator count", "limits out of range", "bad note", "bad output", "bad pulse",
        "bad interval or latency", "bad id or name",
    ];

    /// <summary>The firmware's words for a <c>BodyError</c> number.</summary>
    public static string ErrorText(int detail) => detail >= 0 && detail < Errors.Length ? Errors[detail] : $"error {detail}";

    /// <summary>The chunks that write <paramref name="record"/> (from <see cref="BodyImage.Encode"/>).</summary>
    public static IReadOnlyList<NormalSysExEvent> Chunks(byte[] record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var hex = Encoding.ASCII.GetBytes(BodyImage.ToHex(record));
        var count = (hex.Length + ChunkHex - 1) / ChunkHex;
        return [.. Enumerable.Range(0, count).Select(i => new NormalSysExEvent(
        [
            NonCommercial, FamilyLsb, FamilyMsb, WriteId, (byte)i, (byte)count,
            .. hex.AsSpan(i * ChunkHex, Math.Min(ChunkHex, hex.Length - (i * ChunkHex))),
            EndOfExclusive,
        ]))];
    }

    /// <summary>A Body Write Reply, as a brain sends it.</summary>
    public static NormalSysExEvent Reply(BodyWriteStatus status, int detail = 0) =>
        new([NonCommercial, FamilyLsb, FamilyMsb, ReplyId, (byte)status, (byte)(detail & 0x7F), EndOfExclusive]);

    public static bool TryParseReply(MidiEvent midiEvent, out BodyWriteReply reply)
    {
        if (midiEvent is SysExEvent { Data: [NonCommercial, FamilyLsb, FamilyMsb, ReplyId, var status, var detail, ..] })
        {
            reply = new BodyWriteReply((BodyWriteStatus)status, detail);
            return true;
        }

        reply = null!;
        return false;
    }

    /// <summary>Whether <paramref name="midiEvent"/> is a Body Write chunk.</summary>
    public static bool IsChunk(MidiEvent midiEvent) =>
        midiEvent is SysExEvent { Data: [NonCommercial, FamilyLsb, FamilyMsb, WriteId, ..] };
}

/// <summary>
/// The brain's side of Body Write, for virtual instruments and tests: same order, hex and record
/// checks as the firmware (<c>Brain::bodyChunk</c>).
/// </summary>
/// <remarks>Not thread-safe.</remarks>
public sealed class BodyReceiver
{
    private readonly List<byte> _record = [];
    private int _next;
    private int _count;

    /// <summary>
    /// Feeds one event. Returns the reply to send when the event was a chunk that needs one
    /// (the last chunk, or a bad one), else <c>null</c>. <paramref name="written"/> is the new
    /// body when the reply is <see cref="BodyWriteStatus.Written"/>.
    /// </summary>
    public NormalSysExEvent? Add(MidiEvent midiEvent, out BodyDefinition? written)
    {
        written = null;
        if (midiEvent is not SysExEvent sysEx || !BodyWrite.IsChunk(midiEvent))
        {
            return null;
        }

        var data = sysEx.Data;
        if (data.Length < 7)
        {
            return BodyWrite.Reply(BodyWriteStatus.BadMessage);
        }

        int index = data[4], count = data[5];
        var hex = data.AsSpan(6, data.Length - 7);
        if (index == 0)
        {
            _record.Clear();
            (_next, _count) = (0, count);
        }

        if (count == 0 || count != _count || index != _next || hex.Length % 2 != 0 || hex.Length > BodyWrite.ChunkHex
            || _record.Count + (hex.Length / 2) > BodyImage.MaxBytes || !TryHex(hex, _record))
        {
            (_next, _count) = (0, 0);
            return BodyWrite.Reply(BodyWriteStatus.BadMessage);
        }

        if (++_next < _count)
        {
            return null;
        }

        (_next, _count) = (0, 0);
        var record = _record.ToArray();
        if (!BodyImage.TryDecode(record, out var body, out _) || BodyImage.Encode(body).Length != record.Length)
        {
            return BodyWrite.Reply(BodyWriteStatus.Invalid);
        }

        written = body;
        return BodyWrite.Reply(BodyWriteStatus.Written);
    }

    private static bool TryHex(ReadOnlySpan<byte> hex, List<byte> into)
    {
        for (var i = 0; i < hex.Length; i += 2)
        {
            int high = Digit(hex[i]), low = Digit(hex[i + 1]);
            if (high < 0 || low < 0)
            {
                return false;
            }

            into.Add((byte)((high << 4) | low));
        }

        return true;
    }

    private static int Digit(byte c) => c switch
    {
        >= (byte)'0' and <= (byte)'9' => c - '0',
        >= (byte)'A' and <= (byte)'F' => c - 'A' + 10,
        >= (byte)'a' and <= (byte)'f' => c - 'a' + 10,
        _ => -1,
    };
}

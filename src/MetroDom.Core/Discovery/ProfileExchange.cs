using System.Text;
using Melanchall.DryWetMidi.Core;

namespace MetroDom.Core.Discovery;

/// <summary>
/// MetroDom SysEx for discovery step 3 (ADR 0007): ask a MetroDom instrument for its
/// <a href="../../../docs/references/instrument-profile.md">profile</a> and read the answer.
/// See docs/references/midi-profile-exchange.md for the bytes. Draft until the owner reviews it;
/// MIDI-CI Property Exchange may replace it later.
/// </summary>
/// <remarks>
/// Every MetroDom message starts <c>F0 7D 4D 44</c>: the non-commercial manufacturer ID and the
/// "MD" family bytes from the Identity Reply. Others may use <c>7D</c> too, so only send these
/// to a port whose Identity Reply said it is MetroDom.
/// </remarks>
public static class ProfileExchange
{
    /// <summary>Profile text bytes per reply message. Small enough for any USB-MIDI SysEx buffer.</summary>
    public const int ChunkSize = 128;

    /// <summary>The most chunks a reply can have (7-bit count): 16 KB of profile.</summary>
    public const int MaxChunks = 127;

    private const byte NonCommercial = 0x7D;
    private const byte FamilyLsb = DeviceIdentity.MetroDomFamily & 0x7F;
    private const byte FamilyMsb = (DeviceIdentity.MetroDomFamily >> 7) & 0x7F;
    private const byte ProfileRequestId = 0x01;
    private const byte ProfileReplyId = 0x02;
    private const byte EndOfExclusive = 0xF7;

    /// <summary><c>F0 7D 4D 44 01 F7</c>: "send me your profile".</summary>
    public static NormalSysExEvent Request() => new([NonCommercial, FamilyLsb, FamilyMsb, ProfileRequestId, EndOfExclusive]);

    /// <summary>Whether <paramref name="midiEvent"/> is a MetroDom profile request.</summary>
    public static bool IsRequest(MidiEvent midiEvent) =>
        midiEvent is SysExEvent { Data: [NonCommercial, FamilyLsb, FamilyMsb, ProfileRequestId, ..] };

    /// <summary>
    /// <paramref name="profileJson"/> as reply messages: <c>F0 7D 4D 44 02 &lt;index&gt; &lt;count&gt; &lt;text…&gt; F7</c>.
    /// The text must be ASCII (<see cref="InstrumentProfileJson.Write"/> escapes everything else).
    /// </summary>
    public static IReadOnlyList<NormalSysExEvent> Reply(string profileJson)
    {
        ArgumentNullException.ThrowIfNull(profileJson);
        if (profileJson.Any(c => c > 0x7F))
        {
            throw new ArgumentException("Profile text must be ASCII to fit SysEx data bytes.", nameof(profileJson));
        }

        var bytes = Encoding.ASCII.GetBytes(profileJson);
        var count = Math.Max(1, (bytes.Length + ChunkSize - 1) / ChunkSize);
        if (count > MaxChunks)
        {
            throw new ArgumentException($"Profile is {bytes.Length} bytes; the most a reply can carry is {MaxChunks * ChunkSize}.", nameof(profileJson));
        }

        return [.. Enumerable.Range(0, count).Select(i => new NormalSysExEvent(
        [
            NonCommercial, FamilyLsb, FamilyMsb, ProfileReplyId, (byte)i, (byte)count,
            .. bytes.AsSpan(i * ChunkSize, Math.Min(ChunkSize, bytes.Length - (i * ChunkSize))),
            EndOfExclusive,
        ]))];
    }

    /// <summary>The replies to send if <paramref name="midiEvent"/> is a profile request, else empty.</summary>
    public static IReadOnlyList<NormalSysExEvent> ReplyTo(MidiEvent midiEvent, string profileJson) =>
        IsRequest(midiEvent) ? Reply(profileJson) : [];

    /// <summary>Reads one reply message. Anything else (including a request) returns false.</summary>
    public static bool TryParseChunk(MidiEvent midiEvent, out int index, out int count, out string text)
    {
        index = count = 0;
        text = "";
        if (midiEvent is not SysExEvent { Data: [NonCommercial, FamilyLsb, FamilyMsb, ProfileReplyId, var i, var n, .. var rest] }
            || n == 0 || i >= n)
        {
            return false;
        }

        if (rest is [.., EndOfExclusive])
        {
            rest = rest[..^1];
        }

        if (rest.Any(b => b > 0x7F))
        {
            return false;
        }

        (index, count, text) = (i, n, Encoding.ASCII.GetString(rest));
        return true;
    }
}

/// <summary>
/// Puts a chunked profile reply back together. Chunks may arrive in any order; a chunk claiming a
/// different total than the first starts over (the instrument restarted its answer).
/// </summary>
/// <remarks>Not thread-safe: feed it from one thread or under a lock.</remarks>
public sealed class ProfileAssembler
{
    private string?[] _chunks = [];

    /// <summary>The whole profile text once every chunk has arrived, else null.</summary>
    public string? Text { get; private set; }

    /// <summary>Feeds one event. Returns true when it completed the profile.</summary>
    public bool Add(MidiEvent midiEvent)
    {
        if (Text is not null || !ProfileExchange.TryParseChunk(midiEvent, out var index, out var count, out var text))
        {
            return false;
        }

        if (_chunks.Length != count)
        {
            _chunks = new string?[count];
        }

        _chunks[index] = text;
        if (_chunks.Any(c => c is null))
        {
            return false;
        }

        Text = string.Concat(_chunks);
        return true;
    }
}

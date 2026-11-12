using System.Globalization;
using Melanchall.DryWetMidi.Core;

namespace MetroDom.Core.Timing;

/// <summary>Which kind of MIDI message counts as a tap.</summary>
public enum TapKind
{
    /// <summary>Any press: a Note On, or a controller going from up to down.</summary>
    Any,

    /// <summary>Note On (velocity &gt; 0). Pads, drum triggers, keyboards.</summary>
    Note,

    /// <summary>A controller crossing from below 64 to 64 or above. Sustain-style foot pedals (CC 64).</summary>
    ControlChange,

    /// <summary>Program Change. Many foot controllers send one per switch.</summary>
    ProgramChange,
}

/// <summary>
/// Decides which incoming MIDI messages are taps for <see cref="TapTempo"/>: a foot pedal, a pad,
/// a drum trigger. Written <c>any</c>, <c>note</c>, <c>note:36</c>, <c>cc:64</c>, <c>pc</c>,
/// <c>pc:3</c>, optionally followed by <c>@CHANNEL</c> (1-16), e.g. <c>cc:64@1</c>.
/// </summary>
/// <remarks>
/// A pedal sends "down" (127) and "up" (0); only the press counts, so one stomp is one tap.
/// Controllers are tracked per channel and number. Not thread-safe: feed it from one input thread
/// or under a lock.
/// </remarks>
public sealed class TapTrigger
{
    private const int PressedFrom = 64;
    private readonly HashSet<(int Channel, int Control)> _down = [];

    public TapTrigger(TapKind kind = TapKind.Any, int? number = null, int? channel = null)
    {
        if (number is not null && (kind == TapKind.Any || number is < 0 or > 127))
        {
            throw new ArgumentOutOfRangeException(nameof(number), "A number needs a kind and must be 0-127.");
        }

        ArgumentOutOfRangeException.ThrowIfGreaterThan(channel ?? 0, 15, nameof(channel));
        ArgumentOutOfRangeException.ThrowIfNegative(channel ?? 0, nameof(channel));
        Kind = kind;
        Number = number;
        Channel = channel;
    }

    public TapKind Kind { get; }

    /// <summary>Note, controller or program number; null means any.</summary>
    public int? Number { get; }

    /// <summary>Zero-based channel; null means any.</summary>
    public int? Channel { get; }

    /// <summary>Whether <paramref name="midiEvent"/> is a tap. Call for every message, so pedal releases are seen.</summary>
    public bool IsTap(MidiEvent midiEvent)
    {
        if (midiEvent is not ChannelEvent channelEvent || (Channel is { } channel && channelEvent.Channel != channel))
        {
            return false;
        }

        switch (midiEvent)
        {
            case NoteOnEvent noteOn when Kind is TapKind.Any or TapKind.Note:
                return noteOn.Velocity > 0 && Wanted(noteOn.NoteNumber);
            case ControlChangeEvent cc when Kind is TapKind.Any or TapKind.ControlChange:
                if (!Wanted(cc.ControlNumber) || (Kind == TapKind.Any && cc.ControlNumber >= 120))
                {
                    return false; // Channel mode messages (all notes off…) are never taps.
                }

                var key = ((int)cc.Channel, (int)cc.ControlNumber);
                if (cc.ControlValue >= PressedFrom)
                {
                    return _down.Add(key); // Only the up → down edge; "still down" repeats aren't taps.
                }

                _down.Remove(key);
                return false;
            case ProgramChangeEvent pc when Kind == TapKind.ProgramChange:
                return Wanted(pc.ProgramNumber);
            default:
                return false;
        }
    }

    /// <summary>Parses the written form, or returns false with a reason.</summary>
    public static bool TryParse(string? raw, out TapTrigger trigger, out string? error)
    {
        trigger = new TapTrigger();
        error = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        var text = raw.Trim().ToLowerInvariant();
        int? channel = null;
        var at = text.IndexOf('@', StringComparison.Ordinal);
        if (at >= 0)
        {
            if (!int.TryParse(text[(at + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var ch) || ch is < 1 or > 16)
            {
                error = $"'{raw}': channel must be 1-16.";
                return false;
            }

            channel = ch - 1;
            text = text[..at];
        }

        var parts = text.Split(':', 2);
        TapKind? kind = parts[0] switch
        {
            "any" => TapKind.Any,
            "note" => TapKind.Note,
            "cc" => TapKind.ControlChange,
            "pc" => TapKind.ProgramChange,
            _ => null,
        };
        int? number = null;
        if (parts.Length == 2)
        {
            if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n > 127)
            {
                error = $"'{raw}': the number after ':' must be 0-127.";
                return false;
            }

            number = n;
        }

        if (kind is null || (kind == TapKind.Any && number is not null))
        {
            error = $"'{raw}' is not any, note[:N], cc[:N] or pc[:N], optionally @CHANNEL (e.g. cc:64 or note:36@10).";
            return false;
        }

        trigger = new TapTrigger(kind.Value, number, channel);
        return true;
    }

    /// <summary>The written form: "cc:64@1".</summary>
    public override string ToString()
    {
        var kind = Kind switch
        {
            TapKind.Note => "note",
            TapKind.ControlChange => "cc",
            TapKind.ProgramChange => "pc",
            _ => "any",
        };
        return kind
            + (Number is { } n ? string.Create(CultureInfo.InvariantCulture, $":{n}") : "")
            + (Channel is { } c ? string.Create(CultureInfo.InvariantCulture, $"@{c + 1}") : "");
    }

    private bool Wanted(int number) => Number is null || Number == number;
}

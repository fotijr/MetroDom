using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;

namespace MetroDom.Core;

/// <summary>
/// Routes MIDI channel events to the outputs assigned to each channel, with per-channel mute
/// and a panic that silences every output.
/// </summary>
/// <remarks>
/// Muting only blocks new notes. Note Offs always pass so a channel muted mid-song can't leave
/// an actuator stuck on (the bug in the old WinForms Explorer, TD-08).
/// Outputs are isolated: one that throws (e.g. unplugged) is reported via
/// <see cref="SendFailed"/> and the rest still get the event, including panic.
/// </remarks>
public sealed class Orchestra
{
    /// <summary>Number of MIDI channels.</summary>
    public const int ChannelCount = 16;

    private readonly Lock _gate = new();
    private readonly List<IMidiOutput>[] _routes = Enumerable.Range(0, ChannelCount).Select(_ => new List<IMidiOutput>()).ToArray();
    private readonly bool[] _muted = new bool[ChannelCount];

    /// <summary>Raised when an output throws while sending. The orchestra keeps going.</summary>
    public event Action<IMidiOutput, Exception>? SendFailed;

    /// <summary>Every distinct output with at least one channel assigned.</summary>
    public IReadOnlyCollection<IMidiOutput> Outputs
    {
        get
        {
            lock (_gate)
            {
                return _routes.SelectMany(r => r).Distinct().ToArray();
            }
        }
    }

    /// <summary>Sends events on <paramref name="channel"/> (0–15) to <paramref name="output"/>.</summary>
    public void Assign(int channel, IMidiOutput output)
    {
        ArgumentNullException.ThrowIfNull(output);
        ValidateChannel(channel);
        lock (_gate)
        {
            if (!_routes[channel].Contains(output))
            {
                _routes[channel].Add(output);
            }
        }
    }

    /// <summary>Sends every channel to <paramref name="output"/>.</summary>
    public void AssignAll(IMidiOutput output)
    {
        for (var channel = 0; channel < ChannelCount; channel++)
        {
            Assign(channel, output);
        }
    }

    /// <summary>Mutes or unmutes new notes on <paramref name="channel"/> (0–15).</summary>
    public void SetMuted(int channel, bool muted)
    {
        ValidateChannel(channel);
        lock (_gate)
        {
            _muted[channel] = muted;
        }
    }

    /// <summary>Whether new notes on <paramref name="channel"/> (0–15) are blocked.</summary>
    public bool IsMuted(int channel)
    {
        ValidateChannel(channel);
        lock (_gate)
        {
            return _muted[channel];
        }
    }

    /// <summary>
    /// Routes one event. Non-channel events (SysEx, meta) are ignored. Returns the number of
    /// outputs that accepted the event.
    /// </summary>
    public int Route(MidiEvent midiEvent)
    {
        ArgumentNullException.ThrowIfNull(midiEvent);
        if (midiEvent is not ChannelEvent channelEvent)
        {
            return 0;
        }

        IMidiOutput[] targets;
        lock (_gate)
        {
            int channel = channelEvent.Channel;
            if (_muted[channel] && IsNoteStart(channelEvent))
            {
                return 0;
            }

            targets = [.. _routes[channel]];
        }

        var sent = 0;
        foreach (var output in targets)
        {
            if (TrySend(output, () => output.Send(midiEvent)))
            {
                sent++;
            }
        }

        return sent;
    }

    /// <summary>
    /// Sends All Sound Off (CC 120) and All Notes Off (CC 123) on every channel to every output,
    /// ignoring mutes. See docs/RELIABILITY.md.
    /// </summary>
    public void Panic()
    {
        foreach (var output in Outputs)
        {
            TrySend(output, () => SendPanic(output));
        }
    }

    /// <summary>Sends the panic sequence to a single output.</summary>
    public static void SendPanic(IMidiOutput output)
    {
        ArgumentNullException.ThrowIfNull(output);
        for (var channel = 0; channel < ChannelCount; channel++)
        {
            foreach (var controller in new[] { ControlNumbers.AllSoundOff, ControlNumbers.AllNotesOff })
            {
                output.Send(new ControlChangeEvent((SevenBitNumber)controller, SevenBitNumber.MinValue)
                {
                    Channel = (FourBitNumber)channel,
                });
            }
        }
    }

    private bool TrySend(IMidiOutput output, Action send)
    {
        try
        {
            send();
            return true;
        }
#pragma warning disable CA1031 // Any device failure must not stop the other instruments.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            SendFailed?.Invoke(output, ex);
            return false;
        }
    }

    private static bool IsNoteStart(ChannelEvent channelEvent) =>
        channelEvent is NoteOnEvent noteOn && noteOn.Velocity > 0;

    private static void ValidateChannel(int channel) =>
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)channel, (uint)ChannelCount, nameof(channel));
}

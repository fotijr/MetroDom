using Melanchall.DryWetMidi.Core;
using MetroDom.Core.Timing;

namespace MetroDom.Core.Simulation;

/// <summary>One Note On the virtual instrument accepted.</summary>
/// <param name="Note">MIDI note number.</param>
/// <param name="Velocity">MIDI velocity (1–127).</param>
/// <param name="Channel">Zero-based MIDI channel.</param>
/// <param name="ReceivedAt">When the command arrived.</param>
/// <param name="SoundsAt">When the strike would be heard: arrival plus mechanical latency.</param>
public sealed record Strike(int Note, int Velocity, int Channel, TimeSpan ReceivedAt, TimeSpan SoundsAt);

/// <summary>
/// A robot instrument in software. It behaves like the generic firmware (ADR 0006): Note On →
/// one fixed strike pulse, Note Off ignored (the pulse ends by itself, so nothing can stick on),
/// notes it has no actuator for are dropped, repeats faster than the profile's minimum interval
/// are refused, and panic (CC 120/123) is counted. It hears a strike after the profile's latency,
/// plus optional random jitter, because real mechanics are never exact.
/// </summary>
/// <remarks>Thread-safe: OS MIDI ports deliver on background threads.</remarks>
public sealed class VirtualInstrument : IMidiOutput
{
    private readonly Lock _gate = new();
    private readonly IClock _clock;
    private readonly TimeSpan _jitter;
    private readonly Random _random;
    private readonly List<Strike> _strikes = [];
    private readonly Dictionary<int, TimeSpan> _lastStrike = [];

    /// <param name="profile">Declared behavior: latency, playable notes, minimum interval.</param>
    /// <param name="clock">Where arrival times come from.</param>
    /// <param name="jitter">Each strike lands up to ± this much off the declared latency.</param>
    /// <param name="seed">Seed for the jitter, so simulations repeat exactly.</param>
    public VirtualInstrument(InstrumentProfile profile, IClock clock, TimeSpan jitter = default, int seed = 0)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentOutOfRangeException.ThrowIfLessThan(jitter, TimeSpan.Zero);
        Profile = profile;
        _clock = clock;
        _jitter = jitter;
        _random = new Random(seed);
    }

    /// <summary>Raised for every accepted strike, on the sender's thread.</summary>
    public event Action<VirtualInstrument, Strike>? Struck;

    /// <summary>Raised when a Note On is not played: no actuator, or too soon after the last one.</summary>
    public event Action<VirtualInstrument, int, string>? Rejected;

    public InstrumentProfile Profile { get; }

    public string Name => Profile.Name;

    /// <summary>Every strike so far, in arrival order.</summary>
    public IReadOnlyList<Strike> Strikes
    {
        get
        {
            lock (_gate)
            {
                return [.. _strikes];
            }
        }
    }

    /// <summary>Note Ons for notes the instrument has no actuator for.</summary>
    public int Dropped { get; private set; }

    /// <summary>Note Ons that came faster than the profile's minimum interval.</summary>
    public int Refused { get; private set; }

    /// <summary>How many panic messages (All Sound Off / All Notes Off / MetroDom all-off) arrived.</summary>
    public int PanicMessages { get; private set; }

    public void Send(MidiEvent midiEvent)
    {
        ArgumentNullException.ThrowIfNull(midiEvent);
        switch (midiEvent)
        {
            case NoteOnEvent noteOn when noteOn.Velocity > 0:
                Strike(noteOn);
                break;
            case ControlChangeEvent cc when cc.ControlNumber == ControlNumbers.AllSoundOff || cc.ControlNumber == ControlNumbers.AllNotesOff:
            case SysExEvent when Discovery.ProfileExchange.IsAllOff(midiEvent):
                lock (_gate)
                {
                    PanicMessages++;
                }

                break;
        }
    }

    private void Strike(NoteOnEvent noteOn)
    {
        int note = noteOn.NoteNumber;
        var now = _clock.Now;
        Strike? strike = null;
        string? reason = null;
        lock (_gate)
        {
            if (!Profile.CanPlay(note))
            {
                Dropped++;
                reason = "no actuator for this note";
            }
            else if (_lastStrike.TryGetValue(note, out var last) && now - last < Profile.MinInterval)
            {
                Refused++;
                reason = $"repeat faster than {Profile.MinInterval.TotalMilliseconds:0} ms";
            }
            else
            {
                _lastStrike[note] = now;
                strike = new Strike(note, noteOn.Velocity, noteOn.Channel, now, now + Profile.LatencyFor(note) + Jitter());
                _strikes.Add(strike);
            }
        }

        if (strike is not null)
        {
            Struck?.Invoke(this, strike);
        }
        else
        {
            Rejected?.Invoke(this, note, reason!);
        }
    }

    private TimeSpan Jitter() =>
        _jitter == TimeSpan.Zero ? TimeSpan.Zero : _jitter * ((_random.NextDouble() * 2) - 1);
}

using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;

namespace MetroDom.Core.Timing;

/// <summary>A channel event at a point in the song, in wall time from the start.</summary>
public readonly record struct TimedMidiEvent(TimeSpan At, MidiEvent Event);

/// <summary>Flattens a Standard MIDI File into channel events in time order.</summary>
public static class SongTimeline
{
    /// <summary>
    /// Every channel event with its time from the song start, using the file's tempo map.
    /// Tempo is fixed at load: a live tempo change re-times nothing yet (TD-20).
    /// </summary>
    public static IReadOnlyList<TimedMidiEvent> From(MidiFile midiFile)
    {
        ArgumentNullException.ThrowIfNull(midiFile);
        var tempoMap = midiFile.GetTempoMap();
        return midiFile.GetTimedEvents()
            .Where(e => e.Event is ChannelEvent)
            .Select(e => new TimedMidiEvent(e.TimeAs<MetricTimeSpan>(tempoMap), e.Event))
            .ToArray();
    }
}

/// <summary>One event due to go to one output.</summary>
/// <param name="DueAt">Clock time to send it.</param>
/// <param name="SongTime">Where in the song it belongs.</param>
/// <param name="Output">The instrument it goes to.</param>
/// <param name="Event">The MIDI event.</param>
public sealed record ScheduledSend(TimeSpan DueAt, TimeSpan SongTime, IMidiOutput Output, MidiEvent Event);

/// <summary>
/// Sends each song event to each instrument early by that instrument's latency, so every
/// instrument is <em>heard</em> on time (ADR 0004). The whole band runs
/// <see cref="Lookahead"/> behind the song clock: the slowest note's latency, so no send is
/// due before the start.
/// </summary>
/// <remarks>
/// Driven by polling: call <see cref="Pump"/> whenever the clock reaches <see cref="NextDue"/>.
/// With a <see cref="VirtualClock"/> that makes the scheduler deterministic and instant to test.
/// Routing (mutes, failing outputs) goes through the <see cref="Orchestra"/>; assign channels
/// before <see cref="Start"/>. Not thread-safe: pump from one thread.
/// </remarks>
public sealed class LatencyScheduler
{
    private readonly Orchestra _orchestra;
    private readonly IClock _clock;
    private readonly IReadOnlyDictionary<IMidiOutput, InstrumentProfile> _profiles;
    private readonly bool _compensate;
    private ScheduledSend[] _sends = [];
    private int _next;
    private TimeSpan _origin;

    /// <param name="orchestra">Channel → instrument routing.</param>
    /// <param name="clock">Time source.</param>
    /// <param name="profiles">Latency per instrument. Outputs without a profile get zero latency.</param>
    /// <param name="compensate">False sends everything on the song clock, to hear (or measure) the problem.</param>
    public LatencyScheduler(
        Orchestra orchestra,
        IClock clock,
        IReadOnlyDictionary<IMidiOutput, InstrumentProfile> profiles,
        bool compensate = true)
    {
        ArgumentNullException.ThrowIfNull(orchestra);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(profiles);
        _orchestra = orchestra;
        _clock = clock;
        _profiles = profiles;
        _compensate = compensate;
    }

    /// <summary>How far the band runs behind the song clock. Zero without compensation.</summary>
    public TimeSpan Lookahead { get; private set; }

    /// <summary>The send in progress during <see cref="Pump"/> (for observers), otherwise null.</summary>
    public ScheduledSend? Current { get; private set; }

    /// <summary>Clock time of the next send, or null when the song is done.</summary>
    public TimeSpan? NextDue => _next < _sends.Length ? _sends[_next].DueAt : null;

    public bool IsFinished => _next >= _sends.Length;

    /// <summary>Clock time at which the song moment <paramref name="songTime"/> should be heard.</summary>
    public TimeSpan SoundTime(TimeSpan songTime) => _origin + Lookahead + songTime;

    /// <summary>Plans the song against the current routing and starts it at the current clock time.</summary>
    public void Start(IEnumerable<TimedMidiEvent> song)
    {
        ArgumentNullException.ThrowIfNull(song);
        _origin = _clock.Now;
        Lookahead = _compensate
            ? _orchestra.Outputs.Select(o => _profiles.TryGetValue(o, out var p) ? p.MaxLatency : TimeSpan.Zero).DefaultIfEmpty().Max()
            : TimeSpan.Zero;

        var sequence = 0;
        _sends = song
            .Where(e => e.Event is ChannelEvent)
            .SelectMany(e => _orchestra.OutputsFor(((ChannelEvent)e.Event).Channel)
                .Select(output => (Send: new ScheduledSend(SoundTime(e.At) - Latency(output, e.Event), e.At, output, e.Event), Order: sequence++)))
            .OrderBy(s => s.Send.DueAt)
            .ThenBy(s => s.Order)
            .Select(s => s.Send)
            .ToArray();
        _next = 0;
    }

    /// <summary>Sends everything due by now. Returns how many events outputs accepted.</summary>
    public int Pump()
    {
        var now = _clock.Now;
        var sent = 0;
        while (_next < _sends.Length && _sends[_next].DueAt <= now)
        {
            Current = _sends[_next++];
            if (_orchestra.RouteTo(Current.Output, Current.Event))
            {
                sent++;
            }
        }

        Current = null;
        return sent;
    }

    /// <summary>Drops everything not yet sent and panics every instrument.</summary>
    public void Stop()
    {
        _next = _sends.Length;
        _orchestra.Panic();
    }

    private TimeSpan Latency(IMidiOutput output, MidiEvent midiEvent)
    {
        if (!_compensate || !_profiles.TryGetValue(output, out var profile))
        {
            return TimeSpan.Zero;
        }

        // Note Off uses the note's latency too, so a note keeps its length.
        return midiEvent switch
        {
            NoteEvent note => profile.LatencyFor(note.NoteNumber),
            _ => profile.Latency,
        };
    }
}

using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;

namespace MetroDom.Core.Timing;

/// <summary>A channel event at a point in the song, in wall time from the start.</summary>
public readonly record struct TimedMidiEvent(TimeSpan At, MidiEvent Event);

/// <summary>Flattens a Standard MIDI File into channel events in time order.</summary>
public static class SongTimeline
{
    /// <summary>
    /// Every channel event with its time from the song start at the file's own tempo (its tempo
    /// map). Live tempo changes are applied later by <see cref="LatencyScheduler.SetSpeed"/>.
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
/// <param name="DueAt">Clock time to send it (at the tempo in force when it was sent).</param>
/// <param name="SongTime">Where in the song it belongs, at the file's own tempo.</param>
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
/// <para>
/// Events stay in song time (the file's own tempo) and become clock time only when needed, so a
/// human can change the tempo mid-song (<see cref="SetSpeed"/>): everything not yet sent is
/// re-timed from the current position. Latency is mechanical, so it is never scaled by tempo.
/// An event already sent early can't be recalled, so a tempo change takes effect within the
/// lookahead.
/// </para>
/// <para>
/// Driven by polling: call <see cref="Pump"/> whenever the clock reaches <see cref="NextDue"/>.
/// With a <see cref="VirtualClock"/> that makes the scheduler deterministic and instant to test.
/// Routing (mutes, failing outputs) goes through the <see cref="Orchestra"/>; assign channels
/// before <see cref="Start"/>. Thread-safe: one thread pumps, others may change speed or stop.
/// </para>
/// </remarks>
public sealed class LatencyScheduler
{
    /// <summary>Slowest and fastest allowed speed (× the file's tempo).</summary>
    public const double MinSpeed = 0.25, MaxSpeed = 4;

    private readonly Lock _gate = new();
    private readonly Orchestra _orchestra;
    private readonly IClock _clock;
    private readonly IReadOnlyDictionary<IMidiOutput, InstrumentProfile> _profiles;
    private readonly bool _compensate;
    private Entry[] _entries = [];
    private int _next;
    private TimeSpan _anchorClock;
    private TimeSpan _anchorSong;
    private double _speed = 1;

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

    /// <summary>Raised after an output accepted an event, on the pumping thread. Keep it cheap.</summary>
    public event Action<ScheduledSend>? Sent;

    /// <summary>How far the band runs behind the song clock. Zero without compensation.</summary>
    public TimeSpan Lookahead { get; private set; }

    /// <summary>Song time of the last event: the song's length at the file's tempo.</summary>
    public TimeSpan Duration { get; private set; }

    /// <summary>The send in progress during <see cref="Pump"/> (for observers), otherwise null.</summary>
    public ScheduledSend? Current { get; private set; }

    /// <summary>Tempo as a multiple of the file's tempo (1 = as written).</summary>
    public double Speed
    {
        get
        {
            lock (_gate)
            {
                return _speed;
            }
        }
    }

    /// <summary>Clock time of the next send, or null when the song is done.</summary>
    public TimeSpan? NextDue
    {
        get
        {
            lock (_gate)
            {
                return _next < _entries.Length ? DueAt(_entries[_next]) : null;
            }
        }
    }

    public bool IsFinished
    {
        get
        {
            lock (_gate)
            {
                return _next >= _entries.Length;
            }
        }
    }

    /// <summary>The song moment being heard now (song time, clamped to the song).</summary>
    public TimeSpan Position
    {
        get
        {
            lock (_gate)
            {
                var heard = SongClock(_clock.Now) - (Lookahead * _speed);
                return heard < TimeSpan.Zero ? TimeSpan.Zero : heard > Duration ? Duration : heard;
            }
        }
    }

    /// <summary>Clock time at which the song moment <paramref name="songTime"/> should be heard, at the current speed.</summary>
    public TimeSpan SoundTime(TimeSpan songTime)
    {
        lock (_gate)
        {
            return ClockAt(songTime) + Lookahead;
        }
    }

    /// <summary>Plans the song against the current routing and starts it.</summary>
    /// <param name="song">Channel events in song time.</param>
    /// <param name="speed">Starting tempo as a multiple of the file's.</param>
    /// <param name="preroll">Wait this long before the first send (gives a real-time loop time to get going).</param>
    public void Start(IEnumerable<TimedMidiEvent> song, double speed = 1, TimeSpan preroll = default)
    {
        ArgumentNullException.ThrowIfNull(song);
        ValidateSpeed(speed);
        ArgumentOutOfRangeException.ThrowIfLessThan(preroll, TimeSpan.Zero);
        lock (_gate)
        {
            _anchorClock = _clock.Now + preroll;
            _anchorSong = TimeSpan.Zero;
            _speed = speed;
            Lookahead = _compensate
                ? _orchestra.Outputs.Select(o => _profiles.TryGetValue(o, out var p) ? p.MaxLatency : TimeSpan.Zero).DefaultIfEmpty().Max()
                : TimeSpan.Zero;

            var sequence = 0;
            _entries = song
                .Where(e => e.Event is ChannelEvent)
                .SelectMany(e => _orchestra.OutputsFor(((ChannelEvent)e.Event).Channel)
                    .Select(output => new Entry(e.At, output, e.Event, Latency(output, e.Event), sequence++)))
                .ToArray();
            Duration = _entries.Length == 0 ? TimeSpan.Zero : _entries.Max(e => e.SongTime);
            _next = 0;
            Sort();
        }
    }

    /// <summary>
    /// Changes the tempo to <paramref name="speed"/> × the file's tempo from now on. Everything not
    /// yet sent is re-timed from the current position; the song doesn't jump.
    /// </summary>
    public void SetSpeed(double speed)
    {
        ValidateSpeed(speed);
        lock (_gate)
        {
            var now = _clock.Now;
            _anchorSong = SongClock(now);
            _anchorClock = now;
            _speed = speed;
            Sort();
        }
    }

    /// <summary>Sends everything due by now. Returns how many events outputs accepted.</summary>
    public int Pump()
    {
        lock (_gate)
        {
            var now = _clock.Now;
            var sent = 0;
            while (_next < _entries.Length && DueAt(_entries[_next]) <= now)
            {
                var entry = _entries[_next++];
                Current = new ScheduledSend(DueAt(entry), entry.SongTime, entry.Output, entry.Event);
                if (_orchestra.RouteTo(entry.Output, entry.Event))
                {
                    sent++;
                    Sent?.Invoke(Current);
                }
            }

            Current = null;
            return sent;
        }
    }

    /// <summary>Drops everything not yet sent and panics every instrument.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            _next = _entries.Length;
        }

        _orchestra.Panic();
    }

    private static void ValidateSpeed(double speed)
    {
        if (!(speed >= MinSpeed && speed <= MaxSpeed))
        {
            throw new ArgumentOutOfRangeException(nameof(speed), speed, $"Speed must be between {MinSpeed} and {MaxSpeed}.");
        }
    }

    /// <summary>Song time on the song clock (which runs <see cref="Lookahead"/> ahead of what's heard).</summary>
    private TimeSpan SongClock(TimeSpan clockTime) => _anchorSong + ((clockTime - _anchorClock) * _speed);

    /// <summary>Clock time at which the song clock reaches <paramref name="songTime"/>.</summary>
    private TimeSpan ClockAt(TimeSpan songTime) => _anchorClock + ((songTime - _anchorSong) / _speed);

    private TimeSpan DueAt(Entry entry) => ClockAt(entry.SongTime) + Lookahead - entry.Latency;

    /// <summary>Orders what's left by due time. Speed changes can reorder instruments with different latencies.</summary>
    private void Sort() =>
        Array.Sort(_entries, _next, _entries.Length - _next, Comparer<Entry>.Create((a, b) =>
        {
            var byDue = DueAt(a).CompareTo(DueAt(b));
            return byDue != 0 ? byDue : a.Sequence.CompareTo(b.Sequence);
        }));

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

    private readonly record struct Entry(TimeSpan SongTime, IMidiOutput Output, MidiEvent Event, TimeSpan Latency, int Sequence);
}

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
/// Songs can jump (ADR 0012: vamp, jump to the outro): <see cref="ArmJump"/> says "when the song
/// reaches <c>from</c>, continue at <c>to</c>". Internally events sit on a <em>play time</em> axis
/// that only moves forward; each jump starts a new segment that maps play time back to song time.
/// A jump is committed just before the song clock reaches it, so it can be disarmed until then,
/// and nothing already sent is ever taken back. Notes still sounding at a jump get a Note Off there.
/// </para>
/// <para>
/// Driven by polling: call <see cref="Pump"/> whenever the clock reaches <see cref="NextDue"/>.
/// With a <see cref="VirtualClock"/> that makes the scheduler deterministic and instant to test.
/// Routing (mutes, failing outputs) goes through the <see cref="Orchestra"/>; assign channels
/// before <see cref="Start"/>. Thread-safe: one thread pumps, others may change speed, arm jumps or stop.
/// </para>
/// </remarks>
public sealed class LatencyScheduler
{
    /// <summary>Slowest and fastest allowed speed (× the file's tempo).</summary>
    public const double MinSpeed = 0.25, MaxSpeed = 4;

    /// <summary>How far ahead of the song clock (in song time) an armed jump is committed.</summary>
    private static readonly TimeSpan CommitMargin = TimeSpan.FromMilliseconds(50);

    private readonly Lock _gate = new();
    private readonly Orchestra _orchestra;
    private readonly IClock _clock;
    private readonly IReadOnlyDictionary<IMidiOutput, InstrumentProfile> _profiles;
    private readonly bool _compensate;
    private readonly List<(TimeSpan PlayStart, TimeSpan Offset)> _segments = [(TimeSpan.Zero, TimeSpan.Zero)];
    private Entry[] _plan = [];
    private Entry[] _entries = [];
    private int _next;
    private int _sequence;
    private TimeSpan _anchorClock;
    private TimeSpan _anchorPlay;
    private double _speed = 1;
    private Jump? _jump;

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

    /// <summary>Raised when an armed jump is committed (from, to in song time), on the pumping thread.</summary>
    public event Action<TimeSpan, TimeSpan>? Jumped;

    /// <summary>How far the band runs behind the song clock. Zero without compensation.</summary>
    public TimeSpan Lookahead { get; private set; }

    /// <summary>The <see cref="Lookahead"/> the next <see cref="Start"/> will use, from the current routing.</summary>
    public TimeSpan PlannedLookahead => _compensate
        ? _orchestra.Outputs.Select(o => _profiles.TryGetValue(o, out var p) ? p.MaxLatency : TimeSpan.Zero).DefaultIfEmpty().Max()
        : TimeSpan.Zero;

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

    /// <summary>The armed jump (song times), or null.</summary>
    public (TimeSpan From, TimeSpan To, bool Repeat)? PendingJump
    {
        get
        {
            lock (_gate)
            {
                return _jump is { } j ? (j.From, j.To, j.Repeat) : null;
            }
        }
    }

    /// <summary>Clock time of the next send (or of committing an armed jump), or null when the song is done.</summary>
    public TimeSpan? NextDue
    {
        get
        {
            lock (_gate)
            {
                TimeSpan? due = _next < _entries.Length ? DueAt(_entries[_next]) : null;
                if (JumpPoint() is { } point && (due is null || ClockAt(point - CommitMargin) < due))
                {
                    due = ClockAt(point - CommitMargin);
                }

                return due;
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
                var heard = SongTimeAt(PlayClock(_clock.Now) - (Lookahead * _speed));
                return heard < TimeSpan.Zero ? TimeSpan.Zero : heard > Duration ? Duration : heard;
            }
        }
    }

    /// <summary>Clock time at which the song moment <paramref name="songTime"/> should be heard, at the current speed, in the current pass.</summary>
    public TimeSpan SoundTime(TimeSpan songTime)
    {
        lock (_gate)
        {
            return ClockAt(songTime + _segments[^1].Offset) + Lookahead;
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
            _anchorPlay = TimeSpan.Zero;
            _speed = speed;
            _jump = null;
            _segments.Clear();
            _segments.Add((TimeSpan.Zero, TimeSpan.Zero));
            Lookahead = PlannedLookahead;

            _sequence = 0;
            _plan = song
                .Where(e => e.Event is ChannelEvent)
                .SelectMany(e => _orchestra.OutputsFor(((ChannelEvent)e.Event).Channel)
                    .Select(output => new Entry(e.At, e.At, output, e.Event, Latency(output, e.Event), _sequence++)))
                .ToArray();
            _entries = [.. _plan];
            Duration = _plan.Length == 0 ? TimeSpan.Zero : _plan.Max(e => e.SongTime);
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
            _anchorPlay = PlayClock(now);
            _anchorClock = now;
            _speed = speed;
            Sort();
        }
    }

    /// <summary>
    /// Arms a jump: when the song (in its current pass) reaches <paramref name="from"/>, it continues
    /// at <paramref name="to"/>. With <paramref name="repeat"/> it stays armed, so every pass jumps
    /// again (a vamp, until <see cref="Disarm"/>). Replaces any armed jump. Returns false when the
    /// song clock is already past <paramref name="from"/> in this pass (too late to jump cleanly).
    /// </summary>
    public bool ArmJump(TimeSpan from, TimeSpan to, bool repeat = false)
    {
        lock (_gate)
        {
            if (from < TimeSpan.Zero || to < TimeSpan.Zero || from > Duration || to > Duration || (repeat && to >= from))
            {
                throw new ArgumentOutOfRangeException(nameof(from), "Jump points must lie in the song, and a repeating jump must go back.");
            }

            var point = from + _segments[^1].Offset;
            if (point < _segments[^1].PlayStart || point <= PlayClock(_clock.Now))
            {
                return false;
            }

            _jump = new Jump(from, to, repeat);
            return true;
        }
    }

    /// <summary>Cancels the armed jump, if it hasn't been committed yet. The song carries on as written.</summary>
    public void Disarm()
    {
        lock (_gate)
        {
            _jump = null;
        }
    }

    /// <summary>Sends everything due by now. Returns how many events outputs accepted.</summary>
    public int Pump()
    {
        lock (_gate)
        {
            var now = _clock.Now;
            CommitJump(now);
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
            _jump = null;
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

    /// <summary>Play time on the song clock (which runs <see cref="Lookahead"/> ahead of what's heard).</summary>
    private TimeSpan PlayClock(TimeSpan clockTime) => _anchorPlay + ((clockTime - _anchorClock) * _speed);

    /// <summary>Clock time at which the song clock reaches play time <paramref name="playTime"/>.</summary>
    private TimeSpan ClockAt(TimeSpan playTime) => _anchorClock + ((playTime - _anchorPlay) / _speed);

    /// <summary>Song time for a play time: the latest segment that had started by then.</summary>
    private TimeSpan SongTimeAt(TimeSpan playTime)
    {
        for (var i = _segments.Count - 1; i > 0; i--)
        {
            if (_segments[i].PlayStart <= playTime)
            {
                return playTime - _segments[i].Offset;
            }
        }

        return playTime - _segments[0].Offset;
    }

    /// <summary>Play time of the armed jump in the current pass, or null.</summary>
    private TimeSpan? JumpPoint() => _jump is { } jump ? jump.From + _segments[^1].Offset : null;

    private void CommitJump(TimeSpan now)
    {
        if (_jump is not { } jump || JumpPoint() is not { } point)
        {
            return;
        }

        var playClock = PlayClock(now);
        if (point < playClock)
        {
            _jump = null; // Pumped too late to jump without taking back sent events: carry on as written.
            return;
        }

        if (playClock + CommitMargin < point)
        {
            return;
        }

        // Nothing at or after the jump point has been sent: every due time is at or after its play time.
        var offset = point - jump.To;
        var kept = _entries[_next..].Where(e => e.PlayTime < point).ToList();
        var releases = Sounding(_entries[.._next].Concat(kept), point)
            .Select(e => e with
            {
                PlayTime = point,
                Event = new NoteOffEvent(((NoteEvent)e.Event).NoteNumber, Melanchall.DryWetMidi.Common.SevenBitNumber.MinValue) { Channel = ((NoteEvent)e.Event).Channel },
                Sequence = _sequence++,
            });
        var rest = _plan.Where(e => e.SongTime >= jump.To).Select(e => e with { PlayTime = e.SongTime + offset, Sequence = _sequence++ });
        _entries = [.. _entries[.._next], .. kept, .. releases, .. rest];
        _segments.Add((point, offset));
        if (!jump.Repeat)
        {
            _jump = null;
        }

        Sort();
        Jumped?.Invoke(jump.From, jump.To);
    }

    /// <summary>Note Ons (per output, channel, note) still held at <paramref name="point"/>, so a jump can release them.</summary>
    private static List<Entry> Sounding(IEnumerable<Entry> before, TimeSpan point)
    {
        var on = new Dictionary<(IMidiOutput, int, int), Entry>();
        foreach (var e in before.Where(e => e.PlayTime < point).OrderBy(e => e.PlayTime).ThenBy(e => e.Sequence))
        {
            switch (e.Event)
            {
                case NoteOnEvent n when n.Velocity > 0:
                    on[(e.Output, n.Channel, n.NoteNumber)] = e;
                    break;
                case NoteEvent n:
                    on.Remove((e.Output, n.Channel, n.NoteNumber));
                    break;
            }
        }

        return [.. on.Values];
    }

    private TimeSpan DueAt(Entry entry) => ClockAt(entry.PlayTime) + Lookahead - entry.Latency;

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

    private readonly record struct Entry(TimeSpan SongTime, TimeSpan PlayTime, IMidiOutput Output, MidiEvent Event, TimeSpan Latency, int Sequence);

    private readonly record struct Jump(TimeSpan From, TimeSpan To, bool Repeat);
}

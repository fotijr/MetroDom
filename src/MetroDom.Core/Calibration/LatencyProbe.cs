namespace MetroDom.Core.Calibration;

/// <summary>How a calibration run plays and listens (ADR 0004).</summary>
public sealed record CalibrationOptions
{
    /// <summary>Strikes per note; the median counts, so a stray onset can't move it far.</summary>
    public int Repeats { get; init; } = 5;

    /// <summary>How long after a strike its onset may arrive. Later onsets are strays.</summary>
    public TimeSpan Window { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Quiet after the window before the next strike, so a ringing bar's tail isn't the next onset.</summary>
    public TimeSpan Settle { get; init; } = TimeSpan.FromMilliseconds(150);

    /// <summary>Listening before the first strike: anything heard then is noise on the onset input.</summary>
    public TimeSpan LeadIn { get; init; } = TimeSpan.FromMilliseconds(300);

    /// <summary>Velocity of every strike (latency can depend on it; one velocity for now).</summary>
    public int Velocity { get; init; } = 100;

    /// <summary>
    /// The onset input's own delay (trigger module scan, mic-to-MIDI detection), subtracted from
    /// every measurement so only the instrument's latency is left.
    /// </summary>
    public TimeSpan OnsetLatency { get; init; }

    /// <summary>Measured minus stored latency beyond which a note has drifted: the 5 ms alignment budget.</summary>
    public TimeSpan DriftThreshold { get; init; } = TimeSpan.FromMilliseconds(5);

    /// <summary>The boot check: three strikes on up to three notes.</summary>
    public static CalibrationOptions Quick { get; } = new() { Repeats = 3 };
}

/// <summary>One strike to send: <paramref name="Note"/> at <paramref name="Velocity"/>.</summary>
public sealed record Probe(int Note, int Velocity, int Round);

/// <summary>What was heard for one note.</summary>
/// <param name="Note">The note struck.</param>
/// <param name="Latencies">Strike → onset for each strike that was heard, in order.</param>
/// <param name="Missed">Strikes with no onset in the window.</param>
public sealed record NoteMeasurement(int Note, IReadOnlyList<TimeSpan> Latencies, int Missed)
{
    public bool Heard => Latencies.Count > 0;

    /// <summary>The middle measurement (mean of the middle two for an even count).</summary>
    public TimeSpan? Median => Heard ? LatencyProbe.Median(Latencies) : null;

    /// <summary>Slowest minus fastest: how steady the instrument (and the onset input) is.</summary>
    public TimeSpan Spread => Heard ? Latencies.Max() - Latencies.Min() : TimeSpan.Zero;
}

/// <summary>A run's measurements for one instrument.</summary>
/// <param name="Notes">One entry per note struck, in the order first struck.</param>
/// <param name="Strays">Onsets that matched no strike: noise, crosstalk, a bar still ringing.</param>
/// <param name="Echoes">Extra onsets inside a strike's window after the first (a bounce, a double trigger).</param>
public sealed record CalibrationResult(IReadOnlyList<NoteMeasurement> Notes, int Strays, int Echoes)
{
    public bool HeardAll => Notes.All(n => n.Heard);

    public bool HeardNone => Notes.All(n => !n.Heard);

    /// <summary>The median of the heard notes' medians: the instrument's latency for notes without their own.</summary>
    public TimeSpan? Latency =>
        Notes.Where(n => n.Heard).Select(n => n.Median!.Value).ToArray() is { Length: > 0 } medians ? LatencyProbe.Median(medians) : null;

    /// <summary>
    /// <paramref name="profile"/> with what was measured: each heard note gets its own latency,
    /// and the overall latency becomes <see cref="Latency"/>. Notes not heard keep theirs.
    /// </summary>
    public InstrumentProfile ApplyTo(InstrumentProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (Latency is not { } latency)
        {
            return profile;
        }

        var notes = new Dictionary<int, TimeSpan>(profile.NoteLatency);
        foreach (var note in Notes.Where(n => n.Heard))
        {
            notes[note.Note] = note.Median!.Value;
        }

        return profile with { Latency = latency, NoteLatency = notes };
    }
}

/// <summary>
/// One calibration run against one instrument (ADR 0004): strike each note in turn, round after
/// round, and time the onset each strike causes. Driven from outside: call <see cref="Due"/>
/// with the time now and send the probe it returns at once; report each onset with
/// <see cref="Onset"/>. One strike is in the air at a time and strikes are spaced wider than the
/// window, so every onset belongs to at most one strike. Spacing also respects the instrument's
/// minimum interval and duty limit, so the firmware never refuses a probe. Thread-safe: onsets
/// arrive on the MIDI input's thread.
/// </summary>
public sealed class LatencyProbe
{
    private readonly Lock _gate = new();
    private readonly CalibrationOptions _options;
    private readonly InstrumentProfile _profile;
    private readonly Probe[] _probes;
    private readonly TimeSpan?[] _sentAt;
    private readonly TimeSpan?[] _heard;
    private int _next;
    private int _strays;
    private int _echoes;
    private TimeSpan _nextAt;

    /// <param name="profile">The instrument: its minimum interval, pulse and duty limit set the spacing.</param>
    /// <param name="notes">Notes to measure (see <see cref="AllNotes"/>, <see cref="QuickNotes"/>).</param>
    /// <param name="start">When listening starts; the first strike follows the lead-in.</param>
    public LatencyProbe(InstrumentProfile profile, IReadOnlyList<int> notes, CalibrationOptions options, TimeSpan start)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(notes);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.Repeats, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.Window, TimeSpan.Zero);
        if (notes.Count == 0)
        {
            throw new ArgumentException("Nothing to measure.", nameof(notes));
        }

        _profile = profile;
        _options = options;
        var distinct = notes.Distinct().ToArray();
        _probes = [.. Enumerable.Range(0, options.Repeats).SelectMany(round => distinct.Select(n => new Probe(n, options.Velocity, round)))];
        _sentAt = new TimeSpan?[_probes.Length];
        _heard = new TimeSpan?[_probes.Length];
        _nextAt = start + options.LeadIn;
    }

    public IReadOnlyList<Probe> Probes => _probes;

    /// <summary>When the next strike is due, or when the last window closes; null once that has passed.</summary>
    public TimeSpan? NextAt
    {
        get
        {
            lock (_gate)
            {
                return _next <= _probes.Length ? _nextAt : null;
            }
        }
    }

    /// <summary>Whether every strike has been sent and the last window has closed by <paramref name="now"/>.</summary>
    public bool IsDone(TimeSpan now)
    {
        lock (_gate)
        {
            return _next == _probes.Length && now >= _nextAt || _next > _probes.Length;
        }
    }

    /// <summary>The probe to send now, if one is due; it is taken as sent at <paramref name="now"/>.</summary>
    public Probe? Due(TimeSpan now)
    {
        lock (_gate)
        {
            if (_next >= _probes.Length || now < _nextAt)
            {
                if (_next == _probes.Length && now >= _nextAt)
                {
                    _next++; // the last window has closed
                }

                return null;
            }

            var probe = _probes[_next];
            _sentAt[_next] = now;
            _next++;
            _nextAt = now + (_next == _probes.Length ? _options.Window : Spacing(probe.Note));
            return probe;
        }
    }

    /// <summary>The time between one strike and the next, here or on the same note a round later.</summary>
    public TimeSpan Spacing(int note)
    {
        var spacing = _options.Window + _options.Settle;

        // The same note comes back after one strike per distinct note; make sure that's slow enough.
        var perRound = _probes.Length / _options.Repeats;
        var pulse = _profile.PulseFor(note);
        var duty = _profile.Safety is { } safety && pulse > TimeSpan.Zero ? pulse * 100 / safety.MaxDutyPercent : TimeSpan.Zero;
        var sameNote = new[] { _profile.MinInterval, duty }.Max();
        if (sameNote > spacing * perRound)
        {
            spacing = sameNote / perRound + TimeSpan.FromMilliseconds(1);
        }

        return spacing;
    }

    /// <summary>An onset heard at <paramref name="at"/> (before the onset input's own delay is taken off).</summary>
    public void Onset(TimeSpan at)
    {
        at -= _options.OnsetLatency;
        lock (_gate)
        {
            for (var i = Math.Min(_next, _probes.Length) - 1; i >= 0; i--)
            {
                if (_sentAt[i] is not { } sent || at < sent)
                {
                    continue;
                }

                if (at - sent > _options.Window)
                {
                    break; // after the latest strike's window: nothing to match
                }

                if (_heard[i] is null)
                {
                    _heard[i] = at - sent;
                }
                else
                {
                    _echoes++;
                }

                return;
            }

            _strays++;
        }
    }

    public CalibrationResult Result()
    {
        lock (_gate)
        {
            var notes = _probes.Select(p => p.Note).Distinct().Select(note =>
            {
                var indexes = Enumerable.Range(0, _probes.Length).Where(i => _probes[i].Note == note && _sentAt[i] is not null).ToArray();
                var heard = indexes.Where(i => _heard[i] is not null).Select(i => _heard[i]!.Value).ToArray();
                return new NoteMeasurement(note, heard, indexes.Length - heard.Length);
            }).ToArray();
            return new CalibrationResult(notes, _strays, _echoes);
        }
    }

    /// <summary>How long a run would take, start to last window.</summary>
    public TimeSpan Duration =>
        _options.LeadIn + _probes.Take(_probes.Length - 1).Select(p => Spacing(p.Note)).Aggregate(TimeSpan.Zero, (a, b) => a + b) + _options.Window;

    /// <summary>
    /// Every note to measure in a full calibration: the profile's notes, or one note for an
    /// instrument that plays any (a synth's latency doesn't change by note): the snare on
    /// channel 10, middle C elsewhere.
    /// </summary>
    public static IReadOnlyList<int> AllNotes(InstrumentProfile profile, bool percussion)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return profile.Notes is { Count: > 0 } notes ? [.. notes.Order()] : [percussion ? 38 : 60];
    }

    /// <summary>The boot check's notes: lowest, middle and highest (fewer if it has fewer).</summary>
    public static IReadOnlyList<int> QuickNotes(InstrumentProfile profile, bool percussion)
    {
        var all = AllNotes(profile, percussion);
        return all.Count <= 3 ? all : [all[0], all[all.Count / 2], all[^1]];
    }

    internal static TimeSpan Median(IReadOnlyList<TimeSpan> values)
    {
        var sorted = values.Order().ToArray();
        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }
}

namespace MetroDom.Core.Timing;

/// <summary>What a <see cref="BeatTracker"/> knows right now.</summary>
public enum BeatState
{
    /// <summary>Not enough steady hits yet to know the beat.</summary>
    Listening,

    /// <summary>Following the player: hits are landing on the predicted beats.</summary>
    Locked,

    /// <summary>Lost the beat (a stop, a long fill): keeping the last stable tempo, waiting for the player.</summary>
    Holding,
}

/// <summary>Tuning for <see cref="BeatTracker"/>. Defaults suit a drummer at 60-200 BPM.</summary>
public sealed record BeatTrackerOptions
{
    /// <summary>Steady hits needed to lock on (or relock after the player changed phase).</summary>
    public int LockHits { get; init; } = 4;

    /// <summary>How far, as a fraction of a beat, a hit may land from a predicted beat and still count as one.</summary>
    public double Tolerance { get; init; } = 0.2;

    /// <summary>Share of a beat's timing error corrected at once (phase). Higher follows faster, sounds more nervous.</summary>
    public double PhaseGain { get; init; } = 0.5;

    /// <summary>Share of a beat's timing error that becomes a tempo change.</summary>
    public double TempoGain { get; init; } = 0.2;

    /// <summary>Largest tempo change per beat the tracker accepts (0.04 = 4%), so the band never lurches.</summary>
    public double MaxChangePerBeat { get; init; } = 0.04;

    /// <summary>Beats without a hit on the beat before the tracker says it lost the player.</summary>
    public double HoldAfterBeats { get; init; } = 2.5;

    /// <summary>Slowest and fastest pulse it locks to.</summary>
    public double MinBpm { get; init; } = 40;

    /// <summary>Fastest pulse it locks to.</summary>
    public double MaxBpm { get; init; } = 240;
}

/// <summary>
/// Follows a human player's beat (ADR 0012, "human leads"): from hits (drum triggers, a pad, a
/// pedal) it learns the tempo and <b>predicts</b> the next beats, because robots must fire before
/// the beat they aim for.
/// </summary>
/// <remarks>
/// <para>
/// Locks once <see cref="BeatTrackerOptions.LockHits"/> hits come at a steady interval. If the
/// song's tempo is known, it picks the beat, half beat or double beat nearest to it, so a drummer
/// playing eighths isn't followed at double speed. Locked, each hit near a predicted beat moves
/// the phase part of the way there and the tempo a little (capped per beat). Hits on the off-beat
/// are subdivisions and change nothing. Other hits (fills, flams, ghost notes) are ignored; enough
/// of them at a new steady interval relock on the new phase. No hit on the beat for
/// <see cref="BeatTrackerOptions.HoldAfterBeats"/> beats means <see cref="BeatState.Holding"/>:
/// the last stable tempo carries on and the grid keeps extrapolating, so a player who comes back
/// on the beat is picked up again at once.
/// </para>
/// <para>Not thread-safe: one input thread, or a lock.</para>
/// </remarks>
public sealed class BeatTracker
{
    private readonly BeatTrackerOptions _options;
    private readonly double? _expectedBpm;
    private readonly List<TimeSpan> _candidates = [];
    private TimeSpan _period;
    private TimeSpan _lastBeat;

    /// <param name="expectedBpm">The song's tempo, if known: decides between beat, half and double time when locking.</param>
    /// <param name="options">Tuning.</param>
    public BeatTracker(double? expectedBpm = null, BeatTrackerOptions? options = null)
    {
        _options = options ?? new BeatTrackerOptions();
        _expectedBpm = expectedBpm;
    }

    public BeatState State { get; private set; } = BeatState.Listening;

    /// <summary>The followed tempo, or null while listening.</summary>
    public double? Bpm => State == BeatState.Listening ? null : 60 / _period.TotalSeconds;

    /// <summary>Length of one beat, or null while listening.</summary>
    public TimeSpan? Period => State == BeatState.Listening ? null : _period;

    /// <summary>The last beat the player confirmed (or the tracker extrapolated to while holding).</summary>
    public TimeSpan? LastBeat => State == BeatState.Listening ? null : _lastBeat;

    /// <summary>Hits counted as beats since the start.</summary>
    public int Beats { get; private set; }

    /// <summary>The first predicted beat at or after <paramref name="time"/>, or null while listening.</summary>
    public TimeSpan? NextBeat(TimeSpan time)
    {
        if (State == BeatState.Listening)
        {
            return null;
        }

        var n = Math.Max(0, Math.Ceiling((time - _lastBeat) / _period));
        return _lastBeat + (_period * n);
    }

    /// <summary>Records a hit at <paramref name="time"/> (times must not go backwards). Returns whether it counted as a beat.</summary>
    public bool Hit(TimeSpan time)
    {
        Update(time);
        if (State == BeatState.Listening)
        {
            return TryLock(time);
        }

        var beats = (time - _lastBeat) / _period;
        var n = Math.Round(beats);
        var off = beats - n;
        if (n >= 1 && Math.Abs(off) <= _options.Tolerance)
        {
            Follow(time, n);
            return true;
        }

        if (Math.Abs(Math.Abs(off) - 0.5) <= _options.Tolerance / 2 || (n == 0 && Math.Abs(off) <= _options.Tolerance))
        {
            return false; // An off-beat subdivision, or a second hit on a beat already counted.
        }

        // Off the grid: a fill, or the player started again somewhere else. Steady enough → relock.
        return TryLock(time);
    }

    /// <summary>Moves time on without a hit, so the tracker notices when the player has stopped.</summary>
    public void Update(TimeSpan now)
    {
        if (State == BeatState.Locked && now - _lastBeat > _period * (_options.HoldAfterBeats + _options.Tolerance))
        {
            State = BeatState.Holding;
        }
    }

    private void Follow(TimeSpan time, double n)
    {
        var predicted = _lastBeat + (_period * n);
        var error = time - predicted;
        var change = (error * _options.TempoGain / n) / _period;
        var limit = _options.MaxChangePerBeat * n;
        _period *= 1 + Math.Clamp(change, -limit, limit);
        _lastBeat = predicted + (error * _options.PhaseGain);
        State = BeatState.Locked;
        Beats++;
        _candidates.Clear();
        _candidates.Add(time);
    }

    private bool TryLock(TimeSpan time)
    {
        _candidates.Add(time);
        while (_candidates.Count > _options.LockHits)
        {
            _candidates.RemoveAt(0);
        }

        if (_candidates.Count < _options.LockHits)
        {
            return false;
        }

        var intervals = _candidates.Zip(_candidates.Skip(1), (a, b) => (b - a).TotalSeconds).Order().ToArray();
        var median = intervals[intervals.Length / 2];
        if (median <= 0 || intervals.Any(i => Math.Abs(i - median) > median * _options.Tolerance))
        {
            return false;
        }

        // Pick the pulse level (half, as played, double) nearest the song's tempo, inside the range.
        var levels = new[] { median * 2, median, median / 2 }
            .Where(p => 60 / p >= _options.MinBpm && 60 / p <= _options.MaxBpm)
            .OrderBy(p => _expectedBpm is { } bpm ? Math.Abs(Math.Log(60 / p / bpm)) : p == median ? 0 : 1)
            .ToArray();
        if (levels.Length == 0)
        {
            return false;
        }

        _period = TimeSpan.FromSeconds(levels[0]);
        _lastBeat = time;
        State = BeatState.Locked;
        Beats++;
        _candidates.Clear();
        _candidates.Add(time);
        return true;
    }
}

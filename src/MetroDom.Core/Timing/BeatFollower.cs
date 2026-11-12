using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;

namespace MetroDom.Core.Timing;

/// <summary>A song's beat grid in song time, from its tempo map and time signatures.</summary>
public static class SongBeats
{
    /// <summary>
    /// Every beat (the time signature's denominator: quarters in 4/4, eighths in 6/8) from the start
    /// to <paramref name="end"/> (default: the last event), following tempo and signature changes.
    /// </summary>
    public static IReadOnlyList<TimeSpan> From(MidiFile midiFile, TimeSpan? end = null)
    {
        ArgumentNullException.ThrowIfNull(midiFile);
        var tempoMap = midiFile.GetTempoMap();
        var ticksPerQuarter = tempoMap.TimeDivision is TicksPerQuarterNoteTimeDivision t ? t.TicksPerQuarterNote : 480;
        var last = end ?? SongTimeline.From(midiFile).Select(e => e.At).DefaultIfEmpty().Max();
        var beats = new List<TimeSpan>();
        long tick = 0;
        while (true)
        {
            var at = (TimeSpan)TimeConverter.ConvertTo<MetricTimeSpan>(tick, tempoMap);
            if (at > last && beats.Count > 0)
            {
                break;
            }

            beats.Add(at);
            var signature = tempoMap.GetTimeSignatureAtTime(new MidiTimeSpan(tick));
            tick += Math.Max(1, ticksPerQuarter * 4 / signature.Denominator);
        }

        return beats;
    }

    /// <summary>The grid after a count-in: <paramref name="count"/> beats of <paramref name="beat"/>, then the song moved by count × beat.</summary>
    public static IReadOnlyList<TimeSpan> WithCountIn(IReadOnlyList<TimeSpan> beats, int count, TimeSpan beat)
    {
        ArgumentNullException.ThrowIfNull(beats);
        var offset = beat * count;
        return [.. Enumerable.Range(0, count).Select(i => beat * i), .. beats.Select(b => b + offset)];
    }
}

/// <summary>Tuning for <see cref="BeatFollower"/>.</summary>
public sealed record BeatFollowerOptions
{
    /// <summary>Share of the band's phase error (in beats) turned into extra speed per beat.</summary>
    public double Gain { get; init; } = 0.5;

    /// <summary>Most the band speeds up or slows down to catch the beat (0.08 = 8%), on top of the player's tempo.</summary>
    public double MaxCorrection { get; init; } = 0.08;

    /// <summary>Speed changes smaller than this (relative) are skipped, so the plan isn't re-timed for nothing.</summary>
    public double Deadband { get; init; } = 0.001;

    /// <summary>Shortest wait before the first note of an aligned start.</summary>
    public TimeSpan MinPreroll { get; init; } = TimeSpan.FromMilliseconds(100);
}

/// <summary>
/// Makes the band follow a human's beat (ADR 0012, "human leads"): steers a
/// <see cref="LatencyScheduler"/> so the song's beats are <em>heard</em> on the beats a
/// <see cref="BeatTracker"/> predicts. Its speed is the player's tempo plus a capped correction
/// proportional to how far the band's next beat is from the player's, so phase errors die away
/// over a few beats instead of jumping. Losing the player (<see cref="BeatState.Holding"/>) keeps
/// the band on the tracker's extrapolated grid at the last stable tempo.
/// </summary>
/// <remarks>
/// Only the next beat whose notes haven't been sent yet can be moved (everything inside the
/// lookahead is already on its way), so the band reacts about one lookahead late: inaudible for
/// tempo drift, which is what a following band has to track. Not thread-safe: call under the
/// same lock as the tracker.
/// </remarks>
public sealed class BeatFollower
{
    private readonly LatencyScheduler _scheduler;
    private readonly TimeSpan[] _beats;
    private readonly BeatTracker _tracker;
    private readonly BeatFollowerOptions _options;

    /// <param name="scheduler">The band.</param>
    /// <param name="beats">The song's beat grid in the scheduler's song time (see <see cref="SongBeats"/>), at least two beats.</param>
    /// <param name="tracker">The player.</param>
    /// <param name="options">Tuning.</param>
    public BeatFollower(LatencyScheduler scheduler, IReadOnlyList<TimeSpan> beats, BeatTracker tracker, BeatFollowerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(beats);
        ArgumentNullException.ThrowIfNull(tracker);
        if (beats.Count < 2)
        {
            throw new ArgumentException("A beat grid needs at least two beats.", nameof(beats));
        }

        _scheduler = scheduler;
        _beats = [.. beats];
        _tracker = tracker;
        _options = options ?? new BeatFollowerOptions();
    }

    /// <summary>The band's last measured offset from the player, in beats (+ = band late), or null.</summary>
    public double? PhaseError { get; private set; }

    /// <summary>
    /// Starts the song so its first beat is heard on one of the player's predicted beats, at the
    /// player's tempo. Returns false (and starts nothing) while the tracker is still listening.
    /// </summary>
    public bool StartAligned(IEnumerable<TimedMidiEvent> song, TimeSpan now)
    {
        ArgumentNullException.ThrowIfNull(song);
        if (_tracker.Period is not { } period)
        {
            return false;
        }

        var speed = Clamp((_beats[1] - _beats[0]) / period);
        var lead = _scheduler.PlannedLookahead + (_beats[0] / speed);
        var beat = _tracker.NextBeat(now + lead + _options.MinPreroll)!.Value;
        _scheduler.Start(song, speed, beat - lead - now);
        PhaseError = 0;
        return true;
    }

    /// <summary>
    /// Re-aims the band at the player. Call on every hit the tracker counted and every so often
    /// between (a tenth of a beat is plenty). Returns the new speed, or null if unchanged.
    /// </summary>
    public double? Update(TimeSpan now)
    {
        _tracker.Update(now);
        if (_tracker.Period is not { } period || _scheduler.IsFinished)
        {
            return null;
        }

        // The first beat whose slowest note hasn't been sent: the earliest the band can still move.
        var movable = now + _scheduler.Lookahead;
        var k = FirstBeatAfter(_scheduler.Position);
        while (k < _beats.Length && _scheduler.SoundTime(_beats[k]) <= movable)
        {
            k++;
        }

        if (k >= _beats.Length)
        {
            return null;
        }

        var band = _scheduler.SoundTime(_beats[k]);
        var human = _tracker.NextBeat(band - (period / 2))!.Value;
        var error = (band - human) / period;
        PhaseError = error;

        var songBeat = k + 1 < _beats.Length ? _beats[k + 1] - _beats[k] : _beats[k] - _beats[k - 1];
        var correction = Math.Clamp(_options.Gain * error, -_options.MaxCorrection, _options.MaxCorrection);
        var speed = Clamp(songBeat / period * (1 + correction));
        if (Math.Abs(speed / _scheduler.Speed - 1) < _options.Deadband)
        {
            return null;
        }

        _scheduler.SetSpeed(speed);
        return speed;
    }

    private int FirstBeatAfter(TimeSpan songTime)
    {
        var i = Array.BinarySearch(_beats, songTime);
        return i >= 0 ? i : ~i;
    }

    private static double Clamp(double speed) => Math.Clamp(speed, LatencyScheduler.MinSpeed, LatencyScheduler.MaxSpeed);
}

namespace MetroDom.Core.Timing;

/// <summary>
/// Follows the tempo of incoming MIDI Clock (<c>F8</c>, 24 ticks per quarter note) from a drum
/// machine, DAW or another band. Averages the last beat's worth of ticks so USB jitter doesn't
/// wobble the band, and only reports a tempo change worth acting on.
/// </summary>
/// <remarks>
/// When the clock stops (a gap, or a Stop message via <see cref="Reset"/>) the history is dropped
/// and nothing is reported, so the band holds its last tempo instead of lurching. Not thread-safe.
/// </remarks>
public sealed class ClockTempo
{
    /// <summary>MIDI Clock resolution.</summary>
    public const int TicksPerBeat = 24;

    private readonly Queue<TimeSpan> _intervals = new();
    private readonly int _minTicks;
    private readonly double _threshold;
    private readonly TimeSpan _gap;
    private TimeSpan _sum;
    private TimeSpan? _lastTick;
    private double? _reported;

    /// <param name="minTicks">Intervals needed before the first tempo (default half a beat).</param>
    /// <param name="threshold">Smallest change in BPM worth reporting.</param>
    /// <param name="gap">A pause longer than this means the clock stopped. Default 250 ms (10 BPM).</param>
    public ClockTempo(int minTicks = TicksPerBeat / 2, double threshold = 0.5, TimeSpan? gap = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(minTicks, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(minTicks, TicksPerBeat);
        ArgumentOutOfRangeException.ThrowIfNegative(threshold);
        _minTicks = minTicks;
        _threshold = threshold;
        _gap = gap ?? TimeSpan.FromMilliseconds(250);
    }

    /// <summary>The clock's tempo now, or null until enough ticks arrived (or after a stop).</summary>
    public double? Bpm => _intervals.Count < _minTicks ? null : 60 / (_sum.TotalSeconds / _intervals.Count * TicksPerBeat);

    /// <summary>
    /// Records a tick at <paramref name="now"/>. Returns a tempo when it differs from the last one
    /// returned by at least the threshold and lies in <see cref="TapTempo.MinBpm"/>-<see cref="TapTempo.MaxBpm"/>, else null.
    /// </summary>
    public double? Tick(TimeSpan now)
    {
        if (_lastTick is { } last)
        {
            var interval = now - last;
            if (interval <= TimeSpan.Zero || interval > _gap)
            {
                Clear();
            }
            else
            {
                _intervals.Enqueue(interval);
                _sum += interval;
                if (_intervals.Count > TicksPerBeat)
                {
                    _sum -= _intervals.Dequeue();
                }
            }
        }

        _lastTick = now;
        if (Bpm is not { } bpm || bpm is < TapTempo.MinBpm or > TapTempo.MaxBpm
            || (_reported is { } previous && Math.Abs(bpm - previous) < _threshold))
        {
            return null;
        }

        _reported = bpm;
        return bpm;
    }

    /// <summary>The clock stopped (MIDI Stop): forget the ticks, keep quiet until it runs again.</summary>
    public void Reset()
    {
        Clear();
        _lastTick = null;
    }

    private void Clear()
    {
        _intervals.Clear();
        _sum = TimeSpan.Zero;
    }
}

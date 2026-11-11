namespace MetroDom.Core.Timing;

/// <summary>
/// Turns taps (a key, a foot pedal, a drum hit) into a tempo. Uses the average of the last few
/// intervals so one sloppy tap doesn't lurch the band, and starts over after a pause.
/// </summary>
public sealed class TapTempo
{
    /// <summary>Tempo range accepted from taps.</summary>
    public const double MinBpm = 30, MaxBpm = 300;

    private readonly int _window;
    private readonly TimeSpan _reset;
    private readonly Queue<TimeSpan> _intervals = new();
    private TimeSpan? _lastTap;

    /// <param name="window">How many recent intervals to average.</param>
    /// <param name="reset">A gap longer than this starts a new tap sequence. Default 2 s (30 BPM).</param>
    public TapTempo(int window = 4, TimeSpan? reset = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(window, 1);
        _window = window;
        _reset = reset ?? TimeSpan.FromSeconds(60 / MinBpm);
    }

    /// <summary>The current tapped tempo, or null until two taps in a row.</summary>
    public double? Bpm => _intervals.Count == 0 ? null : 60 / _intervals.Average(i => i.TotalSeconds);

    /// <summary>Records a tap at <paramref name="now"/>. Returns the tempo once there are two taps.</summary>
    public double? Tap(TimeSpan now)
    {
        if (_lastTap is { } last)
        {
            var interval = now - last;
            if (interval > _reset || interval <= TimeSpan.Zero)
            {
                _intervals.Clear();
            }
            else if (interval >= TimeSpan.FromSeconds(60 / MaxBpm))
            {
                _intervals.Enqueue(interval);
                while (_intervals.Count > _window)
                {
                    _intervals.Dequeue();
                }
            }
            else
            {
                return Bpm; // Faster than MaxBpm: a double-trigger, ignore it.
            }
        }

        _lastTap = now;
        return Bpm;
    }
}

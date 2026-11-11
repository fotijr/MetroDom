using System.Diagnostics;

namespace MetroDom.Core.Timing;

/// <summary>
/// A monotonic clock. Scheduling code reads time only through this, so tests and simulations
/// can drive it with <see cref="VirtualClock"/> instead of sleeping (ADR 0002, ADR 0004).
/// </summary>
public interface IClock
{
    /// <summary>Time since the clock started. Never goes backwards.</summary>
    TimeSpan Now { get; }
}

/// <summary>Real time from a high-resolution <see cref="Stopwatch"/>.</summary>
public sealed class SystemClock : IClock
{
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

    public TimeSpan Now => _stopwatch.Elapsed;
}

/// <summary>Time that only moves when told to. Deterministic: same input, same result.</summary>
public sealed class VirtualClock : IClock
{
    public TimeSpan Now { get; private set; }

    /// <summary>Moves time forward to <paramref name="time"/>. Earlier times are ignored.</summary>
    public void AdvanceTo(TimeSpan time)
    {
        if (time > Now)
        {
            Now = time;
        }
    }

    /// <summary>Moves time forward by <paramref name="delta"/>.</summary>
    public void Advance(TimeSpan delta)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(delta, TimeSpan.Zero);
        Now += delta;
    }
}

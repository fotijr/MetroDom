using MetroDom.Core.Timing;

namespace MetroDom.Conductor.Midi;

/// <summary>
/// Drives a <see cref="LatencyScheduler"/> in real time on its own high-priority thread: sleeps
/// until just before the next send, then spins to it, so notes go out within a fraction of a
/// millisecond of their due time without burning a core between them.
/// </summary>
internal sealed class SchedulerLoop : IDisposable
{
    /// <summary>
    /// Sleep until this close to a send, then spin. OS sleeps overshoot: about 1 ms on Linux and
    /// macOS, up to a 15.6 ms timer tick on Windows.
    /// </summary>
    private static readonly TimeSpan SpinWindow = TimeSpan.FromMilliseconds(OperatingSystem.IsWindows() ? 16 : 2);

    private readonly LatencyScheduler _scheduler;
    private readonly IClock _clock;
    private readonly ManualResetEventSlim _wake = new();
    private readonly Thread _thread;
    private volatile bool _stopping;

    public SchedulerLoop(LatencyScheduler scheduler, IClock clock)
    {
        _scheduler = scheduler;
        _clock = clock;
        _thread = new Thread(Run) { IsBackground = true, Name = "MetroDom scheduler", Priority = ThreadPriority.Highest };
    }

    /// <summary>Raised on the loop thread once the last event has been sent.</summary>
    public event Action? Finished;

    /// <summary>Raised on the loop thread if sending throws. The loop stops (callers panic).</summary>
    public event Action<Exception>? Failed;

    public void Start() => _thread.Start();

    /// <summary>Re-reads the next due time. Call after <see cref="LatencyScheduler.SetSpeed"/>.</summary>
    public void Wake() => _wake.Set();

    public void Dispose()
    {
        _stopping = true;
        _wake.Set();
        if (_thread.IsAlive)
        {
            _thread.Join();
        }

        _wake.Dispose();
    }

    private void Run()
    {
        try
        {
            while (!_stopping)
            {
                _scheduler.Pump();
                if (_scheduler.NextDue is not { } due)
                {
                    Finished?.Invoke();
                    return;
                }

                WaitUntil(due);
            }
        }
#pragma warning disable CA1031 // Report instead of crashing the process mid-song; the session panics.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Failed?.Invoke(ex);
        }
    }

    private void WaitUntil(TimeSpan due)
    {
        var sleep = due - _clock.Now - SpinWindow;
        if (sleep > TimeSpan.Zero && _wake.Wait(sleep))
        {
            _wake.Reset(); // Tempo changed or stopping: re-plan.
            return;
        }

        while (!_stopping && !_wake.IsSet && _clock.Now < due)
        {
            Thread.SpinWait(32);
        }

        _wake.Reset();
    }
}

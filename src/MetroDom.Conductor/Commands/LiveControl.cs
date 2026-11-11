using MetroDom.Conductor.Midi;
using MetroDom.Core.Policy;
using MetroDom.Core.Timing;

namespace MetroDom.Conductor.Commands;

/// <summary>Why a wait or a song ended.</summary>
internal enum LiveSignal
{
    /// <summary>Start was pressed while waiting.</summary>
    Start,

    /// <summary>The song played to its end.</summary>
    Finished,

    /// <summary>Start/stop was pressed while playing.</summary>
    Stopped,

    /// <summary>Next was pressed: go to the next song now.</summary>
    Next,

    /// <summary>Panic: every actuator stopped.</summary>
    Panic,
}

/// <summary>The song playing right now, as the controls see it.</summary>
/// <param name="Scheduler">Its scheduler.</param>
/// <param name="Sections">Vamp / outro.</param>
/// <param name="SetTempo">Sets the tempo in BPM, naming where it came from.</param>
/// <param name="Wake">Makes the scheduler loop re-plan (after arming a jump).</param>
internal sealed record ActiveSong(LatencyScheduler Scheduler, SectionNavigator Sections, Action<double, string> SetTempo, Action Wake);

/// <summary>
/// One place every control lands, whatever sent it: terminal keys, a foot controller
/// (<see cref="ControlAction"/>s from the policy), a tap pedal or MIDI Clock. Knows whether a song
/// is playing or the band is waiting, and signals the setlist loop when a song or wait ends.
/// Inputs call it from their own threads.
/// </summary>
internal sealed class LiveControl(Session session, IClock clock) : IDisposable
{
    private readonly Lock _gate = new();
    private readonly TapTempo _tap = new();
    private readonly ManualResetEventSlim _done = new();
    private ActiveSong? _active;
    private LiveSignal? _signal;
    private double? _pendingBpm;

    public IClock Clock => clock;

    /// <summary>Set when the current wait or song has ended; see <see cref="Signal"/>.</summary>
    public ManualResetEventSlim Done => _done;

    /// <summary>Why it ended, once <see cref="Done"/> is set.</summary>
    public LiveSignal? Signal
    {
        get
        {
            lock (_gate)
            {
                return _signal;
            }
        }
    }

    /// <summary>"tapped", "pedal", "MIDI Clock", "nudged": what last set the tempo, for the status line.</summary>
    public string? TempoSource { get; set; }

    /// <summary>Starts waiting (or playing <paramref name="song"/>): clears the last signal.</summary>
    public void Begin(ActiveSong? song)
    {
        lock (_gate)
        {
            _active = song;
            _signal = null;
            _done.Reset();
        }
    }

    /// <summary>Takes a tempo tapped or followed while waiting, for the next song to start at.</summary>
    public double? TakePendingBpm()
    {
        lock (_gate)
        {
            var bpm = _pendingBpm;
            _pendingBpm = null;
            return bpm;
        }
    }

    /// <summary>Ends the current wait or song with <paramref name="signal"/> (the first one wins).</summary>
    public void End(LiveSignal signal)
    {
        lock (_gate)
        {
            _signal ??= signal;
            _done.Set();
        }
    }

    /// <summary>One tap from a key or pedal. While waiting it sets the tempo the next song starts at.</summary>
    public void Tap(string source)
    {
        double? bpm;
        ActiveSong? active;
        lock (_gate)
        {
            bpm = _tap.Tap(clock.Now);
            active = _active;
            if (active is null && bpm is not null)
            {
                _pendingBpm = bpm;
            }
        }

        if (bpm is { } tapped)
        {
            if (active is not null)
            {
                active.SetTempo(tapped, source);
            }
            else
            {
                session.Messages.Add(FormattableString.Invariant($"Next song starts at {tapped:0.#} BPM ({source})."));
            }
        }
    }

    /// <summary>A tempo from MIDI Clock.</summary>
    public void FollowClock(double bpm)
    {
        ActiveSong? active;
        lock (_gate)
        {
            active = _active;
            if (active is null)
            {
                _pendingBpm = bpm;
            }
        }

        active?.SetTempo(bpm, "MIDI Clock");
    }

    /// <summary>A control from the foot controller or its terminal key.</summary>
    public void Control(ControlAction action)
    {
        ActiveSong? active;
        lock (_gate)
        {
            active = _active;
        }

        switch (action)
        {
            case ControlAction.Tap:
                Tap("pedal");
                break;
            case ControlAction.StartStop:
                End(active is null ? LiveSignal.Start : LiveSignal.Stopped);
                break;
            case ControlAction.Next:
                End(LiveSignal.Next);
                break;
            case ControlAction.Vamp when active is not null:
                session.Messages.Add(active.Sections.ToggleVamp());
                active.Wake();
                break;
            case ControlAction.Outro when active is not null:
                session.Messages.Add(active.Sections.JumpToOutro());
                active.Wake();
                break;
            case ControlAction.Panic:
                // Safety first: silence now, on this thread, before anything else.
                active?.Scheduler.Stop();
                session.Orchestra.Panic();
                session.Messages.Add("PANIC: everything stopped.");
                End(LiveSignal.Panic);
                break;
        }
    }

    public void Dispose() => _done.Dispose();
}

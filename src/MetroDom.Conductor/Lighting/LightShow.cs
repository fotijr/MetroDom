using Melanchall.DryWetMidi.Core;
using MetroDom.Core;
using MetroDom.Core.Lighting;
using MetroDom.Core.Timing;

namespace MetroDom.Conductor.Lighting;

/// <summary>
/// The rig during a set (<c>play --lights</c>): reactive lights from the notes the band plays,
/// timed to land with the sound, and the safe look on panic and at the end (ADR 0011 §3, §5).
/// Renders a frame every <see cref="Tick"/> and hands it to the sACN output. Never blocks music:
/// the scheduler only queues a note here.
/// </summary>
internal sealed class LightShow : IDisposable
{
    /// <summary>Frame period: 50 frames per second, about DMX's own refresh.</summary>
    public static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(20);

    private readonly LightProfile _profile;
    private readonly SacnOutput _output;
    private readonly IClock _clock;
    private readonly ReactiveLights _reactive;
    private readonly ITimer? _timer;
    private readonly Lock _gate = new();
    private bool _safe;
    private bool _disposed;

    /// <param name="profile">The rig.</param>
    /// <param name="output">Where frames go. Its own timer must be off: this show pumps it.</param>
    /// <param name="clock">The scheduler's clock, so note times and frame times agree.</param>
    /// <param name="band">Each instrument's channels, to share fixtures out.</param>
    /// <param name="time">Drives the frame timer; null for the system's.</param>
    /// <param name="start">False leaves frames to the caller (<see cref="Frame"/>), for tests.</param>
    public LightShow(LightProfile profile, SacnOutput output, IClock clock, IReadOnlyList<IReadOnlyList<int>> band, TimeProvider? time = null, bool start = true)
    {
        _profile = profile;
        _output = output;
        _clock = clock;
        _reactive = new ReactiveLights(profile, band);
        if (start)
        {
            _timer = (time ?? TimeProvider.System).CreateTimer(_ => Frame(), null, TimeSpan.Zero, Tick);
        }
    }

    public LightProfile Profile => _profile;

    public ReactiveLights Reactive => _reactive;

    /// <summary>Whether the safe look is up (after panic, until the next song).</summary>
    public bool IsSafe
    {
        get
        {
            lock (_gate)
            {
                return _safe;
            }
        }
    }

    /// <summary>
    /// A send from the scheduler. A Note On becomes a pulse when it will be <em>heard</em>
    /// (<see cref="ScheduledSend.DueAt"/> plus the instrument's latency for that note), less the
    /// rig's own latency, so light and sound arrive together (ADR 0004).
    /// </summary>
    public void Played(ScheduledSend send, InstrumentProfile? instrument)
    {
        ArgumentNullException.ThrowIfNull(send);
        if (send.Event is not NoteOnEvent note || note.Velocity == 0)
        {
            return;
        }

        var heard = send.DueAt + (instrument?.LatencyFor(note.NoteNumber) ?? TimeSpan.Zero);
        _reactive.Note(note.Channel, note.Velocity, heard - _profile.Latency);
    }

    /// <summary>Panic: steady safe look now, nothing pending. Stays until <see cref="Resume"/>.</summary>
    public void Safe()
    {
        lock (_gate)
        {
            _safe = true;
            _reactive.Clear();
        }

        Frame();
    }

    /// <summary>A song starts: back to reactive, from the idle glow.</summary>
    public void Resume()
    {
        lock (_gate)
        {
            _safe = false;
            _reactive.Clear();
        }
    }

    /// <summary>Renders the current look and lets the output send what's due.</summary>
    internal void Frame()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            var look = _safe ? _profile.SafeLook : _reactive.Look(_clock.Now);
            _output.Show(LightRenderer.Render(_profile, look));
            _output.Pump();
        }
    }

    /// <summary>Ends on the safe look (sent in full, not just in the terminated packets, whose data receivers ignore), then terminates.</summary>
    public void Dispose()
    {
        _timer?.Dispose();
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _safe = true;
            _output.Show(LightRenderer.Render(_profile, _profile.SafeLook));
            for (var i = 0; i < SacnOutput.Repeats; i++)
            {
                _output.Pump();
            }

            _disposed = true;
        }

        _output.Dispose();
    }
}

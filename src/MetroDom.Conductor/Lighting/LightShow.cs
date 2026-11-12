using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using MetroDom.Core;
using MetroDom.Core.Lighting;
using MetroDom.Core.Timing;

namespace MetroDom.Conductor.Lighting;

/// <summary>
/// The rig during a set (<c>play --lights</c>), as one more output of the band (ADR 0011 §2):
/// the orchestra sends it every channel, and the scheduler sends early by the rig's latency
/// (<see cref="Timing"/>) like any instrument, so whatever arrives here is due on stage now.
/// Notes on the band's channels become reactive pulses; the lights channel carries cues (scenes,
/// master) that override reactive mode. The safe look on panic and at the end (ADR 0011 §3, §5).
/// Renders a frame every <see cref="Tick"/> and hands it to the sACN output. Never blocks music:
/// a send only records the event.
/// </summary>
internal sealed class LightShow : IMidiOutput, IDisposable
{
    /// <summary>Frame period: 50 frames per second, about DMX's own refresh.</summary>
    public static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(20);

    private readonly LightProfile _profile;
    private readonly SacnOutput _output;
    private readonly IClock _clock;
    private readonly ReactiveLights _reactive;
    private readonly LightCues _cues;
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
        _cues = new LightCues(profile);
        if (start)
        {
            _timer = (time ?? TimeProvider.System).CreateTimer(_ => Frame(), null, TimeSpan.Zero, Tick);
        }
    }

    public LightProfile Profile => _profile;

    public string Name => $"lights {_profile.Name}";

    /// <summary>The rig's latency as the scheduler sees it, so light is sent early and lands with sound.</summary>
    public InstrumentProfile Timing => new(Name, _profile.Latency);

    public ReactiveLights Reactive => _reactive;

    public LightCues Cues => _cues;

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
    /// An event from the orchestra, due on stage now: a cue on the lights channel, a pulse for a
    /// Note On on any other. Panic's All Notes Off releases held scenes but isn't the safe look
    /// (that's <see cref="Safe"/>, after the band is silent).
    /// </summary>
    public void Send(MidiEvent midiEvent)
    {
        ArgumentNullException.ThrowIfNull(midiEvent);
        if (midiEvent is not ChannelEvent channelEvent)
        {
            return;
        }

        var now = _clock.Now;
        if (channelEvent.Channel == _profile.Channel)
        {
            _cues.Receive(midiEvent, now);
        }
        else if (midiEvent is NoteOnEvent note && note.Velocity > 0)
        {
            _reactive.Note(note.Channel, note.Velocity, now);
        }
    }

    /// <summary>
    /// A live cue from the operator's pad (<c>play --lights-pad</c>), on whatever channel the pad
    /// sends: treated as if it came on the lights channel, effective at once.
    /// </summary>
    public void Cue(MidiEvent midiEvent)
    {
        ArgumentNullException.ThrowIfNull(midiEvent);
        if (midiEvent.Clone() is ChannelEvent cue)
        {
            cue.Channel = (FourBitNumber)_profile.Channel;
            _cues.Receive(cue, _clock.Now);
        }
    }

    /// <summary>Panic: steady safe look now, nothing pending. Stays until <see cref="Resume"/>.</summary>
    public void Safe()
    {
        lock (_gate)
        {
            _safe = true;
            _reactive.Clear();
            _cues.Clear();
        }

        Frame();
    }

    /// <summary>A song starts: back to reactive, from the idle glow, no scene up.</summary>
    public void Resume()
    {
        lock (_gate)
        {
            _safe = false;
            _reactive.Clear();
            _cues.Clear();
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

            // The safe look ignores the master: nobody trips in the dark because a fader was down.
            var now = _clock.Now;
            var reactive = _reactive.Look(now);
            var (scene, master) = _cues.At(now);
            _output.Show(_safe
                ? LightRenderer.Render(_profile, _profile.SafeLook)
                : LightRenderer.Render(_profile, scene?.Look ?? reactive, master));
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

using Melanchall.DryWetMidi.Core;

namespace MetroDom.Core.Lighting;

/// <summary>How a scene note behaves (light profile <c>midi.cues</c>).</summary>
public enum CueMode
{
    /// <summary>A scene lasts as long as its note: a lights track's note length, or a held pad.</summary>
    Hold,

    /// <summary>A note recalls its scene until another note; the same note again goes back to reactive.</summary>
    Latch,
}

/// <summary>
/// Cues on the lights channel (ADR 0011 §3.2, §3.3): notes recall scenes, which override
/// reactive mode, and the master CC dims everything. Fed by the scheduler, so a lights track
/// follows the human tempo and jumps like any part, and an operator's pads go through the same
/// path. <b>Flash cap:</b> the look changes at most 3 times a second (a later cue waits, the
/// newest wins), and the master moves at most full range per third of a second, so neither cues
/// nor a toggled fader can flash faster than the cap (ADR 0011 §5). Thread-safe.
/// </summary>
public sealed class LightCues
{
    /// <summary>Shortest time between two scene changes: the same 3-per-second cap as pulses.</summary>
    public static readonly TimeSpan MinChangeGap = ReactiveLights.MinPulseGap;

    /// <summary>Fastest master movement, per second: 0 to full in <see cref="MinChangeGap"/>.</summary>
    public static readonly double MasterSlewPerSecond = 255 / MinChangeGap.TotalSeconds;

    private const int AllSoundOff = 120, AllNotesOff = 123;

    private readonly LightProfile _profile;
    private readonly List<int> _held = [];
    private readonly Lock _gate = new();
    private LightScene? _wanted;
    private LightScene? _shown;
    private TimeSpan? _changedAt;
    private double _master = 255;
    private double _masterWanted = 255;
    private TimeSpan? _masterAt;

    public LightCues(LightProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _profile = profile;
    }

    /// <summary>The scene the cues ask for (null: reactive), before the flash cap.</summary>
    public LightScene? Wanted
    {
        get
        {
            lock (_gate)
            {
                return _wanted;
            }
        }
    }

    /// <summary>
    /// An event on the lights channel at <paramref name="now"/>. Other channels, notes without a
    /// scene and other controllers are ignored. All Notes Off / All Sound Off release held scenes.
    /// </summary>
    public void Receive(MidiEvent midiEvent, TimeSpan now)
    {
        ArgumentNullException.ThrowIfNull(midiEvent);
        if (midiEvent is not ChannelEvent channelEvent || channelEvent.Channel != _profile.Channel)
        {
            return;
        }

        lock (_gate)
        {
            Advance(now);
            switch (midiEvent)
            {
                case NoteOnEvent on when on.Velocity > 0:
                    Press(on.NoteNumber);
                    break;
                case NoteOnEvent on:
                    Release(on.NoteNumber);
                    break;
                case NoteOffEvent off:
                    Release(off.NoteNumber);
                    break;
                case ControlChangeEvent cc when cc.ControlNumber == _profile.MasterCc:
                    _masterWanted = Math.Round(cc.ControlValue * 255 / 127.0);
                    break;
                case ControlChangeEvent cc when (int)cc.ControlNumber is AllSoundOff or AllNotesOff && _profile.Cues == CueMode.Hold:
                    _held.Clear();
                    _wanted = null;
                    break;
            }

            Advance(now);
        }
    }

    /// <summary>The scene on stage (null: reactive) and the master (0-255) at <paramref name="now"/>.</summary>
    public (LightScene? Scene, byte Master) At(TimeSpan now)
    {
        lock (_gate)
        {
            Advance(now);
            return (_shown, (byte)Math.Round(_master));
        }
    }

    /// <summary>Back to reactive (a new song, panic). The master stays where the operator left it.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _held.Clear();
            _wanted = null;
            _shown = null;
            _changedAt = null;
        }
    }

    private void Press(int note)
    {
        if (_profile.SceneFor(note) is not { } scene)
        {
            return;
        }

        if (_profile.Cues == CueMode.Latch)
        {
            _wanted = _wanted?.Note == note ? null : scene;
            return;
        }

        _held.Remove(note);
        _held.Add(note);
        _wanted = scene;
    }

    private void Release(int note)
    {
        if (_profile.Cues == CueMode.Hold && _held.Remove(note))
        {
            _wanted = _held.Count > 0 ? _profile.SceneFor(_held[^1]) : null;
        }
    }

    /// <summary>Lets the flash cap catch up: a waiting scene goes up once the gap has passed, the master slews.</summary>
    private void Advance(TimeSpan now)
    {
        if (_shown != _wanted && (_changedAt is not { } last || now - last >= MinChangeGap))
        {
            _shown = _wanted;
            _changedAt = now;
        }

        if (_masterAt is { } then && now > then)
        {
            var step = MasterSlewPerSecond * (now - then).TotalSeconds;
            _master = _master < _masterWanted ? Math.Min(_masterWanted, _master + step) : Math.Max(_masterWanted, _master - step);
        }

        _masterAt = now;
    }
}

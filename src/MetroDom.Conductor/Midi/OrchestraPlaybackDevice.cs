using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Multimedia;
using MetroDom.Core;

namespace MetroDom.Conductor.Midi;

/// <summary>
/// Lets DryWetMIDI's <see cref="Playback"/> drive an <see cref="Orchestra"/>. <paramref name="routed"/>
/// is told about each event an output accepted (runs on the playback thread; keep it cheap).
/// </summary>
internal sealed class OrchestraPlaybackDevice(Orchestra orchestra, Action<MidiEvent>? routed = null) : IOutputDevice
{
    public event EventHandler<MidiEventSentEventArgs>? EventSent;

    public void PrepareForEventsSending()
    {
    }

    public void SendEvent(MidiEvent midiEvent)
    {
        if (orchestra.Route(midiEvent) > 0)
        {
            routed?.Invoke(midiEvent);
            EventSent?.Invoke(this, new MidiEventSentEventArgs(midiEvent));
        }
    }

    public void Dispose()
    {
    }
}

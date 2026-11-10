using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Multimedia;
using MetroDom.Core;

namespace MetroDom.Conductor.Midi;

/// <summary>Lets DryWetMIDI's <see cref="Playback"/> drive an <see cref="Orchestra"/>.</summary>
internal sealed class OrchestraPlaybackDevice(Orchestra orchestra) : IOutputDevice
{
    public event EventHandler<MidiEventSentEventArgs>? EventSent;

    public void PrepareForEventsSending()
    {
    }

    public void SendEvent(MidiEvent midiEvent)
    {
        if (orchestra.Route(midiEvent) > 0)
        {
            EventSent?.Invoke(this, new MidiEventSentEventArgs(midiEvent));
        }
    }

    public void Dispose()
    {
    }
}

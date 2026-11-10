using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Multimedia;
using MetroDom.Core;

namespace MetroDom.Conductor.Midi;

/// <summary>An OS MIDI output port as a Core <see cref="IMidiOutput"/>.</summary>
internal sealed class DeviceOutput(OutputDevice device) : IMidiOutput, IDisposable
{
    public string Name => device.Name;

    public void Send(MidiEvent midiEvent) => device.SendEvent(midiEvent);

    public void Dispose() => device.Dispose();
}

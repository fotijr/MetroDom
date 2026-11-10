using Melanchall.DryWetMidi.Core;

namespace MetroDom.Core.Tests;

internal sealed class FakeOutput(string name = "fake") : IMidiOutput
{
    public string Name { get; } = name;

    public List<MidiEvent> Sent { get; } = [];

    public void Send(MidiEvent midiEvent) => Sent.Add(midiEvent);
}

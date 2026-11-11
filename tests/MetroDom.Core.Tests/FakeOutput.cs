using Melanchall.DryWetMidi.Core;

namespace MetroDom.Core.Tests;

internal sealed class FakeOutput(string name = "fake") : IMidiOutput
{
    public string Name { get; } = name;

    public List<MidiEvent> Sent { get; } = [];

    /// <summary>When true, Send throws like an unplugged device.</summary>
    public bool Broken { get; set; }

    public void Send(MidiEvent midiEvent)
    {
        if (Broken)
        {
            throw new IOException($"{Name} unplugged");
        }

        Sent.Add(midiEvent);
    }
}

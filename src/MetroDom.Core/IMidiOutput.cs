using Melanchall.DryWetMidi.Core;

namespace MetroDom.Core;

/// <summary>
/// Anything the Conductor can send MIDI to: a robotic instrument, a general MIDI device,
/// or a test fake. Instruments are plain MIDI devices (ADR 0001), so this is the whole contract.
/// </summary>
public interface IMidiOutput
{
    /// <summary>Human-readable name, usually the OS MIDI port name.</summary>
    string Name { get; }

    /// <summary>Sends one MIDI event immediately.</summary>
    void Send(MidiEvent midiEvent);
}

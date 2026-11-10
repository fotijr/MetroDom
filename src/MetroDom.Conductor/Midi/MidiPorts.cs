using Melanchall.DryWetMidi.Core;
using MetroDom.Core;

namespace MetroDom.Conductor.Midi;

/// <summary>An open OS MIDI output port (an instrument, synth, or interface).</summary>
internal interface IMidiOutputPort : IMidiOutput, IDisposable;

/// <summary>An open OS MIDI input port (a controller). Events arrive on a background thread.</summary>
internal interface IMidiInputPort : IDisposable
{
    string Name { get; }

    event Action<MidiEvent>? EventReceived;

    void Start();

    void Stop();
}

/// <summary>One platform's MIDI device API. Indexes match the order of the name lists.</summary>
internal interface IMidiBackend
{
    IReadOnlyList<string> OutputNames();

    IReadOnlyList<string> InputNames();

    IMidiOutputPort OpenOutput(int index);

    IMidiInputPort OpenInput(int index);
}

/// <summary>The OS MIDI system is missing or refused a request (no sequencer, port vanished…).</summary>
internal sealed class MidiDeviceException(string message) : Exception(message);

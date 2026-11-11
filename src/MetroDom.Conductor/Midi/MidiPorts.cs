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

/// <summary>
/// A port as the OS lists it. <see cref="Name"/> survives unplug/replug (used to find the device
/// again). <see cref="DisplayName"/> may add volatile detail such as the ALSA address.
/// </summary>
internal record MidiPortInfo(string Name, string DisplayName);

/// <summary>One platform's MIDI device API.</summary>
internal interface IMidiBackend
{
    IReadOnlyList<MidiPortInfo> Outputs();

    IReadOnlyList<MidiPortInfo> Inputs();

    IMidiOutputPort OpenOutput(MidiPortInfo port);

    IMidiInputPort OpenInput(MidiPortInfo port);
}

/// <summary>The OS MIDI system is missing or refused a request (no sequencer, port vanished…).</summary>
internal sealed class MidiDeviceException(string message) : Exception(message);

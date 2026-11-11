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
/// A port this program created (<see cref="IMidiBackend.CreateVirtualInput"/>). Other programs
/// send to it; <see cref="Send"/> sends back to whoever listens to it (e.g. an Identity Reply).
/// </summary>
internal interface IVirtualPort : IMidiInputPort
{
    void Send(MidiEvent midiEvent);
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

    /// <summary>
    /// Creates a new port that other programs (a DAW, <c>metrodom play</c>) see as an output and
    /// can send to. Events they send arrive on the returned port, which can also answer them.
    /// Lives until disposed.
    /// </summary>
    IVirtualPort CreateVirtualInput(string name);
}

/// <summary>The OS MIDI system is missing or refused a request (no sequencer, port vanished…).</summary>
internal sealed class MidiDeviceException(string message) : Exception(message);

using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Multimedia;

namespace MetroDom.Conductor.Midi;

/// <summary>Windows (WinMM) and macOS (CoreMIDI) devices via DryWetMIDI.</summary>
internal sealed class DryWetMidiBackend : IMidiBackend
{
    public IReadOnlyList<MidiPortInfo> Outputs() => OutputDevice.GetAll().Select(Info).ToArray();

    public IReadOnlyList<MidiPortInfo> Inputs() => InputDevice.GetAll().Select(Info).ToArray();

    public IMidiOutputPort OpenOutput(MidiPortInfo port) => new Output(OutputDevice.GetByName(port.Name));

    public IMidiInputPort OpenInput(MidiPortInfo port) => new Input(InputDevice.GetByName(port.Name));

    private static MidiPortInfo Info(MidiDevice device)
    {
        using (device)
        {
            return new MidiPortInfo(device.Name, device.Name);
        }
    }

    private sealed class Output(OutputDevice device) : IMidiOutputPort
    {
        public string Name => device.Name;

        public void Send(MidiEvent midiEvent) => device.SendEvent(midiEvent);

        public void Dispose() => device.Dispose();
    }

    private sealed class Input : IMidiInputPort
    {
        private readonly InputDevice _device;

        public Input(InputDevice device)
        {
            _device = device;
            _device.EventReceived += (_, e) => EventReceived?.Invoke(e.Event);
        }

        public event Action<MidiEvent>? EventReceived;

        public string Name => _device.Name;

        public void Start() => _device.StartEventsListening();

        public void Stop() => _device.StopEventsListening();

        public void Dispose() => _device.Dispose();
    }
}

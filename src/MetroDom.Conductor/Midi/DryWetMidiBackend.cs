using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Multimedia;

namespace MetroDom.Conductor.Midi;

/// <summary>Windows (WinMM) and macOS (CoreMIDI) devices via DryWetMIDI.</summary>
internal sealed class DryWetMidiBackend : IMidiBackend
{
    public IReadOnlyList<string> OutputNames() => OutputDevice.GetAll().Select(Name).ToArray();

    public IReadOnlyList<string> InputNames() => InputDevice.GetAll().Select(Name).ToArray();

    public IMidiOutputPort OpenOutput(int index) => new Output(OutputDevice.GetByIndex(index));

    public IMidiInputPort OpenInput(int index) => new Input(InputDevice.GetByIndex(index));

    private static string Name(MidiDevice device)
    {
        using (device)
        {
            return device.Name;
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

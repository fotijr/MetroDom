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

    public IVirtualPort CreateVirtualInput(string name)
    {
        if (!OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException(
                "Windows has no built-in virtual MIDI ports. Create one with loopMIDI " +
                "(https://www.tobias-erichsen.de/software/loopmidi.html) and play to that instead.");
        }

        return new VirtualInput(VirtualDevice.Create(name));
    }

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

    /// <summary>
    /// A CoreMIDI virtual device. DryWetMIDI makes it a loopback cable, so it also shows up as an
    /// input named the same; whatever is sent to it is echoed there, and <see cref="Send"/> adds
    /// to that stream (listeners see the echo too, so a requester must ignore its own request).
    /// </summary>
    private sealed class VirtualInput : IVirtualPort
    {
        private readonly VirtualDevice _device;
        private readonly Input _input;

        public VirtualInput(VirtualDevice device)
        {
            _device = device;
            _input = new Input(device.InputDevice);
            _input.EventReceived += e => EventReceived?.Invoke(e);
        }

        public event Action<MidiEvent>? EventReceived;

        public string Name => _device.Name;

        public void Start() => _input.Start();

        public void Stop() => _input.Stop();

        public void Send(MidiEvent midiEvent) => _device.OutputDevice.SendEvent(midiEvent);

        public void Dispose()
        {
            _input.Stop();
            _device.Dispose();
        }
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

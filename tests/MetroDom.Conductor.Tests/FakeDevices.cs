using System.Collections.Concurrent;
using Melanchall.DryWetMidi.Core;
using MetroDom.Conductor.Midi;
using MetroDom.Core.Discovery;

namespace MetroDom.Conductor.Tests;

/// <summary>Paired ports whose "device" behaves like real hardware when sent an Identity Request.</summary>
internal sealed class DeviceBackend : IMidiBackend
{
    private readonly List<Device> _devices = [];
    private readonly ConcurrentDictionary<string, int> _requests = new();
    private readonly ConcurrentDictionary<string, int> _metroDomSysEx = new();

    public ConcurrentBag<FakeDevicePort> Opened { get; } = [];

    public void Add(string name, DeviceIdentity? answer = null, bool echo = false, bool hasInput = true, bool failOpen = false, bool onThread = false, string? profile = null) =>
        _devices.Add(new Device(new MidiPortInfo(name, name), answer, echo, hasInput, failOpen, onThread, profile));

    public int RequestsTo(string name) => _requests.GetValueOrDefault(name);

    public int MetroDomSysExTo(string name) => _metroDomSysEx.GetValueOrDefault(name);

    public void Remove(string name) => _devices.RemoveAll(d => d.Info.Name == name);

    public IReadOnlyList<MidiPortInfo> Outputs() => [.. _devices.Select(d => d.Info)];

    public IReadOnlyList<MidiPortInfo> Inputs() => [.. _devices.Where(d => d.HasInput).Select(d => d.Info)];

    public IMidiOutputPort OpenOutput(MidiPortInfo port)
    {
        var device = Find(port);
        var output = new FakeDevicePort(port.Name, e =>
        {
            if (ProfileExchange.IsRequest(e))
            {
                _metroDomSysEx.AddOrUpdate(port.Name, 1, (_, n) => n + 1);
                if (device.Profile is { } profile)
                {
                    var chunks = ProfileExchange.Reply(profile);
                    void SendAll()
                    {
                        foreach (var chunk in chunks)
                        {
                            device.Input?.Raise(chunk);
                        }
                    }

                    if (device.OnThread)
                    {
                        _ = Task.Run(SendAll);
                    }
                    else
                    {
                        SendAll();
                    }
                }

                return;
            }

            if (!DeviceIdentity.IsRequest(e))
            {
                return;
            }

            _requests.AddOrUpdate(port.Name, 1, (_, n) => n + 1);
            if (device.Echo)
            {
                device.Input?.Raise(e);
            }

            if (device.Answer is { } answer)
            {
                if (device.OnThread)
                {
                    _ = Task.Run(() => device.Input?.Raise(answer.ToReply()));
                }
                else
                {
                    device.Input?.Raise(answer.ToReply());
                }
            }
        });
        Opened.Add(output);
        return output;
    }

    public IMidiInputPort OpenInput(MidiPortInfo port)
    {
        var device = Find(port);
        if (device.FailOpen)
        {
            throw new MidiDeviceException($"{port.Name} is in use");
        }

        device.Input = new FakeDevicePort(port.Name, _ => { });
        Opened.Add(device.Input);
        return device.Input;
    }

    public IVirtualPort CreateVirtualInput(string name) => throw new NotSupportedException();

    private Device Find(MidiPortInfo port) => _devices.Single(d => d.Info.Name == port.Name);

    private sealed record Device(MidiPortInfo Info, DeviceIdentity? Answer, bool Echo, bool HasInput, bool FailOpen, bool OnThread, string? Profile)
    {
        public FakeDevicePort? Input { get; set; }
    }
}

internal sealed class FakeDevicePort(string name, Action<MidiEvent> onSend) : IMidiOutputPort, IMidiInputPort
{
    public event Action<MidiEvent>? EventReceived;

    public string Name { get; } = name;

    public bool Disposed { get; private set; }

    public void Raise(MidiEvent midiEvent) => EventReceived?.Invoke(midiEvent);

    public void Send(MidiEvent midiEvent) => onSend(midiEvent);

    public void Start()
    {
    }

    public void Stop()
    {
    }

    public void Dispose() => Disposed = true;
}

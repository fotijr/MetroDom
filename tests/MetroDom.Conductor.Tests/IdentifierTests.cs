using System.Collections.Concurrent;
using System.Diagnostics;
using Melanchall.DryWetMidi.Core;
using MetroDom.Conductor.Commands;
using MetroDom.Conductor.Midi;
using MetroDom.Core.Discovery;
using Spectre.Console;

namespace MetroDom.Conductor.Tests;

public class IdentifierTests
{
    private static readonly DeviceIdentity Xylo = DeviceIdentity.MetroDom(1, new Version(0, 1));
    private static readonly DeviceIdentity Piano = new("43", 0x41, 2, "1.0.0.0");

    [Fact]
    public void Identify_ClassifiesEveryOutput_AndClosesWhatItOpened()
    {
        var backend = new DeviceBackend();
        backend.Add("xylo", answer: Xylo);
        backend.Add("piano", answer: Piano, onThread: true);
        backend.Add("IAC Bus", echo: true); // loopback: our request comes straight back, no reply
        backend.Add("synth", hasInput: false);
        backend.Add("busy", failOpen: true);

        var stopwatch = Stopwatch.StartNew();
        var ports = Identifier.Identify(backend, TimeSpan.FromMilliseconds(150));
        stopwatch.Stop();

        Assert.Equal(
            [PortKind.MetroDom, PortKind.MidiDevice, PortKind.NoReply, PortKind.OutputOnly, PortKind.Unavailable],
            ports.Select(p => p.Kind));
        Assert.Equal(Xylo, ports[0].Identity);
        Assert.Equal(Piano, ports[1].Identity);
        Assert.Contains("in use", ports[4].Error, StringComparison.Ordinal);
        Assert.All(backend.Opened, p => Assert.True(p.Disposed, $"{p.Name} left open"));
        Assert.Equal(1, backend.RequestsTo("xylo")); // once per port, not repeatedly (ADR 0007)
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), "all ports are asked at once");
    }

    [Fact]
    public void Identify_ReturnsAsSoonAsEveryoneAnswered()
    {
        var backend = new DeviceBackend();
        backend.Add("a", answer: Xylo);
        backend.Add("b", answer: Piano, onThread: true);

        var stopwatch = Stopwatch.StartNew();
        var ports = Identifier.Identify(backend, TimeSpan.FromSeconds(10));

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"waited {stopwatch.Elapsed} although both answered");
        Assert.All(ports, p => Assert.NotNull(p.Identity));
    }

    [Fact]
    public void Identify_NoPorts_IsEmpty() =>
        Assert.Empty(Identifier.Identify(new DeviceBackend(), TimeSpan.FromMilliseconds(10)));

    [Fact]
    public void DevicesTable_DescribesEachKind()
    {
        var backend = new DeviceBackend();
        backend.Add("xylo [1]", answer: Xylo);
        backend.Add("piano", answer: Piano);
        backend.Add("quiet");
        backend.Add("synth", hasInput: false);
        var writer = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings { Ansi = AnsiSupport.No, ColorSystem = ColorSystemSupport.NoColors, Out = new AnsiConsoleOutput(writer) });
        console.Profile.Width = 120;

        console.Write(DevicesCommand.Identified(Identifier.Identify(backend, TimeSpan.FromMilliseconds(50))));
        var text = writer.ToString();

        Assert.Contains("xylo [1]", text, StringComparison.Ordinal);
        Assert.Contains("MetroDom instrument model 1 v0.1.0.0", text, StringComparison.Ordinal);
        Assert.Contains("Yamaha · family 65 model 2", text, StringComparison.Ordinal);
        Assert.Contains("no reply", text, StringComparison.Ordinal);
        Assert.Contains("output only", text, StringComparison.Ordinal);
    }

    /// <summary>Paired ports whose "device" behaves like real hardware when sent an Identity Request.</summary>
    private sealed class DeviceBackend : IMidiBackend
    {
        private readonly List<Device> _devices = [];
        private readonly ConcurrentDictionary<string, int> _requests = new();

        public ConcurrentBag<Port> Opened { get; } = [];

        public void Add(string name, DeviceIdentity? answer = null, bool echo = false, bool hasInput = true, bool failOpen = false, bool onThread = false) =>
            _devices.Add(new Device(new MidiPortInfo(name, name), answer, echo, hasInput, failOpen, onThread));

        public int RequestsTo(string name) => _requests.GetValueOrDefault(name);

        public IReadOnlyList<MidiPortInfo> Outputs() => [.. _devices.Select(d => d.Info)];

        public IReadOnlyList<MidiPortInfo> Inputs() => [.. _devices.Where(d => d.HasInput).Select(d => d.Info)];

        public IMidiOutputPort OpenOutput(MidiPortInfo port)
        {
            var device = Find(port);
            var output = new Port(port.Name, e =>
            {
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

            device.Input = new Port(port.Name, _ => { });
            Opened.Add(device.Input);
            return device.Input;
        }

        public IVirtualPort CreateVirtualInput(string name) => throw new NotSupportedException();

        private Device Find(MidiPortInfo port) => _devices.Single(d => d.Info.Name == port.Name);

        private sealed record Device(MidiPortInfo Info, DeviceIdentity? Answer, bool Echo, bool HasInput, bool FailOpen, bool OnThread)
        {
            public Port? Input { get; set; }
        }
    }

    private sealed class Port(string name, Action<MidiEvent> onSend) : IMidiOutputPort, IMidiInputPort
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
}

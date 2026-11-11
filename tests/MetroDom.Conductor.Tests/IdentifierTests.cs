using System.Collections.Concurrent;
using System.Diagnostics;
using Melanchall.DryWetMidi.Core;
using MetroDom.Conductor.Commands;
using MetroDom.Conductor.Midi;
using MetroDom.Core;
using MetroDom.Core.Discovery;
using Spectre.Console;

namespace MetroDom.Conductor.Tests;

public class IdentifierTests
{
    private static readonly DeviceIdentity Xylo = DeviceIdentity.MetroDom(1, new Version(0, 1));
    private static readonly DeviceIdentity Piano = new("43", 0x41, 2, "1.0.0.0");
    private static readonly string XyloProfile = InstrumentProfileJson.Write(new InstrumentProfile("xylo", TimeSpan.FromMilliseconds(9))
    {
        Notes = new HashSet<int> { 60, 64, 67 },
        NoteLatency = new Dictionary<int, TimeSpan> { [64] = TimeSpan.FromMilliseconds(12) },
        MinInterval = TimeSpan.FromMilliseconds(60),
    });

    [Fact]
    public void Identify_FetchesProfiles_FromMetroDomInstrumentsOnly()
    {
        var backend = new DeviceBackend();
        backend.Add("xylo", answer: Xylo, profile: XyloProfile, onThread: true);
        backend.Add("old firmware", answer: Xylo); // MetroDom, but doesn't know the profile request
        backend.Add("garbled", answer: Xylo, profile: "{\"schema\":\"nope\"}");
        backend.Add("piano", answer: Piano, profile: XyloProfile); // would answer, must never be asked
        backend.Add("quiet");

        var ports = Identifier.Identify(backend, TimeSpan.FromMilliseconds(150));

        Assert.Equal("xylo", ports[0].Profile?.Name);
        Assert.Equal([60, 64, 67], ports[0].Profile!.Notes!.Order());
        Assert.Equal(TimeSpan.FromMilliseconds(12), ports[0].Profile!.MaxLatency);
        Assert.Null(ports[1].Profile);
        Assert.Equal("didn't send its profile", ports[1].ProfileError);
        Assert.Contains("doesn't parse", ports[2].ProfileError, StringComparison.Ordinal);
        Assert.Null(ports[3].Profile);
        Assert.Null(ports[3].ProfileError);
        Assert.Equal(0, backend.MetroDomSysExTo("piano")); // 7D is shared: only MetroDom gets MetroDom SysEx
        Assert.Equal(0, backend.MetroDomSysExTo("quiet"));
        Assert.Equal(1, backend.MetroDomSysExTo("xylo"));
        Assert.All(backend.Opened, p => Assert.True(p.Disposed, $"{p.Name} left open"));
    }

    [Fact]
    public void Identify_ReturnsAsSoonAsEveryProfileArrived()
    {
        var backend = new DeviceBackend();
        backend.Add("a", answer: Xylo, profile: XyloProfile, onThread: true);
        backend.Add("b", answer: Xylo, profile: XyloProfile);

        var stopwatch = Stopwatch.StartNew();
        var ports = Identifier.Identify(backend, TimeSpan.FromSeconds(10));

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"waited {stopwatch.Elapsed} although both answered");
        Assert.All(ports, p => Assert.NotNull(p.Profile));
    }

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
        backend.Add("a", answer: Xylo, profile: XyloProfile);
        backend.Add("b", answer: Piano, onThread: true);

        var stopwatch = Stopwatch.StartNew();
        var ports = Identifier.Identify(backend, TimeSpan.FromSeconds(10));

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"waited {stopwatch.Elapsed} although both answered");
        Assert.All(ports, p => Assert.NotNull(p.Identity));
    }

    [Fact]
    public void Identify_OnlyTheGivenPorts_AsksNoOthers()
    {
        var backend = new DeviceBackend();
        backend.Add("xylo", answer: Xylo, profile: XyloProfile);
        backend.Add("piano", answer: Piano);

        var ports = Identifier.Identify(backend, TimeSpan.FromMilliseconds(100), [new MidiPortInfo("xylo", "xylo")]);

        Assert.Equal("xylo", Assert.Single(ports).Profile?.Name);
        Assert.Equal(0, backend.RequestsTo("piano"));
    }

    [Fact]
    public void Identify_NoPorts_IsEmpty() =>
        Assert.Empty(Identifier.Identify(new DeviceBackend(), TimeSpan.FromMilliseconds(10)));

    [Fact]
    public void DevicesTable_DescribesEachKind()
    {
        var backend = new DeviceBackend();
        backend.Add("xylo [1]", answer: Xylo, profile: XyloProfile);
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
        Assert.Contains("profile: xylo · notes 60,64,67 (3) · 9-12 ms · repeat ≥ 60 ms", text, StringComparison.Ordinal);
        Assert.Contains("Yamaha · family 65 model 2", text, StringComparison.Ordinal);
        Assert.Contains("no reply", text, StringComparison.Ordinal);
        Assert.Contains("output only", text, StringComparison.Ordinal);
    }

    /// <summary>Paired ports whose "device" behaves like real hardware when sent an Identity Request.</summary>
    private sealed class DeviceBackend : IMidiBackend
    {
        private readonly List<Device> _devices = [];
        private readonly ConcurrentDictionary<string, int> _requests = new();
        private readonly ConcurrentDictionary<string, int> _metroDomSysEx = new();

        public ConcurrentBag<Port> Opened { get; } = [];

        public void Add(string name, DeviceIdentity? answer = null, bool echo = false, bool hasInput = true, bool failOpen = false, bool onThread = false, string? profile = null) =>
            _devices.Add(new Device(new MidiPortInfo(name, name), answer, echo, hasInput, failOpen, onThread, profile));

        public int RequestsTo(string name) => _requests.GetValueOrDefault(name);

        public int MetroDomSysExTo(string name) => _metroDomSysEx.GetValueOrDefault(name);

        public IReadOnlyList<MidiPortInfo> Outputs() => [.. _devices.Select(d => d.Info)];

        public IReadOnlyList<MidiPortInfo> Inputs() => [.. _devices.Where(d => d.HasInput).Select(d => d.Info)];

        public IMidiOutputPort OpenOutput(MidiPortInfo port)
        {
            var device = Find(port);
            var output = new Port(port.Name, e =>
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

            device.Input = new Port(port.Name, _ => { });
            Opened.Add(device.Input);
            return device.Input;
        }

        public IVirtualPort CreateVirtualInput(string name) => throw new NotSupportedException();

        private Device Find(MidiPortInfo port) => _devices.Single(d => d.Info.Name == port.Name);

        private sealed record Device(MidiPortInfo Info, DeviceIdentity? Answer, bool Echo, bool HasInput, bool FailOpen, bool OnThread, string? Profile)
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

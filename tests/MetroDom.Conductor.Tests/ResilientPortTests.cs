using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using MetroDom.Conductor.Midi;
using MetroDom.Core;

namespace MetroDom.Conductor.Tests;

public class ResilientPortTests
{
    private static readonly MidiPortInfo Xylo = new("Xylophone:MIDI 1", "Xylophone:MIDI 1 (20:0)");

    private static NoteOnEvent Note(int note = 60) => new((SevenBitNumber)note, (SevenBitNumber)100);

    private static ResilientOutput OpenOutput(FakeBackend backend, List<string> log) =>
        new(backend, Xylo, log.Add, pollInterval: Timeout.InfiniteTimeSpan);

    [Fact]
    public void Output_UnpluggedDevice_GoesSilentWithoutThrowing()
    {
        var backend = new FakeBackend(Xylo);
        var log = new List<string>();
        using var output = OpenOutput(backend, log);

        backend.Unplug(Xylo);
        output.Poll();
        output.Send(Note());

        Assert.False(output.IsConnected);
        Assert.True(backend.OpenedOutputs[0].Disposed);
        Assert.Empty(backend.OpenedOutputs[0].Sent);
        Assert.Contains(log, l => l.Contains("disconnected", StringComparison.Ordinal));
    }

    [Fact]
    public void Output_SendFailure_GoesSilentWithoutThrowing()
    {
        var backend = new FakeBackend(Xylo);
        using var output = OpenOutput(backend, []);
        backend.OpenedOutputs[0].Broken = true;

        output.Send(Note());
        output.Send(Note());

        Assert.False(output.IsConnected);
    }

    [Fact]
    public void Output_Replug_ReopensAtNewAddressAndPanicsFirst()
    {
        var backend = new FakeBackend(Xylo);
        var log = new List<string>();
        using var output = OpenOutput(backend, log);
        backend.Unplug(Xylo);
        output.Poll();

        backend.Plug(Xylo with { DisplayName = "Xylophone:MIDI 1 (24:0)" });
        output.Poll();
        output.Send(Note(64));

        Assert.True(output.IsConnected);
        var reopened = backend.OpenedOutputs[1];
        Assert.Equal("Xylophone:MIDI 1 (24:0)", reopened.Info.DisplayName);
        Assert.Equal(Orchestra.ChannelCount * 2, reopened.Sent.Count(e => e is ControlChangeEvent));
        Assert.Equal(64, (int)Assert.IsType<NoteOnEvent>(reopened.Sent[^1]).NoteNumber);
        Assert.Contains(log, l => l.Contains("reconnected", StringComparison.Ordinal));
    }

    [Fact]
    public void Output_ReopenFailure_RetriesNextPoll()
    {
        var backend = new FakeBackend(Xylo);
        using var output = OpenOutput(backend, []);
        backend.Unplug(Xylo);
        output.Poll();
        backend.Plug(Xylo);
        backend.FailOpens = 1;

        output.Poll();
        Assert.False(output.IsConnected);
        output.Poll();
        Assert.True(output.IsConnected);
    }

    [Fact]
    public void Input_Replug_ResumesListeningAndForwarding()
    {
        var backend = new FakeBackend(Xylo);
        using var input = new ResilientInput(backend, Xylo, _ => { }, pollInterval: Timeout.InfiniteTimeSpan);
        var received = new List<MidiEvent>();
        input.EventReceived += received.Add;
        input.Start();

        backend.Unplug(Xylo);
        input.Poll();
        backend.Plug(Xylo);
        input.Poll();
        var reopened = backend.OpenedInputs[1];
        reopened.Raise(Note(67));

        Assert.True(backend.OpenedInputs[0].Disposed);
        Assert.True(reopened.Started);
        Assert.Equal(67, (int)Assert.IsType<NoteOnEvent>(Assert.Single(received)).NoteNumber);
    }

    [Fact]
    public void Poll_ListingFailure_KeepsCurrentState()
    {
        var backend = new FakeBackend(Xylo);
        using var output = OpenOutput(backend, []);
        backend.ListingFails = true;

        output.Poll();

        Assert.True(output.IsConnected);
    }

    private sealed class FakeBackend(params MidiPortInfo[] ports) : IMidiBackend
    {
        private readonly List<MidiPortInfo> _ports = [.. ports];

        public List<FakeOutputPort> OpenedOutputs { get; } = [];

        public List<FakeInputPort> OpenedInputs { get; } = [];

        public int FailOpens { get; set; }

        public bool ListingFails { get; set; }

        public void Unplug(MidiPortInfo port) => _ports.RemoveAll(p => p.Name == port.Name);

        public void Plug(MidiPortInfo port) => _ports.Add(port);

        public IReadOnlyList<MidiPortInfo> Outputs() => ListingFails ? throw new IOException("busy") : [.. _ports];

        public IReadOnlyList<MidiPortInfo> Inputs() => Outputs();

        public IMidiOutputPort OpenOutput(MidiPortInfo port)
        {
            ThrowIfFailing();
            var opened = new FakeOutputPort(port);
            OpenedOutputs.Add(opened);
            return opened;
        }

        public IVirtualPort CreateVirtualInput(string name) => throw new NotSupportedException();

        public IMidiInputPort OpenInput(MidiPortInfo port)
        {
            ThrowIfFailing();
            var opened = new FakeInputPort(port.Name);
            OpenedInputs.Add(opened);
            return opened;
        }

        private void ThrowIfFailing()
        {
            if (FailOpens > 0)
            {
                FailOpens--;
                throw new IOException("still enumerating");
            }
        }
    }

    private sealed class FakeOutputPort(MidiPortInfo info) : IMidiOutputPort
    {
        public MidiPortInfo Info { get; } = info;

        public string Name => Info.Name;

        public List<MidiEvent> Sent { get; } = [];

        public bool Broken { get; set; }

        public bool Disposed { get; private set; }

        public void Send(MidiEvent midiEvent)
        {
            if (Broken)
            {
                throw new IOException("unplugged");
            }

            Sent.Add(midiEvent);
        }

        public void Dispose() => Disposed = true;
    }

    private sealed class FakeInputPort(string name) : IMidiInputPort
    {
        public event Action<MidiEvent>? EventReceived;

        public string Name { get; } = name;

        public bool Started { get; private set; }

        public bool Disposed { get; private set; }

        public void Raise(MidiEvent midiEvent) => EventReceived?.Invoke(midiEvent);

        public void Start() => Started = true;

        public void Stop() => Started = false;

        public void Dispose() => Disposed = true;
    }
}

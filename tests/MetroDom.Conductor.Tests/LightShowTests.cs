using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using MetroDom.Conductor.Lighting;
using MetroDom.Core;
using MetroDom.Core.Lighting;
using MetroDom.Core.Timing;

namespace MetroDom.Conductor.Tests;

public sealed class LightShowTests : IDisposable
{
    private const string Rig = """
        {"schema":"metrodom.light-profile/0","name":"rig","latencyMs":20,
         "fixtures":[{"id":"par","address":1,"channels":["dimmer","red","green","blue"]}],
         "safeLook":{"*":{"dimmer":90,"red":200}}}
        """;

    private static readonly TimeSpan Ms = TimeSpan.FromMilliseconds(1);

    private readonly VirtualClock _clock = new();
    private readonly Socket _socket = new();
    private readonly LightShow _show;

    public LightShowTests()
    {
        Assert.True(LightProfileJson.TryParse(Rig, out var rig, out _));
        _show = new LightShow(rig, new SacnOutput(_socket, rig.Output, "test", start: false), _clock, [[0]], start: false);
    }

    public void Dispose() => _show.Dispose();

    private byte Dimmer() => _socket.Last![0];

    private static ScheduledSend Send(TimeSpan dueAt, int velocity = 127) =>
        new(dueAt, TimeSpan.Zero, new NullOutput(), new NoteOnEvent((SevenBitNumber)60, (SevenBitNumber)velocity));

    [Fact]
    public void Pulse_LandsWhenTheNoteIsHeard_LessTheRigsLatency()
    {
        // Sent at 100 ms to a 35 ms instrument: heard at 135 ms. The rig takes 20 ms: light changes at 115 ms.
        _show.Played(Send(100 * Ms), new InstrumentProfile("drums", 35 * Ms));

        _clock.AdvanceTo(114 * Ms);
        _show.Frame();
        Assert.Equal(40, Dimmer());

        _clock.AdvanceTo(115 * Ms);
        _show.Frame();
        Assert.Equal(255, Dimmer());
    }

    [Fact]
    public void NoteOffs_AndZeroVelocity_DontPulse()
    {
        _show.Played(Send(TimeSpan.Zero, velocity: 0), null);
        _show.Played(new ScheduledSend(TimeSpan.Zero, TimeSpan.Zero, new NullOutput(), new NoteOffEvent((SevenBitNumber)60, (SevenBitNumber)0)), null);

        _show.Frame();

        Assert.Equal(40, Dimmer());
    }

    [Fact]
    public void Panic_ShowsTheSafeLookAtOnce_UntilTheNextSong()
    {
        _show.Played(Send(20 * Ms), null); // light due at 20 ms - 20 ms rig latency = 0
        _show.Frame();
        Assert.Equal(255, Dimmer());

        _show.Safe();
        Assert.True(_show.IsSafe);
        Assert.Equal(90, Dimmer()); // sent by Safe itself, not on the next tick
        Assert.Equal(200, _socket.Last![1]);

        _show.Played(Send(10 * Ms), null); // anything still in flight doesn't flash
        _clock.AdvanceTo(10 * Ms);
        _show.Frame();
        Assert.Equal(90, Dimmer());

        _show.Resume();
        _show.Frame();
        Assert.Equal(40, Dimmer());
    }

    [Fact]
    public void Dispose_EndsOnTheSafeLook_ThenTerminates()
    {
        _show.Frame();

        _show.Dispose();

        var live = _socket.Packets.Where(p => !p.Terminated).ToList();
        Assert.Equal(90, live[^1].Data[0]); // receivers honor this one; terminated packets' data is ignored
        Assert.Equal(3, _socket.Packets.Count(p => p.Terminated));
        Assert.Equal(90, _socket.Packets[^1].Data[0]);
    }

    private sealed class Socket : ISacnSocket
    {
        public List<E131Packet> Packets { get; } = [];

        public byte[]? Last => Packets.LastOrDefault()?.Data;

        public void Send(byte[] packet, string host, int port)
        {
            Assert.True(E131.TryParse(packet, out var parsed));
            Packets.Add(parsed);
        }

        public void Dispose()
        {
        }
    }

    private sealed class NullOutput : IMidiOutput
    {
        public string Name => "null";

        public void Send(MidiEvent midiEvent)
        {
        }
    }
}

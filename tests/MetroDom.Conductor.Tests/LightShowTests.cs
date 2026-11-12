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
         "scenes":[{"note":36,"set":{"*":{"dimmer":200,"blue":255}}}],
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

    private static NoteOnEvent On(int note, int velocity = 127, int channel = 0) =>
        new((SevenBitNumber)note, (SevenBitNumber)velocity) { Channel = (FourBitNumber)channel };

    private static ControlChangeEvent Cc(int number, int value) =>
        new((SevenBitNumber)number, (SevenBitNumber)value) { Channel = (FourBitNumber)15 };

    [Fact]
    public void AsASchedulerOutput_LightLandsWithTheSound()
    {
        // A 35 ms instrument and the 20 ms rig: the band runs 35 ms behind, the drum is sent at 0,
        // the light at 15 ms, and both land at 35 ms. A lights-track cue is timed the same way.
        var orchestra = new Orchestra();
        var drums = new Recorder();
        orchestra.Assign(0, drums);
        orchestra.AssignAll(_show);
        var profiles = new Dictionary<IMidiOutput, InstrumentProfile> { [drums] = new("drums", 35 * Ms), [_show] = _show.Timing };
        var scheduler = new LatencyScheduler(orchestra, _clock, profiles);
        scheduler.Start([new(TimeSpan.Zero, On(60)), new(500 * Ms, On(36, channel: 15))]);

        scheduler.Pump();
        Assert.Single(drums.Events);
        _clock.AdvanceTo(14 * Ms);
        scheduler.Pump();
        _show.Frame();
        Assert.Equal(40, Dimmer());

        _clock.AdvanceTo(15 * Ms);
        scheduler.Pump();
        _show.Frame();
        Assert.Equal(255, Dimmer());

        _clock.AdvanceTo(515 * Ms);
        scheduler.Pump();
        _show.Frame();
        Assert.Equal(200, Dimmer());
        Assert.Equal(255, _socket.Last![3]);
        Assert.Single(drums.Events); // the cue never reached the drums
    }

    [Fact]
    public void NoteOffs_AndZeroVelocity_DontPulse()
    {
        _show.Send(On(60, velocity: 0));
        _show.Send(new NoteOffEvent((SevenBitNumber)60, (SevenBitNumber)0));

        _show.Frame();

        Assert.Equal(40, Dimmer());
    }

    [Fact]
    public void Scene_OverridesReactive_AndMasterDimsIt()
    {
        _show.Send(On(36, channel: 15));
        _show.Send(On(60)); // a pulse under the scene doesn't show
        _show.Frame();
        Assert.Equal(200, Dimmer());

        _show.Send(Cc(7, 0)); // master down: slews, never a jump
        _clock.AdvanceTo(LightCues.MinChangeGap);
        _show.Frame();
        Assert.Equal(0, Dimmer());

        _show.Send(new NoteOffEvent((SevenBitNumber)36, (SevenBitNumber)0) { Channel = (FourBitNumber)15 });
        _show.Send(Cc(7, 127));
        _clock.AdvanceTo(2 * LightCues.MinChangeGap);
        _show.Frame();
        Assert.Equal(40, Dimmer()); // back to the reactive glow
    }

    [Fact]
    public void PadCue_OnAnyChannel_RecallsTheScene()
    {
        _show.Cue(On(36, channel: 9)); // a pad grid sending on channel 10

        _show.Frame();

        Assert.Equal(200, Dimmer());
    }

    [Fact]
    public void Panic_ShowsTheSafeLookAtOnce_UntilTheNextSong()
    {
        _show.Send(On(60));
        _show.Send(Cc(7, 0));
        _show.Frame();
        Assert.Equal(255, Dimmer());

        _show.Safe();
        Assert.True(_show.IsSafe);
        Assert.Equal(90, Dimmer()); // sent by Safe itself, not on the next tick
        Assert.Equal(200, _socket.Last![1]);

        _show.Send(On(60)); // anything still in flight doesn't flash
        _show.Send(On(36, channel: 15));
        _clock.AdvanceTo(LightCues.MinChangeGap);
        _show.Frame();
        Assert.Equal(90, Dimmer()); // and the master doesn't dim the safe look

        _show.Resume();
        _show.Send(Cc(7, 127));
        _clock.AdvanceTo(2 * LightCues.MinChangeGap);
        _show.Frame();
        Assert.Equal(40, Dimmer());
    }

    [Fact]
    public void VenueLimits_CapBrightness_AndQuietHoursCapItMore_SafeLookIncluded()
    {
        Assert.True(LightProfileJson.TryParse(Rig, out var rig, out _));
        var time = new LocalTime(new DateTimeOffset(2026, 11, 11, 22, 0, 0, TimeSpan.Zero));
        var limits = new LightLimits { MaxBrightness = 50, Quiet = new QuietHours(new TimeOnly(23, 0), new TimeOnly(7, 0), 20) };
        using var show = new LightShow(rig, new SacnOutput(_socket, rig.Output, "test", start: false), _clock, [[0]], limits, time, start: false);

        show.Send(On(60));
        show.Frame();
        Assert.Equal(128, Dimmer()); // full pulse at 50%

        time.Now = time.Now.AddHours(2); // 00:00, quiet hours
        show.Safe();
        Assert.Equal(18, Dimmer()); // safe look 90 at 20%: dim, never dark
    }

    [Fact]
    public void Strobe_ReachesFixtures_OnlyWhenThePolicyAllowsIt_AndNotInQuietHours()
    {
        const string strobeRig = """
            {"schema":"metrodom.light-profile/0","name":"rig",
             "fixtures":[{"id":"par","address":1,"channels":["dimmer","strobe"]}],
             "scenes":[{"note":36,"set":{"*":{"dimmer":255,"strobe":200}}}]}
            """;
        Assert.True(LightProfileJson.TryParse(strobeRig, out var rig, out _));
        var time = new LocalTime(new DateTimeOffset(2026, 11, 11, 22, 0, 0, TimeSpan.Zero));
        var limits = new LightLimits { Strobe = true, Quiet = new QuietHours(new TimeOnly(23, 0), new TimeOnly(7, 0), 100) };
        using var show = new LightShow(rig, new SacnOutput(_socket, rig.Output, "test", start: false), _clock, [[0]], limits, time, start: false);

        show.Cue(On(36));
        show.Frame();
        Assert.Equal(200, _socket.Last![1]);

        time.Now = time.Now.AddHours(1);
        show.Frame();
        Assert.Equal(0, _socket.Last![1]);
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

    /// <summary>Wall-clock time in UTC, settable, for quiet hours.</summary>
    private sealed class LocalTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Recorder : IMidiOutput
    {
        public List<MidiEvent> Events { get; } = [];

        public string Name => "recorder";

        public void Send(MidiEvent midiEvent) => Events.Add(midiEvent);
    }
}

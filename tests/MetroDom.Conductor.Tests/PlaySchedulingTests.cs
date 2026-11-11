using System.Collections.Concurrent;
using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using MetroDom.Conductor.Cli;
using MetroDom.Conductor.Commands;
using MetroDom.Conductor.Midi;
using MetroDom.Core.Timing;

namespace MetroDom.Conductor.Tests;

public class PlaySchedulingTests
{
    private static TimeSpan Ms(double ms) => TimeSpan.FromMilliseconds(ms);

    [Theory]
    [InlineData("0", "0", null, 0)]
    [InlineData("MetroDom drums=10@35", "MetroDom drums", new[] { 9 }, 35)]
    [InlineData("xylo@12.5ms", "xylo", null, 12.5)]
    [InlineData("fluid=1-9,11-16", "fluid", new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 10, 11, 12, 13, 14, 15 }, 0)]
    [InlineData("Midi Through:Midi Through Port-0 (14:0)", "Midi Through:Midi Through Port-0 (14:0)", null, 0)]
    public void OutputSpec_Parses(string raw, string port, int[]? channels, double latencyMs)
    {
        Assert.True(OutputSpec.TryParse(raw, out var spec, out var error), error);

        Assert.Equal(port, spec.Port);
        Assert.Equal(channels ?? InstrumentSpec.AllChannels, spec.Channels);
        Assert.Equal(Ms(latencyMs), spec.Latency);
    }

    [Theory]
    [InlineData("drums=17@5")]
    [InlineData("drums=0")]
    [InlineData("=10@5")]
    [InlineData("drums@99999")]
    public void OutputSpec_RejectsBadSpecs(string raw) =>
        Assert.False(OutputSpec.TryParse(raw, out _, out _));

    [Theory]
    [InlineData("play", "song.mid", "-o", "drums=17@5")]
    [InlineData("play", "song.mid", "-o", "0", "--tempo", "10")]
    public void Play_BadOptions_ExitWith2BeforeOpeningAnyDevice(params string[] args)
    {
        var path = Path.Combine(Path.GetTempPath(), $"metrodom-play-{Guid.NewGuid():N}.mid");
        Core.Simulation.DemoSong.Create().Write(path);
        try
        {
            var resolved = args.Select(a => a == "song.mid" ? path : a).ToArray();
            Assert.Equal(MetroDomApp.UsageError, MetroDomApp.Create().Run(resolved, TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Session_RoutesEachOutputItsChannels_AndExposesLatency()
    {
        var xylo = new RecordingPort("xylo");
        var drums = new RecordingPort("drums");
        using (var session = new Session(
            [new SessionOutput(xylo, [0], Ms(10)), new SessionOutput(drums, [9], Ms(35))],
            mutedChannels: []))
        {
            session.Orchestra.Route(new NoteOnEvent((SevenBitNumber)60, (SevenBitNumber)100) { Channel = (FourBitNumber)0 });
            session.Orchestra.Route(new NoteOnEvent((SevenBitNumber)36, (SevenBitNumber)100) { Channel = (FourBitNumber)9 });

            Assert.Equal(Ms(35), session.Profiles[session.Governors[1]].Latency);
            Assert.Same(drums, session.Governors[1].Inner);
            Assert.Equal("xylo, drums", session.OutputName);
        }

        Assert.Equal(60, Assert.IsType<NoteOnEvent>(xylo.Events.First().Event).NoteNumber);
        Assert.Equal(36, Assert.IsType<NoteOnEvent>(drums.Events.First().Event).NoteNumber);
        Assert.All([xylo, drums], p => Assert.True(p.Disposed));
    }

    /// <summary>Real clock, real thread: generous tolerances so a busy CI machine still passes.</summary>
    [Fact]
    public void SchedulerLoop_SendsOnTimeInRealTime_AndFollowsASpeedChange()
    {
        var clock = new SystemClock();
        var fast = new RecordingPort("fast", clock);
        var slow = new RecordingPort("slow", clock);
        using var session = new Session([new SessionOutput(fast, [0], Ms(0)), new SessionOutput(slow, [1], Ms(30))], []);
        var scheduler = new LatencyScheduler(session.Orchestra, clock, session.Profiles);
        TimedMidiEvent Hit(double ms, int channel) => new(Ms(ms), new NoteOnEvent((SevenBitNumber)60, (SevenBitNumber)100) { Channel = (FourBitNumber)channel });

        // Same start-up as `play`: a panic warms every send path, and a 250 ms pre-roll leaves time
        // for the loop thread to start. With 50 ms a cold CI runner started the loop after beat 1
        // was due for both instruments, so they were sent together instead of 30 ms apart.
        session.Orchestra.Panic();
        var started = clock.Now;
        scheduler.Start([Hit(0, 0), Hit(0, 1), Hit(200, 0), Hit(200, 1), Hit(400, 0), Hit(400, 1)], preroll: Ms(250));

        using var finished = new ManualResetEventSlim();
        using var loop = new SchedulerLoop(scheduler, clock);
        loop.Finished += finished.Set;
        loop.Start();
        var wait = started + Ms(400) - clock.Now;
        Thread.Sleep(wait > TimeSpan.Zero ? wait : TimeSpan.Zero); // Song clock ≈ 150 ms: beat 1 sent, beat 2 not yet.
        scheduler.SetSpeed(2);
        loop.Wake();

        Assert.True(finished.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken), "loop never finished");
        var f = fast.Events.Select(e => e.At).ToArray();
        var s = slow.Events.Select(e => e.At).ToArray();
        Assert.Equal(3, f.Length);
        Assert.Equal(3, s.Length);

        // The slow instrument is always sent 30 ms early, so both are heard together.
        for (var i = 0; i < 3; i++)
        {
            Assert.InRange((f[i] - s[i]).TotalMilliseconds, 20, 40);
        }

        // Beat 1 → 2 is 200 song-ms: 150 at speed 1 plus 50 at speed 2 = 175 ms. Beat 2 → 3 at speed 2: 100 ms.
        Assert.InRange((f[1] - f[0]).TotalMilliseconds, 150, 200);
        Assert.InRange((f[2] - f[1]).TotalMilliseconds, 85, 115);
    }

    private sealed class RecordingPort(string name, IClock? clock = null) : IMidiOutputPort
    {
        public string Name { get; } = name;

        public ConcurrentQueue<(TimeSpan At, MidiEvent Event)> Events { get; } = new();

        public bool Disposed { get; private set; }

        public void Send(MidiEvent midiEvent)
        {
            if (midiEvent is NoteOnEvent)
            {
                Events.Enqueue((clock?.Now ?? TimeSpan.Zero, midiEvent));
            }
        }

        public void Dispose() => Disposed = true;
    }
}

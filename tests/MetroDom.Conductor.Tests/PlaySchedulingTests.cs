using System.Collections.Concurrent;
using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using MetroDom.Conductor.Cli;
using MetroDom.Conductor.Commands;
using MetroDom.Conductor.Midi;
using MetroDom.Core;
using MetroDom.Core.Timing;

namespace MetroDom.Conductor.Tests;

public class PlaySchedulingTests
{
    private static TimeSpan Ms(double ms) => TimeSpan.FromMilliseconds(ms);

    [Theory]
    [InlineData("0", "0", null, null)]
    [InlineData("MetroDom drums=10@35", "MetroDom drums", new[] { 9 }, 35.0)]
    [InlineData("xylo@12.5ms", "xylo", null, 12.5)]
    [InlineData("xylo@0", "xylo", null, 0.0)]
    [InlineData("fluid=1-9,11-16", "fluid", new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 10, 11, 12, 13, 14, 15 }, null)]
    [InlineData("Midi Through:Midi Through Port-0 (14:0)", "Midi Through:Midi Through Port-0 (14:0)", null, null)]
    public void OutputSpec_Parses(string raw, string port, int[]? channels, double? latencyMs)
    {
        Assert.True(OutputSpec.TryParse(raw, out var spec, out var error), error);

        Assert.Equal(port, spec.Port);
        Assert.Equal(channels ?? InstrumentSpec.AllChannels, spec.Channels);
        Assert.Equal(latencyMs is { } ms ? Ms(ms) : null, spec.Latency);
    }

    [Theory]
    [InlineData("xylo", true)]
    [InlineData("xylo@10", true)]
    [InlineData("xylo/60,64", true)]
    [InlineData("xylo@10/60,64", false)] // fully described: don't ask
    public void OutputSpec_WantsProfile_OnlyWhenSomethingIsMissing(string raw, bool wants)
    {
        Assert.True(OutputSpec.TryParse(raw, out var spec, out _));
        Assert.Equal(wants, spec.WantsProfile);
    }

    [Fact]
    public void Merge_FillsWhatTheCommandLineLeftOut_FromTheSentProfile()
    {
        var sent = new InstrumentProfile("Xylophone", Ms(9))
        {
            Notes = new HashSet<int> { 60, 64, 67 },
            NoteLatency = new Dictionary<int, TimeSpan> { [64] = Ms(12) },
            MinInterval = Ms(60),
        };
        static OutputSpec Spec(string raw) => OutputSpec.TryParse(raw, out var s, out _) ? s : throw new InvalidOperationException(raw);

        var plain = SessionOutput.Merge("port", Spec("xylo"), sent);
        Assert.Equal("port", plain.Name);
        Assert.Equal(Ms(12), plain.LatencyFor(64));
        Assert.Equal([60, 64, 67], plain.Notes!.Order());
        Assert.Equal(Ms(60), plain.MinInterval);

        var latency = SessionOutput.Merge("port", Spec("xylo@20"), sent);
        Assert.Equal(Ms(20), latency.LatencyFor(64)); // @ replaces per-note latencies too
        Assert.Equal([60, 64, 67], latency.Notes!.Order());
        Assert.Equal(Ms(60), latency.MinInterval);

        var notes = SessionOutput.Merge("port", Spec("xylo/60"), sent);
        Assert.Equal([60], notes.Notes!.Order());
        Assert.Equal(Ms(12), notes.MaxLatency);

        var none = SessionOutput.Merge("port", Spec("xylo"), null);
        Assert.Equal(TimeSpan.Zero, none.MaxLatency);
        Assert.Null(none.Notes);
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

    /// <summary>
    /// Real clock, real thread. The scheduling logic is checked exactly through the due times the
    /// scheduler reports. How late the OS actually ran the thread depends on the machine: hosted
    /// CI runners are shared VMs (the macOS one woke the loop 30+ ms late, so both instruments went
    /// out together), so the 5 ms precision bound applies only off CI, like the CoreMIDI tests.
    /// </summary>
    [Fact]
    public void SchedulerLoop_SendsOnTimeInRealTime_AndFollowsASpeedChange()
    {
        var clock = new SystemClock();
        using var session = new Session([new SessionOutput(new RecordingPort("fast"), [0], Ms(0)), new SessionOutput(new RecordingPort("slow"), [1], Ms(30))], []);
        IMidiOutput fast = session.Governors[0], slow = session.Governors[1];
        var scheduler = new LatencyScheduler(session.Orchestra, clock, session.Profiles);
        var sends = new ConcurrentQueue<(ScheduledSend Send, TimeSpan At)>();
        using var firstBeat = new ManualResetEventSlim();
        scheduler.Sent += send =>
        {
            sends.Enqueue((send, clock.Now));
            if (send.Output == fast && send.SongTime == TimeSpan.Zero)
            {
                firstBeat.Set();
            }
        };
        TimedMidiEvent Hit(double ms, int channel) => new(Ms(ms), new NoteOnEvent((SevenBitNumber)60, (SevenBitNumber)100) { Channel = (FourBitNumber)channel });

        // Same start-up as `play`: a panic warms every send path, then a 250 ms pre-roll.
        session.Orchestra.Panic();
        scheduler.Start([Hit(0, 0), Hit(0, 1), Hit(200, 0), Hit(200, 1), Hit(400, 0), Hit(400, 1)], preroll: Ms(250));
        using var finished = new ManualResetEventSlim();
        using var loop = new SchedulerLoop(scheduler, clock);
        loop.Finished += finished.Set;
        loop.Start();

        // Double the tempo just after beat 1, while the loop is waiting for beat 2.
        Assert.True(firstBeat.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken), "beat 1 never sent");
        scheduler.SetSpeed(2);
        loop.Wake();
        Assert.True(finished.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken), "loop never finished");

        var all = sends.ToArray();
        var f = all.Where(x => x.Send.Output == fast).ToArray();
        var s = all.Where(x => x.Send.Output == slow).ToArray();
        Assert.Equal(3, f.Length);
        Assert.Equal(3, s.Length);

        // Logic, exact: the slow instrument is due 30 ms before the fast one on every beat.
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(30, (f[i].Send.DueAt - s[i].Send.DueAt).TotalMilliseconds, 0.01);
        }

        // Beat 1 → 2 is 200 song-ms, mostly at double speed: under 200 ms. Beat 2 → 3 is all at double speed: 100 ms.
        Assert.InRange((f[1].Send.DueAt - f[0].Send.DueAt).TotalMilliseconds, 100, 199);
        Assert.Equal(100, (f[2].Send.DueAt - f[1].Send.DueAt).TotalMilliseconds, 0.01);

        // Real time: never early; late only by how long the OS took to run the thread.
        Assert.All(all, x => Assert.True(x.At >= x.Send.DueAt, $"sent {(x.Send.DueAt - x.At).TotalMilliseconds:0.###} ms early"));
        var lateness = all.Select(x => (x.At - x.Send.DueAt).TotalMilliseconds).ToArray();
        TestContext.Current.TestOutputHelper?.WriteLine($"Lateness (ms): {string.Join(", ", lateness.Select(l => l.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)))}");
        Assert.InRange(lateness.Max(), 0, 1000);
        if (Environment.GetEnvironmentVariable("CI") is null)
        {
            Assert.InRange(lateness.Max(), 0, 5);
        }
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

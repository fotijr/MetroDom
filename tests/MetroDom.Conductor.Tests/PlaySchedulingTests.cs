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
    internal sealed class RecordingPort(string name, IClock? clock = null) : IMidiOutputPort
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

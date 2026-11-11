using MetroDom.Core.Simulation;
using MetroDom.Core.Timing;

namespace MetroDom.Core.Tests;

public class BandSimulationTests
{
    private static TimeSpan Ms(double ms) => TimeSpan.FromMilliseconds(ms);

    /// <summary>The roadmap S1 band: a fast xylophone and slow drums.</summary>
    private static SimulatedPart[] Band(IReadOnlySet<int>? xyloNotes = null) =>
    [
        new(new InstrumentProfile("xylo", Ms(10)) { Notes = xyloNotes }, [DemoSong.MelodyChannel]),
        new(new InstrumentProfile("drums", Ms(35)), [DemoSong.DrumChannel]),
    ];

    private static readonly IReadOnlyList<TimedMidiEvent> Song = SongTimeline.From(DemoSong.Create());

    [Fact]
    public void Compensated_EveryoneLandsTogether()
    {
        var result = BandSimulation.Run(Song, Band());

        Assert.Equal(Ms(35), result.Lookahead);
        Assert.Equal(16, result.SharedMoments);
        Assert.Equal(TimeSpan.Zero, result.MaxSpread);
        Assert.All(result.Notes, n => Assert.Equal(TimeSpan.Zero, n.Offset));
        Assert.Equal([16, 32], result.Instruments.Select(i => i.Played));
        Assert.All(result.Instruments, i => Assert.True(i.GotPanic));
    }

    [Fact]
    public void Uncompensated_SpreadIsTheLatencyDifference()
    {
        var result = BandSimulation.Run(Song, Band(), new SimulationOptions { Compensate = false });

        Assert.Equal(Ms(25), result.MaxSpread);
        Assert.Equal(Ms(10), result.Instruments[0].MaxOffset);
        Assert.Equal(Ms(35), result.Instruments[1].MaxOffset);
    }

    [Fact]
    public void WithJitter_SpreadStaysInsideTheJitterBudget()
    {
        var options = new SimulationOptions { Jitter = Ms(2), Seed = 42 };

        var result = BandSimulation.Run(Song, Band(), options);

        Assert.InRange(result.MaxSpread, Ms(0.001), Ms(4));
        Assert.True(result.MaxSpread <= Ms(5), "ADR 0004 budget: ≤ 5 ms between instruments");
        Assert.Equal(result, BandSimulation.Run(Song, Band(), options) with { }, new ResultComparer());
    }

    [Fact]
    public void NotesTheInstrumentCannotPlay_AreReportedAsDropped()
    {
        var result = BandSimulation.Run(Song, Band(xyloNotes: new HashSet<int> { 60, 64, 67 }));

        Assert.Equal(11, result.Instruments[0].Played);
        Assert.Equal(5, result.Instruments[0].Dropped);
    }

    private sealed class ResultComparer : IEqualityComparer<SimulationResult>
    {
        public bool Equals(SimulationResult? x, SimulationResult? y) =>
            x is not null && y is not null && x.MaxSpread == y.MaxSpread && x.Notes.SequenceEqual(y.Notes);

        public int GetHashCode(SimulationResult obj) => obj.MaxSpread.GetHashCode();
    }
}

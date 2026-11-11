using MetroDom.Core.Timing;

namespace MetroDom.Core.Tests;

public class TapTempoTests
{
    private static TimeSpan Ms(double ms) => TimeSpan.FromMilliseconds(ms);

    [Fact]
    public void TwoTaps_GiveATempo()
    {
        var tap = new TapTempo();

        Assert.Null(tap.Tap(Ms(0)));
        Assert.Equal(120, tap.Tap(Ms(500))!.Value, precision: 6);
    }

    [Fact]
    public void AveragesTheLastFewIntervals()
    {
        var tap = new TapTempo(window: 4);
        double[] times = [0, 1000, 1500, 2000, 2500, 3000]; // One slow interval, then four at 500 ms.
        double? bpm = null;
        foreach (var t in times)
        {
            bpm = tap.Tap(Ms(t));
        }

        Assert.Equal(120, bpm!.Value, precision: 6);
    }

    [Fact]
    public void APauseStartsOver()
    {
        var tap = new TapTempo();
        tap.Tap(Ms(0));
        tap.Tap(Ms(500));

        Assert.Null(tap.Tap(Ms(5000)));
        Assert.Equal(60, tap.Tap(Ms(6000))!.Value, precision: 6);
    }

    [Fact]
    public void DoubleTriggersAreIgnored()
    {
        var tap = new TapTempo();
        tap.Tap(Ms(0));
        tap.Tap(Ms(10)); // Pedal bounce: faster than 300 BPM.

        Assert.Equal(120, tap.Tap(Ms(500))!.Value, precision: 6);
    }
}

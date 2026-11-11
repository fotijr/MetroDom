using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using MetroDom.Core.Simulation;
using MetroDom.Core.Timing;
using static MetroDom.Core.Tests.VirtualInstrumentTests;

namespace MetroDom.Core.Tests;

public class LatencySchedulerTests
{
    private static TimeSpan Ms(double ms) => TimeSpan.FromMilliseconds(ms);

    private readonly VirtualClock _clock = new();
    private readonly Orchestra _orchestra = new();
    private readonly VirtualInstrument _xylo;
    private readonly VirtualInstrument _drums;

    public LatencySchedulerTests()
    {
        _xylo = new VirtualInstrument(new InstrumentProfile("xylo", Ms(10)), _clock);
        _drums = new VirtualInstrument(new InstrumentProfile("drums", Ms(35)), _clock);
        _orchestra.Assign(0, _xylo);
        _orchestra.Assign(9, _drums);
    }

    private LatencyScheduler Scheduler(bool compensate = true) =>
        new(_orchestra, _clock, new Dictionary<IMidiOutput, InstrumentProfile> { [_xylo] = _xylo.Profile, [_drums] = _drums.Profile }, compensate);

    /// <summary>Melody and drums together on two beats, 500 ms apart.</summary>
    private static TimedMidiEvent[] TwoBeats() =>
    [
        new(Ms(0), On(60, channel: 0)),
        new(Ms(0), On(36, channel: 9)),
        new(Ms(500), On(64, channel: 0)),
        new(Ms(500), On(38, channel: 9)),
    ];

    private static void RunToEnd(LatencyScheduler scheduler, VirtualClock clock)
    {
        while (scheduler.NextDue is { } due)
        {
            clock.AdvanceTo(due);
            scheduler.Pump();
        }
    }

    [Fact]
    public void Compensated_SlowInstrumentGetsItEarly_AndBothSoundTogether()
    {
        var scheduler = Scheduler();
        scheduler.Start(TwoBeats());
        RunToEnd(scheduler, _clock);

        Assert.Equal(Ms(35), scheduler.Lookahead);
        Assert.Equal([Ms(25), Ms(525)], _xylo.Strikes.Select(s => s.ReceivedAt));
        Assert.Equal([Ms(0), Ms(500)], _drums.Strikes.Select(s => s.ReceivedAt));
        Assert.Equal(_xylo.Strikes.Select(s => s.SoundsAt), _drums.Strikes.Select(s => s.SoundsAt));
        Assert.Equal([Ms(35), Ms(535)], _drums.Strikes.Select(s => s.SoundsAt));
    }

    [Fact]
    public void Uncompensated_InstrumentsDriftApartByTheLatencyDifference()
    {
        var scheduler = Scheduler(compensate: false);
        scheduler.Start(TwoBeats());
        RunToEnd(scheduler, _clock);

        Assert.Equal(TimeSpan.Zero, scheduler.Lookahead);
        Assert.Equal([Ms(10), Ms(510)], _xylo.Strikes.Select(s => s.SoundsAt));
        Assert.Equal([Ms(35), Ms(535)], _drums.Strikes.Select(s => s.SoundsAt));
    }

    [Fact]
    public void NothingIsSentBeforeItsTime()
    {
        var scheduler = Scheduler();
        scheduler.Start(TwoBeats());

        Assert.Equal(1, scheduler.Pump()); // t=0: only the slow drums' first hit.
        _clock.AdvanceTo(Ms(24));
        Assert.Equal(0, scheduler.Pump());
        _clock.AdvanceTo(Ms(25));
        Assert.Equal(1, scheduler.Pump());
        Assert.Equal(Ms(500), scheduler.NextDue);
    }

    [Fact]
    public void StartUsesTheCurrentClockAsTheSongOrigin()
    {
        _clock.AdvanceTo(TimeSpan.FromSeconds(3));
        var scheduler = Scheduler();
        scheduler.Start(TwoBeats());
        RunToEnd(scheduler, _clock);

        Assert.Equal(TimeSpan.FromSeconds(3) + Ms(35), _xylo.Strikes[0].SoundsAt);
    }

    [Fact]
    public void NoteOff_KeepsTheNotesLength()
    {
        var sent = new FakeOutput("synth");
        _orchestra.Assign(1, sent);
        var profile = new InstrumentProfile("synth", Ms(20));
        var scheduler = new LatencyScheduler(_orchestra, _clock, new Dictionary<IMidiOutput, InstrumentProfile> { [sent] = profile, [_drums] = _drums.Profile });
        var times = new List<TimeSpan>();
        scheduler.Start(
        [
            new(Ms(0), On(60, channel: 1)),
            new(Ms(250), new NoteOffEvent((SevenBitNumber)60, SevenBitNumber.MinValue) { Channel = (FourBitNumber)1 }),
        ]);
        while (scheduler.NextDue is { } due)
        {
            _clock.AdvanceTo(due);
            if (scheduler.Pump() > 0)
            {
                times.Add(_clock.Now);
            }
        }

        Assert.Equal([Ms(15), Ms(265)], times); // 35 ms lookahead − 20 ms latency.
    }

    [Fact]
    public void MuteIsCheckedWhenTheEventIsSent()
    {
        var scheduler = Scheduler();
        scheduler.Start(TwoBeats());
        _clock.AdvanceTo(Ms(100));
        scheduler.Pump();

        _orchestra.SetMuted(9, true);
        RunToEnd(scheduler, _clock);

        Assert.Equal([36], _drums.Strikes.Select(s => s.Note));
        Assert.Equal([60, 64], _xylo.Strikes.Select(s => s.Note));
    }

    [Fact]
    public void Stop_DropsTheRestAndPanicsEveryInstrument()
    {
        var scheduler = Scheduler();
        scheduler.Start(TwoBeats());
        scheduler.Pump();

        scheduler.Stop();

        Assert.True(scheduler.IsFinished);
        Assert.Null(scheduler.NextDue);
        Assert.Single(_drums.Strikes);
        Assert.Empty(_xylo.Strikes);
        Assert.True(_xylo.PanicMessages > 0);
        Assert.True(_drums.PanicMessages > 0);
    }

    [Fact]
    public void AFailingInstrumentDoesNotDelayTheOthers()
    {
        var broken = new FakeOutput("broken") { Broken = true };
        _orchestra.Assign(0, broken);
        var failures = 0;
        _orchestra.SendFailed += (_, _) => failures++;
        var scheduler = Scheduler();
        scheduler.Start(TwoBeats());
        RunToEnd(scheduler, _clock);

        Assert.Equal(2, failures);
        Assert.Equal(2, _xylo.Strikes.Count);
        Assert.Equal(2, _drums.Strikes.Count);
    }
}

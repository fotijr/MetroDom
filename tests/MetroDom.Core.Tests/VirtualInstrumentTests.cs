using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using MetroDom.Core.Simulation;
using MetroDom.Core.Timing;

namespace MetroDom.Core.Tests;

public class VirtualInstrumentTests
{
    private static readonly TimeSpan Ms10 = TimeSpan.FromMilliseconds(10);

    internal static NoteOnEvent On(int note, int channel = 0, int velocity = 100) =>
        new((SevenBitNumber)note, (SevenBitNumber)velocity) { Channel = (FourBitNumber)channel };

    [Fact]
    public void NoteOn_SoundsAfterLatency_NoteOffIsIgnored()
    {
        var clock = new VirtualClock();
        var xylo = new VirtualInstrument(new InstrumentProfile("xylo", Ms10), clock);
        clock.AdvanceTo(TimeSpan.FromMilliseconds(100));

        xylo.Send(On(60));
        xylo.Send(new NoteOffEvent((SevenBitNumber)60, SevenBitNumber.MinValue));
        xylo.Send(On(62, velocity: 0)); // Velocity 0 is a Note Off.

        var strike = Assert.Single(xylo.Strikes);
        Assert.Equal(new Strike(60, 100, 0, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(110)), strike);
    }

    [Fact]
    public void PerNoteLatency_OverridesTheDefault()
    {
        var clock = new VirtualClock();
        var profile = new InstrumentProfile("servo", Ms10) { NoteLatency = new Dictionary<int, TimeSpan> { [72] = TimeSpan.FromMilliseconds(80) } };
        var servo = new VirtualInstrument(profile, clock);

        servo.Send(On(60));
        servo.Send(On(72));

        Assert.Equal([Ms10, TimeSpan.FromMilliseconds(80)], servo.Strikes.Select(s => s.SoundsAt));
        Assert.Equal(TimeSpan.FromMilliseconds(80), profile.MaxLatency);
    }

    [Fact]
    public void NotesWithoutAnActuator_AreDropped()
    {
        var xylo = new VirtualInstrument(new InstrumentProfile("xylo", Ms10) { Notes = new HashSet<int> { 60, 64, 67 } }, new VirtualClock());
        var rejected = new List<int>();
        xylo.Rejected += (_, note, _) => rejected.Add(note);

        xylo.Send(On(60));
        xylo.Send(On(61));

        Assert.Single(xylo.Strikes);
        Assert.Equal(1, xylo.Dropped);
        Assert.Equal([61], rejected);
    }

    [Fact]
    public void RepeatsFasterThanMinInterval_AreRefused()
    {
        var clock = new VirtualClock();
        var xylo = new VirtualInstrument(new InstrumentProfile("xylo", Ms10) { MinInterval = TimeSpan.FromMilliseconds(60) }, clock);

        xylo.Send(On(60));
        clock.Advance(TimeSpan.FromMilliseconds(30));
        xylo.Send(On(60)); // Too soon.
        xylo.Send(On(64)); // Other notes are independent.
        clock.Advance(TimeSpan.FromMilliseconds(30));
        xylo.Send(On(60)); // 60 ms after the first: fine.

        Assert.Equal([60, 64, 60], xylo.Strikes.Select(s => s.Note));
        Assert.Equal(1, xylo.Refused);
    }

    [Fact]
    public void EachNote_IsRefusedByItsOwnInterval()
    {
        var clock = new VirtualClock();
        var drums = new VirtualInstrument(new InstrumentProfile("drums", Ms10)
        {
            NoteInterval = new Dictionary<int, TimeSpan> { [38] = TimeSpan.FromMilliseconds(40), [36] = TimeSpan.FromMilliseconds(150) },
        }, clock);

        drums.Send(On(38));
        drums.Send(On(36));
        clock.Advance(TimeSpan.FromMilliseconds(50));
        drums.Send(On(38));
        drums.Send(On(36)); // Too soon for the kick.

        Assert.Equal([38, 36, 38], drums.Strikes.Select(s => s.Note));
        Assert.Equal(1, drums.Refused);
    }

    [Fact]
    public void Panic_IsCounted()
    {
        var xylo = new VirtualInstrument(new InstrumentProfile("xylo", Ms10), new VirtualClock());

        Orchestra.SendPanic(xylo);

        Assert.Equal(32, xylo.PanicMessages);
    }

    [Fact]
    public void Jitter_StaysInBoundsAndRepeatsWithTheSameSeed()
    {
        static IEnumerable<TimeSpan> Run(int seed)
        {
            var clock = new VirtualClock();
            var xylo = new VirtualInstrument(new InstrumentProfile("xylo", Ms10), clock, TimeSpan.FromMilliseconds(2), seed);
            for (var i = 0; i < 200; i++)
            {
                xylo.Send(On(60));
                clock.Advance(TimeSpan.FromSeconds(1));
            }

            return xylo.Strikes.Select(s => s.SoundsAt - s.ReceivedAt);
        }

        var latencies = Run(seed: 7).ToArray();
        Assert.All(latencies, l => Assert.InRange(l, TimeSpan.FromMilliseconds(8), TimeSpan.FromMilliseconds(12)));
        Assert.True(latencies.Distinct().Count() > 1);
        Assert.Equal(latencies, Run(seed: 7));
    }
}

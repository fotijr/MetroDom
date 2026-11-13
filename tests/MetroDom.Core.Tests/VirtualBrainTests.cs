using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using MetroDom.Core.Bodies;
using MetroDom.Core.Simulation;
using MetroDom.Core.Timing;

namespace MetroDom.Core.Tests;

/// <summary>
/// Virtual instruments made from a body (<c>virtual --body</c>, <c>simulate --body</c>) decide as a
/// brain would, and the governor <c>play</c> puts in front never lets through what the brain refuses.
/// </summary>
public class VirtualBrainTests
{
    private static BodyDefinition M1()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AGENTS.md")))
        {
            dir = dir.Parent;
        }

        var json = File.ReadAllText(Path.Combine(dir!.FullName, "Instruments", "metrodom_brain", "bodies", "m1-xylophone.json"));
        Assert.True(BodyImage.TryParseJson(json, out var body, out var error), error);
        return body;
    }

    /// <summary>One bar, hot: the pulse is as long as the interval, so duty is what stops it.</summary>
    private static BodyDefinition Hot() => M1() with
    {
        Polyphony = 1,
        Notes = [new BodyActuator(60, 0, 30, 10, 30, 0), new BodyActuator(62, 1, 20, 0, 25, 0)],
    };

    private static NoteOnEvent On(int note, int velocity = 100, int channel = 0) =>
        new((SevenBitNumber)note, (SevenBitNumber)velocity) { Channel = (FourBitNumber)channel };

    [Fact]
    public void ForBody_StrikesAsTheBrainWould_WithPulseAndReason()
    {
        var clock = new VirtualClock();
        var instrument = VirtualInstrument.ForBody(M1(), clock);
        var reasons = new List<string>();
        instrument.Rejected += (_, _, why) => reasons.Add(why);

        instrument.Send(On(72, 127));
        clock.AdvanceTo(TimeSpan.FromMilliseconds(10));
        instrument.Send(On(72));
        instrument.Send(On(60));
        clock.AdvanceTo(TimeSpan.FromMilliseconds(100));
        instrument.Send(On(74, 1));

        Assert.Equal([30, 12], instrument.Strikes.Select(s => s.PulseMs));
        Assert.Equal(1, instrument.Refused);
        Assert.Equal(1, instrument.Dropped);
        Assert.Equal(["still striking", "no actuator for this note"], reasons);
        Assert.Equal(1, instrument.RefusedBy[BrainRefusal.Busy]);
        Assert.Equal("MetroDom xylophone", instrument.Name);
    }

    [Fact]
    public void ForBody_PanicEndsTheStrike_ButNotItsInterval()
    {
        var clock = new VirtualClock();
        var instrument = VirtualInstrument.ForBody(M1(), clock);

        instrument.Send(On(72));
        instrument.Send(new ControlChangeEvent((SevenBitNumber)123, (SevenBitNumber)0));
        clock.AdvanceTo(TimeSpan.FromMilliseconds(40));
        instrument.Send(On(72));

        Assert.Equal(1, instrument.PanicMessages);
        Assert.Equal(1, instrument.RefusedBy[BrainRefusal.Interval]);
    }

    [Fact]
    public void Simulation_StraightToTheBrain_ShowsWhatItRefuses_TheGovernorHoldsItBack()
    {
        // Sixteenth notes at 300 BPM on one bar: 50 ms apart, under M1's 60 ms interval.
        var song = Enumerable.Range(0, 40)
            .Select(i => new TimedMidiEvent(TimeSpan.FromMilliseconds(50 * i), On(72)))
            .ToArray();
        SimulatedPart[] parts = [new(M1().ToProfile(), [0]) { Body = M1() }];

        var straight = BandSimulation.Run(song, parts, new SimulationOptions { Governed = false }).Instruments[0];
        var governed = BandSimulation.Run(song, parts).Instruments[0];

        Assert.Equal(20, straight.Played);
        Assert.Equal(20, straight.RefusedBy[BrainRefusal.Interval]);
        Assert.Equal(20, governed.Played);
        Assert.Equal(20, governed.Held);
        Assert.Equal(0, governed.Refused);
    }

    public static TheoryData<int> Seeds() => [.. Enumerable.Range(1, 40)];

    /// <summary>
    /// Random streams (dense, mixed velocities, chords) through the governor into the brain model:
    /// the brain must never refuse a note the governor passed, or <c>play</c> would send notes that
    /// silently don't sound. The governor may be stricter (it charges full-velocity pulses).
    /// </summary>
    [Theory]
    [MemberData(nameof(Seeds))]
    public void Governor_NeverPassesWhatTheBrainRefuses(int seed)
    {
        var random = new Random(seed);
        foreach (var body in new[] { M1(), Hot() })
        {
            var notes = body.Notes.Select(n => n.Note).ToArray();
            var time = 0.0;
            var song = new List<TimedMidiEvent>();
            for (var i = 0; i < 400; i++)
            {
                time += random.Next(4) == 0 ? 0 : random.NextDouble() * 70; // a quarter of them are chords
                song.Add(new TimedMidiEvent(TimeSpan.FromMilliseconds(time), On(notes[random.Next(notes.Length)], random.Next(1, 128))));
            }

            SimulatedPart[] parts = [new(body.ToProfile(), [0]) { Body = body }];
            var result = BandSimulation.Run(song, parts).Instruments[0];

            Assert.True(result.Refused == 0, $"seed {seed}, {body.Id}: the brain refused {result.Refused} the governor passed ({string.Join(", ", result.RefusedBy.Select(kv => $"{kv.Key} {kv.Value}"))})");
            Assert.True(result.Played > 0);
        }
    }
}

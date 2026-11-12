using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using MetroDom.Core.Calibration;
using MetroDom.Core.Simulation;
using MetroDom.Core.Timing;

namespace MetroDom.Core.Tests;

/// <summary>Latency calibration and the boot check against simulated onsets (ADR 0004, plan 0004 S5).</summary>
public class CalibrationTests
{
    private static TimeSpan Ms(double ms) => TimeSpan.FromMilliseconds(ms);

    private static InstrumentProfile Xylo(double latency = 12, params int[] notes) =>
        new("xylo", Ms(latency)) { Notes = (notes.Length > 0 ? notes : [60, 64, 67]).ToHashSet() };

    /// <summary>
    /// Runs <paramref name="probe"/> against <paramref name="instrument"/> on a virtual clock: each
    /// strike is heard at its <see cref="Strike.SoundsAt"/>, plus <paramref name="extra"/> onsets.
    /// </summary>
    private static CalibrationResult Run(LatencyProbe probe, VirtualClock clock, VirtualInstrument instrument, params TimeSpan[] extra)
    {
        var onsets = new PriorityQueue<TimeSpan, TimeSpan>();
        foreach (var at in extra)
        {
            onsets.Enqueue(at, at);
        }

        instrument.Struck += (_, s) => onsets.Enqueue(s.SoundsAt, s.SoundsAt);
        while (!probe.IsDone(clock.Now))
        {
            if (probe.Due(clock.Now) is { } p)
            {
                instrument.Send(new NoteOnEvent((SevenBitNumber)p.Note, (SevenBitNumber)p.Velocity));
            }

            while (onsets.TryPeek(out var at, out _) && at <= clock.Now)
            {
                probe.Onset(onsets.Dequeue());
            }

            var next = probe.NextAt ?? clock.Now;
            if (onsets.TryPeek(out var onset, out _) && onset < next)
            {
                next = onset;
            }

            clock.AdvanceTo(next > clock.Now ? next : clock.Now + Ms(1));
        }

        return probe.Result();
    }

    [Fact]
    public void Measures_each_notes_latency()
    {
        var clock = new VirtualClock();
        var actual = Xylo() with { NoteLatency = new Dictionary<int, TimeSpan> { [67] = Ms(20) } };
        var probe = new LatencyProbe(Xylo(), [60, 64, 67], new CalibrationOptions(), clock.Now);

        var result = Run(probe, clock, new VirtualInstrument(actual, clock));

        Assert.Equal([Ms(12), Ms(12), Ms(20)], result.Notes.Select(n => n.Median!.Value));
        Assert.All(result.Notes, n => Assert.Equal((5, 0), (n.Latencies.Count, n.Missed)));
        Assert.Equal(Ms(12), result.Latency);
        Assert.Equal((0, 0), (result.Strays, result.Echoes));
    }

    [Fact]
    public void The_median_keeps_jitter_and_one_bad_onset_from_moving_it()
    {
        var clock = new VirtualClock();
        var probe = new LatencyProbe(Xylo(30), [60], new CalibrationOptions { Repeats = 7 }, clock.Now);

        var result = Run(probe, clock, new VirtualInstrument(Xylo(30), clock, jitter: Ms(2), seed: 3));

        var note = Assert.Single(result.Notes);
        Assert.InRange(note.Median!.Value.TotalMilliseconds, 28, 32);
        Assert.InRange(note.Spread.TotalMilliseconds, 0.01, 4);
    }

    [Fact]
    public void Notes_never_heard_are_missed_and_keep_their_old_latency()
    {
        var clock = new VirtualClock();
        var probe = new LatencyProbe(Xylo(), [60, 61], new CalibrationOptions { Repeats = 2 }, clock.Now);

        var result = Run(probe, clock, new VirtualInstrument(Xylo(15), clock)); // 61: no actuator

        Assert.False(result.HeardAll);
        Assert.Equal((0, 2), (result.Notes[1].Latencies.Count, result.Notes[1].Missed));
        var applied = result.ApplyTo(Xylo(12) with { NoteLatency = new Dictionary<int, TimeSpan> { [61] = Ms(9) } });
        Assert.Equal(Ms(15), applied.LatencyFor(60));
        Assert.Equal(Ms(9), applied.LatencyFor(61));
    }

    [Fact]
    public void Onsets_outside_any_window_are_strays_and_a_second_one_inside_is_an_echo()
    {
        var clock = new VirtualClock();
        var options = new CalibrationOptions { Repeats = 1 };
        var probe = new LatencyProbe(Xylo(), [60], options, clock.Now);

        // Noise during the lead-in, and a bounce 5 ms after the real onset (lead-in 300 + 12 + 5).
        var result = Run(probe, clock, new VirtualInstrument(Xylo(12), clock), Ms(100), Ms(317));

        Assert.Equal(Ms(12), result.Notes[0].Median);
        Assert.Equal((1, 1), (result.Strays, result.Echoes));
    }

    [Fact]
    public void The_onset_inputs_own_delay_is_taken_off()
    {
        var clock = new VirtualClock();
        var probe = new LatencyProbe(Xylo(), [60], new CalibrationOptions { Repeats = 1, OnsetLatency = Ms(4) }, clock.Now);

        Assert.Equal(Ms(8), Run(probe, clock, new VirtualInstrument(Xylo(12), clock)).Latency);
    }

    [Fact]
    public void Strikes_are_spaced_so_the_firmware_never_refuses_one()
    {
        // One note, 1 s minimum interval: the spacing grows past window + settle.
        var slow = Xylo(10, 60) with { MinInterval = Ms(1000) };
        var hot = Xylo(10, 60) with { NotePulse = new Dictionary<int, TimeSpan> { [60] = Ms(40) }, Safety = new ActuatorSafety(Ms(45), 5, Ms(1000)) };
        var options = new CalibrationOptions();

        Assert.True(new LatencyProbe(slow, [60], options, TimeSpan.Zero).Spacing(60) >= Ms(1000));
        Assert.True(new LatencyProbe(hot, [60], options, TimeSpan.Zero).Spacing(60) >= Ms(800)); // 40 ms at 5 %
        Assert.Equal(Ms(650), new LatencyProbe(Xylo(), [60, 64, 67], options, TimeSpan.Zero).Spacing(60));

        var clock = new VirtualClock();
        var instrument = new VirtualInstrument(slow, clock);
        Run(new LatencyProbe(slow, [60], options, clock.Now), clock, instrument);
        Assert.Equal(0, instrument.Refused);
    }

    [Fact]
    public void Quick_notes_are_lowest_middle_highest()
    {
        Assert.Equal([60, 67, 72], LatencyProbe.QuickNotes(Xylo(10, 60, 62, 64, 67, 69, 72), percussion: false));
        Assert.Equal([60, 64], LatencyProbe.QuickNotes(Xylo(10, 60, 64), percussion: false));
        Assert.Equal([38], LatencyProbe.AllNotes(new InstrumentProfile("gm kit", Ms(5)), percussion: true));
        Assert.Equal([60], LatencyProbe.AllNotes(new InstrumentProfile("synth", Ms(5)), percussion: false));
    }

    [Fact]
    public void Check_flags_drift_beyond_the_budget_and_silence()
    {
        var stored = Xylo(12);
        CalibrationResult Heard(params double?[] latencies) => new(
            [.. latencies.Select((l, i) => new NoteMeasurement(60 + i, l is { } v ? [Ms(v)] : [], l is null ? 1 : 0))], 0, 0);

        Assert.Equal(CheckStatus.Ok, LatencyCheck.Compare(Heard(12, 16.9), stored, Ms(5)).Status);
        var drifted = LatencyCheck.Compare(Heard(12, 20), stored, Ms(5));
        Assert.Equal(CheckStatus.Drifted, drifted.Status);
        Assert.Equal((61, Ms(8)), (drifted.Worst!.Note, drifted.Worst.Delta));
        Assert.Equal(CheckStatus.Partial, LatencyCheck.Compare(Heard(12, null), stored, Ms(5)).Status);
        Assert.Equal(CheckStatus.Silent, LatencyCheck.Compare(Heard(null, null), stored, Ms(5)).Status);
    }

    [Fact]
    public void A_drifted_virtual_instrument_is_caught_by_the_quick_check()
    {
        var clock = new VirtualClock();
        var declared = Xylo(12, 60, 62, 64, 65, 67);
        var worn = declared with { Latency = Ms(19) };
        var notes = LatencyProbe.QuickNotes(declared, percussion: false);

        var result = Run(new LatencyProbe(declared, notes, CalibrationOptions.Quick, clock.Now), clock, new VirtualInstrument(worn, clock));

        Assert.Equal(9, result.Notes.Sum(n => n.Latencies.Count));
        Assert.Equal(CheckStatus.Drifted, LatencyCheck.Compare(result, declared, CalibrationOptions.Quick.DriftThreshold).Status);
    }

    [Fact]
    public void Book_round_trips_and_applies_measured_latencies()
    {
        var book = new CalibrationBook();
        var at = new DateTimeOffset(2026, 11, 12, 21, 0, 0, TimeSpan.Zero);
        book.Set(new MeasuredLatency("MetroDom xylo", at, Ms(13.25), new Dictionary<int, TimeSpan> { [60] = Ms(13), [67] = Ms(14.5) }));

        Assert.True(CalibrationBook.TryParse(book.ToJson(), out var read, out var error), error);
        var c = read.Find("MetroDom xylo")!;
        Assert.Equal((at, Ms(13.25)), (c.MeasuredAt, c.Latency));
        var applied = c.ApplyTo(Xylo(12) with { Polyphony = 2 });
        Assert.Equal((Ms(14.5), Ms(13.25), 2), (applied.LatencyFor(67), applied.LatencyFor(64), applied.Polyphony));
        Assert.Null(read.Find("other"));
    }

    [Theory]
    [InlineData("""{"schema":"nope"}""")]
    [InlineData("""{"schema":"metrodom.calibrations/0","calibrations":[{"port":"x","latencyMs":-1,"measuredAt":"2026-01-01T00:00:00Z"}]}""")]
    [InlineData("""{"schema":"metrodom.calibrations/0","calibrations":[{"port":"x","latencyMs":3,"measuredAt":"2026-01-01T00:00:00Z","notes":[{"note":200,"latencyMs":1}]}]}""")]
    [InlineData("not json")]
    public void Book_rejects_bad_files(string json) =>
        Assert.False(CalibrationBook.TryParse(json, out _, out _));

    [Fact]
    public void Nothing_heard_saves_nothing() =>
        Assert.Null(MeasuredLatency.From("x", new CalibrationResult([new NoteMeasurement(60, [], 3)], 0, 0), Xylo(), DateTimeOffset.UnixEpoch));
}

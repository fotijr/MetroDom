using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using MetroDom.Core.Timing;

namespace MetroDom.Core.Tests;

public class MidiTempoTests
{
    private static NoteOnEvent NoteOn(int note, int velocity = 100, int channel = 0) =>
        new((SevenBitNumber)note, (SevenBitNumber)velocity) { Channel = (FourBitNumber)channel };

    private static ControlChangeEvent Cc(int number, int value, int channel = 0) =>
        new((SevenBitNumber)number, (SevenBitNumber)value) { Channel = (FourBitNumber)channel };

    [Theory]
    [InlineData(null, "any")]
    [InlineData("any", "any")]
    [InlineData("note", "note")]
    [InlineData("NOTE:36", "note:36")]
    [InlineData("cc:64@1", "cc:64@1")]
    [InlineData("pc:3", "pc:3")]
    [InlineData("pc@16", "pc@16")]
    public void Trigger_Parses(string? raw, string written)
    {
        Assert.True(TapTrigger.TryParse(raw, out var trigger, out var error), error);
        Assert.Equal(written, trigger.ToString());
    }

    [Theory]
    [InlineData("pedal")]
    [InlineData("any:3")]
    [InlineData("note:200")]
    [InlineData("cc:x")]
    [InlineData("cc:64@0")]
    [InlineData("cc:64@17")]
    public void Trigger_RejectsNonsense(string raw)
    {
        Assert.False(TapTrigger.TryParse(raw, out _, out var error));
        Assert.Contains(raw, error, StringComparison.Ordinal);
    }

    [Fact]
    public void Pedal_CountsThePressOnly_OncePerStomp()
    {
        var trigger = new TapTrigger(TapKind.ControlChange, 64);

        // A sustain pedal: 127 down, sometimes repeated, then 0 up.
        Assert.Equal(
            [true, false, false, true, false],
            new[] { Cc(64, 127), Cc(64, 127), Cc(64, 0), Cc(64, 100), Cc(64, 10) }.Select(trigger.IsTap));
        Assert.False(trigger.IsTap(Cc(1, 127))); // other controller
    }

    [Fact]
    public void Any_TakesNotesAndPresses_ButNeverChannelModeOrNoteOff()
    {
        var trigger = new TapTrigger();

        Assert.True(trigger.IsTap(NoteOn(36)));
        Assert.False(trigger.IsTap(NoteOn(36, velocity: 0))); // Note On velocity 0 is a Note Off
        Assert.False(trigger.IsTap(new NoteOffEvent((SevenBitNumber)36, (SevenBitNumber)0)));
        Assert.True(trigger.IsTap(Cc(64, 127)));
        Assert.False(trigger.IsTap(Cc(123, 127))); // all notes off is not a tap
        Assert.False(trigger.IsTap(new ProgramChangeEvent((SevenBitNumber)1))); // pc must be asked for
        Assert.False(trigger.IsTap(new TimingClockEvent()));
    }

    [Fact]
    public void Trigger_FiltersNumberAndChannel()
    {
        Assert.True(TapTrigger.TryParse("note:36@10", out var kick, out _));

        Assert.True(kick.IsTap(NoteOn(36, channel: 9)));
        Assert.False(kick.IsTap(NoteOn(36, channel: 0)));
        Assert.False(kick.IsTap(NoteOn(38, channel: 9)));

        Assert.True(TapTrigger.TryParse("pc", out var pc, out _));
        Assert.True(pc.IsTap(new ProgramChangeEvent((SevenBitNumber)5)));
        Assert.False(pc.IsTap(NoteOn(60)));
    }

    /// <summary>Ticks of a clock at <paramref name="bpm"/> starting at <paramref name="start"/>, with optional ± jitter.</summary>
    private static IEnumerable<TimeSpan> Ticks(double bpm, int count, TimeSpan start = default, double jitterMs = 0, int seed = 1)
    {
        var random = new Random(seed);
        var interval = 60.0 / bpm / ClockTempo.TicksPerBeat;
        for (var i = 0; i < count; i++)
        {
            yield return start + TimeSpan.FromSeconds(i * interval) + TimeSpan.FromMilliseconds(jitterMs * ((random.NextDouble() * 2) - 1));
        }
    }

    [Fact]
    public void Clock_ReportsTheTempo_AfterHalfABeat()
    {
        var follower = new ClockTempo();
        var reports = Ticks(120, 13).Select(follower.Tick).ToList();

        Assert.All(reports[..12], r => Assert.Null(r));
        Assert.Equal(120, reports[12]!.Value, precision: 6);
    }

    [Fact]
    public void Clock_IgnoresUsbJitter_AndReportsOnlyRealChanges()
    {
        var follower = new ClockTempo();
        var reports = Ticks(100, 24 * 8, jitterMs: 1).Select(follower.Tick).OfType<double>().ToList();

        Assert.InRange(reports[0], 98, 102);
        Assert.True(reports.Count <= 3, $"±1 ms jitter should not keep retiming the band, got {reports.Count} reports");
        Assert.InRange(follower.Bpm!.Value, 99, 101);
    }

    [Fact]
    public void Clock_FollowsATempoChange_WithinABeat()
    {
        var follower = new ClockTempo();
        var slow = Ticks(90, 48).ToList();
        foreach (var t in slow)
        {
            follower.Tick(t);
        }

        double? last = null;
        foreach (var t in Ticks(140, 25, slow[^1] + TimeSpan.FromSeconds(60.0 / 140 / 24)))
        {
            last = follower.Tick(t) ?? last;
        }

        Assert.InRange(last!.Value, 139.5, 140.5); // within the reporting threshold
        Assert.Equal(140, follower.Bpm!.Value, precision: 2);
    }

    [Fact]
    public void Clock_StopOrGap_HoldsQuiet_UntilItRunsAgain()
    {
        var follower = new ClockTempo();
        foreach (var t in Ticks(120, 30))
        {
            follower.Tick(t);
        }

        follower.Reset(); // MIDI Stop
        Assert.Null(follower.Bpm);

        // Restart at the same tempo after a pause: nothing new to report.
        Assert.All(Ticks(120, 30, TimeSpan.FromSeconds(10)).Select(follower.Tick), r => Assert.Null(r));
        Assert.Equal(120, follower.Bpm!.Value, precision: 6);

        // A long gap without Stop also drops the history.
        follower.Tick(TimeSpan.FromSeconds(20));
        Assert.Null(follower.Bpm);
    }

    [Fact]
    public void Clock_OutOfRange_IsNotReported()
    {
        var follower = new ClockTempo();
        Assert.All(Ticks(400, 30).Select(follower.Tick), r => Assert.Null(r));
    }
}

using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using MetroDom.Core.Safety;
using MetroDom.Core.Timing;

namespace MetroDom.Core.Tests;

public class SafetyGovernorTests
{
    private readonly VirtualClock _clock = new();
    private readonly FakeOutput _out = new("xylo");

    private static NoteOnEvent On(int note, int velocity = 100, int channel = 0) =>
        new((SevenBitNumber)note, (SevenBitNumber)velocity) { Channel = (FourBitNumber)channel };

    private static NoteOffEvent Off(int note) => new((SevenBitNumber)note, SevenBitNumber.MinValue);

    private SafetyGovernor Governor(InstrumentProfile? profile = null, SafetyLimits? limits = null, Func<TimeOnly>? time = null) =>
        new(_out, profile ?? new InstrumentProfile("xylo", TimeSpan.Zero), limits ?? SafetyLimits.None, _clock, time ?? (() => new TimeOnly(12, 0)));

    private int[] SentNotes() => [.. _out.Sent.OfType<NoteOnEvent>().Select(e => (int)e.NoteNumber)];

    [Fact]
    public void NoLimits_PassesEverything()
    {
        var governor = Governor();
        governor.Send(On(60));
        governor.Send(On(60));
        governor.Send(Off(60));

        Assert.Equal(3, _out.Sent.Count);
        Assert.Equal(2, governor.Passed);
        Assert.Null(governor.Summary());
    }

    [Fact]
    public void NoteOutsideTheInstrument_IsHeldBack()
    {
        var governor = Governor(new InstrumentProfile("xylo", TimeSpan.Zero) { Notes = new HashSet<int> { 60, 64, 67 } });
        var blocked = new List<BlockedNote>();
        governor.Blocked += blocked.Add;

        governor.Send(On(60));
        governor.Send(On(61, channel: 3));

        Assert.Equal([60], SentNotes());
        Assert.Equal(new BlockedNote("xylo", 61, 3, BlockReason.NotPlayable), Assert.Single(blocked));
        Assert.Equal(1, governor.BlockedFor(BlockReason.NotPlayable));
    }

    [Fact]
    public void SameNoteFasterThanMinInterval_IsHeldBack_OtherNotesAreNot()
    {
        var governor = Governor(new InstrumentProfile("xylo", TimeSpan.Zero) { MinInterval = TimeSpan.FromMilliseconds(60) });

        governor.Send(On(60));
        _clock.Advance(TimeSpan.FromMilliseconds(30));
        governor.Send(On(60)); // too soon
        governor.Send(On(64)); // different actuator
        _clock.Advance(TimeSpan.FromMilliseconds(30));
        governor.Send(On(60)); // 60 ms after the last one that played

        Assert.Equal([60, 64, 60], SentNotes());
        Assert.Equal(1, governor.BlockedFor(BlockReason.TooSoon));
    }

    [Fact]
    public void HeldBackNote_DoesNotRestartTheInterval()
    {
        var governor = Governor(new InstrumentProfile("xylo", TimeSpan.Zero) { MinInterval = TimeSpan.FromMilliseconds(60) });

        governor.Send(On(60));
        _clock.Advance(TimeSpan.FromMilliseconds(50));
        governor.Send(On(60)); // held back at 50 ms
        _clock.Advance(TimeSpan.FromMilliseconds(10));
        governor.Send(On(60)); // 60 ms after the strike that played

        Assert.Equal([60, 60], SentNotes());
    }

    [Fact]
    public void RateLimit_CapsStrikesPerSecond_AndRecovers()
    {
        var governor = Governor(limits: new SafetyLimits { MaxNotesPerSecond = 3 });

        for (var i = 0; i < 5; i++)
        {
            governor.Send(On(60 + i));
            _clock.Advance(TimeSpan.FromMilliseconds(100));
        }

        Assert.Equal([60, 61, 62], SentNotes());
        Assert.Equal(2, governor.BlockedFor(BlockReason.RateLimited));

        _clock.AdvanceTo(TimeSpan.FromSeconds(1)); // the first strike leaves the window
        governor.Send(On(70));
        Assert.Equal(70, SentNotes()[^1]);
    }

    [Fact]
    public void VelocityCap_TurnsDownACopy_NeverTheSharedEvent()
    {
        var governor = Governor(limits: new SafetyLimits { MaxVelocity = 80 });
        var loud = On(60, velocity: 120);
        var soft = On(62, velocity: 50);

        governor.Send(loud);
        governor.Send(soft);

        var sent = _out.Sent.OfType<NoteOnEvent>().ToArray();
        Assert.Equal(80, (int)sent[0].Velocity);
        Assert.Equal(120, (int)loud.Velocity); // the scheduler sends this same object to other instruments
        Assert.Same(soft, sent[1]);
        Assert.Equal(1, governor.Capped);
        Assert.Equal(2, governor.Passed);
    }

    [Fact]
    public void SilentQuietHours_HoldBackNewNotes_ButNoteOffsAndPanicPass()
    {
        var now = new TimeOnly(23, 30);
        var governor = Governor(limits: new SafetyLimits { Quiet = new QuietHours(new TimeOnly(23, 0), new TimeOnly(7, 0)) }, time: () => now);

        governor.Send(On(60));
        governor.Send(Off(60));
        Orchestra.SendPanic(governor);

        Assert.Empty(SentNotes());
        Assert.IsType<NoteOffEvent>(_out.Sent[0]);
        Assert.Equal(1 + (Orchestra.ChannelCount * 2), _out.Sent.Count);
        Assert.Equal(1, governor.BlockedFor(BlockReason.QuietHours));

        now = new TimeOnly(7, 0); // window over (end is exclusive)
        governor.Send(On(60));
        Assert.Equal([60], SentNotes());
    }

    [Fact]
    public void QuietHoursWithVelocity_TurnNotesDown()
    {
        var governor = Governor(
            limits: new SafetyLimits { MaxVelocity = 100, Quiet = new QuietHours(new TimeOnly(22, 0), new TimeOnly(8, 0), 40) },
            time: () => new TimeOnly(2, 0));

        governor.Send(On(60, velocity: 90));

        Assert.Equal(40, (int)_out.Sent.OfType<NoteOnEvent>().Single().Velocity);
    }

    [Theory]
    [InlineData(23, 0, true)]
    [InlineData(3, 0, true)]
    [InlineData(6, 59, true)]
    [InlineData(7, 0, false)]
    [InlineData(12, 0, false)]
    [InlineData(22, 59, false)]
    public void QuietHours_CrossMidnight(int hour, int minute, bool inside) =>
        Assert.Equal(inside, new QuietHours(new TimeOnly(23, 0), new TimeOnly(7, 0)).Contains(new TimeOnly(hour, minute)));

    [Fact]
    public void QuietHours_SameDayWindow()
    {
        var lunch = new QuietHours(new TimeOnly(12, 0), new TimeOnly(13, 0));
        Assert.True(lunch.Contains(new TimeOnly(12, 30)));
        Assert.False(lunch.Contains(new TimeOnly(13, 0)));
        Assert.False(lunch.Contains(new TimeOnly(11, 59)));
    }

    [Fact]
    public void Panic_PassesEvenWhenEverythingElseIsBlocked()
    {
        var governor = Governor(
            new InstrumentProfile("xylo", TimeSpan.Zero) { Notes = new HashSet<int>(), MinInterval = TimeSpan.FromSeconds(10) },
            new SafetyLimits { MaxNotesPerSecond = 1, MaxVelocity = 1, Quiet = new QuietHours(new TimeOnly(0, 0), new TimeOnly(23, 59)) });
        var orchestra = new Orchestra();
        orchestra.AssignAll(governor);

        orchestra.Route(On(60));
        orchestra.Panic();

        Assert.Empty(SentNotes());
        Assert.Equal(Orchestra.ChannelCount * 2, _out.Sent.OfType<ControlChangeEvent>().Count());
    }

    private static InstrumentProfile Coil(int pulseMs = 30, int intervalMs = 30, int? polyphony = null, params int[] notes) =>
        new("coil", TimeSpan.Zero)
        {
            Notes = notes.Length == 0 ? new HashSet<int> { 60 } : notes.ToHashSet(),
            MinInterval = TimeSpan.FromMilliseconds(intervalMs),
            NotePulse = (notes.Length == 0 ? [60] : notes).ToDictionary(n => n, _ => TimeSpan.FromMilliseconds(pulseMs)),
            Polyphony = polyphony,
            Safety = new ActuatorSafety(TimeSpan.FromMilliseconds(45), 30, ActuatorSafety.DefaultDutyWindow),
        };

    [Fact]
    public void DutyCycle_HoldsBackWhatTheBrainWouldRefuse()
    {
        // The same run as the firmware's duty_RefusesWhenTheThermalBudgetIsSpent (brain_test.cpp):
        // 30 ms pulses back to back against a 30% budget. 13 in a row, then about one in three.
        var governor = Governor(Coil());
        for (var i = 0; i < 20; i++)
        {
            governor.Send(On(60));
            _clock.Advance(TimeSpan.FromMilliseconds(30));
        }

        Assert.Equal(15, governor.Passed);
        Assert.Equal(5, governor.BlockedFor(BlockReason.DutyCycle));

        _clock.Advance(TimeSpan.FromSeconds(1)); // a rest refills it
        governor.Send(On(60));
        Assert.Equal(16, governor.Passed);
    }

    [Fact]
    public void DutyCycle_IsPerActuator()
    {
        var governor = Governor(Coil(notes: [60, 64]));
        for (var i = 0; i < 20; i++)
        {
            governor.Send(On(60));
            _clock.Advance(TimeSpan.FromMilliseconds(30));
        }

        governor.Send(On(64)); // a cold actuator plays while 60 is hot
        Assert.Equal(64, (int)_out.Sent.OfType<NoteOnEvent>().Last().NoteNumber);
    }

    [Fact]
    public void DutyCycle_WithoutAPulse_AssumesTheLongestTheFirmwareAllows()
    {
        // No pulseMs: each strike costs maxOnMs (45 ms of the 300 ms budget), draining 13.5 ms per
        // 45 ms gap. 9 fit; a 30 ms pulse would fit 17.
        var governor = Governor(Coil() with { NotePulse = new Dictionary<int, TimeSpan>(), MinInterval = TimeSpan.Zero });
        for (var i = 0; i < 10; i++)
        {
            governor.Send(On(60));
            _clock.Advance(TimeSpan.FromMilliseconds(45));
        }

        Assert.Equal(9, governor.Passed);
        Assert.Equal(1, governor.BlockedFor(BlockReason.DutyCycle));
    }

    [Fact]
    public void StillStriking_IsTooSoon_EvenWithoutAMinInterval()
    {
        var governor = Governor(Coil(pulseMs: 30, intervalMs: 0));
        governor.Send(On(60));
        _clock.Advance(TimeSpan.FromMilliseconds(10));
        governor.Send(On(60));

        Assert.Equal(1, governor.BlockedFor(BlockReason.TooSoon));
    }

    [Fact]
    public void Polyphony_CapsActuatorsOnAtOnce_AndFreesThemWhenThePulseEnds()
    {
        var governor = Governor(Coil(polyphony: 2, notes: [60, 64, 67]));
        var blocked = new List<BlockedNote>();
        governor.Blocked += blocked.Add;

        governor.Send(On(60));
        governor.Send(On(64));
        governor.Send(On(67)); // a third coil while two are on
        _clock.Advance(TimeSpan.FromMilliseconds(30));
        governor.Send(On(67)); // both pulses have ended

        Assert.Equal([60, 64, 67], SentNotes());
        Assert.Equal(BlockReason.Polyphony, Assert.Single(blocked).Reason);
    }

    [Fact]
    public void ProfileWithoutSafety_HasNoDutyOrPolyphonyLimit()
    {
        var governor = Governor(new InstrumentProfile("synth", TimeSpan.Zero));
        for (var i = 0; i < 100; i++)
        {
            governor.Send(On(60 + (i % 12)));
        }

        Assert.Equal(100, governor.Passed);
    }

    [Fact]
    public void Panic_ToAMetroDomInstrument_StartsWithTheSysExAllOff()
    {
        var metroDom = Governor(Coil() with { SpeaksMetroDom = true });
        var other = new SafetyGovernor(new FakeOutput("synth"), Coil(), SafetyLimits.None, _clock);

        Orchestra.SendPanic(metroDom);
        Orchestra.SendPanic(other);

        Assert.True(Discovery.ProfileExchange.IsAllOff(_out.Sent[0]), "one message silences it before the CC burst");
        Assert.Equal(1 + (Orchestra.ChannelCount * 2), _out.Sent.Count);
        Assert.All(((FakeOutput)other.Inner).Sent, e => Assert.IsType<ControlChangeEvent>(e)); // 7D means something else to others
    }

    [Fact]
    public void Summary_ListsReasonsAndCaps()
    {
        var governor = Governor(
            new InstrumentProfile("xylo", TimeSpan.Zero) { Notes = new HashSet<int> { 60 } },
            new SafetyLimits { MaxVelocity = 64 });

        governor.Send(On(61));
        governor.Send(On(60, velocity: 127));

        Assert.Equal("1 not playable, 1 turned down", governor.Summary());
        Assert.Equal(1, governor.TotalBlocked);
    }

    [Theory]
    [InlineData(0, null, null)]
    [InlineData(128, null, null)]
    [InlineData(null, 0, null)]
    [InlineData(null, null, 128)]
    public void BadLimits_Throw(int? velocity, int? rate, int? quietVelocity) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Governor(limits: new SafetyLimits
        {
            MaxVelocity = velocity,
            MaxNotesPerSecond = rate,
            Quiet = quietVelocity is { } q ? new QuietHours(new TimeOnly(1, 0), new TimeOnly(2, 0), q) : null,
        }));

    [Fact]
    public void ScheduledSong_ThroughGovernors_StaysInsideTheLimits()
    {
        // A song with a fast repeat on one note and a note the instrument lacks, played by the scheduler.
        var profile = new InstrumentProfile("xylo", TimeSpan.FromMilliseconds(10)) { Notes = new HashSet<int> { 60, 64 }, MinInterval = TimeSpan.FromMilliseconds(60) };
        var governor = new SafetyGovernor(_out, profile, SafetyLimits.None, _clock);
        var orchestra = new Orchestra();
        orchestra.AssignAll(governor);
        var scheduler = new LatencyScheduler(orchestra, _clock, new Dictionary<IMidiOutput, InstrumentProfile> { [governor] = profile });
        TimedMidiEvent At(double ms, int note) => new(TimeSpan.FromMilliseconds(ms), On(note));
        scheduler.Start([At(0, 60), At(20, 60), At(40, 61), At(100, 64), At(120, 60)]);

        while (scheduler.NextDue is { } due)
        {
            _clock.AdvanceTo(due);
            scheduler.Pump();
        }

        Assert.Equal([60, 64, 60], SentNotes());
        Assert.Equal(1, governor.BlockedFor(BlockReason.TooSoon));
        Assert.Equal(1, governor.BlockedFor(BlockReason.NotPlayable));
    }
}

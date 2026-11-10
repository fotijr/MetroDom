using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;

namespace MetroDom.Core.Tests;

public class OrchestraTests
{
    private static NoteOnEvent NoteOn(int channel, int note = 60, int velocity = 100) =>
        new((SevenBitNumber)note, (SevenBitNumber)velocity) { Channel = (FourBitNumber)channel };

    private static NoteOffEvent NoteOff(int channel, int note = 60) =>
        new((SevenBitNumber)note, SevenBitNumber.MinValue) { Channel = (FourBitNumber)channel };

    [Fact]
    public void Route_SendsOnlyToOutputsAssignedToThatChannel()
    {
        var xylo = new FakeOutput("xylo");
        var drums = new FakeOutput("drums");
        var orchestra = new Orchestra();
        orchestra.Assign(0, xylo);
        orchestra.Assign(9, drums);

        orchestra.Route(NoteOn(0));
        orchestra.Route(NoteOn(9));
        orchestra.Route(NoteOn(3));

        Assert.Single(xylo.Sent);
        Assert.Single(drums.Sent);
    }

    [Fact]
    public void Route_IgnoresNonChannelEvents()
    {
        var output = new FakeOutput();
        var orchestra = new Orchestra();
        orchestra.AssignAll(output);

        var sent = orchestra.Route(new SetTempoEvent(500_000));

        Assert.Equal(0, sent);
        Assert.Empty(output.Sent);
    }

    [Fact]
    public void Mute_BlocksNewNotesButAlwaysPassesNoteOffs()
    {
        var output = new FakeOutput();
        var orchestra = new Orchestra();
        orchestra.Assign(2, output);

        orchestra.Route(NoteOn(2));
        orchestra.SetMuted(2, true);
        orchestra.Route(NoteOff(2));
        orchestra.Route(NoteOn(2, velocity: 0));
        orchestra.Route(NoteOn(2, note: 62));

        Assert.Collection(
            output.Sent,
            e => Assert.IsType<NoteOnEvent>(e),
            e => Assert.IsType<NoteOffEvent>(e),
            e => Assert.Equal(0, ((NoteOnEvent)e).Velocity));
    }

    [Fact]
    public void Assign_SameOutputTwice_SendsOnce()
    {
        var output = new FakeOutput();
        var orchestra = new Orchestra();
        orchestra.Assign(0, output);
        orchestra.Assign(0, output);

        orchestra.Route(NoteOn(0));

        Assert.Single(output.Sent);
    }

    [Fact]
    public void Panic_SendsAllSoundOffAndAllNotesOffOnEveryChannel_EvenWhenMuted()
    {
        var output = new FakeOutput();
        var orchestra = new Orchestra();
        orchestra.Assign(0, output);
        orchestra.SetMuted(0, true);

        orchestra.Panic();

        var controls = output.Sent.Cast<ControlChangeEvent>().ToList();
        Assert.Equal(Orchestra.ChannelCount * 2, controls.Count);
        for (var channel = 0; channel < Orchestra.ChannelCount; channel++)
        {
            Assert.Contains(controls, c => c.Channel == channel && c.ControlNumber == ControlNumbers.AllSoundOff);
            Assert.Contains(controls, c => c.Channel == channel && c.ControlNumber == ControlNumbers.AllNotesOff);
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(16)]
    public void Assign_RejectsChannelsOutsideZeroToFifteen(int channel)
    {
        var orchestra = new Orchestra();

        Assert.Throws<ArgumentOutOfRangeException>(() => orchestra.Assign(channel, new FakeOutput()));
    }
}

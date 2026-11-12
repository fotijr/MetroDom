using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using MetroDom.Core.Lighting;

namespace MetroDom.Core.Tests;

public class LightCuesTests
{
    private static readonly TimeSpan Gap = LightCues.MinChangeGap;

    private static LightProfile Rig(string extra = "")
    {
        var json = """{"schema":"metrodom.light-profile/0","name":"rig" """ + extra + """
            ,"fixtures":[{"id":"par","address":1,"channels":["dimmer"]}],
             "scenes":[{"note":36,"name":"warm","set":{"*":{"dimmer":100}}},{"note":37,"name":"cold","set":{"*":{"dimmer":200}}}]}
            """;
        Assert.True(LightProfileJson.TryParse(json, out var rig, out var error), error);
        return rig;
    }

    private static NoteOnEvent On(int note, int channel = 15) =>
        new((SevenBitNumber)note, (SevenBitNumber)100) { Channel = (FourBitNumber)channel };

    private static NoteOffEvent Off(int note) =>
        new((SevenBitNumber)note, (SevenBitNumber)0) { Channel = (FourBitNumber)15 };

    private static ControlChangeEvent Cc(int number, int value) =>
        new((SevenBitNumber)number, (SevenBitNumber)value) { Channel = (FourBitNumber)15 };

    [Fact]
    public void Hold_SceneLastsAsLongAsItsNote_AndTheLastHeldWins()
    {
        var cues = new LightCues(Rig());

        cues.Receive(On(36), TimeSpan.Zero);
        Assert.Equal("warm", cues.At(TimeSpan.Zero).Scene?.Name);

        cues.Receive(On(37), Gap);
        Assert.Equal("cold", cues.At(Gap).Scene?.Name);

        cues.Receive(Off(37), 2 * Gap); // warm is still held
        Assert.Equal("warm", cues.At(2 * Gap).Scene?.Name);

        cues.Receive(new NoteOnEvent((SevenBitNumber)36, (SevenBitNumber)0) { Channel = (FourBitNumber)15 }, 3 * Gap);
        Assert.Null(cues.At(3 * Gap).Scene); // back to reactive
    }

    [Fact]
    public void Latch_SceneStaysAfterItsNote_SameNoteAgainReleases()
    {
        var cues = new LightCues(Rig(""","midi":{"cues":"latch"}"""));

        cues.Receive(On(36), TimeSpan.Zero);
        cues.Receive(Off(36), TimeSpan.Zero);
        Assert.Equal("warm", cues.At(TimeSpan.Zero).Scene?.Name);

        cues.Receive(On(37), Gap);
        Assert.Equal("cold", cues.At(Gap).Scene?.Name);

        cues.Receive(On(37), 2 * Gap);
        Assert.Null(cues.At(2 * Gap).Scene);
    }

    [Fact]
    public void FlashCap_AFastCueWaits_AndTheNewestWins()
    {
        var cues = new LightCues(Rig());
        cues.Receive(On(36), TimeSpan.Zero);

        var soon = TimeSpan.FromMilliseconds(50);
        cues.Receive(Off(36), soon);
        cues.Receive(On(37), soon * 2);
        Assert.Equal("warm", cues.At(soon * 2).Scene?.Name); // too soon after the last change
        Assert.Equal("cold", cues.Wanted?.Name);

        Assert.Equal("cold", cues.At(Gap).Scene?.Name); // reactive in between never showed
    }

    [Fact]
    public void Master_FollowsItsCc_ButNeverFasterThanTheCap()
    {
        var cues = new LightCues(Rig());
        Assert.Equal(255, cues.At(TimeSpan.Zero).Master);

        cues.Receive(Cc(7, 0), TimeSpan.Zero);
        Assert.Equal(255, cues.At(TimeSpan.Zero).Master); // no jump
        Assert.InRange(cues.At(Gap / 2).Master, 126, 129); // halfway
        Assert.Equal(0, cues.At(Gap).Master);

        cues.Receive(Cc(7, 64), 2 * Gap);
        Assert.Equal(129, cues.At(3 * Gap).Master); // 64/127 of 255
    }

    [Fact]
    public void Ignores_OtherChannels_UnknownNotes_AndOtherControllers()
    {
        var cues = new LightCues(Rig());

        cues.Receive(On(36, channel: 0), TimeSpan.Zero);
        cues.Receive(On(50), TimeSpan.Zero);
        cues.Receive(Cc(1, 0), TimeSpan.Zero);

        Assert.Null(cues.At(Gap).Scene);
        Assert.Equal(255, cues.At(Gap).Master);
    }

    [Fact]
    public void AllNotesOff_ReleasesHeldScenes_ClearKeepsTheMaster()
    {
        var cues = new LightCues(Rig());
        cues.Receive(On(36), TimeSpan.Zero);
        cues.Receive(Cc(7, 0), TimeSpan.Zero);

        cues.Receive(Cc(123, 0), Gap);
        Assert.Null(cues.At(Gap).Scene);

        cues.Receive(On(37), 2 * Gap);
        cues.Clear();
        Assert.Null(cues.At(2 * Gap).Scene);
        Assert.Equal(0, cues.At(2 * Gap).Master);
    }

    [Fact]
    public void Profile_RejectsAnUnknownCueMode()
    {
        const string json = """
            {"schema":"metrodom.light-profile/0","name":"rig","midi":{"cues":"toggle"},
             "fixtures":[{"id":"par","address":1,"channels":["dimmer"]}]}
            """;

        Assert.False(LightProfileJson.TryParse(json, out _, out var error));
        Assert.Contains("hold or latch", error, StringComparison.Ordinal);
    }
}

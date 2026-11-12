using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using MetroDom.Core.Lighting;
using MetroDom.Core.Policy;

namespace MetroDom.Core.Tests;

public class LightLimitsTests
{
    private static PolicyProfile Policy(string lights)
    {
        var json = """{"schema":"metrodom.policy/0","mode":"bar","lights":""" + lights + "}";
        Assert.True(PolicyProfile.TryParse(json, out var policy, out var error), error);
        return policy;
    }

    private static string? Error(string lights)
    {
        var json = """{"schema":"metrodom.policy/0","mode":"bar","lights":""" + lights + "}";
        Assert.False(PolicyProfile.TryParse(json, out _, out var error));
        return error;
    }

    [Fact]
    public void Defaults_AreTheSafeOnes()
    {
        var limits = PolicyProfile.LiveBand.Lights;

        Assert.False(limits.Strobe);
        Assert.Equal(3, limits.MaxFlashesPerSecond);
        Assert.Equal(TimeSpan.FromSeconds(1.0 / 3), limits.FlashGap);
        Assert.Equal(255, limits.BrightnessAt(new TimeOnly(23, 30)));
        Assert.True(limits.IsDefault);
        Assert.DoesNotContain("lights", PolicyProfile.LiveBand.ToJson(), StringComparison.Ordinal);
    }

    [Fact]
    public void Policy_ReadsLights_AndRoundTrips()
    {
        var policy = Policy("""{"strobe":true,"maxFlashesPerSecond":5,"maxBrightness":80,"quietHours":{"from":"23:00","to":"07:00","maxBrightness":30}}""");

        Assert.True(policy.Lights.Strobe);
        Assert.Equal(5, policy.Lights.MaxFlashesPerSecond);
        Assert.Equal(new QuietHours(new TimeOnly(23, 0), new TimeOnly(7, 0), 30), policy.Lights.Quiet);
        Assert.True(PolicyProfile.TryParse(policy.ToJson(), out var again, out _));
        Assert.Equal(policy.Lights, again.Lights);
        Assert.Equal("≤ 5 flashes/s, strobe allowed, brightness 80%, quiet 23:00-07:00 at 30%", policy.Lights.Describe());
    }

    [Theory]
    [InlineData("""{"maxFlashesPerSecond":4}""", "needs \"lights.strobe\"")]
    [InlineData("""{"strobe":true,"maxFlashesPerSecond":12}""", "at most 10")]
    [InlineData("""{"maxFlashesPerSecond":0}""", "above 0")]
    [InlineData("""{"maxBrightness":5}""", "10-100")]
    [InlineData("""{"quietHours":{"from":"23:00","to":"07:00","maxBrightness":0}}""", "10-100")]
    [InlineData("""{"quietHours":{"from":"11pm","to":"07:00"}}""", "HH:mm")]
    [InlineData("""{"quietHours":{"from":"07:00","to":"07:00"}}""", "must differ")]
    [InlineData("""[]""", "must be an object")]
    public void Policy_RejectsUnsafeOrUnclearLights(string lights, string expected)
    {
        Assert.Contains(expected, Error(lights), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(22, 59, false)]
    [InlineData(23, 0, true)]
    [InlineData(3, 0, true)]
    [InlineData(7, 0, false)]
    public void QuietHours_WrapPastMidnight(int hour, int minute, bool quiet)
    {
        var limits = Policy("""{"strobe":true,"maxBrightness":80,"quietHours":{"from":"23:00","to":"07:00","maxBrightness":30}}""").Lights;
        var time = new TimeOnly(hour, minute);

        Assert.Equal(quiet ? 77 : 204, limits.BrightnessAt(time)); // 30% or 80% of 255
        Assert.Equal(!quiet, limits.StrobeAt(time));
    }

    [Fact]
    public void QuietHours_NeverRaiseTheCap()
    {
        var limits = new LightLimits { MaxBrightness = 20, Quiet = new QuietHours(new TimeOnly(0, 0), new TimeOnly(6, 0), 50) };

        Assert.Equal(51, limits.BrightnessAt(new TimeOnly(1, 0)));
    }

    [Fact]
    public void Renderer_SendsStrobe_OnlyWhenAllowed()
    {
        const string json = """
            {"schema":"metrodom.light-profile/0","name":"rig",
             "fixtures":[{"id":"par","address":1,"channels":["dimmer","strobe"]}],
             "scenes":[{"note":36,"set":{"par":{"dimmer":255,"strobe":180}}}]}
            """;
        Assert.True(LightProfileJson.TryParse(json, out var rig, out _));
        var look = rig.SceneFor(36)!.Look;

        Assert.Equal(0, LightRenderer.Render(rig, look)[1][1]);
        Assert.Equal(180, LightRenderer.Render(rig, look, strobe: true)[1][1]);
    }

    [Fact]
    public void FlashRate_SetsThePulseAndCueGaps()
    {
        const string json = """
            {"schema":"metrodom.light-profile/0","name":"rig",
             "fixtures":[{"id":"par","address":1,"channels":["dimmer"]}],
             "scenes":[{"note":36,"set":{"*":{"dimmer":100}}},{"note":37,"set":{"*":{"dimmer":200}}}]}
            """;
        Assert.True(LightProfileJson.TryParse(json, out var rig, out _));
        var calm = new LightLimits { MaxFlashesPerSecond = 2 };

        var reactive = new ReactiveLights(rig, [[0]], calm);
        reactive.Note(0, 127, TimeSpan.Zero);
        reactive.Note(0, 127, TimeSpan.FromMilliseconds(400)); // inside 500 ms: ignored
        Assert.Equal(40, reactive.Look(TimeSpan.FromMilliseconds(400)).ValueFor("par", "dimmer"));

        var cues = new LightCues(rig, calm);
        Assert.Equal(TimeSpan.FromMilliseconds(500), cues.ChangeGap);
        Assert.Equal(510, cues.MasterSlewPerSecond);
        cues.Receive(On(36), TimeSpan.Zero);
        cues.Receive(On(37), TimeSpan.FromMilliseconds(400));
        Assert.Equal(36, cues.At(TimeSpan.FromMilliseconds(400)).Scene?.Note);
        Assert.Equal(37, cues.At(TimeSpan.FromMilliseconds(500)).Scene?.Note);
    }

    private static NoteOnEvent On(int note) => new((SevenBitNumber)note, (SevenBitNumber)100) { Channel = (FourBitNumber)15 };
}

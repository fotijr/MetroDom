using MetroDom.Core.Lighting;

namespace MetroDom.Core.Tests;

public class ReactiveLightsTests
{
    private static readonly TimeSpan Ms = TimeSpan.FromMilliseconds(1);

    private static LightProfile Rig(string extra = "")
    {
        var json = $$"""
            {"schema":"metrodom.light-profile/0","name":"rig",
             "fixtures":[{"id":"a","address":1,"channels":["dimmer","red","green","blue"]},
                         {"id":"b","address":5,"channels":["dimmer","red","green","blue"]},
                         {"id":"strip","address":9,"channels":["red","green","blue"],"count":2}]
             {{extra}}}
            """;
        Assert.True(LightProfileJson.TryParse(json, out var rig, out var error), error);
        return rig;
    }

    [Fact]
    public void Pulse_PeaksWithVelocity_InTheChannelsColour_AndFadesToTheGlow()
    {
        var lights = new ReactiveLights(Rig(), [[0]]);

        Assert.Equal(40, lights.Look(TimeSpan.Zero).ValueFor("a", "dimmer")); // idle glow, never dark
        Assert.Equal(0x2F, lights.Look(TimeSpan.Zero).ValueFor("a", "red")); // already in channel 1's colour

        lights.Note(0, 127, 100 * Ms);
        Assert.Equal(255, lights.Look(100 * Ms).ValueFor("a", "dimmer"));
        Assert.Equal(148, lights.Look(250 * Ms).ValueFor("a", "dimmer")); // halfway down a 300 ms fade
        Assert.Equal(40, lights.Look(400 * Ms).ValueFor("a", "dimmer"));

        var soft = new ReactiveLights(Rig(), [[0]]);
        soft.Note(0, 64, TimeSpan.Zero);
        Assert.Equal(148, soft.Look(TimeSpan.Zero).ValueFor("a", "dimmer"));
    }

    [Fact]
    public void Fixtures_AreSharedOutAcrossTheBand_UnlessTheProfileSays()
    {
        var lights = new ReactiveLights(Rig(), [[0], [9]]);
        Assert.Equal([0], lights.Following["a"]!);
        Assert.Equal([9], lights.Following["b"]!);
        Assert.Equal([0], lights.Following["strip"]!);

        lights.Note(9, 127, TimeSpan.Zero); // drums
        var look = lights.Look(TimeSpan.Zero);
        Assert.Equal(255, look.ValueFor("b", "dimmer"));
        Assert.Equal(0xB0, look.ValueFor("b", "green")); // amber
        Assert.Equal(40, look.ValueFor("a", "dimmer"));

        var assigned = new ReactiveLights(Rig(""","reactive":{"follow":{"a":[10]}}"""), [[0], [9]]);
        Assert.Equal([9], assigned.Following["a"]!);
        Assert.Equal([0], assigned.Following["b"]!); // the rest still shared out

        Assert.Null(new ReactiveLights(Rig(), []).Following["a"]); // no band: follow everything
    }

    [Fact]
    public void TheLightsChannel_IsNeverPulsed()
    {
        var lights = new ReactiveLights(Rig(), []);

        lights.Note(15, 127, TimeSpan.Zero);

        Assert.Equal(40, lights.Look(TimeSpan.Zero).ValueFor("a", "dimmer"));
    }

    [Fact]
    public void Notes_ShowAtTheirTime_InTimeOrder()
    {
        var lights = new ReactiveLights(Rig(), [[0, 1]]);

        // Sent in send order (drums go out earlier), shown in sound order.
        lights.Note(1, 127, 200 * Ms);
        lights.Note(0, 127, 150 * Ms);

        Assert.Equal(40, lights.Look(149 * Ms).ValueFor("a", "dimmer"));
        var look = lights.Look(150 * Ms);
        Assert.Equal(255, look.ValueFor("a", "dimmer"));
        Assert.Equal(0x2F, look.ValueFor("a", "red")); // channel 1 won; channel 2 at 200 ms is inside the gap
    }

    [Fact]
    public void FlashCap_NoFixtureRisesMoreThanThreeTimesASecond()
    {
        var lights = new ReactiveLights(Rig(""","reactive":{"decayMs":100}"""), [[0]]);

        // 16th notes at 150 BPM: 10 notes a second, for 4 seconds.
        for (var t = 0; t < 4000; t += 100)
        {
            lights.Note(0, 127, t * Ms);
        }

        var rises = new List<int>();
        byte last = 0;
        for (var t = 0; t <= 4000; t += 5)
        {
            var level = lights.Look(t * Ms).ValueFor("a", "dimmer");
            if (level > last)
            {
                rises.Add(t);
            }

            last = level;
        }

        Assert.All(rises, r => Assert.True(rises.Count(o => o > r - 1000 && o <= r) <= 3, $"more than 3 rises in the second up to {r} ms"));
        Assert.True(rises.Count >= 10, "the lights still follow the music");
    }

    [Fact]
    public void FixtureWithoutDimmer_GetsItsColourScaled()
    {
        var lights = new ReactiveLights(Rig(""","reactive":{"colours":{"1":"#ff8000"},"baseLevel":51}"""), [[0]]);

        Assert.Equal(51, lights.Look(TimeSpan.Zero).ValueFor("strip", "red"));
        Assert.Equal(26, lights.Look(TimeSpan.Zero).ValueFor("strip", "green"));
        lights.Note(0, 127, TimeSpan.Zero);
        Assert.Equal(255, lights.Look(TimeSpan.Zero).ValueFor("strip", "red"));
        Assert.Equal(128, lights.Look(TimeSpan.Zero).ValueFor("strip", "green"));
    }

    [Fact]
    public void Clear_DropsPulsesAndPendingNotes()
    {
        var lights = new ReactiveLights(Rig(), [[0]]);
        lights.Note(0, 127, TimeSpan.Zero);
        lights.Note(0, 127, 500 * Ms);
        Assert.Equal(255, lights.Look(TimeSpan.Zero).ValueFor("a", "dimmer"));

        lights.Clear();

        Assert.Equal(40, lights.Look(10 * Ms).ValueFor("a", "dimmer"));
        Assert.Equal(40, lights.Look(500 * Ms).ValueFor("a", "dimmer"));
    }

    [Theory]
    [InlineData(""","reactive":[]""", "must be an object")]
    [InlineData(""","reactive":{"baseLevel":300}""", "baseLevel must be 0-255")]
    [InlineData(""","reactive":{"colours":{"17":"#ffffff"}}""", "channel 1-16")]
    [InlineData(""","reactive":{"colours":{"1":"red"}}""", "#ffb000")]
    [InlineData(""","reactive":{"follow":{"nope":[1]}}""", "'nope' must be a fixture")]
    [InlineData(""","reactive":{"follow":{"a":[0]}}""", "channels 1-16")]
    public void Parse_RejectsBadReactiveSettings(string extra, string reason)
    {
        var json = """{"schema":"metrodom.light-profile/0","name":"x","fixtures":[{"id":"a","address":1,"channels":["red"]}]""" + extra + "}";

        Assert.False(LightProfileJson.TryParse(json, out _, out var error));
        Assert.Contains(reason, error, StringComparison.Ordinal);
    }

    [Fact]
    public void Rgb_ParsesHex()
    {
        Assert.True(Rgb.TryParse("#FFb000", out var amber));
        Assert.Equal(new Rgb(255, 176, 0), amber);
        Assert.Equal("#ffb000", amber.ToString());
        Assert.False(Rgb.TryParse("ffb000", out _));
        Assert.False(Rgb.TryParse("#ggg000", out _));
    }
}

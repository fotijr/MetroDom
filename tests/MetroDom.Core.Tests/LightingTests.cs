using System.Buffers.Binary;
using System.Text.RegularExpressions;
using MetroDom.Core.Lighting;

namespace MetroDom.Core.Tests;

public partial class LightingTests
{
    private const string Head = """{"schema":"metrodom.light-profile/0","name":"x",""";
    private const string OnePar = """ "fixtures":[{"id":"par","address":1,"channels":["dimmer","red","green","blue","strobe"]}]""";

    /// <summary>The example in docs/references/light-profile.md, so the doc can't drift from the parser.</summary>
    private static string DocExample()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AGENTS.md")))
        {
            dir = dir.Parent;
        }

        var doc = File.ReadAllText(Path.Combine(dir!.FullName, "docs", "references", "light-profile.md"));
        return JsonBlock().Match(doc).Groups[1].Value;
    }

    private static LightProfile Parse(string json)
    {
        Assert.True(LightProfileJson.TryParse(json, out var profile, out var error), error);
        return profile;
    }

    [Fact]
    public void Parse_ReadsTheDocumentedExample()
    {
        var rig = Parse(DocExample());

        Assert.Equal("Home rig", rig.Name);
        Assert.Equal(15, rig.Channel);
        Assert.Equal(7, rig.MasterCc);
        Assert.Equal(TimeSpan.FromMilliseconds(30), rig.Latency);
        Assert.Null(rig.Output.Destination);
        Assert.Equal([1, 2], rig.Universes);
        Assert.Equal(180, rig.Fixtures.Single(f => f.Id == "strip").Footprint);
        Assert.Equal("Blue wash", rig.SceneFor(36)!.Name);
        Assert.Null(rig.SceneFor(40));
        Assert.Equal(140, rig.SafeLook.ValueFor("left-par", "dimmer"));
    }

    [Fact]
    public void Parse_FillsTheAdrDefaults()
    {
        var rig = Parse(Head + OnePar + "}");

        Assert.Equal(LightProfile.DefaultChannel, rig.Channel);
        Assert.Equal(LightProfile.DefaultMasterCc, rig.MasterCc);
        Assert.Equal(LightOutput.DefaultPriority, rig.Output.Priority);
        Assert.Same(LightLook.DefaultSafe, rig.SafeLook);
        Assert.Equal(1, rig.Fixtures[0].Universe);
        Assert.Empty(rig.Scenes);
    }

    [Theory]
    [InlineData("nope", "not valid JSON")]
    [InlineData("""{"name":"x"}""", "missing \"schema\"")]
    [InlineData("""{"schema":"metrodom.light-profile/0"}""", "missing \"name\"")]
    [InlineData(Head + "\"fixtures\":[]}", "at least one fixture")]
    [InlineData(Head + """ "fixtures":[{"address":1,"channels":["red"]}]}""", "needs an \"id\"")]
    [InlineData(Head + """ "fixtures":[{"id":"*","address":1,"channels":["red"]}]}""", "reserved")]
    [InlineData(Head + """ "fixtures":[{"id":"a","address":0,"channels":["red"]}]}""", "1-512")]
    [InlineData(Head + """ "fixtures":[{"id":"a","universe":64000,"address":1,"channels":["red"]}]}""", "1-63999")]
    [InlineData(Head + """ "fixtures":[{"id":"a","address":1,"channels":[]}]}""", "name each channel")]
    [InlineData(Head + """ "fixtures":[{"id":"a","address":510,"channels":["r","g","b","w"]}]}""", "run past 512")]
    [InlineData(Head + """ "fixtures":[{"id":"a","address":1,"channels":["r","g","b"],"count":10},{"id":"b","address":30,"channels":["d"]}]}""", "overlap")]
    [InlineData(Head + """ "fixtures":[{"id":"a","address":1,"channels":["r"]},{"id":"a","address":2,"channels":["r"]}]}""", "listed twice")]
    [InlineData(Head + OnePar + ""","scenes":[{"note":36,"set":{"nope":{"red":1}}}]}""", "no fixture 'nope'")]
    [InlineData(Head + OnePar + ""","scenes":[{"note":36,"set":{"par":{"red":300}}}]}""", "0-255")]
    [InlineData(Head + OnePar + ""","scenes":[{"note":36,"set":{}},{"note":36,"set":{}}]}""", "two scenes use note 36")]
    [InlineData(Head + OnePar + ""","scenes":[{"set":{}}]}""", "\"note\" from 0 to 127")]
    [InlineData(Head + OnePar + ""","midi":{"channel":17}}""", "1-16")]
    [InlineData(Head + OnePar + ""","output":{"protocol":"artnet"}}""", "only sacn")]
    [InlineData(Head + OnePar + ""","output":{"priority":201}}""", "0-200")]
    [InlineData(Head + OnePar + ""","latencyMs":"slow"}""", "wrong type")]
    public void Parse_RejectsBadProfiles_WithAReason(string json, string reason)
    {
        Assert.False(LightProfileJson.TryParse(json, out _, out var error));
        Assert.Contains(reason, error, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_PutsEachRoleInItsSlot_AndAFixturesOwnValuesWin()
    {
        var rig = Parse(DocExample());

        var frames = LightRenderer.Render(rig, rig.SceneFor(37)!.Look);

        // left-par at 1: dimmer red green blue strobe; right-par at 6.
        Assert.Equal(new byte[] { 200, 255, 120, 0, 0, 200, 255, 120, 0, 0 }, frames[1][..10]);
        Assert.All(frames[1][10..], b => Assert.Equal(0, b));

        // The strip's own red/green win over "*", and every pixel gets them.
        Assert.Equal(new byte[] { 80, 40, 0 }, frames[2][177..180]);
        Assert.Equal(new byte[] { 80, 40, 0 }, frames[2][..3]);
        Assert.Equal(0, frames[2][180]);
    }

    [Fact]
    public void Render_NeverSendsStrobe()
    {
        var rig = Parse(Head + OnePar + ""","scenes":[{"note":36,"set":{"*":{"dimmer":255,"strobe":255}}}]}""");

        var frame = LightRenderer.Render(rig, rig.SceneFor(36)!.Look)[1];

        Assert.Equal(255, frame[0]);
        Assert.Equal(0, frame[4]);
    }

    [Fact]
    public void Render_MasterScalesLight_NotOtherChannels()
    {
        var rig = Parse(Head + """ "fixtures":[{"id":"par","address":1,"channels":["dimmer","red","program"]}],"scenes":[{"note":36,"set":{"par":{"dimmer":255,"red":100,"program":42}}}]}""");

        var frame = LightRenderer.Render(rig, rig.SceneFor(36)!.Look, master: 128)[1];

        Assert.Equal(new byte[] { 128, 50, 42 }, frame[..3]);
        Assert.All(LightRenderer.Render(rig, rig.SceneFor(36)!.Look, master: 0)[1][..2], b => Assert.Equal(0, b));
    }

    [Fact]
    public void DefaultSafeLook_IsLitAndSteady()
    {
        var rig = Parse(Head + OnePar + "}");

        var frame = LightRenderer.Render(rig, rig.SafeLook)[1];

        Assert.True(frame[0] > 0, "safe look must not be dark");
        Assert.Equal(0, frame[4]);
        Assert.All(LightRenderer.Render(rig, LightLook.Blackout)[1], b => Assert.Equal(0, b));
    }

    [Fact]
    public void DataPacket_HasTheE131Layout()
    {
        var cid = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
        var slots = new byte[512];
        slots[0] = 255;
        slots[511] = 7;

        var p = E131.DataPacket(cid, "MetroDom test", universe: 258, sequence: 9, slots, priority: 120);

        Assert.Equal(638, p.Length);
        Assert.Equal(new byte[] { 0x00, 0x10, 0x00, 0x00 }, p[..4]);
        Assert.Equal("ASC-E1.17\0\0\0"u8.ToArray(), p[4..16]);
        Assert.Equal(0x7000 | 622, BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(16)));
        Assert.Equal(4u, BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(18)));
        Assert.Equal(new byte[] { 0x00, 0x11, 0x22, 0x33 }, p[22..26]); // CID in network order
        Assert.Equal(0x7000 | 600, BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(38)));
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(40)));
        Assert.Equal((byte)'M', p[44]);
        Assert.Equal(0, p[44 + 13]); // name is zero-terminated
        Assert.Equal(120, p[108]);
        Assert.Equal(9, p[111]);
        Assert.Equal(0, p[112]);
        Assert.Equal(258, BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(113)));
        Assert.Equal(0x7000 | 523, BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(115)));
        Assert.Equal(new byte[] { 0x02, 0xA1, 0x00, 0x00, 0x00, 0x01, 0x02, 0x01, 0x00 }, p[117..126]);
        Assert.Equal(255, p[126]);
        Assert.Equal(7, p[637]);
    }

    [Fact]
    public void DataPacket_RoundTrips_WithTheTerminatedFlag()
    {
        var cid = Guid.NewGuid();
        var slots = Enumerable.Range(0, 512).Select(i => (byte)i).ToArray();

        Assert.True(E131.TryParse(E131.DataPacket(cid, "rig", 1, 255, slots, terminated: true), out var packet));

        Assert.Equal(cid, packet.Cid);
        Assert.Equal("rig", packet.SourceName);
        Assert.Equal(100, packet.Priority);
        Assert.Equal(255, packet.Sequence);
        Assert.True(packet.Terminated);
        Assert.Equal(1, packet.Universe);
        Assert.Equal(slots, packet.Data);
        Assert.False(E131.TryParse(new byte[638], out _));
        Assert.False(E131.TryParse("hello"u8, out _));
    }

    [Fact]
    public void DataPacket_TruncatesLongNames_AndRefusesBadUniverses()
    {
        var p = E131.DataPacket(Guid.Empty, new string('n', 100), 1, 0, []);
        Assert.True(E131.TryParse(p, out var packet));
        Assert.Equal(63, packet.SourceName.Length);

        Assert.Throws<ArgumentOutOfRangeException>(() => E131.DataPacket(Guid.Empty, "x", 0, 0, []));
        Assert.Throws<ArgumentOutOfRangeException>(() => E131.DataPacket(Guid.Empty, "x", 64_000, 0, []));
        Assert.Throws<ArgumentOutOfRangeException>(() => E131.DataPacket(Guid.Empty, "x", 1, 0, [], priority: 201));
    }

    [Theory]
    [InlineData(1, "239.255.0.1")]
    [InlineData(258, "239.255.1.2")]
    [InlineData(63999, "239.255.249.255")]
    public void MulticastAddress_IsHiLo(int universe, string address) =>
        Assert.Equal(address, E131.MulticastAddress(universe));

    [GeneratedRegex("```json\\r?\\n(.*?)```", RegexOptions.Singleline)] // CRLF on a Windows checkout
    private static partial Regex JsonBlock();
}

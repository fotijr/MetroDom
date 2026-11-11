using Melanchall.DryWetMidi.Core;
using MetroDom.Core.Discovery;

namespace MetroDom.Core.Tests;

public class ProfileExchangeTests
{
    private const string DocExample = """
        {
          "schema": "metrodom.instrument-profile/0",
          "id": "xylophone-solenoid",
          "name": "Xylophone (solenoid)",
          "firmwareVersion": "0.1.0",
          "midi": { "channel": 1, "identity": { "manufacturer": "7D", "family": 8781, "model": 1 } },
          "notes": [
            { "note": 60, "latencyMs": 9,  "minIntervalMs": 60 },
            { "note": 64, "latencyMs": 10, "minIntervalMs": 60 },
            { "note": 67, "latencyMs": 9,  "minIntervalMs": 80 }
          ],
          "polyphony": 3,
          "velocity": { "supported": false },
          "safety": { "maxOnMs": 45, "maxDutyPercent": 30, "watchdogMs": 500 }
        }
        """;

    private static byte[] Bytes(SysExEvent e) => [0xF0, .. e.Data];

    [Fact]
    public void Parse_ReadsTheDocumentedExample()
    {
        Assert.True(InstrumentProfileJson.TryParse(DocExample, out var profile, out var error), error);

        Assert.Equal("Xylophone (solenoid)", profile.Name);
        Assert.Equal([60, 64, 67], profile.Notes!.Order());
        Assert.Equal(TimeSpan.FromMilliseconds(9), profile.LatencyFor(60));
        Assert.Equal(TimeSpan.FromMilliseconds(10), profile.MaxLatency);
        Assert.Equal(TimeSpan.FromMilliseconds(80), profile.MinInterval); // slowest note wins
        Assert.False(profile.CanPlay(62));
    }

    [Theory]
    [InlineData("not json", "not valid JSON")]
    [InlineData("[]", "not a JSON object")]
    [InlineData("""{"name":"x"}""", "missing \"schema\"")]
    [InlineData("""{"schema":"metrodom.instrument-profile/9","name":"x"}""", "is not metrodom.instrument-profile/0")]
    [InlineData("""{"schema":"metrodom.instrument-profile/0"}""", "missing \"name\"")]
    [InlineData("""{"schema":"metrodom.instrument-profile/0","name":"x","notes":[{"note":200}]}""", "from 0 to 127")]
    [InlineData("""{"schema":"metrodom.instrument-profile/0","name":"x","notes":[{"note":1},{"note":1}]}""", "listed twice")]
    [InlineData("""{"schema":"metrodom.instrument-profile/0","name":"x","latencyMs":"fast"}""", "wrong type")]
    [InlineData("""{"schema":"metrodom.instrument-profile/0","name":"x","latencyMs":-1}""", "negative")]
    public void Parse_RejectsBadProfiles_WithAReason(string json, string reason)
    {
        Assert.False(InstrumentProfileJson.TryParse(json, out _, out var error));
        Assert.Contains(reason, error, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_RoundTrips_WithAndWithoutNotes()
    {
        var limited = new InstrumentProfile("drüms", TimeSpan.FromMilliseconds(35))
        {
            Notes = new HashSet<int> { 36, 38 },
            NoteLatency = new Dictionary<int, TimeSpan> { [36] = TimeSpan.FromMilliseconds(40) },
            MinInterval = TimeSpan.FromMilliseconds(50),
        };
        var any = new InstrumentProfile("xylo", TimeSpan.FromMilliseconds(10.5));

        foreach (var original in new[] { limited, any })
        {
            var json = InstrumentProfileJson.Write(original);
            Assert.All(json, c => Assert.True(c <= 0x7F, "ASCII only, so it fits SysEx"));
            Assert.True(InstrumentProfileJson.TryParse(json, out var parsed, out var error), error);
            Assert.Equal(original.Name, parsed.Name);
            Assert.Equal(original.Latency, parsed.Latency);
            Assert.Equal(original.MinInterval, parsed.MinInterval);
            Assert.Equal(original.Notes?.Order(), parsed.Notes?.Order());
            Assert.Equal(original.NoteLatency, parsed.NoteLatency);
        }
    }

    [Fact]
    public void Request_HasTheDocumentedBytes()
    {
        Assert.Equal(new byte[] { 0xF0, 0x7D, (byte)'M', (byte)'D', 0x01, 0xF7 }, Bytes(ProfileExchange.Request()));
        Assert.True(ProfileExchange.IsRequest(ProfileExchange.Request()));
        Assert.False(ProfileExchange.IsRequest(DeviceIdentity.Request()));
        Assert.False(ProfileExchange.IsRequest(new NormalSysExEvent([0x7D, 0x01, 0x02, 0x01, 0xF7]))); // someone else's 7D
    }

    [Fact]
    public void Reply_SplitsIntoNumberedChunks_ThatReassembleInAnyOrder()
    {
        var json = InstrumentProfileJson.Write(new InstrumentProfile("big", TimeSpan.FromMilliseconds(9))
        {
            Notes = Enumerable.Range(21, 88).ToHashSet(),
        });
        var chunks = ProfileExchange.Reply(json);

        Assert.True(chunks.Count > 1, "an 88-note profile needs several messages");
        Assert.All(chunks, c => Assert.True(c.Data.Length <= ProfileExchange.ChunkSize + 7));
        Assert.Equal(new byte[] { 0xF0, 0x7D, 0x4D, 0x44, 0x02, 0x00, (byte)chunks.Count }, Bytes(chunks[0])[..7]);
        Assert.All(chunks.SelectMany(c => c.Data[..^1]), b => Assert.True(b <= 0x7F));

        var assembler = new ProfileAssembler();
        var completed = chunks.Reverse().Select(assembler.Add).ToList();
        Assert.Equal([.. Enumerable.Repeat(false, chunks.Count - 1), true], completed);
        Assert.Equal(json, assembler.Text);
    }

    [Fact]
    public void Assembler_IgnoresOtherMessages_AndRestartsOnANewTotal()
    {
        var assembler = new ProfileAssembler();
        Assert.False(assembler.Add(ProfileExchange.Request()));
        Assert.False(assembler.Add(DeviceIdentity.Request()));

        // First chunk of a 2-part answer, then the instrument restarts with a 1-part answer.
        Assert.False(assembler.Add(new NormalSysExEvent([0x7D, 0x4D, 0x44, 0x02, 0, 2, (byte)'a', 0xF7])));
        Assert.True(assembler.Add(new NormalSysExEvent([0x7D, 0x4D, 0x44, 0x02, 0, 1, (byte)'b', 0xF7])));
        Assert.Equal("b", assembler.Text);
    }

    [Fact]
    public void Reply_RejectsNonAsciiAndOversizedText()
    {
        Assert.Throws<ArgumentException>(() => ProfileExchange.Reply("é"));
        Assert.Throws<ArgumentException>(() => ProfileExchange.Reply(new string('x', (ProfileExchange.MaxChunks * ProfileExchange.ChunkSize) + 1)));
    }
}

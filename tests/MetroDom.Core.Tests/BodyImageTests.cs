using Melanchall.DryWetMidi.Core;
using MetroDom.Core.Bodies;
using MetroDom.Core.Calibration;

namespace MetroDom.Core.Tests;

public class BodyImageTests
{
    private static string BodyFile(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AGENTS.md")))
        {
            dir = dir.Parent;
        }

        return Path.Combine(dir!.FullName, "Instruments", "metrodom_brain", "bodies", name);
    }

    private static BodyDefinition M1()
    {
        Assert.True(BodyImage.TryParseJson(File.ReadAllText(BodyFile("m1-xylophone.json")), out var body, out var error), error);
        return body;
    }

    [Fact]
    public void M1Body_EncodesToTheBytesTheFirmwareTestCommitted()
    {
        // brain_test.cpp body_MatchesTheCommittedFixture builds the same body in C++ and checks this file.
        var hex = File.ReadAllText(BodyFile("m1-xylophone.hex")).Trim();

        Assert.Equal(hex, BodyImage.ToHex(BodyImage.Encode(M1())));
    }

    private static BodyDefinition M1Accent()
    {
        Assert.True(BodyImage.TryParseJson(File.ReadAllText(BodyFile("m1-xylophone-accent.json")), out var body, out var error), error);
        return body;
    }

    [Fact]
    public void AccentBody_EncodesAsVersion2_ToTheBytesTheFirmwareTestCommitted()
    {
        // brain_test.cpp accent_RoundTripsAsVersion2_AndMatchesTheFixture checks the same file.
        var hex = File.ReadAllText(BodyFile("m1-xylophone-accent.hex")).Trim();
        var record = BodyImage.Encode(M1Accent());

        Assert.Equal(hex, BodyImage.ToHex(record));
        Assert.Equal(2, record[3]);
        Assert.Equal(1, BodyImage.Encode(M1())[3]);
        Assert.True(BodyImage.TryDecode(record, out var back, out var error), error);
        Assert.Equal(new AccentStrip(9, 20, 80), back.Accent);
        Assert.Equal(new AccentLed(6, 3, 0x0080FF), back.Notes[2].Led);
        Assert.Equal(record, BodyImage.Encode(back));
        Assert.Equal(BodyImage.ToJson(M1Accent()), BodyImage.ToJson(back));
    }

    [Theory]
    [InlineData("\"maxBrightnessPercent\": 20", "\"maxBrightnessPercent\": 26", "\"accent.maxBrightnessPercent\" must be 1-25 (the firmware's ceiling).")]
    [InlineData("\"flashMs\": 80", "\"flashMs\": 5", "\"accent.flashMs\" must be 10-1000.")]
    [InlineData("\"leds\": 9", "\"leds\": 65", "\"accent.leds\" must be 1-64.")]
    [InlineData("\"first\": 6", "\"first\": 7", "note 76: its LEDs must be on the strip (0-8).")]
    [InlineData("\"first\": 3", "\"first\": 2", "note 74: its LEDs overlap note 72's.")]
    [InlineData("\"color\": \"#FF4000\"", "\"color\": \"orange\"", "note 72: \"led\" needs \"first\", \"count\" and a \"color\" like \"#FF8000\".")]
    public void AccentJson_RefusesWhatTheFirmwareWouldRefuse(string find, string replace, string expected)
    {
        var json = File.ReadAllText(BodyFile("m1-xylophone-accent.json")).Replace(find, replace, StringComparison.Ordinal);

        Assert.False(BodyImage.TryParseJson(json, out _, out var error));
        Assert.Equal(expected, error);
    }

    [Fact]
    public void Accent_LedsNeedAStrip_AndGpio7IsTheStripsOnABodyThatHasOne()
    {
        var noStrip = M1Accent() with { Accent = null };
        Assert.Equal("note 72: \"led\" needs an \"accent\" strip.", BodyImage.Validate(noStrip));

        var gpio = M1() with { Driver = BodyDriver.Gpio, Address = 0 };
        gpio = gpio with { Notes = [gpio.Notes[0] with { Output = 7 }, .. gpio.Notes.Skip(1)] };
        Assert.Null(BodyImage.Validate(gpio));
        Assert.Equal("note 72: GPIO 7 drives the accent LEDs on a body that has them.", BodyImage.Validate(gpio with { Accent = new AccentStrip(4, 10, 80) }));

        var record = BodyImage.Encode(M1Accent());
        record[20 + (3 * 10) + 19 + 18] = 0; // the strip's LED count
        var crc = BodyImage.Crc16(record.AsSpan(0, record.Length - 2));
        record[^2] = (byte)crc;
        record[^1] = (byte)(crc >> 8);
        Assert.False(BodyImage.TryDecode(record, out _, out var error));
        Assert.Equal("bad accent LEDs.", error);
    }

    [Fact]
    public void AccentProfile_IsWhatTheBrainSends_AndRoundTrips()
    {
        var profile = M1Accent().ToProfile();
        Assert.Equal(new AccentStrip(9, 20, 80), profile.Accent);
        Assert.Equal(new AccentLed(3, 3, 0xFFC000), profile.NoteLed[74]);

        // The brain's own wording (Brain.cpp buildProfile).
        const string brain = """{"schema":"metrodom.instrument-profile/0","id":"xylophone-m1-accent","name":"MetroDom xylophone","firmwareVersion":"0.1.0","midi":{"identity":{"manufacturer":"7D","family":8781,"model":1}},"notes":[{"note":72,"led":{"first":0,"count":3,"color":"#FF4000"},"minIntervalMs":60,"minPulseMs":12,"pulseMs":30}],"accent":{"leds":9,"maxBrightnessPercent":20,"flashMs":80},"polyphony":2,"velocity":{"supported":true},"safety":{"maxOnMs":45,"maxDutyPercent":30,"dutyWindowMs":1000,"watchdogMs":500}}""";
        Assert.True(InstrumentProfileJson.TryParse(brain, out var sent, out var error), error);
        Assert.Equal(profile.Accent, sent.Accent);
        Assert.Equal(profile.NoteLed[72], sent.NoteLed[72]);

        Assert.True(InstrumentProfileJson.TryParse(InstrumentProfileJson.Write(profile), out var back, out error), error);
        Assert.Equal(profile.Accent, back.Accent);
        Assert.Equal(profile.NoteLed.OrderBy(p => p.Key), back.NoteLed.OrderBy(p => p.Key));
    }

    [Fact]
    public void Decode_GivesBackTheDefinition_AndIgnoresTheRestOfTheEeprom()
    {
        var record = BodyImage.Encode(M1());
        byte[] eeprom = [.. record, .. Enumerable.Repeat((byte)0xFF, 200)];

        Assert.True(BodyImage.TryDecode(eeprom, out var body, out var error), error);
        Assert.Equal(record, BodyImage.Encode(body));
        Assert.Equal(BodyDriver.Pca9685, body.Driver);
        Assert.Equal(0x40, body.Address);
        Assert.Equal(new BodyActuator(76, 2, 30, 12, 60, 0), body.Notes[2]);
        Assert.Null(body.Channel);
        Assert.True(body.Velocity);
    }

    [Fact]
    public void Decode_SaysWhatsWrong()
    {
        var record = BodyImage.Encode(M1());
        var flipped = (byte[])record.Clone();
        flipped[25] ^= 1;

        Assert.False(BodyImage.TryDecode(flipped, out _, out var error));
        Assert.Equal("bad CRC.", error);
        Assert.False(BodyImage.TryDecode(record.AsSpan(0, record.Length - 1), out _, out error));
        Assert.Equal("bad length.", error);
        Assert.False(BodyImage.TryDecode(new byte[64], out _, out error));
        Assert.Equal("not a body record.", error);
    }

    public static TheoryData<string, string, string> Broken => new()
    {
        { "\"address\": \"0x40\"", "\"address\": \"0x20\"", "address must be 0x40-0x7F" },
        { "\"maxOnMs\": 45", "\"maxOnMs\": 251", "maxOnMs\" must be 1-250" },
        { "\"maxDutyPercent\": 30", "\"maxDutyPercent\": 51", "maxDutyPercent\" must be 1-50" },
        { "\"watchdogMs\": 500", "\"watchdogMs\": 100", "watchdogMs\" must be 200-8000" },
        { "\"note\": 76, \"output\": 2", "\"note\": 76, \"output\": 1", "output 1 must be 0-15 and used by one note only" },
        { "\"note\": 72", "\"note\": 74", "note 74: notes must be 0-127 and each listed once" },
        { "\"note\": 72, \"output\": 0, \"pulseMs\": 30, \"minPulseMs\": 12", "\"note\": 72, \"output\": 0, \"pulseMs\": 30, \"minPulseMs\": 31", "can't be more than \"pulseMs\"" },
        { "\"minPulseMs\": 12, \"minIntervalMs\": 60 },\n    { \"note\": 74", "\"minPulseMs\": 12, \"minIntervalMs\": 20 },\n    { \"note\": 74", "at least \"pulseMs\"" },
        { "\"id\": \"xylophone-m1\"", "\"id\": \"Xylo\"", "\"id\" must be" },
        { "\"type\": \"pca9685\"", "\"type\": \"servo\"", "\"driver.type\"" },
        { "\"schema\": \"metrodom.body/0\"", "\"schema\": \"metrodom.instrument-profile/0\"", "is not metrodom.body/0" },
    };

    [Theory]
    [MemberData(nameof(Broken))]
    public void Json_RefusesWhatTheFirmwareWouldRefuse(string find, string replace, string expected)
    {
        var json = File.ReadAllText(BodyFile("m1-xylophone.json")).ReplaceLineEndings("\n");
        Assert.Contains(find, json, StringComparison.Ordinal);

        Assert.False(BodyImage.TryParseJson(json.Replace(find, replace, StringComparison.Ordinal), out _, out var error));
        Assert.Contains(expected, error, StringComparison.Ordinal);
    }

    [Fact]
    public void GpioBodies_KeepOffTheBackplanesPins()
    {
        var gpio = M1() with { Driver = BodyDriver.Gpio, Address = 0 };
        Assert.Null(BodyImage.Validate(gpio with { Notes = [gpio.Notes[0] with { Output = 28 }] }));
        foreach (var pin in new[] { 4, 5, 6 })
        {
            Assert.Contains("taken by the backplane", BodyImage.Validate(gpio with { Notes = [gpio.Notes[0] with { Output = pin }] }), StringComparison.Ordinal);
        }

        Assert.Null(BodyImage.Validate(M1() with { Notes = [M1().Notes[0] with { Output = 6 }] })); // a PCA9685 channel 6 is fine
    }

    [Fact]
    public void Json_AcceptsANumericAddress_AndDefaultsThePca9685s()
    {
        var json = File.ReadAllText(BodyFile("m1-xylophone.json"));

        Assert.True(BodyImage.TryParseJson(json.Replace("\"address\": \"0x40\"", "\"address\": 65", StringComparison.Ordinal), out var body, out _));
        Assert.Equal(0x41, body.Address);
        Assert.True(BodyImage.TryParseJson(json.Replace(", \"address\": \"0x40\"", "", StringComparison.Ordinal), out body, out _));
        Assert.Equal(0x40, body.Address);
    }

    [Fact]
    public void Profile_IsWhatTheBrainWillSend()
    {
        var profile = M1().ToProfile();
        var json = InstrumentProfileJson.Write(profile, "xylophone-m1");

        Assert.True(InstrumentProfileJson.TryParse(json, out var back, out var error), error);
        Assert.Equal("MetroDom xylophone", back.Name);
        Assert.Equal([72, 74, 76], back.Notes!.Order());
        Assert.Equal(2, back.Polyphony);
        Assert.Equal(TimeSpan.FromMilliseconds(30), back.PulseFor(74));
        Assert.Equal(TimeSpan.FromMilliseconds(60), back.MinInterval);
        Assert.Equal(TimeSpan.FromMilliseconds(60), back.IntervalFor(76));
        Assert.Equal(new ActuatorSafety(TimeSpan.FromMilliseconds(45), 30, TimeSpan.FromSeconds(1)), back.Safety);
    }

    [Fact]
    public void BodyWrite_ChunksAreWhatTheBrainReads_AndTheReceiverStoresThem()
    {
        var record = BodyImage.Encode(M1());
        var chunks = BodyWrite.Chunks(record);
        Assert.Equal(2, chunks.Count);
        Assert.Equal([0x7D, 0x4D, 0x44, 0x04, 0, 2, (byte)'4', (byte)'D'], chunks[0].Data.Take(8));
        Assert.Equal(0xF7, chunks[1].Data[^1]);
        Assert.All(chunks, c => Assert.All(c.Data, b => Assert.True(b <= 0x7F || b == 0xF7)));

        var receiver = new BodyReceiver();
        Assert.Null(receiver.Add(chunks[0], out _));
        var reply = receiver.Add(chunks[1], out var written);
        Assert.True(BodyWrite.TryParseReply(reply!, out var parsed));
        Assert.Equal(BodyWriteStatus.Written, parsed.Status);
        Assert.Equal(record, BodyImage.Encode(written!));
        Assert.Contains("restarting as the new body", parsed.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void BodyWrite_OutOfOrder_IsABadMessage()
    {
        var chunks = BodyWrite.Chunks(BodyImage.Encode(M1()));
        var receiver = new BodyReceiver();

        Assert.True(BodyWrite.TryParseReply(receiver.Add(chunks[1], out _)!, out var reply));
        Assert.Equal(BodyWriteStatus.BadMessage, reply.Status);
        Assert.Null(receiver.Add(new NoteOnEvent(), out _));
    }

    [Fact]
    public void BodyWriteReply_SaysWhyInTheFirmwaresWords()
    {
        // As the firmware sends a CRC refusal (brain_test.cpp bodyWrite_RefusesBadRecords_AndKeepsPlaying).
        var reply = new NormalSysExEvent([0x7D, 0x4D, 0x44, 0x05, 2, 5, 0xF7]);

        Assert.True(BodyWrite.TryParseReply(reply, out var parsed));
        Assert.Equal(BodyWriteStatus.Invalid, parsed.Status);
        Assert.Equal("the brain refused the record: bad CRC; nothing was stored", parsed.Describe());
        Assert.Contains("write button held", new BodyWriteReply(BodyWriteStatus.Failed, 0).Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void ToJson_ReadsBackToTheSameBody()
    {
        var m1 = M1() with { Channel = 3, Notes = [M1().Notes[0] with { LatencyMs = 13 }, .. M1().Notes.Skip(1)] };

        Assert.True(BodyImage.TryParseJson(BodyImage.ToJson(m1), out var back, out var error), error);
        Assert.Equal(BodyImage.Encode(m1), BodyImage.Encode(back));
        Assert.Contains("\"address\": \"0x40\"", BodyImage.ToJson(m1), StringComparison.Ordinal);
    }

    [Fact]
    public void WithMeasured_RoundsToWholeMilliseconds_AndFillsNotesWithoutTheirOwn()
    {
        var measured = new MeasuredLatency("MetroDom xylophone", DateTimeOffset.UnixEpoch, TimeSpan.FromMilliseconds(14.2),
            new Dictionary<int, TimeSpan> { [72] = TimeSpan.FromMilliseconds(12.5), [74] = TimeSpan.FromMilliseconds(13.4) });

        var body = BodyImage.WithMeasured(M1(), measured);

        Assert.Equal([13, 13, 14], body.Notes.Select(n => n.LatencyMs));
        Assert.Null(BodyImage.Validate(body));
        Assert.Equal(TimeSpan.FromMilliseconds(14), body.ToProfile().LatencyFor(76));
    }

    [Fact]
    public void BodyRead_ReassemblesTheRecord_InAnyOrder_OrSaysThereIsNone()
    {
        var record = BodyImage.Encode(M1());
        var chunks = BodyWrite.ReadReply(record);
        Assert.Equal(2, chunks.Count);

        var assembler = new BodyReadAssembler();
        Assert.False(assembler.Add(chunks[1]));
        Assert.True(assembler.Add(chunks[0]));
        Assert.Equal(record, assembler.Record);

        var none = new BodyReadAssembler();
        Assert.True(none.Add(Assert.Single(BodyWrite.ReadReply(null))));
        Assert.True(none.Done);
        Assert.Null(none.Record);

        // As the firmware sends "no record" (brain_test.cpp bodyRead_SendsTheRecordItBootedFrom).
        var firmware = new BodyReadAssembler();
        Assert.True(firmware.Add(new NormalSysExEvent([0x7D, 0x4D, 0x44, 0x07, 0, 0, 0xF7])));
        Assert.Null(firmware.Record);
        Assert.True(BodyWrite.IsReadRequest(BodyWrite.ReadRequest()));
    }

    [Fact]
    public void Receiver_RemembersTheRecordItWrote()
    {
        var receiver = new BodyReceiver();
        Assert.Null(receiver.Record);
        foreach (var chunk in BodyWrite.Chunks(BodyImage.Encode(M1())))
        {
            receiver.Add(chunk, out _);
        }

        Assert.Equal(BodyImage.Encode(M1()), receiver.Record);
    }
}

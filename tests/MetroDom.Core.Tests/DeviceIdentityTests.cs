using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using MetroDom.Core.Discovery;

namespace MetroDom.Core.Tests;

public class DeviceIdentityTests
{
    private static byte[] Bytes(SysExEvent e) => [0xF0, .. e.Data];

    [Fact]
    public void Request_IsTheUniversalIdentityRequest() =>
        Assert.Equal(new byte[] { 0xF0, 0x7E, 0x7F, 0x06, 0x01, 0xF7 }, Bytes(DeviceIdentity.Request()));

    [Fact]
    public void IsRequest_MatchesAllDevicesOrOurId_Only()
    {
        Assert.True(DeviceIdentity.IsRequest(DeviceIdentity.Request()));
        Assert.True(DeviceIdentity.IsRequest(new NormalSysExEvent([0x7E, 0x10, 0x06, 0x01, 0xF7]), deviceId: 0x10));
        Assert.False(DeviceIdentity.IsRequest(new NormalSysExEvent([0x7E, 0x10, 0x06, 0x01, 0xF7]), deviceId: 0x11));
        Assert.False(DeviceIdentity.IsRequest(new NormalSysExEvent([0x7E, 0x7F, 0x06, 0x02, 0xF7])));
        Assert.False(DeviceIdentity.IsRequest(new NoteOnEvent((SevenBitNumber)60, (SevenBitNumber)100)));
    }

    [Fact]
    public void MetroDomReply_HasTheDocumentedBytes()
    {
        var identity = DeviceIdentity.MetroDom(DeviceIdentity.VirtualModel, new Version(0, 1, 2));

        // F0 7E 7F 06 02 | 7D | "M" "D" | model 0 0 | version 0 1 2 0 | F7
        Assert.Equal(
            new byte[] { 0xF0, 0x7E, 0x7F, 0x06, 0x02, 0x7D, (byte)'M', (byte)'D', 0x00, 0x00, 0, 1, 2, 0, 0xF7 },
            Bytes(identity.ToReply()));
        Assert.True(identity.IsMetroDom);
        Assert.Equal("MetroDom virtual instrument v0.1.2.0", identity.Describe());
    }

    [Fact]
    public void Reply_RoundTrips_WithOneAndThreeByteManufacturers()
    {
        foreach (var identity in new[]
        {
            new DeviceIdentity("41", 0x0123, 0x2001, "1.2.3.4"),
            new DeviceIdentity("00 20 29", 5, 300, "0.0.0.9"),
            DeviceIdentity.MetroDom(7, new Version(1, 0)),
        })
        {
            Assert.True(DeviceIdentity.TryParseReply(identity.ToReply(), out var parsed));
            Assert.Equal(identity, parsed);
        }
    }

    [Fact]
    public void ParsesAThirdPartyReply()
    {
        // A Roland-style reply from device 0x10: family 0x0123 (LSB first), model 2, v1.0.0.0.
        var reply = new NormalSysExEvent([0x7E, 0x10, 0x06, 0x02, 0x41, 0x23, 0x02, 0x02, 0x00, 0x01, 0x00, 0x00, 0x00, 0xF7]);

        Assert.True(DeviceIdentity.TryParseReply(reply, out var identity));
        Assert.Equal(new DeviceIdentity("41", 0x23 | (0x02 << 7), 2, "1.0.0.0"), identity);
        Assert.False(identity.IsMetroDom);
        Assert.Equal("Roland", identity.ManufacturerName);
        Assert.Equal("Roland · family 291 model 2 · v1.0.0.0", identity.Describe());
    }

    [Fact]
    public void NonCommercialIdWithAnotherFamily_IsNotMetroDom()
    {
        var someoneElse = new DeviceIdentity(DeviceIdentity.NonCommercialId, 1, 1, "0.0.0.1");
        Assert.False(someoneElse.IsMetroDom);
        Assert.StartsWith("Non-commercial", someoneElse.Describe(), StringComparison.Ordinal);
        Assert.StartsWith("manufacturer 00 7F 7F", new DeviceIdentity("00 7F 7F", 1, 1, "0.0.0.0").Describe(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(new byte[] { 0x7E, 0x7F, 0x06, 0x02, 0x41, 0x01, 0xF7 })] // too short
    [InlineData(new byte[] { 0x7E, 0x7F, 0x06, 0x02, 0x41, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0xF7 })] // too long
    [InlineData(new byte[] { 0x7E, 0x7F, 0x06, 0x01, 0xF7 })] // a request
    [InlineData(new byte[] { 0x7D, 0x7F, 0x06, 0x02, 0x41, 0, 0, 0, 0, 0, 0, 0, 0, 0xF7 })] // not universal
    public void RejectsWhatIsNotAReply(byte[] data) =>
        Assert.False(DeviceIdentity.TryParseReply(new NormalSysExEvent(data), out _));

    [Fact]
    public void ReplyTo_AnswersOnlyRequests()
    {
        var identity = DeviceIdentity.MetroDom(0, new Version(1, 0));
        Assert.NotNull(identity.ReplyTo(DeviceIdentity.Request()));
        Assert.Null(identity.ReplyTo(identity.ToReply()));
    }
}

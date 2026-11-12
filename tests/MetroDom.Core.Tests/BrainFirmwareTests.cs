using Melanchall.DryWetMidi.Core;
using MetroDom.Core.Discovery;

namespace MetroDom.Core.Tests;

/// <summary>
/// The brain firmware (Instruments/metrodom_brain) speaks what the Conductor reads. Its host tests
/// check it generates exactly bench-profile.json and these identity bytes; here the Conductor's
/// own readers take them.
/// </summary>
public class BrainFirmwareTests
{
    private static string BrainFile(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AGENTS.md")))
        {
            dir = dir.Parent;
        }

        return Path.Combine(dir!.FullName, "Instruments", "metrodom_brain", name);
    }

    [Fact]
    public void BenchProfile_ParsesWithTheConductorsReader()
    {
        var json = File.ReadAllText(BrainFile("bench-profile.json"));

        Assert.True(InstrumentProfileJson.TryParse(json, out var profile, out var error), error);
        Assert.Equal("Bench solenoid", profile.Name);
        Assert.Equal([60], profile.Notes!.Order());
        Assert.Equal(TimeSpan.FromMilliseconds(60), profile.MinInterval);
        Assert.Null(profile.Channel); // omni: the auto band picks a free channel
        Assert.Equal(TimeSpan.FromMilliseconds(30), profile.PulseFor(60));
        Assert.Equal(1, profile.Polyphony);
        Assert.Equal(new ActuatorSafety(TimeSpan.FromMilliseconds(45), 30, TimeSpan.FromSeconds(1)), profile.Safety);
    }

    [Fact]
    public void IdentityReply_IsAMetroDomInstrument_Model1()
    {
        // As the firmware sends it (brain_test.cpp identityRequest_GetsMetroDomsReply), without F0.
        var reply = new NormalSysExEvent([0x7E, 0x7F, 0x06, 0x02, 0x7D, 0x4D, 0x44, 0x01, 0x00, 0, 2, 0, 0, 0xF7]);

        Assert.True(DeviceIdentity.TryParseReply(reply, out var identity));
        Assert.True(identity.IsMetroDom);
        Assert.Equal(DeviceIdentity.BrainModel, identity.Model);
        Assert.Equal("MetroDom brain v0.2.0.0", identity.Describe());
    }
}

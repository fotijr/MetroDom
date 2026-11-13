using Melanchall.DryWetMidi.Core;
using MetroDom.Conductor.Midi;
using MetroDom.Core.Discovery;

namespace MetroDom.Conductor.Tests;

public class PanicTests
{
    private static readonly DeviceIdentity Brain = DeviceIdentity.MetroDom(1, new Version(0, 1));
    private static readonly DeviceIdentity Piano = new("43", 0x41, 2, "1.0.0.0");

    [Fact]
    public void EveryPortGetsTheCcs_AndOnlyBrainsGetTheAllOff()
    {
        var backend = new DeviceBackend();
        backend.Add("xylo", answer: Brain);
        backend.Add("piano", answer: Piano);
        backend.Add("synth", hasInput: false);

        var results = Panicker.Send(backend, backend.Outputs(), TimeSpan.FromMilliseconds(100));

        Assert.Equal([true, false, false], results.Select(r => r.AllOff));
        Assert.All(results, r => Assert.Null(r.Error));
        foreach (var name in new[] { "xylo", "piano", "synth" })
        {
            Assert.Equal(32, backend.Sent[name].Count(e => e is ControlChangeEvent));
        }

        Assert.Equal(1, backend.Sent["xylo"].Count(ProfileExchange.IsAllOff));
        Assert.DoesNotContain(backend.Sent["piano"], ProfileExchange.IsAllOff);
        Assert.DoesNotContain(backend.Sent["synth"], ProfileExchange.IsAllOff);
    }

    [Fact]
    public void ThePanicGoesOutBeforeAnyQuestionIsAsked()
    {
        var backend = new DeviceBackend();
        backend.Add("xylo", answer: Brain);

        Panicker.Send(backend, backend.Outputs(), TimeSpan.FromMilliseconds(100));

        Assert.IsType<ControlChangeEvent>(backend.Sent["xylo"].First());
    }

    [Fact]
    public void APortThatWontOpen_DoesNotStopTheOthers()
    {
        var backend = new DeviceBackend();
        backend.Add("xylo", answer: Brain);
        MidiPortInfo[] ports = [new("unplugged", "unplugged"), .. backend.Outputs()];

        var results = Panicker.Send(backend, ports, TimeSpan.FromMilliseconds(100));

        Assert.NotNull(results[0].Error);
        Assert.False(results[0].AllOff);
        Assert.True(results[1].AllOff);
    }
}

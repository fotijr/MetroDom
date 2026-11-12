using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using MetroDom.Conductor.Cli;
using MetroDom.Conductor.Commands;
using MetroDom.Conductor.Midi;
using MetroDom.Core.Timing;

namespace MetroDom.Conductor.Tests;

public class TempoInputTests
{
    [Fact]
    public void Listen_TapsOnlyOnMatchingPresses()
    {
        using var input = new FakeInput();
        var taps = 0;
        PlayCommand.Listen(input, new TapTrigger(TapKind.ControlChange, 64), () => taps++);

        input.Raise(new ControlChangeEvent((SevenBitNumber)64, (SevenBitNumber)127));
        input.Raise(new ControlChangeEvent((SevenBitNumber)64, (SevenBitNumber)0));
        input.Raise(new NoteOnEvent((SevenBitNumber)60, (SevenBitNumber)100));
        input.Raise(new ControlChangeEvent((SevenBitNumber)64, (SevenBitNumber)127));

        Assert.Equal(2, taps);
    }

    [Fact]
    public void Follow_TurnsClockTicksIntoTempo_AndStopHolds()
    {
        using var input = new FakeInput();
        var clock = new VirtualClock();
        var tempos = new List<double>();
        PlayCommand.Follow(input, new ClockTempo(), clock, tempos.Add);

        var tick = TimeSpan.FromSeconds(60.0 / 128 / ClockTempo.TicksPerBeat);
        for (var i = 0; i < 24; i++)
        {
            input.Raise(new TimingClockEvent());
            clock.Advance(tick);
        }

        input.Raise(new StopEvent());
        clock.Advance(TimeSpan.FromSeconds(5));
        input.Raise(new TimingClockEvent());

        Assert.Equal(128, Assert.Single(tempos), precision: 2); // TimeSpan rounds to 100 ns
    }

    [Theory]
    [InlineData("--tap-on", "pedal")]
    [InlineData("--tap-on", "cc:64")] // without --tap
    public void Play_RejectsBadTapOptions(string option, string value)
    {
        var path = Path.GetTempFileName();
        try
        {
            Assert.Equal(MetroDomApp.UsageError, MetroDomApp.Create().Run(["play", path, "-o", "0", option, value], TestContext.Current.CancellationToken));
            Assert.Equal(MetroDomApp.UsageError, MetroDomApp.Create().Run(["play", path, "-o", "0", "--tap", "0", "--clock-in", "0"], TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Play_LightsPad_NeedsLights_AndItsOwnPort()
    {
        var path = Path.GetTempFileName();
        var rig = Path.GetTempFileName();
        File.WriteAllText(rig, """{"schema":"metrodom.light-profile/0","name":"rig","fixtures":[{"id":"par","address":1,"channels":["dimmer"]}]}""");
        try
        {
            PlayCommand.Settings Settings(string? lights, string? tap) =>
                new() { Paths = [path], Outputs = ["0"], Lights = lights, LightsPad = "1", Tap = tap };
            Assert.True(Settings(rig, null).Validate().Successful);
            Assert.Contains("own", Settings(rig, "1").Validate().Message, StringComparison.Ordinal);
            Assert.Contains("needs --lights", Settings(null, null).Validate().Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
            File.Delete(rig);
        }
    }

    private sealed class FakeInput : IMidiInputPort
    {
        public event Action<MidiEvent>? EventReceived;

        public string Name => "pedal";

        public void Raise(MidiEvent midiEvent) => EventReceived?.Invoke(midiEvent);

        public void Start()
        {
        }

        public void Stop()
        {
        }

        public void Dispose()
        {
        }
    }
}

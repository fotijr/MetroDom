using System.Collections.Concurrent;
using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using MetroDom.Conductor.Midi;

namespace MetroDom.Conductor.Tests;

/// <summary>
/// Real OS round trip for <c>metrodom virtual</c>: create a virtual port, find it as an output
/// like any other program would, send to it, and receive. Linux (ALSA) runs where the sequencer
/// exists, like <see cref="AlsaLoopbackTests"/>. macOS (CoreMIDI) runs locally but skips on hosted
/// CI, whose headless runners aren't known to have a MIDI server (TD-19).
/// </summary>
public class VirtualPortTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    [Fact]
    public void VirtualPort_IsListedAsAnOutput_AndReceivesWhatIsSentToIt()
    {
        var requiredAlsa = Environment.GetEnvironmentVariable("METRODOM_REQUIRE_ALSA") == "1";
        var linux = requiredAlsa || (OperatingSystem.IsLinux() && File.Exists("/dev/snd/seq"));
        var mac = OperatingSystem.IsMacOS() && Environment.GetEnvironmentVariable("CI") is null;
        Assert.SkipUnless(linux || mac, "Needs the ALSA sequencer (Linux) or CoreMIDI outside CI (macOS).");

        var name = $"MetroDom test-{Guid.NewGuid().ToString("N")[..8]}";
        var received = new BlockingCollection<MidiEvent>();
        using var port = MidiDevices.CreateVirtualInput(name);
        port.EventReceived += received.Add;
        port.Start();

        var listed = Assert.Single(MidiDevices.OutputNames(), n => n.Contains(name[9..], StringComparison.Ordinal));
        using var output = MidiDevices.OpenOutput(listed);
        output.Send(new NoteOnEvent((SevenBitNumber)38, (SevenBitNumber)110) { Channel = (FourBitNumber)9 });
        output.Send(new ControlChangeEvent((SevenBitNumber)120, (SevenBitNumber)0) { Channel = (FourBitNumber)9 });

        var note = Assert.IsType<NoteOnEvent>(Next(received));
        Assert.Equal((9, 38, 110), ((int)note.Channel, (int)note.NoteNumber, (int)note.Velocity));
        var cc = Assert.IsType<ControlChangeEvent>(Next(received));
        Assert.Equal(120, (int)cc.ControlNumber);
        port.Stop();
    }

    private static MidiEvent Next(BlockingCollection<MidiEvent> events) =>
        events.TryTake(out var e, Timeout) ? e : throw new TimeoutException("Nothing arrived at the virtual port.");
}

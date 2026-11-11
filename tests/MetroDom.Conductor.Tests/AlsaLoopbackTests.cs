using System.Collections.Concurrent;
using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using MetroDom.Conductor.Midi;

namespace MetroDom.Conductor.Tests;

/// <summary>
/// Real ALSA round trip through the kernel's "Midi Through" port (module snd-seq-dummy).
/// Skipped unless running on Linux with /dev/snd/seq. CI loads the module on ubuntu and sets
/// METRODOM_REQUIRE_ALSA=1 so a missing sequencer fails instead of silently skipping.
/// </summary>
public class AlsaLoopbackTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    [Fact]
    public void MidiThrough_RoundTripsChannelAndSysExEvents()
    {
        var required = Environment.GetEnvironmentVariable("METRODOM_REQUIRE_ALSA") == "1";
        Assert.SkipUnless(required || (OperatingSystem.IsLinux() && File.Exists("/dev/snd/seq")), "Needs Linux with the ALSA sequencer.");
        var outIndex = IndexOfThrough(MidiDevices.OutputNames());
        var inIndex = IndexOfThrough(MidiDevices.InputNames());
        Assert.SkipWhen(!required && (outIndex < 0 || inIndex < 0), "No 'Midi Through' port (modprobe snd-seq-dummy).");
        Assert.True(outIndex >= 0 && inIndex >= 0, "No 'Midi Through' port (modprobe snd-seq-dummy).");

        var received = new BlockingCollection<MidiEvent>();
        using var input = MidiDevices.OpenInput(inIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var output = MidiDevices.OpenOutput(outIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
        input.EventReceived += received.Add;
        input.Start();

        output.Send(new NoteOnEvent((SevenBitNumber)60, (SevenBitNumber)100) { Channel = (FourBitNumber)9 });
        output.Send(new ControlChangeEvent((SevenBitNumber)120, (SevenBitNumber)0) { Channel = (FourBitNumber)3 });
        output.Send(new NormalSysExEvent([0x7E, 0x7F, 0x06, 0x01, 0xF7])); // Identity Request

        var note = Assert.IsType<NoteOnEvent>(Next(received));
        Assert.Equal((9, 60, 100), ((int)note.Channel, (int)note.NoteNumber, (int)note.Velocity));
        var cc = Assert.IsType<ControlChangeEvent>(Next(received));
        Assert.Equal((3, 120), ((int)cc.Channel, (int)cc.ControlNumber));
        var sysEx = Assert.IsType<NormalSysExEvent>(Next(received));
        Assert.Equal(new byte[] { 0x7E, 0x7F, 0x06, 0x01, 0xF7 }, sysEx.Data);
        input.Stop();
    }

    private static MidiEvent Next(BlockingCollection<MidiEvent> events) =>
        events.TryTake(out var e, Timeout) ? e : throw new TimeoutException("No MIDI event came back.");

    private static int IndexOfThrough(IReadOnlyList<string> names)
    {
        for (var i = 0; i < names.Count; i++)
        {
            if (names[i].StartsWith("Midi Through:", StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }
}

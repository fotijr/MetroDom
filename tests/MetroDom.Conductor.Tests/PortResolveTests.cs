using MetroDom.Conductor;
using MetroDom.Conductor.Midi;

namespace MetroDom.Conductor.Tests;

public class PortResolveTests
{
    private static readonly string[] Names =
    [
        "Midi Through:Midi Through Port-0 (14:0)",
        "USB MIDI Interface:USB MIDI Interface MIDI 1 (20:0)",
        "FLUID Synth (1234):Synth input port (1234:0) (128:0)",
    ];

    [Theory]
    [InlineData("1", 1)]
    [InlineData("midi through:midi through port-0 (14:0)", 0)]
    [InlineData("20:0", 1)]
    [InlineData("fluid", 2)]
    public void Resolve_FindsByIndexNameOrUniquePart(string query, int expected) =>
        Assert.Equal(expected, MidiDevices.Resolve(query, Names, "output"));

    [Theory]
    [InlineData("nope")]
    [InlineData("MIDI")] // ambiguous
    [InlineData("3")] // out of range and not a substring
    public void Resolve_RejectsMissingOrAmbiguous(string query) =>
        Assert.Throws<UsageException>(() => MidiDevices.Resolve(query, Names, "output"));
}

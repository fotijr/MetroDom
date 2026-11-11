using MetroDom.Conductor.Cli;

namespace MetroDom.Conductor.Tests;

public class ArrangeCliTests
{
    [Theory]
    [InlineData("36,38,76", new[] { 36, 38, 76 })]
    [InlineData("C4-E4", new[] { 60, 61, 62, 63, 64 })]
    [InlineData("F#3, Bb3", new[] { 54, 58 })]
    [InlineData("60-62,C4", new[] { 60, 61, 62 })]
    public void NoteList_ParsesNumbersNamesAndRanges(string raw, int[] expected)
    {
        Assert.True(NoteList.TryParse(raw, out var notes, out var error), error);
        Assert.Equal(expected, notes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("H9")]
    [InlineData("128")]
    [InlineData("C6-C4")]
    public void NoteList_RejectsBadNotes(string raw) =>
        Assert.False(NoteList.TryParse(raw, out _, out _));

    [Fact]
    public void NoteList_FormatsRunsAsRanges() =>
        Assert.Equal("36,38,60-72", NoteList.Format(new HashSet<int>([60, 61, 62, 63, 64, 65, 66, 67, 68, 69, 70, 71, 72, 36, 38])));

    [Fact]
    public void InstrumentSpec_TakesNotes_AndPlaysOnItsFirstChannel()
    {
        Assert.True(InstrumentSpec.TryParse("drums=10,11@35/36,38", out var spec, out var error), error);

        var member = spec.ToBandMember();
        Assert.Equal([36, 38], member.Profile.Notes!.Order());
        Assert.Equal(9, member.Channel);
        Assert.True(member.IsPercussion);
        Assert.Null(InstrumentSpec.DefaultBand[0].ToProfile().Notes);
    }

    [Theory]
    [InlineData("MetroDom drums=10@35/36,38", "MetroDom drums", new[] { 36, 38 })]
    [InlineData("xylo@10/C4-D4", "xylo", new[] { 60, 61, 62 })]
    [InlineData("20:0", "20:0", null)]
    [InlineData("Midi Through:Midi Through Port-0 (14:0)", "Midi Through:Midi Through Port-0 (14:0)", null)]
    public void OutputSpec_TakesNotes(string raw, string port, int[]? notes)
    {
        Assert.True(OutputSpec.TryParse(raw, out var spec, out var error), error);

        Assert.Equal(port, spec.Port);
        Assert.Equal(notes, spec.Notes?.Order().ToArray());
    }

    [Theory]
    [InlineData("arrange")]
    [InlineData("arrange", "-i", "xylo=1@10/60,64,67", "-i", "drums=10@35/36,38")]
    [InlineData("arrange", "--no-fold", "-i", "xylo=1@10/C4-C5")]
    [InlineData("simulate", "--arrange", "-i", "xylo=1@10/C5-C6", "-i", "drums=10@35/36,38,76")]
    public void Arrange_ExitsWith0(params string[] args) =>
        Assert.Equal(0, MetroDomApp.Create().Run(args, TestContext.Current.CancellationToken));

    [Theory]
    [InlineData("arrange", "missing.mid")]
    [InlineData("arrange", "-i", "xylo=1@10/H9")]
    [InlineData("arrange", "-i", "a@1", "-i", "a@2")]
    public void Arrange_BadInput_ExitsWith2(params string[] args) =>
        Assert.Equal(MetroDomApp.UsageError, MetroDomApp.Create().Run(args, TestContext.Current.CancellationToken));
}

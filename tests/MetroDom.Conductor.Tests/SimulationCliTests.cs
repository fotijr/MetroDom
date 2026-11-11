using Melanchall.DryWetMidi.Core;
using MetroDom.Conductor.Cli;

namespace MetroDom.Conductor.Tests;

public class SimulationCliTests
{
    [Theory]
    [InlineData("1-9,11-16", new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 10, 11, 12, 13, 14, 15 })]
    [InlineData("10-10", new[] { 9 })]
    [InlineData("3-1", null)]
    [InlineData("0-4", null)]
    [InlineData("1-", null)]
    public void ChannelList_AcceptsRanges(string raw, int[]? expected)
    {
        var ok = ChannelList.TryParse(raw, out var channels, out _);

        Assert.Equal(expected is not null, ok);
        if (expected is not null)
        {
            Assert.Equal(expected, channels);
        }
    }

    [Theory]
    [InlineData(new[] { 9 }, "10")]
    [InlineData(new[] { 9, 10 }, "10,11")]
    [InlineData(new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 10, 11, 12, 13, 14, 15 }, "1-9,11-16")]
    public void ChannelList_FormatsRunsAsRanges(int[] channels, string expected) =>
        Assert.Equal(expected, ChannelList.Format(channels));

    [Theory]
    [InlineData("drums=10@35", "drums", new[] { 9 }, 35)]
    [InlineData("xylo@10ms", "xylo", null, 10)]
    [InlineData("bells=1-2,4@2.5", "bells", new[] { 0, 1, 3 }, 2.5)]
    public void InstrumentSpec_Parses(string raw, string name, int[]? channels, double latencyMs)
    {
        Assert.True(InstrumentSpec.TryParse(raw, out var spec, out var error), error);

        Assert.Equal(name, spec.Name);
        Assert.Equal(channels ?? InstrumentSpec.AllChannels, spec.Channels);
        Assert.Equal(TimeSpan.FromMilliseconds(latencyMs), spec.Latency);
    }

    [Theory]
    [InlineData("drums")]
    [InlineData("@10")]
    [InlineData("drums=17@10")]
    [InlineData("drums=10@fast")]
    [InlineData("drums=10@-5")]
    [InlineData("big drum@5")]
    public void InstrumentSpec_RejectsBadSpecs(string raw) =>
        Assert.False(InstrumentSpec.TryParse(raw, out _, out _));

    [Fact]
    public void InstrumentSpec_RejectsDuplicateNames()
    {
        Assert.False(InstrumentSpec.TryParseAll(["a@1", "A@2"], out _, out var error));
        Assert.Contains("twice", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("simulate")]
    [InlineData("simulate", "--no-compensation", "--notes")]
    [InlineData("simulate", "-i", "xylo=1@10", "-i", "drums=10@35", "--jitter", "2", "--seed", "3")]
    public void Simulate_BuiltInDemo_ExitsWith0(params string[] args) =>
        Assert.Equal(0, MetroDomApp.Create().Run(args, TestContext.Current.CancellationToken));

    [Theory]
    [InlineData("simulate", "missing.mid")]
    [InlineData("simulate", "-i", "drums")]
    [InlineData("simulate", "--jitter", "-1")]
    [InlineData("virtual", "-i", "xylo=0@10")]
    public void Simulate_And_Virtual_BadInput_ExitsWith2(params string[] args) =>
        Assert.Equal(MetroDomApp.UsageError, MetroDomApp.Create().Run(args, TestContext.Current.CancellationToken));

    [Fact]
    public void DemoSong_WritesAPlayableFile_AndRefusesToOverwrite()
    {
        var path = Path.Combine(Path.GetTempPath(), $"metrodom-demo-{Guid.NewGuid():N}.mid");
        try
        {
            var ct = TestContext.Current.CancellationToken;
            Assert.Equal(0, MetroDomApp.Create().Run(["demo-song", path], ct));
            Assert.Equal(2, MidiFile.Read(path).GetTrackChunks().Count());
            Assert.Equal(MetroDomApp.UsageError, MetroDomApp.Create().Run(["demo-song", path], ct));
            Assert.Equal(0, MetroDomApp.Create().Run(["demo-song", path, "--force"], ct));
            Assert.Equal(0, MetroDomApp.Create().Run(["simulate", path], ct));
        }
        finally
        {
            File.Delete(path);
        }
    }
}

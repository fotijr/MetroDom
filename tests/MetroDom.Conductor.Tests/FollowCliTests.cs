using MetroDom.Conductor.Cli;

namespace MetroDom.Conductor.Tests;

public class FollowCliTests
{
    [Theory]
    [InlineData("--follow", "pads", "--tap", "pedal")]
    [InlineData("--follow", "pads", "--clock-in", "drum machine")]
    [InlineData("--follow", "pads", "--follow-on", "cc:999")]
    [InlineData("--follow-on", "note:38", "--tempo", "100")]
    [InlineData("--follow", "pads", "--control", "pads")]
    public void Play_RejectsConflictingFollowOptions(string a, string b, string c, string d)
    {
        var path = Path.GetTempFileName();
        try
        {
            Assert.Equal(MetroDomApp.UsageError, MetroDomApp.Create().Run(["play", path, "-o", "0", a, b, c, d], TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Simulate_FollowsRecordedHits()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "seconds,note,velocity\n" + string.Join("\n", Enumerable.Range(0, 24).Select(i => FormattableString.Invariant($"{i * 0.62:0.000},38,100"))));
            Assert.Equal(0, MetroDomApp.Create().Run(["simulate", "--follow", path, "--notes"], TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("0.5\nlater\n")]
    [InlineData("# nothing\n")]
    public void Simulate_RejectsBadHitFiles(string text)
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, text);
            Assert.Equal(MetroDomApp.UsageError, MetroDomApp.Create().Run(["simulate", "--follow", path], TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("--on", "nonsense")]
    [InlineData("--tempo", "5")]
    public void Beats_RejectsBadOptions(string option, string value) =>
        Assert.Equal(MetroDomApp.UsageError, MetroDomApp.Create().Run(["beats", "pads", option, value], TestContext.Current.CancellationToken));
}

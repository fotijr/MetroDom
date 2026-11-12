using MetroDom.Conductor.Cli;
using MetroDom.Conductor.Commands;

namespace MetroDom.Conductor.Tests;

public class CountInCliTests
{
    [Theory]
    [InlineData(null, 9, 76)]
    [InlineData("10/76", 9, 76)]
    [InlineData("1/C6", 0, 84)]
    [InlineData(" 16 / 0 ", 15, 0)]
    public void Click_Parses(string? raw, int channel, int note)
    {
        Assert.True(PlayCommand.TryParseClick(raw, out var click, out var error), error);
        Assert.Equal((channel, note), click);
    }

    [Theory]
    [InlineData("76")]
    [InlineData("0/76")]
    [InlineData("17/76")]
    [InlineData("10/60-62")]
    [InlineData("10/H2")]
    public void Click_RejectsNonsense(string raw)
    {
        Assert.False(PlayCommand.TryParseClick(raw, out _, out var error));
        Assert.Contains(raw, error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--count-in", "9")]
    [InlineData("--count-in", "-1")]
    [InlineData("--click", "10")]
    public void Play_RejectsBadCountIn(string option, string value)
    {
        var path = Path.GetTempFileName();
        try
        {
            Assert.Equal(MetroDomApp.UsageError, MetroDomApp.Create().Run(["play", path, "-o", "0", option, value], TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(path);
        }
    }
}

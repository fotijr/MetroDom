using MetroDom.Conductor.Cli;
using MetroDom.Conductor.Commands;

namespace MetroDom.Conductor.Tests;

/// <summary><c>-i NAME@PROFILE.json</c> and <c>-o PORT@PROFILE.json</c> (plan 0004 S4).</summary>
public sealed class ProfileFileTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("metrodom-profile-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Write(string json, string name = "profile.json")
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, json);
        return path;
    }

    private string Xylo() => Write("""
        {"schema":"metrodom.instrument-profile/0","id":"xylo","name":"Xylophone","midi":{"channel":3},
         "latencyMs":18,"notes":[{"note":60},{"note":62,"latencyMs":25},{"note":64}],"polyphony":2,
         "safety":{"maxOnMs":40,"maxDutyPercent":30}}
        """);

    [Fact]
    public void InstrumentSpec_TakesEverythingButTheName_FromTheFile()
    {
        Assert.True(InstrumentSpec.TryParse($"xy@{Xylo()}", out var spec, out var error), error);

        var member = spec.ToBandMember();
        Assert.Equal("xy", member.Profile.Name);
        Assert.Equal(2, member.Channel); // the profile's channel 3
        Assert.Equal([60, 62, 64], member.Profile.Notes!.Order());
        Assert.Equal(2, member.Profile.Polyphony);
        Assert.Equal(TimeSpan.FromMilliseconds(40), member.Profile.Safety!.MaxOnTime);
        Assert.Equal(TimeSpan.FromMilliseconds(25), spec.Latency);
    }

    [Fact]
    public void InstrumentSpec_ChannelsOnTheCommandLine_BeatTheFile()
    {
        Assert.True(InstrumentSpec.TryParse($"xy=5@{Xylo()}", out var spec, out var error), error);
        Assert.Equal(4, spec.ToBandMember().Channel);
    }

    [Fact]
    public void OutputSpec_ProfileFile_StandsInForTheInstrument()
    {
        var path = Xylo();
        Assert.True(OutputSpec.TryParse($"MetroDom xylo=2@{path}", out var spec, out var error), error);

        Assert.Equal("MetroDom xylo", spec.Port);
        Assert.Equal([1], spec.Channels);
        Assert.False(spec.WantsProfile);

        var sent = new MetroDom.Core.InstrumentProfile("sent", TimeSpan.FromMilliseconds(99));
        var merged = SessionOutput.Merge("MetroDom xylo", spec, sent);
        Assert.Equal(2, merged.Polyphony);
        Assert.Equal([60, 62, 64], merged.Notes!.Order());
    }

    [Theory]
    [InlineData("xy@missing.json", "not found")]
    [InlineData("two words@{0}", "one-word NAME")]
    public void InstrumentSpec_ReportsBadFiles(string raw, string expected)
    {
        Assert.False(InstrumentSpec.TryParse(string.Format(System.Globalization.CultureInfo.InvariantCulture, raw, Xylo()), out _, out var error));
        Assert.Contains(expected, error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_that_is_not_a_profile_says_why()
    {
        var path = Write("""{"schema":"nope"}""", "bad.json");
        Assert.False(OutputSpec.TryParse($"port@{path}", out _, out var error));
        Assert.Contains(path, error, StringComparison.Ordinal);
    }

    [Fact]
    public void Arrange_UsesAProfileFile()
    {
        var ct = TestContext.Current.CancellationToken;
        Assert.Equal(0, MetroDomApp.Create().Run(["arrange", "-i", $"xylo=1@{Xylo()}", "-i", "drums=10@35/36,38"], ct));
        Assert.Equal(MetroDomApp.UsageError, MetroDomApp.Create().Run(["arrange", "-i", "xylo@nope.json"], ct));
    }
}

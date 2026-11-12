using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using MetroDom.Core.Policy;

namespace MetroDom.Core.Tests;

public class PolicyTests
{
    private static ControlChangeEvent Cc(int number, int value) => new((SevenBitNumber)number, (SevenBitNumber)value);

    private static string RepoFile(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, relative);
            if (File.Exists(path))
            {
                return path;
            }
        }

        throw new FileNotFoundException(relative);
    }

    [Fact]
    public void ShippedLiveBandFile_MatchesTheBuiltInProfile()
    {
        Assert.True(PolicyProfile.TryParse(File.ReadAllText(RepoFile("policies/live-band.json")), out var shipped, out var error), error);

        Assert.Equal("live-band", shipped.Mode);
        Assert.Equal(PolicyProfile.LiveBand.Controls.OrderBy(c => c.Key), shipped.Controls.OrderBy(c => c.Key));
        Assert.Equal(6, shipped.Controls.Count);
    }

    [Fact]
    public void Profile_RoundTripsThroughJson()
    {
        Assert.True(PolicyProfile.TryParse(PolicyProfile.LiveBand.ToJson(), out var parsed, out var error), error);
        Assert.Equal(PolicyProfile.LiveBand.Controls.OrderBy(c => c.Key), parsed.Controls.OrderBy(c => c.Key));
    }

    [Theory]
    [InlineData("""{"mode":"x"}""", "schema")]
    [InlineData("""{"schema":"metrodom.policy/0"}""", "mode")]
    [InlineData("""{"schema":"metrodom.policy/0","mode":"x","controls":{"jump":"cc:1"}}""", "unknown control 'jump'")]
    [InlineData("""{"schema":"metrodom.policy/0","mode":"x","controls":{"tap":"any"}}""", "not 'any'")]
    [InlineData("""{"schema":"metrodom.policy/0","mode":"x","controls":{"tap":"pedal"}}""", "control 'tap'")]
    [InlineData("""{"schema":"metrodom.policy/0","mode":"x","controls":{"tap":"cc:64","panic":"CC:64"}}""", "both tap and panic")]
    [InlineData("nope", "valid")]
    public void Profile_RejectsBadFiles_WithAReason(string json, string reason)
    {
        Assert.False(PolicyProfile.TryParse(json, out _, out var error));
        Assert.Contains(reason, error, StringComparison.Ordinal);
    }

    [Fact]
    public void Profile_MayLeaveActionsUnmapped()
    {
        Assert.True(PolicyProfile.TryParse("""{"schema":"metrodom.policy/0","mode":"drummer","controls":{"panic":"note:49@10"}}""", out var profile, out var error), error);
        Assert.Equal("note:49@10", Assert.Single(profile.Controls).Value);
    }

    [Fact]
    public void ControlMap_FiresOncePerPress_PerPedal()
    {
        var map = PolicyProfile.LiveBand.NewControlMap();

        Assert.Equal([ControlAction.StartStop], map.Match(Cc(80, 127)));
        Assert.Empty(map.Match(Cc(80, 127))); // still held
        Assert.Equal([ControlAction.Panic], map.Match(Cc(85, 127))); // another pedal while the first is down
        Assert.Empty(map.Match(Cc(80, 0)));
        Assert.Equal([ControlAction.StartStop], map.Match(Cc(80, 127)));
        Assert.Equal([ControlAction.Tap], map.Match(Cc(64, 100)));
        Assert.Empty(map.Match(Cc(7, 127))); // volume pedal: not a control
        Assert.Empty(map.Match(new NoteOnEvent((SevenBitNumber)60, (SevenBitNumber)100)));
    }
}

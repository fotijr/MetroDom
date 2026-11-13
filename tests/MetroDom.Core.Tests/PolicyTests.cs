using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using MetroDom.Core.Policy;
using MetroDom.Core.Safety;

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

    private const string Head = """{"schema":"metrodom.policy/0","mode":"x",""";

    [Theory]
    [InlineData("live-band")]
    [InlineData("crowd-jukebox")]
    [InlineData("rental")]
    public void ShippedProfiles_Parse_AndRoundTrip(string mode)
    {
        Assert.True(PolicyProfile.TryParse(File.ReadAllText(RepoFile($"policies/{mode}.json")), out var shipped, out var error), error);
        Assert.Equal(mode, shipped.Mode);

        Assert.True(PolicyProfile.TryParse(shipped.ToJson(), out var back, out error), error);
        Assert.Equal(shipped.Limits, back.Limits);
        Assert.Equal(shipped.Queue, back.Queue);
        Assert.Equal(shipped.Sessions, back.Sessions);
        Assert.Equal(shipped.Roles.For(Role.Operator), back.Roles.For(Role.Operator));
        Assert.Equal(shipped.Roles.For(Role.Guest), back.Roles.For(Role.Guest));
    }

    [Fact]
    public void OnlyTheJukebox_StartsQueuedSongsByItself()
    {
        Assert.True(PolicyProfile.TryParse(File.ReadAllText(RepoFile("policies/crowd-jukebox.json")), out var jukebox, out _));
        Assert.True(PolicyProfile.TryParse(File.ReadAllText(RepoFile("policies/rental.json")), out var rental, out _));
        Assert.True(jukebox.Queue.Autoplay);
        Assert.False(rental.Queue.Autoplay);
        Assert.False(PolicyProfile.LiveBand.Queue.Autoplay);
    }

    [Fact]
    public void Rental_IsTheStrictest_AndOpensRequestsToGuests()
    {
        Assert.True(PolicyProfile.TryParse(File.ReadAllText(RepoFile("policies/rental.json")), out var rental, out var error), error);

        Assert.Equal(100, rental.Limits.MaxVelocity);
        Assert.Equal(0, rental.Limits.Quiet!.MaxVelocity); // silent at night
        Assert.True(rental.Roles.Allows(Role.Guest, Grant.QueueRequest));
        Assert.False(rental.Roles.Allows(Role.Guest, Grant.Panic));
        Assert.True(rental.Roles.Allows(Role.Operator, Grant.QueueEdit));
        Assert.Equal(TimeSpan.FromHours(72), rental.Sessions.MaxTokenLife);
    }

    [Fact]
    public void WithoutNewSections_TheDefaultsAreClosed()
    {
        var live = PolicyProfile.LiveBand;
        Assert.Equal(SafetyLimits.None, live.Limits);
        Assert.Equal(QueueRules.Closed, live.Queue);
        Assert.Empty(live.Roles.For(Role.Guest));
        Assert.Equal([Grant.View, Grant.Transport, Grant.Tempo, Grant.Sections, Grant.Panic, Grant.QueueEdit, Grant.Curfew, Grant.Volume], live.Roles.For(Role.Operator));
        Assert.True(live.Roles.Allows(Role.Owner, Grant.Tokens));
    }

    [Theory]
    [InlineData(Head + """ "roles":{"guest":["view","panic"]}}""", "guests can't have \"panic\"")]
    [InlineData(Head + """ "roles":{"guest":["transport"]}}""", "guests can't have \"transport\"")]
    [InlineData(Head + """ "roles":{"operator":["view","transport"]}}""", "must include \"panic\"")]
    [InlineData(Head + """ "roles":{"operator":["panic"]}}""", "must include \"view\"")]
    [InlineData(Head + """ "roles":{"operator":["view","panic","tokens"]}}""", "only the owner hands out tokens")]
    [InlineData(Head + """ "roles":{"owner":["view"]}}""", "the owner can always do everything")]
    [InlineData(Head + """ "roles":{"dj":["view"]}}""", "unknown role 'dj'")]
    [InlineData(Head + """ "roles":{"guest":["dance"]}}""", "unknown permission 'dance'")]
    [InlineData(Head + """ "roles":{"guest":"view"}}""", "must be a list")]
    [InlineData(Head + """ "limits":{"maxVelocity":0}}""", "maxVelocity\" must be 1-127")]
    [InlineData(Head + """ "limits":{"maxNotesPerSecond":0}}""", "at least 1")]
    [InlineData(Head + """ "limits":{"quietHours":{"from":"23:00","to":"23:00"}}}""", "different")]
    [InlineData(Head + """ "limits":{"curfew":{"from":"23:00"}}}""", "limits.curfew")]
    [InlineData(Head + """ "limits":{"endsAt":"2026-11-14 01:00"}}""", "UTC offset")]
    [InlineData(Head + """ "limits":{"graceMinutes":90}}""", "0-60")]
    [InlineData(Head + """ "limits":{"quietHours":{"from":"23:00","to":"07:00","maxVelocity":200}}}""", "0-127")]
    [InlineData(Head + """ "queue":{"maxPerGuest":0}}""", "1-20")]
    [InlineData(Head + """ "queue":{"cooldownSeconds":-1}}""", "0-3600")]
    [InlineData(Head + """ "sessions":{"maxTokenHours":200}}""", "a week")]
    public void NewSections_RejectBadValues_WithAReason(string json, string reason)
    {
        Assert.False(PolicyProfile.TryParse(json, out _, out var error));
        Assert.Contains(reason, error, StringComparison.Ordinal);
    }

    [Fact]
    public void Curfew_AndEndTime_ReadAndRoundTrip()
    {
        Assert.True(PolicyProfile.TryParse(Head + """ "limits":{"curfew":{"from":"23:00","to":"10:00"},"endsAt":"2026-11-14T01:30-05:00","graceMinutes":2}}""", out var policy, out var error), error);

        Assert.Equal(new Curfew(new TimeOnly(23, 0), new TimeOnly(10, 0), new DateTimeOffset(2026, 11, 14, 1, 30, 0, TimeSpan.FromHours(-5)), TimeSpan.FromMinutes(2)), policy.Curfew);
        Assert.Equal(SafetyLimits.None, policy.Limits);
        Assert.True(PolicyProfile.TryParse(policy.ToJson(), out var back, out error), error);
        Assert.Equal(policy.Curfew, back.Curfew);
        Assert.False(PolicyProfile.LiveBand.Curfew.IsSet);
    }

    [Fact]
    public void Limits_FromThePolicy_ReadAsTheGovernorsLimits()
    {
        Assert.True(PolicyProfile.TryParse(Head + """ "limits":{"maxVelocity":90,"maxNotesPerSecond":10,"quietHours":{"from":"22:30","to":"07:00","maxVelocity":40}}}""", out var policy, out var error), error);
        Assert.Equal(90, policy.Limits.MaxVelocity);
        Assert.Equal(10, policy.Limits.MaxNotesPerSecond);
        Assert.Equal(new QuietHours(new TimeOnly(22, 30), new TimeOnly(7, 0), 40), policy.Limits.Quiet);
        Assert.Equal("velocity ≤ 90 · ≤ 10 notes/s per instrument · quiet 22:30-07:00 ≤ velocity 40", policy.Limits.Describe());
    }

    [Fact]
    public void Flags_OnlyTightenNumbers_AndQuietHoursReplace()
    {
        var policy = new SafetyLimits { MaxVelocity = 100, MaxNotesPerSecond = 16, Quiet = new QuietHours(new TimeOnly(22, 0), new TimeOnly(8, 0)) };

        var tighter = policy.Overridden(80, 20, null);
        Assert.Equal(80, tighter.MaxVelocity);       // lower flag wins
        Assert.Equal(16, tighter.MaxNotesPerSecond); // a looser flag doesn't loosen the policy
        Assert.Equal(policy.Quiet, tighter.Quiet);

        var tonight = new QuietHours(new TimeOnly(23, 0), new TimeOnly(7, 0), 30);
        Assert.Equal(tonight, policy.Overridden(null, null, tonight).Quiet);
        Assert.Equal(new SafetyLimits { MaxVelocity = 60 }, SafetyLimits.None.Overridden(60, null, null));
        Assert.Equal("none", SafetyLimits.None.Describe());
    }
}

using MetroDom.Core.Policy;
using MetroDom.Core.Setlist;
using MetroDom.Core.Timing;

namespace MetroDom.Core.Tests;

/// <summary>Guest requests, votes and their limits (plan 0004 S6, crowd-jukebox spec).</summary>
public sealed class RequestDeskTests
{
    private static readonly QueueRules Jukebox = new(Requests: true, Votes: true, MaxPerGuest: 2, Cooldown: TimeSpan.FromMinutes(5), Autoplay: true)
    {
        SongCooldown = TimeSpan.FromMinutes(45),
    };

    private readonly VirtualClock _clock = new();
    private readonly SetQueue _queue = new();

    private RequestDesk Desk(QueueRules? rules = null, params string[] set)
    {
        _queue.Replace(set.Select(s => (s, s)));
        _queue.Cue(0);
        return new RequestDesk(_queue, rules ?? Jukebox, _clock);
    }

    private string[] Coming()
    {
        var s = _queue.Snapshot();
        return [.. s.Songs.Skip(s.EditableFrom).Select(x => x.Title)];
    }

    private static DeskReply Ask(RequestDesk desk, string guest, string song) => desk.Request(guest, guest != "owner", song, song, guest);

    [Fact]
    public void Requests_play_before_the_set_and_most_votes_first()
    {
        var desk = Desk(null, "opener", "fill 1", "fill 2");

        Assert.True(Ask(desk, "g1", "A").Accepted);
        _clock.Advance(TimeSpan.FromSeconds(1));
        var b = Ask(desk, "g2", "B");
        Assert.Equal(["A", "B", "fill 1", "fill 2"], Coming());

        Assert.True(desk.Vote("g3", guest: true, b.Song!.Id, up: true).Accepted);
        Assert.Equal(["B", "A", "fill 1", "fill 2"], Coming());
        Assert.Equal(2, _queue.Snapshot().Songs.First(s => s.Title == "B").Votes);
    }

    [Fact]
    public void Equal_votes_play_first_come_first_served()
    {
        var desk = Desk(null, "opener");
        Ask(desk, "g1", "A");
        Ask(desk, "g2", "B");
        var c = Ask(desk, "g3", "C");
        desk.Vote("g4", true, c.Song!.Id, up: true);
        desk.Vote("g5", true, _queue.Snapshot().Songs.First(s => s.Title == "A").Id, up: true);

        Assert.Equal(["A", "C", "B"], Coming()); // A and C have 2: A asked first
    }

    [Fact]
    public void An_operator_placing_a_song_at_the_front_pins_it_ahead_of_requests()
    {
        var desk = Desk(null, "opener", "fill");
        var a = Ask(desk, "g1", "A");

        Assert.True(_queue.TryAdd("encore", "encore", "Sam", at: _queue.Snapshot().EditableFrom, out var pinned, out _));
        Assert.Equal(SongKind.Pinned, pinned.Kind);
        Assert.True(_queue.TryAdd("late", "late", "Sam", at: null, out var appended, out _));
        Assert.Equal(SongKind.Set, appended.Kind);
        Assert.Equal(["encore", "A", "fill", "late"], Coming());

        // Moving a request places it for good: it stops riding on votes.
        Assert.True(_queue.TryMove(a.Song!.Id, _queue.Snapshot().Songs.Count - 1, out _));
        Assert.Equal(["encore", "fill", "late", "A"], Coming());
        Assert.False(desk.Vote("g2", true, a.Song.Id, up: true).Accepted);
    }

    [Fact]
    public void Asking_for_a_waiting_request_counts_as_a_vote_and_never_queues_it_twice()
    {
        var desk = Desk(null, "opener", "fill");
        Ask(desk, "g1", "A");

        var again = Ask(desk, "g2", "A");
        Assert.True(again.CountedAsVote);
        Assert.Equal(2, again.Song!.Votes);
        Assert.Equal(Refusal.Conflict, Ask(desk, "g2", "A").Refused); // already voted
        Assert.Equal(Refusal.Conflict, Ask(desk, "g3", "fill").Refused); // in the set already
        Assert.Single(Coming(), t => t == "A");
    }

    [Fact]
    public void A_song_that_just_played_waits_out_its_cooldown()
    {
        var desk = Desk(null, "opener", "next");
        Assert.Contains("is up now", Ask(desk, "g1", "opener").Message, StringComparison.Ordinal);
        _clock.Advance(TimeSpan.FromMinutes(10));
        _queue.Advance();

        var refused = Ask(desk, "g2", "opener");
        Assert.Equal(Refusal.Conflict, refused.Refused);
        Assert.Contains("played 10 min ago", refused.Message, StringComparison.Ordinal);
        Assert.Contains("35 min", refused.Message, StringComparison.Ordinal);

        _clock.Advance(TimeSpan.FromMinutes(36));
        Assert.True(Ask(desk, "g2", "opener").Accepted);
    }

    [Fact]
    public void A_guest_waits_between_requests_and_has_only_so_many_waiting()
    {
        var desk = Desk(null, "opener");
        Assert.True(Ask(desk, "g1", "A").Accepted);

        var soon = Ask(desk, "g1", "B");
        Assert.Equal((Refusal.TooSoon, TimeSpan.FromMinutes(5)), (soon.Refused!.Value, soon.RetryAfter!.Value));
        Assert.True(Ask(desk, "g2", "B").Accepted); // limits are per guest

        _clock.Advance(TimeSpan.FromMinutes(5));
        Assert.True(Ask(desk, "g1", "C").Accepted);
        _clock.Advance(TimeSpan.FromMinutes(5));
        var full = Ask(desk, "g1", "D");
        Assert.Equal(Refusal.TooSoon, full.Refused);
        Assert.Contains("2 requests waiting", full.Message, StringComparison.Ordinal);
        Assert.Equal(new GuestStatus(2, 2, null), desk.Status("g1"));

        _queue.Advance(); // A comes up: one fewer waiting
        Assert.True(Ask(desk, "g1", "D").Accepted);
    }

    [Fact]
    public void Owners_and_operators_skip_the_guest_limits_but_not_the_song_rules()
    {
        var desk = Desk(null, "opener");
        Assert.True(Ask(desk, "owner", "A").Accepted);
        Assert.True(Ask(desk, "owner", "B").Accepted);
        Assert.True(Ask(desk, "owner", "C").Accepted);
        Assert.Equal(Refusal.Conflict, Ask(desk, "owner", "opener").Refused);
    }

    [Fact]
    public void A_flood_is_cut_off_after_twenty_tries_a_minute_refused_ones_included()
    {
        var desk = Desk(null, "opener");
        for (var i = 0; i < RequestDesk.TriesPerMinute; i++)
        {
            _ = Ask(desk, "bot", $"song {i}"); // first accepted, the rest refused for the cooldown
        }

        var cut = Ask(desk, "bot", "one more");
        Assert.Contains("Too many tries", cut.Message, StringComparison.Ordinal);
        Assert.Equal(Refusal.TooSoon, desk.Vote("bot", true, "1", up: true).Refused);
        _clock.Advance(TimeSpan.FromMinutes(1));
        Assert.DoesNotContain("Too many", Ask(desk, "bot", "later").Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Paused_or_closed_requests_are_refused()
    {
        var desk = Desk(null, "opener");
        Assert.True(desk.Pause(true));
        Assert.Equal(Refusal.Closed, Ask(desk, "g1", "A").Refused);
        Assert.False(desk.VotesOpen);
        Assert.True(desk.Pause(false));
        Assert.True(Ask(desk, "g1", "A").Accepted);

        var closed = new RequestDesk(new SetQueue(), QueueRules.Closed, _clock);
        Assert.Contains("doesn't take requests", closed.Request("g", true, "A", "A", null).Message, StringComparison.Ordinal);
        Assert.Equal(Refusal.Closed, closed.Vote("g", true, "1", up: true).Refused);
    }

    [Fact]
    public void Votes_are_one_per_guest_and_can_be_taken_back_but_not_on_your_own_request()
    {
        var desk = Desk(null, "opener");
        var a = Ask(desk, "g1", "A").Song!;

        Assert.Equal(Refusal.Conflict, desk.Vote("g1", true, a.Id, up: true).Refused);
        Assert.Equal(Refusal.Conflict, desk.Vote("g1", true, a.Id, up: false).Refused);
        Assert.True(desk.Vote("g2", true, a.Id, up: true).Accepted);
        Assert.Equal(Refusal.Conflict, desk.Vote("g2", true, a.Id, up: true).Refused);
        Assert.True(desk.Vote("g2", true, a.Id, up: false).Accepted);
        Assert.Equal(Refusal.NotFound, desk.Vote("g2", true, "zz", up: true).Refused);
        Assert.Equal(Refusal.Conflict, desk.Vote("g2", true, _queue.Now!.Id, up: true).Refused); // playing
    }

    [Fact]
    public void Fill_picks_a_song_that_has_rested()
    {
        var desk = Desk(null, "opener");
        var random = new Random(1);

        Assert.Equal("other", desk.PickFill(["opener", "other"], random));
        Assert.Equal("opener", desk.PickFill(["opener"], random)); // nothing rested: the longest ago
        Assert.Null(desk.PickFill([], random));
    }

    [Fact]
    public void Policy_reads_and_writes_song_cooldown_and_fill()
    {
        const string json = """{"schema":"metrodom.policy/0","mode":"x","queue":{"requests":true,"songCooldownMinutes":20,"fill":true}}""";
        Assert.True(PolicyProfile.TryParse(json, out var policy, out var error), error);
        Assert.Equal((TimeSpan.FromMinutes(20), true), (policy.Queue.SongCooldown, policy.Queue.Fill));
        Assert.True(PolicyProfile.TryParse(policy.ToJson(), out var again, out _));
        Assert.Equal(policy.Queue, again.Queue);
        Assert.False(PolicyProfile.TryParse(json.Replace("20", "2000", StringComparison.Ordinal), out _, out error));
        Assert.Contains("songCooldownMinutes", error, StringComparison.Ordinal);
    }
}

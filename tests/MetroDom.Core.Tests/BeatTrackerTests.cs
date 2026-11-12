using MetroDom.Core.Simulation;
using MetroDom.Core.Timing;

namespace MetroDom.Core.Tests;

/// <summary>
/// A modeled human player until real recordings exist (M5 triggers): beats with Gaussian timing
/// error, an optional tempo curve, and the things drummers do that a tracker must survive.
/// </summary>
internal static class Human
{
    public static readonly TimeSpan Start = TimeSpan.FromSeconds(1);

    /// <summary><paramref name="count"/> beats at <paramref name="bpm"/>(beat), each off by N(0, jitter).</summary>
    public static List<TimeSpan> Play(Func<int, double> bpm, int count, double jitterMs = 0, int seed = 1)
    {
        var random = new Random(seed);
        var hits = new List<TimeSpan>();
        var t = Start.TotalSeconds;
        for (var i = 0; i < count; i++)
        {
            hits.Add(TimeSpan.FromSeconds(t + (Gaussian(random) * jitterMs / 1000)));
            t += 60 / bpm(i);
        }

        return hits;
    }

    public static List<TimeSpan> Play(double bpm, int count, double jitterMs = 0, int seed = 1) => Play(_ => bpm, count, jitterMs, seed);

    /// <summary>Adds hits between beats <paramref name="from"/>..<paramref name="to"/> at <paramref name="per"/> per beat (a fill, or eighths).</summary>
    public static List<TimeSpan> Subdivide(this List<TimeSpan> hits, int from, int to, int per)
    {
        var extra = new List<TimeSpan>();
        for (var i = from; i < to && i + 1 < hits.Count; i++)
        {
            for (var j = 1; j < per; j++)
            {
                extra.Add(hits[i] + ((hits[i + 1] - hits[i]) * j / per));
            }
        }

        return [.. hits.Concat(extra).Order()];
    }

    /// <summary>Removes beats <paramref name="from"/>..<paramref name="to"/> (the player stops).</summary>
    public static List<TimeSpan> Rest(this List<TimeSpan> hits, int from, int to) =>
        [.. hits.Where((_, i) => i < from || i >= to)];

    /// <summary>Moves every beat from <paramref name="from"/> on by <paramref name="shift"/>.</summary>
    public static List<TimeSpan> Shift(this List<TimeSpan> hits, int from, TimeSpan shift) =>
        [.. hits.Select((h, i) => i >= from ? h + shift : h)];

    private static double Gaussian(Random random) =>
        Math.Sqrt(-2 * Math.Log(1 - random.NextDouble())) * Math.Cos(2 * Math.PI * random.NextDouble());
}

public class BeatTrackerTests
{
    private static BeatTracker Feed(IEnumerable<TimeSpan> hits, double? expected = null)
    {
        var tracker = new BeatTracker(expected);
        foreach (var hit in hits)
        {
            tracker.Hit(hit);
        }

        return tracker;
    }

    [Fact]
    public void Locks_after_four_steady_hits_and_predicts_the_next_beat()
    {
        var hits = Human.Play(120, 4);
        var tracker = new BeatTracker();
        Assert.False(tracker.Hit(hits[0]));
        Assert.False(tracker.Hit(hits[1]));
        Assert.False(tracker.Hit(hits[2]));
        Assert.Equal(BeatState.Listening, tracker.State);
        Assert.Null(tracker.NextBeat(hits[2]));

        Assert.True(tracker.Hit(hits[3]));
        Assert.Equal(BeatState.Locked, tracker.State);
        Assert.Equal(120, tracker.Bpm!.Value, 3);
        Assert.Equal(hits[3] + TimeSpan.FromSeconds(0.5), tracker.NextBeat(hits[3] + TimeSpan.FromMilliseconds(1)));
        Assert.Equal(hits[3] + TimeSpan.FromSeconds(1.5), tracker.NextBeat(hits[3] + TimeSpan.FromSeconds(1.2)));
    }

    [Fact]
    public void Uneven_hits_do_not_lock()
    {
        var t = Human.Start;
        var tracker = Feed([t, t + TimeSpan.FromSeconds(0.5), t + TimeSpan.FromSeconds(0.8), t + TimeSpan.FromSeconds(1.5)]);
        Assert.Equal(BeatState.Listening, tracker.State);
    }

    [Theory]
    [InlineData(240.0, 120.0, 120.0)] // Eighths at 120: follows the beat, not the hi-hat.
    [InlineData(60.0, 120.0, 120.0)] // Half time: doubles up.
    [InlineData(240.0, null, 240.0)] // No song tempo: takes the hits as played.
    [InlineData(90.0, 92.0, 90.0)]
    public void Picks_the_pulse_nearest_the_song_tempo(double playedBpm, double? songBpm, double followed)
    {
        Assert.Equal(followed, Feed(Human.Play(playedBpm, 4), songBpm).Bpm!.Value, 3);
    }

    [Fact]
    public void Follows_a_sloppy_drummer_within_two_percent_and_predicts_within_a_few_ms()
    {
        var hits = Human.Play(100, 64, jitterMs: 15, seed: 7);
        var tracker = new BeatTracker(100);
        var errors = new List<double>();
        foreach (var hit in hits)
        {
            if (tracker.Beats >= 8 && tracker.NextBeat(hit - TimeSpan.FromMilliseconds(300)) is { } predicted)
            {
                errors.Add(Math.Abs((hit - predicted).TotalMilliseconds));
            }

            tracker.Hit(hit);
        }

        Assert.Equal(64, tracker.Beats + 3); // Every beat after the lock counted.
        Assert.InRange(tracker.Bpm!.Value, 98, 102);
        Assert.InRange(errors.Average(), 0, 20); // Jitter itself is 15 ms σ: prediction can't beat that.
    }

    [Fact]
    public void Ignores_a_fill_and_off_beat_eighths()
    {
        var hits = Human.Play(110, 32).Subdivide(20, 24, 4).Subdivide(8, 16, 2); // Later beats first, so indices stay beats.
        var tracker = Feed(hits, 110);
        Assert.Equal(BeatState.Locked, tracker.State);
        Assert.Equal(110, tracker.Bpm!.Value, 1);
        Assert.Equal(29, tracker.Beats); // 32 beats: the first three only build the lock.
    }

    [Fact]
    public void Holds_tempo_when_the_player_stops_and_picks_them_up_on_the_grid()
    {
        var hits = Human.Play(120, 24);
        var tracker = Feed(hits.Take(12));
        tracker.Update(hits[12] + TimeSpan.FromSeconds(2)); // Four beats of silence.
        Assert.Equal(BeatState.Holding, tracker.State);
        Assert.Equal(120, tracker.Bpm!.Value, 3);

        Assert.True(tracker.Hit(hits[16])); // Back on the beat: counted at once.
        Assert.Equal(BeatState.Locked, tracker.State);
    }

    [Fact]
    public void Relocks_when_the_player_comes_back_on_a_new_phase()
    {
        var hits = Human.Play(120, 24).Shift(12, TimeSpan.FromSeconds(0.17));
        var tracker = Feed(hits.Take(15));
        tracker.Hit(hits[15]);
        Assert.Equal(BeatState.Locked, tracker.State);
        Assert.InRange(Math.Abs((hits[15] + TimeSpan.FromSeconds(0.5) - tracker.NextBeat(hits[15] + TimeSpan.FromMilliseconds(1))!.Value).TotalMilliseconds), 0, 30);
    }

    [Fact]
    public void Caps_the_tempo_change_from_one_late_hit()
    {
        var hits = Human.Play(120, 8);
        var tracker = Feed(hits);
        var before = tracker.Period!.Value;
        tracker.Hit(hits[7] + TimeSpan.FromSeconds(0.5 * 1.18)); // 18% late: still a beat, but a lurch.
        Assert.InRange(tracker.Period!.Value / before, 1, 1.0401);
    }

    [Fact]
    public void Tracks_a_drummer_speeding_up()
    {
        var hits = Human.Play(i => 100 + (30.0 * Math.Min(i, 32) / 32), 48, jitterMs: 8, seed: 3);
        var tracker = Feed(hits, 100);
        Assert.Equal(BeatState.Locked, tracker.State);
        Assert.InRange(tracker.Bpm!.Value, 127, 133);
    }
}

public class BeatFollowerTests
{
    private static readonly IReadOnlyList<TimeSpan> Grid120 = [.. Enumerable.Range(0, 200).Select(i => TimeSpan.FromSeconds(i * 0.5))];

    private static double Ms(TimeSpan t) => Math.Abs(t.TotalMilliseconds);

    [Fact]
    public void Band_starts_on_the_players_beat_at_their_tempo()
    {
        var hits = Human.Play(96, 40);
        var result = FollowSimulation.Run(hits, Grid120, 120);
        Assert.Equal(hits[3], result.LockedAt);
        Assert.InRange(Ms(hits[4] - result.Beats[0].Heard), 0, 1); // The next beat clear of lookahead + preroll.
        Assert.All(result.Errors(), e => Assert.InRange(Ms(e), 0, 1));
    }

    [Fact]
    public void Band_stays_with_a_sloppy_drummer()
    {
        var hits = Human.Play(100, 96, jitterMs: 12, seed: 11);
        var errors = FollowSimulation.Run(hits, Grid120, 120).Errors(settle: 4).Select(Ms).Order().ToArray();
        Assert.True(errors.Length > 80);
        Assert.InRange(errors[errors.Length / 2], 0, 15); // Median: about the drummer's own jitter.
        Assert.InRange(errors[(int)(errors.Length * 0.95)], 0, 35);
    }

    [Fact]
    public void Band_follows_a_drummer_who_speeds_up_without_falling_behind()
    {
        var hits = Human.Play(i => 100 + (24.0 * Math.Min(i, 48) / 48), 80, jitterMs: 8, seed: 5);
        var result = FollowSimulation.Run(hits, Grid120, 110);
        var errors = result.Errors(settle: 4).Select(e => e.TotalMilliseconds).ToArray();
        Assert.InRange(errors.Select(Math.Abs).Order().ElementAt((int)(errors.Length * 0.95)), 0, 40);
        Assert.InRange(result.Final.Bpm!.Value, 121, 127);
    }

    [Fact]
    public void Band_holds_tempo_through_a_break_and_lands_with_the_drummer_after()
    {
        var hits = Human.Play(100, 48).Rest(16, 24);
        var result = FollowSimulation.Run(hits, Grid120, 100);

        var gap = result.Beats.Where(b => b.Heard > hits[15] && b.Heard < hits[16]).ToArray(); // hits[16] is beat 24 now.
        Assert.True(gap.Length >= 8);
        var intervals = gap.Zip(gap.Skip(1), (a, b) => (b.Heard - a.Heard).TotalSeconds);
        Assert.All(intervals, i => Assert.Equal(0.6, i, 3)); // Held at 100 BPM, no lurch.

        var after = result.Beats.Where(b => b.Heard >= hits[16] && b.Error is not null).ToArray();
        Assert.All(after, b => Assert.InRange(Ms(b.Error!.Value), 0, 2));
    }

    [Fact]
    public void Band_catches_a_drummer_who_comes_back_late_within_a_few_beats()
    {
        var hits = Human.Play(100, 64).Shift(32, TimeSpan.FromMilliseconds(90));
        var result = FollowSimulation.Run(hits, Grid120, 100);
        var late = result.Beats.Where(b => b.Hit >= hits[32] && b.Error is not null).ToArray();
        Assert.True(Ms(late[0].Error!.Value) > 50); // The band can't know in advance.
        Assert.All(late.Skip(12), b => Assert.InRange(Ms(b.Error!.Value), 0, 10));

        // Never a jump: consecutive band beats change length by at most the correction cap.
        var beats = result.Beats.Select(b => b.Heard).ToArray();
        var lengths = beats.Zip(beats.Skip(1), (a, b) => (b - a).TotalSeconds).ToArray();
        Assert.All(lengths, l => Assert.InRange(l, 0.6 / 1.09, 0.6 * 1.09));
    }

    [Fact]
    public void Latency_does_not_change_how_well_the_band_follows()
    {
        var hits = Human.Play(i => 90 + (i / 4.0), 64, jitterMs: 10, seed: 2);
        double Average(int latencyMs) => FollowSimulation
            .Run(hits, Grid120, 100, new FollowOptions { Latency = TimeSpan.FromMilliseconds(latencyMs) })
            .Errors(settle: 4).Select(Ms).Average();

        var fast = Average(5);
        var slow = Average(80);
        Assert.InRange(fast, 0, 20); // A drummer drifting all song, with 10 ms σ of their own jitter.
        Assert.InRange(slow - fast, -3, 3);
    }

    [Fact]
    public void Song_beats_follow_tempo_and_count_in()
    {
        var song = DemoSong.Create();
        var beats = SongBeats.From(song);
        Assert.Equal(TimeSpan.Zero, beats[0]);
        Assert.Equal(beats[1] - beats[0], beats[5] - beats[4]);

        var counted = SongBeats.WithCountIn(beats, 4, beats[1]);
        Assert.Equal(beats.Count + 4, counted.Count);
        Assert.Equal(beats[1] * 4, counted[4]);
    }
}

public class HitLogTests
{
    [Fact]
    public void Round_trips_hits_relative_to_the_first()
    {
        var text = HitLog.Write([(TimeSpan.FromSeconds(10), 38, 90), (TimeSpan.FromSeconds(10.6125), 36, 100)]);
        Assert.StartsWith(HitLog.Header, text, StringComparison.Ordinal);
        Assert.True(HitLog.TryParse(text, out var hits, out var error), error);
        Assert.Equal([TimeSpan.Zero, TimeSpan.FromSeconds(0.6125)], hits);
    }

    [Fact]
    public void Skips_comments_and_blank_lines_and_sorts()
    {
        Assert.True(HitLog.TryParse("# groove\n\n1.5\n0.5, 38\n1.0\r\n", out var hits, out _));
        Assert.Equal([0.5, 1.0, 1.5], hits.Select(h => h.TotalSeconds));
    }

    [Theory]
    [InlineData("0.5\nsoon\n", "line 2")]
    [InlineData("-1\n", "line 1")]
    [InlineData("", "no hits")]
    public void Rejects_bad_lines(string text, string expected)
    {
        Assert.False(HitLog.TryParse(text, out _, out var error));
        Assert.Contains(expected, error, StringComparison.Ordinal);
    }
}

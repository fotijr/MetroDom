using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using MetroDom.Core.Timing;

namespace MetroDom.Core.Simulation;

/// <summary>How to run a <see cref="FollowSimulation"/>.</summary>
public sealed record FollowOptions
{
    /// <summary>The band's instrument latency (the click it plays on every beat).</summary>
    public TimeSpan Latency { get; init; } = TimeSpan.FromMilliseconds(30);

    /// <summary>How often the follower re-aims between hits.</summary>
    public TimeSpan UpdateEvery { get; init; } = TimeSpan.FromMilliseconds(50);

    public BeatTrackerOptions Tracker { get; init; } = new();

    public BeatFollowerOptions Follower { get; init; } = new();
}

/// <summary>One beat the band played, and the player's nearest hit (null if none within half a beat).</summary>
public sealed record FollowedBeat(int Index, TimeSpan Heard, TimeSpan? Hit)
{
    /// <summary>Band minus player: + = band late.</summary>
    public TimeSpan? Error => Hit is { } hit ? Heard - hit : null;
}

/// <summary>What happened when the band followed a player.</summary>
/// <param name="LockedAt">When the tracker first locked (and the band started), or null if it never did.</param>
/// <param name="Beats">Every beat the band played.</param>
/// <param name="Final">The tracker after the last hit.</param>
public sealed record FollowResult(TimeSpan? LockedAt, IReadOnlyList<FollowedBeat> Beats, BeatTracker Final)
{
    /// <summary>Errors of beats with a nearby hit, skipping the first <paramref name="settle"/> beats.</summary>
    public IReadOnlyList<TimeSpan> Errors(int settle = 0) =>
        [.. Beats.Skip(settle).Where(b => b.Error is not null).Select(b => b.Error!.Value)];
}

/// <summary>
/// A band following a recorded (or modelled) player on a <see cref="VirtualClock"/>, through the
/// real tracker, follower and scheduler: the band plays a click on every beat of a beat grid, and
/// the result says how close each click landed to the player's hits. The song starts aligned as
/// soon as the tracker locks, like a drummer counting the band in. Instant and repeatable.
/// </summary>
public static class FollowSimulation
{
    /// <param name="hits">The player's hits, in order (clock time).</param>
    /// <param name="beats">The song's beat grid in song time (see <see cref="SongBeats"/>).</param>
    /// <param name="expectedBpm">The song's tempo, for choosing beat / half / double time.</param>
    /// <param name="options">Tuning.</param>
    public static FollowResult Run(IReadOnlyList<TimeSpan> hits, IReadOnlyList<TimeSpan> beats, double? expectedBpm, FollowOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(hits);
        ArgumentNullException.ThrowIfNull(beats);
        options ??= new FollowOptions();

        var clock = new VirtualClock();
        var orchestra = new Orchestra();
        var click = new VirtualInstrument(new InstrumentProfile("band", options.Latency), clock);
        orchestra.Assign(0, click);
        var scheduler = new LatencyScheduler(orchestra, clock, new Dictionary<IMidiOutput, InstrumentProfile> { [click] = click.Profile });
        var tracker = new BeatTracker(expectedBpm, options.Tracker);
        var follower = new BeatFollower(scheduler, beats, tracker, options.Follower);
        var song = beats.SelectMany(b => new TimedMidiEvent[]
        {
            new(b, new NoteOnEvent((SevenBitNumber)60, (SevenBitNumber)100)),
            new(b + TimeSpan.FromMilliseconds(20), new NoteOffEvent((SevenBitNumber)60, SevenBitNumber.MinValue)),
        }).ToArray();

        TimeSpan[] grid = [.. beats];
        var heard = new List<(int Index, TimeSpan At)>();
        click.Struck += (_, strike) =>
        {
            var index = Array.BinarySearch(grid, scheduler.Current!.SongTime);
            heard.Add((index, strike.SoundsAt));
        };

        TimeSpan? lockedAt = null;
        var next = 0;
        var nextUpdate = TimeSpan.Zero;
        while (true)
        {
            TimeSpan? due = lockedAt is null ? null : scheduler.NextDue;
            var hitDue = next < hits.Count ? hits[next] : (TimeSpan?)null;
            var when = Min(Min(due, hitDue), lockedAt is null || scheduler.IsFinished ? null : nextUpdate);
            if (when is null)
            {
                break;
            }

            clock.AdvanceTo(when.Value);
            if (hitDue == when)
            {
                next++;
                var counted = tracker.Hit(clock.Now);
                if (lockedAt is null && tracker.State == BeatState.Locked && follower.StartAligned(song, clock.Now))
                {
                    lockedAt = clock.Now;
                    nextUpdate = clock.Now + options.UpdateEvery;
                }
                else if (counted && lockedAt is not null)
                {
                    follower.Update(clock.Now);
                }
            }

            if (lockedAt is not null && clock.Now >= nextUpdate)
            {
                follower.Update(clock.Now);
                nextUpdate = clock.Now + options.UpdateEvery;
            }

            if (lockedAt is not null)
            {
                scheduler.Pump();
            }
        }

        var played = heard.Select(h => new FollowedBeat(h.Index, h.At, Nearest(hits, h.At, tracker.Period ?? TimeSpan.FromSeconds(0.5)))).ToArray();
        return new FollowResult(lockedAt, played, tracker);
    }

    private static TimeSpan? Nearest(IReadOnlyList<TimeSpan> hits, TimeSpan at, TimeSpan period)
    {
        TimeSpan? best = null;
        foreach (var hit in hits)
        {
            if ((best is null || (hit - at).Duration() < (best.Value - at).Duration()) && (hit - at).Duration() < period / 2)
            {
                best = hit;
            }
        }

        return best;
    }

    private static TimeSpan? Min(TimeSpan? a, TimeSpan? b) => a is null ? b : b is null ? a : a < b ? a : b;
}

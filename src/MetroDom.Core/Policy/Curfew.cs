using System.Globalization;

namespace MetroDom.Core.Policy;

/// <summary>
/// When the band must stop playing songs (policy <c>limits.curfew</c> and <c>limits.endsAt</c>):
/// a venue's nightly curfew, a rental's end time, or both. A curfew ends the set at a song
/// boundary rather than cutting a song off note by note: no song starts that can't finish by the
/// stop time plus <see cref="Grace"/>, and a song still playing then (slowed down, vamping) is
/// stopped. Quiet hours (the governors) still hold back notes whatever the curfew says.
/// </summary>
/// <param name="From">Start of the nightly curfew, local wall-clock time; null for none.</param>
/// <param name="To">Its end (may wrap past midnight). Songs may start again from then.</param>
/// <param name="EndsAt">A rental's end: nothing plays after it, ever (until the policy changes).</param>
/// <param name="Grace">How long past the stop time a song may still run to finish.</param>
public sealed record Curfew(TimeOnly? From, TimeOnly? To, DateTimeOffset? EndsAt, TimeSpan Grace)
{
    /// <summary>Grace when the policy doesn't give one.</summary>
    public static readonly TimeSpan DefaultGrace = TimeSpan.FromMinutes(5);

    /// <summary>No curfew and no end time.</summary>
    public static readonly Curfew None = new(null, null, null, DefaultGrace);

    /// <summary>
    /// Tonight's curfew, set live by the owner (<c>POST /curfew</c>): an extra stop on top of the
    /// policy's, so it can only end the night sooner. In force from then until the owner lifts it.
    /// </summary>
    public DateTimeOffset? StopAt { get; init; }

    /// <summary>Whether anything is set.</summary>
    public bool IsSet => From is not null || EndsAt is not null || StopAt is not null;

    /// <summary>Whether songs may not start now (inside the nightly window, or past the end or tonight's curfew).</summary>
    public bool InForce(DateTimeOffset now) => InWindow(now) || now >= EndsAt || now >= StopAt;

    /// <summary>The next moment songs must have ended by, or null if nothing is coming (or the booking is over).</summary>
    public DateTimeOffset? NextStop(DateTimeOffset now)
    {
        if (now >= EndsAt || now >= StopAt)
        {
            return null; // over: nothing more to stop
        }

        DateTimeOffset?[] stops = [From is { } from && !InWindow(now) ? Next(now, from) : null, EndsAt, StopAt];
        return stops.Where(s => s > now).Min();
    }

    /// <summary>When songs may start again: the window's end, or null if never (past the end time) or not in force.</summary>
    public DateTimeOffset? LiftsAt(DateTimeOffset now)
    {
        if (now >= EndsAt || now >= StopAt || !InWindow(now))
        {
            return null;
        }

        var lifts = Next(now, To!.Value);
        return lifts >= EndsAt || lifts >= StopAt ? null : lifts;
    }

    /// <summary>Whether a song of <paramref name="length"/> may start now: not in force, and done by the next stop plus grace.</summary>
    public bool Fits(DateTimeOffset now, TimeSpan length) =>
        !InForce(now) && (NextStop(now) is not { } stop || now + length <= stop + Grace);

    /// <summary>When a song started at <paramref name="start"/> must be stopped, or null if never.</summary>
    public DateTimeOffset? Deadline(DateTimeOffset start) => NextStop(start) + Grace;

    /// <summary>"curfew 23:00-10:00 · ends 2026-11-14 01:00 · 5 min grace", or null when nothing is set.</summary>
    public string? Describe()
    {
        if (!IsSet)
        {
            return null;
        }

        var parts = new List<string>();
        if (From is { } from && To is { } to)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"curfew {from:HH\\:mm}-{to:HH\\:mm}"));
        }

        if (EndsAt is { } end)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"ends {end:yyyy-MM-dd HH\\:mm}"));
        }

        if (StopAt is { } stop)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"tonight's curfew {stop:HH\\:mm}"));
        }

        parts.Add(string.Create(CultureInfo.InvariantCulture, $"{Grace.TotalMinutes:0.#} min grace"));
        return string.Join(" · ", parts);
    }

    /// <summary>Why nothing may start now, for the status line, or null if songs may start.</summary>
    public string? Reason(DateTimeOffset now)
    {
        if (now >= EndsAt)
        {
            return string.Create(CultureInfo.InvariantCulture, $"The booking ended at {EndsAt:HH\\:mm}.");
        }

        if (now >= StopAt)
        {
            return string.Create(CultureInfo.InvariantCulture, $"Curfew since {StopAt:HH\\:mm}, until the owner lifts it.");
        }

        return InWindow(now) && LiftsAt(now) is { } lifts
            ? string.Create(CultureInfo.InvariantCulture, $"Curfew until {lifts:HH\\:mm}.")
            : InWindow(now) ? "Curfew." : null;
    }

    /// <summary>
    /// This curfew with tonight's at <paramref name="at"/>: the next time the wall clock reads it
    /// after <paramref name="now"/> (so "01:00" set at 22:00 means after midnight).
    /// </summary>
    public Curfew Tonight(DateTimeOffset now, TimeOnly at) => this with { StopAt = Next(now, at) };

    private bool InWindow(DateTimeOffset now)
    {
        if (From is not { } from || To is not { } to)
        {
            return false;
        }

        var time = TimeOnly.FromDateTime(now.DateTime);
        return from <= to ? time >= from && time < to : time >= from || time < to;
    }

    /// <summary>The next time the wall clock reads <paramref name="time"/>, after <paramref name="now"/>.</summary>
    private static DateTimeOffset Next(DateTimeOffset now, TimeOnly time)
    {
        var today = new DateTimeOffset(now.Date + time.ToTimeSpan(), now.Offset);
        return today > now ? today : today.AddDays(1);
    }
}

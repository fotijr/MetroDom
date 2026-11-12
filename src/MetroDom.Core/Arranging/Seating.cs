using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;

namespace MetroDom.Core.Arranging;

/// <summary>A human in the band (ADR 0012): they play <paramref name="Role"/>, so the robots don't.</summary>
/// <param name="Role">A role as <see cref="Roles"/> or a saved mapping names it ("guitar", "drums").</param>
/// <param name="Player">Who sits there, for display. Optional.</param>
public sealed record Seat(string Role, string? Player = null)
{
    /// <summary>"guitar (Dom)" or "guitar".</summary>
    public override string ToString() => Player is null ? Role : $"{Role} ({Player})";
}

/// <summary>What filling the seats did to a song.</summary>
/// <param name="Song">A copy of the song without the seated parts' notes. Everything else (tempo, markers, controllers) is kept.</param>
/// <param name="Taken">Each part a seat took, with that seat.</param>
/// <param name="Empty">Seats whose role no part has: nothing was removed for them.</param>
public sealed record SeatedSong(MidiFile Song, IReadOnlyList<(SongPart Part, Seat Seat)> Taken, IReadOnlyList<Seat> Empty);

/// <summary>
/// Fills seats (ADR 0012): every part whose role a human has taken loses its notes, giving the
/// "minus-one" mix. Done on the song itself, before arranging or routing, so the arranger never
/// hands a human's part to a robot and plain channel routing leaves it out too. Unlike
/// <c>--mute</c> (whole channels), a seat removes exactly the parts with that role, even when
/// another part shares the channel.
/// </summary>
public static class Seating
{
    public static SeatedSong Apply(MidiFile midiFile, IReadOnlyList<Seat> seats, RoleMapping? roles = null)
    {
        ArgumentNullException.ThrowIfNull(midiFile);
        ArgumentNullException.ThrowIfNull(seats);
        if (seats.Count == 0)
        {
            return new SeatedSong(midiFile, [], []);
        }

        var parts = Arranger.Parts(midiFile, roles);
        var taken = new List<(SongPart, Seat)>();
        foreach (var part in parts)
        {
            if (seats.FirstOrDefault(s => string.Equals(s.Role, part.Role, StringComparison.OrdinalIgnoreCase)) is { } seat)
            {
                taken.Add((part, seat));
            }
        }

        var empty = seats.Where(s => !taken.Any(t => t.Item2 == s)).ToArray();
        if (taken.Count == 0)
        {
            return new SeatedSong(midiFile, [], empty);
        }

        var song = midiFile.Clone();
        var chunks = song.GetTrackChunks().ToArray();
        foreach (var group in taken.GroupBy(t => t.Item1.Track))
        {
            var channels = group.Select(t => t.Item1.Channel).ToHashSet();
            chunks[group.Key].RemoveTimedEvents(e => e.Event is NoteEvent note && channels.Contains(note.Channel));
        }

        return new SeatedSong(song, taken, empty);
    }
}

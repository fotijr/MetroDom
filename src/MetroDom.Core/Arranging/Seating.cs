using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;

namespace MetroDom.Core.Arranging;

/// <summary>A human in the band (ADR 0012): they play <paramref name="Role"/>, so the robots don't.</summary>
/// <param name="Role">A role as <see cref="Roles"/> or a saved mapping names it ("guitar", "drums").</param>
/// <param name="Player">Who sits there, for display. Optional.</param>
public sealed record Seat(string Role, string? Player = null)
{
    /// <summary>
    /// Only these notes of the role's parts are the human's (ADR 0012: a shared kit, piece by
    /// piece: the drummer plays kick and snare, the robots the hats and cymbals). Null: all of them.
    /// </summary>
    public IReadOnlySet<int>? Notes { get; init; }

    /// <summary>How the pieces were written ("kick, snare"), for display.</summary>
    public string? Pieces { get; init; }

    /// <summary>"drums (Dom): kick, snare", "guitar (Dom)" or "guitar".</summary>
    public override string ToString() => (Player is null ? Role : $"{Role} ({Player})") + (Pieces is null ? "" : $": {Pieces}");
}

/// <summary>What filling the seats did to a song.</summary>
/// <param name="Song">A copy of the song without the seated parts' notes (or, for a seat with pieces, those pieces' notes). Everything else (tempo, markers, controllers) is kept.</param>
/// <param name="Taken">Each part a seat took (all of it, or some of its notes), with that seat.</param>
/// <param name="Empty">Seats whose role no part has: nothing was removed for them.</param>
public sealed record SeatedSong(MidiFile Song, IReadOnlyList<(SongPart Part, Seat Seat)> Taken, IReadOnlyList<Seat> Empty);

/// <summary>
/// Fills seats (ADR 0012): every part whose role a human has taken loses its notes, giving the
/// "minus-one" mix. Done on the song itself, before arranging or routing, so the arranger never
/// hands a human's part to a robot and plain channel routing leaves it out too. Unlike
/// <c>--mute</c> (whole channels), a seat removes exactly the parts with that role, even when
/// another part shares the channel; a seat with pieces removes only those notes of them.
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
            // Several seats can share a part piece by piece (Sam on kick and snare, Ana on cymbals).
            foreach (var seat in seats.Where(s => string.Equals(s.Role, part.Role, StringComparison.OrdinalIgnoreCase)))
            {
                if (seat.Notes is null || HasAny(midiFile, part, seat.Notes))
                {
                    taken.Add((part, seat));
                }
            }
        }

        var empty = seats.Where(s => !taken.Any(t => ReferenceEquals(t.Item2, s))).ToArray();
        if (taken.Count == 0)
        {
            return new SeatedSong(midiFile, [], empty);
        }

        var song = midiFile.Clone();
        var chunks = song.GetTrackChunks().ToArray();
        foreach (var (part, seat) in taken)
        {
            chunks[part.Track].RemoveTimedEvents(e => e.Event is NoteEvent note && note.Channel == part.Channel
                && (seat.Notes is null || seat.Notes.Contains(note.NoteNumber)));
        }

        return new SeatedSong(song, taken, empty);
    }

    private static bool HasAny(MidiFile midiFile, SongPart part, IReadOnlySet<int> notes) =>
        midiFile.GetTrackChunks().ElementAt(part.Track).Events.OfType<NoteOnEvent>()
            .Any(e => e.Channel == part.Channel && notes.Contains(e.NoteNumber));
}

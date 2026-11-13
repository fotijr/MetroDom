using Melanchall.DryWetMidi.Core;
using MetroDom.Core.Arranging;
using Spectre.Console;

namespace MetroDom.Conductor.Cli;

/// <summary><c>--seat ROLE[=PLAYER]</c> values, and the report of what filling them did.</summary>
internal static class SeatList
{
    public const string Help = "A human plays this role, so the band leaves its parts out (a minus-one mix): [bold]ROLE[[=PLAYER]][[/PIECES]][/], repeatable (e.g. [bold]--seat guitar=Dom[/], [bold]--seat drums[/]). PIECES shares a part: the human plays only those notes, the band the rest (e.g. [bold]--seat drums=Sam/kick,snare[/]; pieces: kick, snare, hihat, toms, crash, ride, cymbals, cowbell, tambourine, percussion, or note numbers and names). Roles come from [bold]metrodom roles SONG[/].";

    public static bool TryParse(IEnumerable<string>? raw, out IReadOnlyList<Seat> seats, out string? error)
    {
        var parsed = new List<Seat>();
        seats = parsed;
        error = null;
        foreach (var raw1 in raw ?? [])
        {
            var text = raw1;
            IReadOnlySet<int>? notes = null;
            string? pieces = null;
            var slash = text.IndexOf('/', StringComparison.Ordinal);
            if (slash >= 0)
            {
                if (!TryParsePieces(text[(slash + 1)..], out var parsed1, out pieces, out var pieceError))
                {
                    error = $"'{raw1}': {pieceError}";
                    return false;
                }

                notes = parsed1;
                text = text[..slash];
            }

            var eq = text.IndexOf('=', StringComparison.Ordinal);
            var player = eq < 0 ? null : text[(eq + 1)..].Trim();
            if (!Roles.TryNormalize(eq < 0 ? text : text[..eq], out var role) || player is "")
            {
                error = $"'{raw1}' is not ROLE[=PLAYER][/PIECES] with a one-word role (e.g. guitar=Dom, drums=Sam/kick,snare).";
                return false;
            }

            // One role can have several seats only when each takes different pieces of it.
            if (parsed.FirstOrDefault(s => s.Role == role && (s.Notes is null || notes is null || s.Notes.Overlaps(notes))) is { } clash)
            {
                error = clash.Notes is null || notes is null
                    ? $"the {role} seat is listed twice."
                    : $"two {role} seats share notes ({clash.Pieces} / {pieces}); give each person different pieces.";
                return false;
            }

            parsed.Add(new Seat(role, player) { Notes = notes, Pieces = pieces });
        }

        return true;
    }

    /// <summary>"kick,snare,42" → the notes, and "kick, snare, 42" to show.</summary>
    internal static bool TryParsePieces(string raw, out IReadOnlySet<int> notes, out string pieces, out string? error)
    {
        var all = new HashSet<int>();
        var names = new List<string>();
        notes = all;
        pieces = "";
        foreach (var item in raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (DrumMap.Pieces.TryGetValue(item, out var piece))
            {
                all.UnionWith(piece);
            }
            else if (NoteList.TryParse(item, out var parsed, out _))
            {
                all.UnionWith(parsed);
            }
            else
            {
                error = $"'{item}' is not a piece ({string.Join(", ", DrumMap.Pieces.Keys)}) or a note.";
                return false;
            }

            names.Add(item.ToLowerInvariant());
        }

        if (all.Count == 0)
        {
            error = "no pieces after the /.";
            return false;
        }

        pieces = string.Join(", ", names);
        error = null;
        return true;
    }

    /// <summary>Fills the seats, prints what each one took, and returns the song the band should play.</summary>
    public static MidiFile Apply(MidiFile song, IReadOnlyList<Seat> seats, RoleMapping? roles)
    {
        var seated = Seating.Apply(song, seats, roles);
        foreach (var line in Describe(seated))
        {
            AnsiConsole.MarkupLine(line);
        }

        return seated.Song;
    }

    /// <summary>One line per seat: "Seat guitar (Dom): band leaves out Lead Gtr (#3:2), Rhythm (#4:3)".</summary>
    internal static IEnumerable<string> Describe(SeatedSong seated)
    {
        foreach (var group in seated.Taken.GroupBy(t => t.Seat))
        {
            var parts = string.Join(", ", group.Select(t => $"{Markup.Escape(t.Part.Name)} [grey]({RoleMapping.KeyFor(t.Part)})[/]"));
            yield return group.Key.Notes is null
                ? $"[grey]Seat[/] [bold]{Markup.Escape(group.Key.ToString())}[/][grey]: band leaves out[/] {parts}"
                : $"[grey]Seat[/] [bold]{Markup.Escape(group.Key.ToString())}[/][grey]: band leaves those notes out of[/] {parts}[grey] and plays the rest[/]";
        }

        foreach (var seat in seated.Empty)
        {
            yield return seat.Notes is null
                ? $"[yellow]Seat {Markup.Escape(seat.ToString())}: no {Markup.Escape(seat.Role)} part in this song, nothing left out.[/] [grey]See metrodom roles SONG.[/]"
                : $"[yellow]Seat {Markup.Escape(seat.ToString())}: no {Markup.Escape(seat.Role)} part in this song plays those pieces, nothing left out.[/]";
        }
    }
}

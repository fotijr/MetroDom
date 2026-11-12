using Melanchall.DryWetMidi.Core;
using MetroDom.Core.Arranging;
using Spectre.Console;

namespace MetroDom.Conductor.Cli;

/// <summary><c>--seat ROLE[=PLAYER]</c> values, and the report of what filling them did.</summary>
internal static class SeatList
{
    public const string Help = "A human plays this role, so the band leaves its parts out (a minus-one mix): [bold]ROLE[[=PLAYER]][/], repeatable (e.g. [bold]--seat guitar=Dom[/], [bold]--seat drums[/]). Roles come from [bold]metrodom roles SONG[/].";

    public static bool TryParse(IEnumerable<string>? raw, out IReadOnlyList<Seat> seats, out string? error)
    {
        var parsed = new List<Seat>();
        seats = parsed;
        error = null;
        foreach (var text in raw ?? [])
        {
            var eq = text.IndexOf('=', StringComparison.Ordinal);
            var player = eq < 0 ? null : text[(eq + 1)..].Trim();
            if (!Roles.TryNormalize(eq < 0 ? text : text[..eq], out var role) || player is "")
            {
                error = $"'{text}' is not ROLE[=PLAYER] with a one-word role (e.g. guitar=Dom).";
                return false;
            }

            if (parsed.Any(s => s.Role == role))
            {
                error = $"the {role} seat is listed twice.";
                return false;
            }

            parsed.Add(new Seat(role, player));
        }

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
            yield return $"[grey]Seat[/] [bold]{Markup.Escape(group.Key.ToString())}[/][grey]: band leaves out[/] {parts}";
        }

        foreach (var seat in seated.Empty)
        {
            yield return $"[yellow]Seat {Markup.Escape(seat.ToString())}: no {Markup.Escape(seat.Role)} part in this song, nothing left out.[/] [grey]See metrodom roles SONG.[/]";
        }
    }
}

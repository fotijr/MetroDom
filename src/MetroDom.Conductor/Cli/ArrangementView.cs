using System.Globalization;
using MetroDom.Core.Arranging;
using Spectre.Console;

namespace MetroDom.Conductor.Cli;

/// <summary>Prints who plays what after <see cref="Arranger.Arrange"/>, and what had to change.</summary>
internal static class ArrangementView
{
    public static void Write(Arrangement arrangement, IReadOnlyList<BandMember> band)
    {
        var inv = CultureInfo.InvariantCulture;
        var table = new Table().Title("[bold]Arrangement[/]").Border(TableBorder.Rounded).BorderColor(Color.Grey)
            .AddColumn("Instrument [grey](notes)[/]")
            .AddColumn("Plays")
            .AddColumn("Fit");
        foreach (var member in band)
        {
            var instrument = $"{Markup.Escape(member.Profile.Name)} [grey]({(member.IsPercussion ? "drums: " : "")}{Markup.Escape(NoteList.Format(member.Profile.Notes))})[/]";
            var a = arrangement.Assignments.FirstOrDefault(x => x.Instrument == member.Profile.Name);
            if (a is null)
            {
                table.AddRow(instrument, "[yellow]nothing it can play[/]", "");
                continue;
            }

            var transpose = a.Transpose == 0 ? "" : $" [blue]{a.Transpose.ToString("+0;-0", inv)}[/]";
            var fit = new List<string> { $"{a.Played.ToString(inv)} played" };
            AddCount(fit, a.Folded, "folded", "grey");
            AddCount(fit, a.Dropped, "dropped", "yellow");
            AddCount(fit, a.Thinned, "thinned", "yellow");
            table.AddRow(
                instrument,
                $"{Markup.Escape(a.Part.Name)} [grey]ch {a.Part.Channel + 1} {NoteList.Name(a.Part.LowestNote)}–{NoteList.Name(a.Part.HighestNote)}[/]{transpose}",
                string.Join(" [grey]·[/] ", fit));
        }

        AnsiConsole.Write(table);
        if (arrangement.Unassigned.Count > 0)
        {
            AnsiConsole.MarkupLine("[grey]Not played:[/] " + string.Join(", ", arrangement.Unassigned.Select(p =>
                string.Create(inv, $"{Markup.Escape(p.Name)} [grey](ch {p.Channel + 1}, {p.NoteCount} notes)[/]"))));
        }
    }

    private static void AddCount(List<string> parts, int n, string label, string color)
    {
        if (n > 0)
        {
            parts.Add($"[{color}]{n.ToString(CultureInfo.InvariantCulture)} {label}[/]");
        }
    }
}

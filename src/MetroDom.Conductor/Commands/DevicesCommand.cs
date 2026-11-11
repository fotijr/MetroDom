using MetroDom.Conductor.Midi;
using Spectre.Console;
using Spectre.Console.Cli;

namespace MetroDom.Conductor.Commands;

internal sealed class DevicesCommand : Command
{
    public override int Execute(CommandContext context, CancellationToken cancellationToken)
    {
        AnsiConsole.MarkupLine("[bold]Outputs[/] [grey](instruments, for --output)[/]");
        AnsiConsole.Write(Ports(MidiDevices.OutputNames()));
        AnsiConsole.MarkupLine("[bold]Inputs[/] [grey](controllers, for --input)[/]");
        AnsiConsole.Write(Ports(MidiDevices.InputNames()));
        AnsiConsole.MarkupLine("[grey]Pass the # or a unique part of the name.[/]");
        return 0;
    }

    private static Table Ports(IReadOnlyList<string> names)
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .AddColumn(new TableColumn("[grey]#[/]").RightAligned())
            .AddColumn(new TableColumn("Name"));
        if (names.Count == 0)
        {
            table.AddRow("", "[grey](none)[/]");
        }

        for (var i = 0; i < names.Count; i++)
        {
            table.AddRow($"[bold]{i}[/]", Markup.Escape(names[i]));
        }

        return table;
    }
}

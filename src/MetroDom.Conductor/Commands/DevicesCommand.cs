using System.ComponentModel;
using System.Globalization;
using MetroDom.Conductor.Cli;
using MetroDom.Conductor.Midi;
using MetroDom.Core;
using Spectre.Console;
using Spectre.Console.Cli;

namespace MetroDom.Conductor.Commands;

internal sealed class DevicesCommand : Command<DevicesCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--identify")]
        [Description("Ask each output who it is (MIDI Identity Request, ADR 0007) and show the answer. Takes about a quarter of a second.")]
        public bool Identify { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        AnsiConsole.MarkupLine("[bold]Outputs[/] [grey](instruments, for --output)[/]");
        if (settings.Identify)
        {
            AnsiConsole.Write(Identified(Identifier.Identify(MidiDevices.Backend.Value, Identifier.DefaultTimeout)));
        }
        else
        {
            AnsiConsole.Write(Ports(MidiDevices.OutputNames()));
        }

        AnsiConsole.MarkupLine("[bold]Inputs[/] [grey](controllers, for --input)[/]");
        AnsiConsole.Write(Ports(MidiDevices.InputNames()));
        AnsiConsole.MarkupLine(settings.Identify
            ? "[grey]Pass the # or a unique part of the name.[/]"
            : "[grey]Pass the # or a unique part of the name. Add --identify to ask each output what it is.[/]");
        return 0;
    }

    internal static Table Identified(IReadOnlyList<IdentifiedPort> ports)
    {
        var table = NewTable().AddColumn(new TableColumn("Identity"));
        if (ports.Count == 0)
        {
            table.AddRow("", "[grey](none)[/]", "");
        }

        for (var i = 0; i < ports.Count; i++)
        {
            table.AddRow($"[bold]{i}[/]", Markup.Escape(ports[i].Output.DisplayName), Describe(ports[i]));
        }

        return table;
    }

    internal static string Describe(IdentifiedPort port) => port.Kind switch
    {
        PortKind.MetroDom => $"[green]{Markup.Escape(port.Identity!.Describe())}[/]\n" + DescribeProfile(port),
        PortKind.MidiDevice => Markup.Escape(port.Identity!.Describe()),
        PortKind.NoReply => "[grey]no reply (still usable)[/]",
        PortKind.OutputOnly => "[grey]output only, can't answer[/]",
        _ => $"[yellow]couldn't ask: {Markup.Escape(port.Error ?? "")}[/]",
    };

    /// <summary>Step 3's answer: "profile: xylo · notes 60,64,67 (3) · 9-10 ms · repeat ≥ 60 ms".</summary>
    internal static string DescribeProfile(IdentifiedPort port)
    {
        if (port.Profile is not { } profile)
        {
            return $"[yellow]no profile: {Markup.Escape(port.ProfileError ?? "")}[/]";
        }

        return $"[grey]profile: {Markup.Escape(profile.Name)} · {Markup.Escape(Summary(profile))}[/]";
    }

    /// <summary>"notes 60,64,67 (3) · 9-12 ms · repeat ≥ 60 ms".</summary>
    internal static string Summary(InstrumentProfile profile)
    {
        var notes = profile.Notes is null ? "any note" : $"notes {NoteList.Format(profile.Notes)} ({profile.Notes.Count})";
        var fastest = profile.NoteLatency.Values.Append(profile.Latency).Min();
        var latency = fastest == profile.MaxLatency
            ? string.Create(CultureInfo.InvariantCulture, $"{fastest.TotalMilliseconds:0.#} ms")
            : string.Create(CultureInfo.InvariantCulture, $"{fastest.TotalMilliseconds:0.#}-{profile.MaxLatency.TotalMilliseconds:0.#} ms");
        var interval = profile.MinInterval > TimeSpan.Zero
            ? string.Create(CultureInfo.InvariantCulture, $" · repeat ≥ {profile.MinInterval.TotalMilliseconds:0.#} ms")
            : "";
        return $"{notes} · {latency}{interval}";
    }

    private static Table Ports(IReadOnlyList<string> names)
    {
        var table = NewTable();
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

    private static Table NewTable() => new Table()
        .Border(TableBorder.Rounded)
        .BorderColor(Color.Grey)
        .AddColumn(new TableColumn("[grey]#[/]").RightAligned())
        .AddColumn(new TableColumn("Name"));
}

using System.ComponentModel;
using System.Globalization;
using MetroDom.Conductor.Cli;
using MetroDom.Conductor.Midi;
using MetroDom.Core;
using MetroDom.Core.Discovery;
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

        [CommandOption("--assign <PORT=CHANNELS>")]
        [Description("Remember what a device plays, as [bold]PORT=CHANNELS@LATENCY_MS/NOTES[/] (the [bold]-o[/] syntax, e.g. [bold]\"Roland=1-4\"[/] or [bold]\"drums=10@35/36,38\"[/]). From then on [bold]play[/] without [bold]-o[/] adds it whenever it's plugged in. Repeatable.")]
        public string[]? Assign { get; init; }

        [CommandOption("--forget <PORT>")]
        [Description("Forget a remembered assignment (port name or part of it). Repeatable.")]
        public string[]? Forget { get; init; }

        [CommandOption("--assignments <FILE>")]
        [Description("Where remembered assignments live. Default: $METRODOM_ASSIGNMENTS, else metrodom/assignments.json in your app data folder.")]
        public string? Assignments { get; init; }

        public override ValidationResult Validate()
        {
            foreach (var assign in Assign ?? [])
            {
                if (!OutputSpec.TryParse(assign, out _, out var error))
                {
                    return ValidationResult.Error($"--assign: {error}");
                }
            }

            return ValidationResult.Success();
        }
    }

    public override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var path = AssignmentStore.Resolve(settings.Assignments);
        var book = AssignmentStore.Load(path, Ui.Warn);
        if (settings.Assign is { Length: > 0 } || settings.Forget is { Length: > 0 })
        {
            Change(book, settings);
            AssignmentStore.Save(book, path);
            AnsiConsole.Write(Remembered(book));
            AnsiConsole.MarkupLine($"[grey]Kept in {Markup.Escape(path)}[/]");
            return 0;
        }

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
        if (book.All.Count > 0)
        {
            AnsiConsole.Write(Remembered(book));
            AnsiConsole.MarkupLine($"[grey]Kept in {Markup.Escape(path)}[/]");
        }

        return 0;
    }

    /// <summary>Forgets, then assigns. An assignment is keyed by what the device says it is right now.</summary>
    private static void Change(AssignmentBook book, Settings settings)
    {
        foreach (var what in settings.Forget ?? [])
        {
            var removed = book.Remove(what);
            if (removed.Count == 0)
            {
                Ui.Warn($"Nothing remembered matches '{what}'.");
            }

            foreach (var assignment in removed)
            {
                Ui.Success($"Forgot {assignment.Port} ({AutoBand.Describe(assignment)}).");
            }
        }

        foreach (var raw in settings.Assign ?? [])
        {
            if (!OutputSpec.TryParse(raw, out var spec, out var error))
            {
                throw new UsageException($"--assign: {error}");
            }

            var port = MidiDevices.FindOutput(spec.Port);
            var identity = Identifier.Identify(MidiDevices.Backend.Value, Identifier.DefaultTimeout, [port])[0].Identity;
            var assignment = new Assignment(AssignmentBook.KeyFor(port.Name, identity), port.Name, spec.Channels, spec.Latency, spec.Notes);
            book.Set(assignment);
            Ui.Success($"{port.Name}: {AutoBand.Describe(assignment)}{(identity is null ? " (it doesn't identify itself, so this is kept by port name)" : "")}.");
        }
    }

    internal static Table Remembered(AssignmentBook book)
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .Title("[bold]Remembered[/]")
            .AddColumn("Port")
            .AddColumn("Plays")
            .AddColumn("[grey]Key[/]");
        if (book.All.Count == 0)
        {
            table.AddRow("[grey](none)[/]", "", "");
        }

        foreach (var assignment in book.All)
        {
            table.AddRow(Markup.Escape(assignment.Port), Markup.Escape(AutoBand.Describe(assignment)), $"[grey]{Markup.Escape(assignment.Key)}[/]");
        }

        return table;
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
        var intervals = profile.Notes is { Count: > 0 } playable ? playable.Select(profile.IntervalFor).ToList() : [profile.MinInterval];
        TimeSpan quickest = intervals.Min(), slowest = intervals.Max();
        var interval = slowest <= TimeSpan.Zero ? ""
            : quickest == slowest ? string.Create(CultureInfo.InvariantCulture, $" · repeat ≥ {slowest.TotalMilliseconds:0.#} ms")
            : string.Create(CultureInfo.InvariantCulture, $" · repeat ≥ {quickest.TotalMilliseconds:0.#}-{slowest.TotalMilliseconds:0.#} ms");
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

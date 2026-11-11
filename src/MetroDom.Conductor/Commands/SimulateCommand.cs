using System.ComponentModel;
using System.Globalization;
using Melanchall.DryWetMidi.Core;
using MetroDom.Conductor.Cli;
using MetroDom.Core.Arranging;
using MetroDom.Core.Simulation;
using MetroDom.Core.Timing;
using Spectre.Console;
using Spectre.Console.Cli;

namespace MetroDom.Conductor.Commands;

/// <summary>
/// Plays a song to a band of virtual instruments on a virtual clock, through the real Orchestra
/// and scheduler, and reports how well they line up. No devices, instant, repeatable (roadmap S1).
/// </summary>
internal sealed class SimulateCommand : Command<SimulateCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[SONG]")]
        [Description("Standard MIDI File (.mid). Omit to use the built-in two-piece demo.")]
        public string? Path { get; init; }

        [CommandOption("-i|--instrument <SPEC>")]
        [Description("A virtual instrument, NAME[=CHANNELS]@LATENCY_MS[/NOTES], repeatable (e.g. [bold]-i xylo=1@10 -i drums=10@35[/]). Default: xylo on every channel but 10 at 10 ms, drums on 10 at 35 ms.")]
        public string[]? Instruments { get; init; }

        [CommandOption("--no-compensation")]
        [Description("Send every note on the song clock, ignoring latency, to see the problem compensation solves.")]
        public bool NoCompensation { get; init; }

        [CommandOption("--jitter <MS>")]
        [Description("Each strike lands up to ± this many ms off its declared latency, like real mechanics.")]
        [DefaultValue(0.0)]
        public double JitterMs { get; init; }

        [CommandOption("--seed <N>")]
        [Description("Seed for --jitter. Same seed, same result.")]
        [DefaultValue(0)]
        public int Seed { get; init; }

        [CommandOption("-a|--arrange")]
        [Description("Let the arranger give each instrument the part it plays best (transposed, folded, thinned to fit), instead of routing by channel.")]
        public bool Arrange { get; init; }

        [CommandOption("--notes")]
        [Description("Also list every note as heard, with its offset.")]
        public bool ShowNotes { get; init; }

        public IReadOnlyList<InstrumentSpec> Band =>
            InstrumentSpec.TryParseAll(Instruments, out var specs, out _) && specs.Count > 0 ? specs : InstrumentSpec.DefaultBand;

        public override ValidationResult Validate()
        {
            if (Path is not null && !File.Exists(Path))
            {
                return ValidationResult.Error($"file not found: {Path}");
            }

            if (JitterMs is < 0 or > 1000)
            {
                return ValidationResult.Error("--jitter must be between 0 and 1000 ms.");
            }

            return InstrumentSpec.TryParseAll(Instruments, out _, out var error)
                ? ValidationResult.Success()
                : ValidationResult.Error($"--instrument: {error}");
        }
    }

    public override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var midiFile = settings.Path is null ? DemoSong.Create() : MidiFile.Read(settings.Path);
        var songName = settings.Path is null ? "built-in demo" : System.IO.Path.GetFileName(settings.Path);
        var band = settings.Band;
        var options = new SimulationOptions
        {
            Compensate = !settings.NoCompensation,
            Jitter = TimeSpan.FromMilliseconds(settings.JitterMs),
            Seed = settings.Seed,
        };

        IReadOnlyList<TimedMidiEvent> timeline;
        IReadOnlyList<SimulatedPart> parts;
        if (settings.Arrange)
        {
            var members = band.Select(b => b.ToBandMember()).ToArray();
            var arrangement = Arranger.Arrange(midiFile, members, new ArrangerOptions { Roles = SongRoles.Load(settings.Path) });
            ArrangementView.Write(arrangement, members);
            AnsiConsole.WriteLine();
            timeline = arrangement.Timeline;
            parts = members.Select(m => new SimulatedPart(m.Profile, [m.Channel])).ToArray();
        }
        else
        {
            timeline = SongTimeline.From(midiFile);
            parts = band.Select(b => new SimulatedPart(b.ToProfile(), b.Channels)).ToArray();
        }

        var result = BandSimulation.Run(timeline, parts, options);

        Report(songName, band, options, result, settings.ShowNotes);
        return 0;
    }

    private static void Report(string songName, IReadOnlyList<InstrumentSpec> band, SimulationOptions options, SimulationResult result, bool showNotes)
    {
        var inv = CultureInfo.InvariantCulture;
        var summary = new Grid().AddColumn(new GridColumn().PadRight(2)).AddColumn();
        summary.AddRow("[grey]Song[/]", $"[bold]{Markup.Escape(songName)}[/]");
        summary.AddRow("[grey]Compensation[/]", options.Compensate
            ? string.Create(inv, $"on [grey](band runs {Ms(result.Lookahead)} behind the song clock)[/]")
            : "[yellow]off[/]");
        summary.AddRow("[grey]Jitter[/]", options.Jitter == TimeSpan.Zero ? "none" : string.Create(inv, $"± {Ms(options.Jitter)} [grey](seed {options.Seed})[/]"));
        AnsiConsole.Write(summary);
        AnsiConsole.WriteLine();

        var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey)
            .AddColumn("Instrument")
            .AddColumn("Channels")
            .AddColumn(new TableColumn("Latency").RightAligned())
            .AddColumn(new TableColumn("Played").RightAligned())
            .AddColumn(new TableColumn("Dropped").RightAligned())
            .AddColumn(new TableColumn("Heard").RightAligned())
            .AddColumn("Panic");
        for (var i = 0; i < band.Count; i++)
        {
            var r = result.Instruments[i];
            table.AddRow(
                Markup.Escape(r.Name),
                ChannelList.Format(band[i].Channels),
                Ms(r.Latency),
                r.Played.ToString(inv),
                r.Dropped + r.Refused == 0 ? "[grey]0[/]" : $"[yellow]{r.Dropped + r.Refused}[/]",
                r.Played == 0 ? "[grey]-[/]" : Offsets(r.MinOffset, r.MaxOffset),
                r.GotPanic ? "[green]✓[/]" : "[red]✗[/]");
        }

        AnsiConsole.Write(table);

        if (showNotes)
        {
            var notes = new Table().Border(TableBorder.Simple)
                .AddColumn(new TableColumn("Song time").RightAligned())
                .AddColumn("Instrument")
                .AddColumn(new TableColumn("Note").RightAligned())
                .AddColumn(new TableColumn("Offset").RightAligned());
            foreach (var n in result.Notes)
            {
                notes.AddRow(string.Create(inv, $"{n.SongTime:m\\:ss\\.fff}"), Markup.Escape(n.Instrument), n.Note.ToString(inv), Signed(n.Offset));
            }

            AnsiConsole.Write(notes);
        }

        if (result.SharedMoments == 0)
        {
            AnsiConsole.MarkupLine("[grey]No moments where two instruments play together, so nothing to align.[/]");
            return;
        }

        const double budgetMs = 5; // ADR 0004 proposed jitter budget between instruments.
        var spread = result.MaxSpread.TotalMilliseconds;
        var color = spread <= budgetMs ? "green" : "red";
        AnsiConsole.MarkupLine(string.Create(
            inv,
            $"Worst spread between instruments meant to sound together: [{color} bold]{spread:0.#} ms[/] " +
            $"[grey]over {result.SharedMoments} shared moments (budget ≤ {budgetMs:0} ms, ADR 0004)[/]"));
    }

    private static string Ms(TimeSpan t) => string.Create(CultureInfo.InvariantCulture, $"{t.TotalMilliseconds:0.#} ms");

    private static string Signed(TimeSpan t) => string.Create(CultureInfo.InvariantCulture, $"{t.TotalMilliseconds:+0.#;-0.#;0} ms");

    private static string Offsets(TimeSpan min, TimeSpan max) =>
        min == max ? Signed(min) : $"{Signed(min)} … {Signed(max)}";
}

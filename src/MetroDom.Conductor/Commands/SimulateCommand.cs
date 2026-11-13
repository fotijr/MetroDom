using System.ComponentModel;
using System.Globalization;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;
using MetroDom.Conductor.Cli;
using MetroDom.Core;
using MetroDom.Core.Arranging;
using MetroDom.Core.Bodies;
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
        [Description("A virtual instrument, NAME[[=CHANNELS]]@LATENCY_MS[[/NOTES]], repeatable (e.g. [bold]-i xylo=1@10 -i drums=10@35[/]). Default: xylo on every channel but 10 at 10 ms, drums on 10 at 35 ms.")]
        public string[]? Instruments { get; init; }

        [CommandOption("-b|--body <FILE>")]
        [Description("A brain running this body file, repeatable: it strikes and refuses exactly as the real one would, and the report shows what it would refuse played straight from a DAW. Omni bodies hear every channel ([bold]--arrange[/]: the first free one). Without [bold]-i[/], only the bodies.")]
        public string[]? Bodies { get; init; }

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

        [CommandOption("-s|--seat <ROLE>")]
        [Description(SeatList.Help)]
        public string[]? Seat { get; init; }

        public IReadOnlyList<Core.Arranging.Seat> Seats => SeatList.TryParse(Seat, out var seats, out _) ? seats : [];

        [CommandOption("-a|--arrange")]
        [Description("Let the arranger give each instrument the part it plays best (transposed, folded, thinned to fit), instead of routing by channel.")]
        public bool Arrange { get; init; }

        [CommandOption("--follow <HITS>")]
        [Description("Instead of the band report: let the band follow a player's recorded hits (a file from [bold]metrodom beats --record[/], one time in seconds per line) and report how close its beats land to theirs.")]
        public string? Follow { get; init; }

        [CommandOption("--notes")]
        [Description("Also list every note as heard, with its offset.")]
        public bool ShowNotes { get; init; }

        public IReadOnlyList<InstrumentSpec> Band =>
            InstrumentSpec.TryParseAll(Instruments, out var specs, out _) && specs.Count > 0 ? specs
            : Bodies is { Length: > 0 } ? []
            : InstrumentSpec.DefaultBand;

        public override ValidationResult Validate()
        {
            if (!SeatList.TryParse(Seat, out _, out var seatError))
            {
                return ValidationResult.Error($"--seat: {seatError}");
            }

            if (Path is not null && !File.Exists(Path))
            {
                return ValidationResult.Error($"file not found: {Path}");
            }

            if (Follow is not null)
            {
                if (!File.Exists(Follow))
                {
                    return ValidationResult.Error($"--follow: file not found: {Follow}");
                }

                if (!HitLog.TryParse(File.ReadAllText(Follow), out _, out var hitError))
                {
                    return ValidationResult.Error($"--follow: {hitError}");
                }
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
        var roles = SongRoles.Load(settings.Path);
        var midiFile = SeatList.Apply(settings.Path is null ? DemoSong.Create() : MidiFile.Read(settings.Path), settings.Seats, roles);
        var songName = settings.Path is null ? "built-in demo" : System.IO.Path.GetFileName(settings.Path);
        var band = settings.Band;
        var bodies = (settings.Bodies ?? []).Select(BodyFile.Load).ToArray();
        if (settings.Follow is { } follow)
        {
            var hits = HitLog.TryParse(File.ReadAllText(follow), out var parsed, out _) ? parsed : [];
            var latency = band.Select(b => b.Latency).Concat(bodies.Select(b => b.ToProfile().MaxLatency)).Max();
            var bpm = midiFile.GetTempoMap().GetTempoAtTime(new MidiTimeSpan(0)).BeatsPerMinute;
            var followed = FollowSimulation.Run(hits, SongBeats.From(midiFile), bpm, new FollowOptions { Latency = latency });
            ReportFollow(songName, System.IO.Path.GetFileName(follow), bpm, latency, hits.Count, followed, settings.ShowNotes);
            return 0;
        }

        var options = new SimulationOptions
        {
            Compensate = !settings.NoCompensation,
            Jitter = TimeSpan.FromMilliseconds(settings.JitterMs),
            Seed = settings.Seed,
        };

        IReadOnlyList<TimedMidiEvent> timeline;
        IReadOnlyList<SimulatedPart> parts;
        var rows = band.Select(b => b.Channels).ToList();
        if (settings.Arrange)
        {
            var members = band.Select(b => b.ToBandMember()).ToList();
            var bodyMembers = BodyMembers(bodies, members.Select(m => m.Channel));
            members.AddRange(bodyMembers);
            rows.AddRange(bodyMembers.Select(m => (IReadOnlyList<int>)[m.Channel]));
            var arrangement = Arranger.Arrange(midiFile, members, new ArrangerOptions { Roles = roles });
            ArrangementView.Write(arrangement, members);
            AnsiConsole.WriteLine();
            timeline = arrangement.Timeline;
            parts = members.Select((m, i) => new SimulatedPart(m.Profile, [m.Channel]) { Body = i < band.Count ? null : bodies[i - band.Count] }).ToArray();
        }
        else
        {
            timeline = SongTimeline.From(midiFile);
            var bodyParts = bodies.Select(b => new SimulatedPart(b.ToProfile(), b.Channel is { } c ? [c] : InstrumentSpec.AllChannels) { Body = b }).ToArray();
            rows.AddRange(bodyParts.Select(p => p.Channels));
            parts = [.. band.Select(b => new SimulatedPart(b.ToProfile(), b.Channels)), .. bodyParts];
        }

        var result = BandSimulation.Run(timeline, parts, options);

        Report(songName, rows, options, result, settings.ShowNotes);
        if (bodies.Length > 0)
        {
            ReportBrains(BandSimulation.Run(timeline, parts, options with { Governed = false }), band.Count);
        }

        return 0;
    }

    /// <summary>Each body on its own channel, or the first free one, as the auto band picks for an omni brain.</summary>
    private static List<BandMember> BodyMembers(IReadOnlyList<BodyDefinition> bodies, IEnumerable<int> taken)
    {
        var used = taken.ToHashSet();
        var members = new List<BandMember>();
        foreach (var body in bodies)
        {
            var channel = body.Channel is { } own && !used.Contains(own)
                ? own
                : Enumerable.Range(0, Orchestra.ChannelCount).Where(c => c != Core.Arranging.GeneralMidi.PercussionChannel || body.Channel == c).Cast<int?>().FirstOrDefault(c => !used.Contains(c!.Value))
                    ?? throw new UsageException("every channel is taken: too many instruments to arrange.");
            used.Add(channel);
            members.Add(new BandMember(body.ToProfile(), channel));
        }

        return members;
    }

    /// <summary>What each body's brain refuses with nothing in front of it, as when a DAW plays it.</summary>
    private static void ReportBrains(SimulationResult straight, int firstBody)
    {
        var inv = CultureInfo.InvariantCulture;
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Played straight to each brain[/] [grey](a DAW or keyboard, no governor in front)[/]");
        foreach (var r in straight.Instruments.Skip(firstBody))
        {
            var refused = r.RefusedBy.Values.Sum();
            var why = string.Join(", ", r.RefusedBy.Where(kv => kv.Value > 0).OrderByDescending(kv => kv.Value)
                .Select(kv => string.Create(inv, $"{kv.Value} {Reason(kv.Key)}")));
            var verdict = refused == 0 ? "[green]refused 0[/]" : string.Create(inv, $"[yellow]refused {refused}[/] [grey]({why})[/]");
            var others = r.Dropped == 0 ? "" : string.Create(inv, $" [grey]· {r.Dropped} not its notes[/]");
            AnsiConsole.MarkupLine(string.Create(inv, $"  {Markup.Escape(r.Name)}: struck {r.Played}, {verdict}{others}"));
        }

        static string Reason(BrainRefusal why) => why switch
        {
            BrainRefusal.Busy => "still striking",
            BrainRefusal.Interval => "under minIntervalMs",
            BrainRefusal.Duty => "over duty",
            BrainRefusal.Polyphony => "over polyphony",
            _ => why.ToString().ToLowerInvariant(),
        };
    }

    /// <summary>Held by the governor, plus anything that still got past it to be dropped or refused.</summary>
    private static string Held(InstrumentResult r)
    {
        var past = r.Dropped + r.Refused;
        var held = r.Held == 0 ? "[grey]0[/]" : $"[yellow]{r.Held.ToString(CultureInfo.InvariantCulture)}[/]";
        return past == 0 ? held : $"{held} [red]+{past.ToString(CultureInfo.InvariantCulture)} refused[/]";
    }

    private static void Report(string songName, List<IReadOnlyList<int>> channels, SimulationOptions options, SimulationResult result, bool showNotes)
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
            .AddColumn(new TableColumn("Held").RightAligned())
            .AddColumn(new TableColumn("Heard").RightAligned())
            .AddColumn("Panic");
        for (var i = 0; i < channels.Count; i++)
        {
            var r = result.Instruments[i];
            table.AddRow(
                Markup.Escape(r.Name),
                ChannelList.Format(channels[i]),
                Ms(r.Latency),
                r.Played.ToString(inv),
                Held(r),
                r.Played == 0 ? "[grey]-[/]" : Offsets(r.MinOffset, r.MaxOffset),
                r.GotPanic ? "[green]✓[/]" : "[red]✗[/]");
        }

        AnsiConsole.Write(table);
        foreach (var r in result.Instruments.Where(r => r.HeldWhy is not null))
        {
            AnsiConsole.MarkupLine($"[grey]{Markup.Escape(r.Name)}: governor held {Markup.Escape(r.HeldWhy!)}[/]");
        }

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

    private static void ReportFollow(string songName, string hitsName, double fileBpm, TimeSpan latency, int hitCount, FollowResult result, bool showBeats)
    {
        const int settle = 4;
        var inv = CultureInfo.InvariantCulture;
        var summary = new Grid().AddColumn(new GridColumn().PadRight(2)).AddColumn();
        summary.AddRow("[grey]Song[/]", string.Create(inv, $"[bold]{Markup.Escape(songName)}[/] [grey](file {fileBpm:0.#} BPM)[/]"));
        summary.AddRow("[grey]Player[/]", string.Create(inv, $"{Markup.Escape(hitsName)} [grey]({hitCount} hits)[/]"));
        summary.AddRow("[grey]Band latency[/]", Ms(latency));
        AnsiConsole.Write(summary);
        AnsiConsole.WriteLine();

        if (result.LockedAt is not { } locked)
        {
            AnsiConsole.MarkupLine("[yellow]Never locked on:[/] [grey]the tracker needs 4 hits at a steady interval to count the band in.[/]");
            return;
        }

        AnsiConsole.MarkupLine(string.Create(inv, $"Counted in at {locked.TotalSeconds:0.00} s; the band played {result.Beats.Count} beats and ended {result.Final.State.ToString().ToLowerInvariant()} at {result.Final.Bpm:0.#} BPM."));
        if (showBeats)
        {
            var table = new Table().Border(TableBorder.Simple)
                .AddColumn(new TableColumn("Beat").RightAligned())
                .AddColumn(new TableColumn("Band").RightAligned())
                .AddColumn(new TableColumn("Player").RightAligned())
                .AddColumn(new TableColumn("Band − player").RightAligned());
            foreach (var beat in result.Beats)
            {
                table.AddRow(
                    (beat.Index + 1).ToString(inv),
                    string.Create(inv, $"{beat.Heard.TotalSeconds:0.000}"),
                    beat.Hit is { } hit ? string.Create(inv, $"{hit.TotalSeconds:0.000}") : "[grey]-[/]",
                    beat.Error is { } error ? Signed(error) : "[grey]no hit[/]");
            }

            AnsiConsole.Write(table);
        }

        var errors = result.Errors(settle).Select(e => e.Duration().TotalMilliseconds).Order().ToArray();
        if (errors.Length == 0)
        {
            AnsiConsole.MarkupLine("[grey]No player hits near the band's beats after it settled, so nothing to compare.[/]");
            return;
        }

        const double budgetMs = 20; // Audible flamming between a drummer and the band starts around here.
        var p95 = errors[Math.Min(errors.Length - 1, (int)(errors.Length * 0.95))];
        var color = p95 <= budgetMs ? "green" : "yellow";
        AnsiConsole.MarkupLine(string.Create(
            inv,
            $"Band vs player after {settle} beats: median [bold]{errors[errors.Length / 2]:0.#} ms[/], 95% within [{color} bold]{p95:0.#} ms[/], worst {errors[^1]:0.#} ms [grey]over {errors.Length} beats with a hit[/]"));
    }

    private static string Ms(TimeSpan t) => string.Create(CultureInfo.InvariantCulture, $"{t.TotalMilliseconds:0.#} ms");

    private static string Signed(TimeSpan t) => string.Create(CultureInfo.InvariantCulture, $"{t.TotalMilliseconds:+0.#;-0.#;0} ms");

    private static string Offsets(TimeSpan min, TimeSpan max) =>
        min == max ? Signed(min) : $"{Signed(min)} … {Signed(max)}";
}

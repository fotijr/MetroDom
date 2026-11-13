using System.ComponentModel;
using System.Globalization;
using System.Text;
using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.MusicTheory;
using MetroDom.Conductor.Cli;
using MetroDom.Conductor.Midi;
using MetroDom.Core;
using MetroDom.Core.Calibration;
using MetroDom.Core.Safety;
using MetroDom.Core.Timing;
using Spectre.Console;
using Spectre.Console.Cli;

namespace MetroDom.Conductor.Commands;

/// <summary>The person at the bench: answers what only ears and fingers can tell.</summary>
internal interface IBenchOperator
{
    /// <summary>One of <paramref name="choices"/>, or null when nobody can answer (not a terminal).</summary>
    string? Ask(string question, IReadOnlyList<string> choices);

    void Say(string markup);
}

/// <summary>Prompts in a terminal; answers nothing when piped, so every answer reads "not asked".</summary>
internal sealed class ConsoleOperator : IBenchOperator
{
    public string? Ask(string question, IReadOnlyList<string> choices) =>
        Ui.IsInteractive
            ? AnsiConsole.Prompt(new SelectionPrompt<string>().Title(question).AddChoices(choices))
            : null;

    public void Say(string markup) => AnsiConsole.MarkupLine(markup);
}

internal sealed record BenchOptions
{
    /// <summary>Velocity of the soft strike; the hard one is 127.</summary>
    public int SoftVelocity { get; init; } = 30;

    /// <summary>Gap between strikes while listening, so each one can be told apart.</summary>
    public TimeSpan Gap { get; init; } = TimeSpan.FromMilliseconds(600);

    /// <summary>How long to hammer one note at the limit; zero skips the soak.</summary>
    public TimeSpan Soak { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>The note to soak, or the lowest when null.</summary>
    public int? SoakNote { get; init; }

    /// <summary>Latency measurement, when there's an onset input.</summary>
    public CalibrationOptions Calibration { get; init; } = new();
}

/// <summary>One note's bench result. A null answer means nobody was asked.</summary>
internal sealed record BenchNote(int Note, string? Heard, TimeSpan? Latency);

/// <summary>What a bench run found, ready to paste into a build record.</summary>
internal sealed record BenchReport(
    string Instrument,
    IReadOnlyList<BenchNote> Notes,
    int SoakNote,
    TimeSpan Soak,
    int SoakStruck,
    int SoakHeld,
    string? Coil,
    CalibrationResult? Calibration)
{
    public static readonly string[] HeardChoices = ["both", "soft only", "hard only", "neither", "again"];
    public static readonly string[] CoilChoices = ["cool", "warm", "hot (can't hold it)"];

    /// <summary>The bench table for the instrument's README and its build record.</summary>
    public string Markdown()
    {
        var inv = CultureInfo.InvariantCulture;
        var s = new StringBuilder();
        s.AppendLine(inv, $"Bench: {Instrument}, {DateTime.Now:yyyy-MM-dd} (`metrodom bench`)");
        s.AppendLine();
        s.AppendLine("| Note | Heard (soft, hard) | Latency |");
        s.AppendLine("| --- | --- | --- |");
        foreach (var n in Notes)
        {
            var latency = n.Latency is { } l ? string.Create(inv, $"{l.TotalMilliseconds:0.#} ms") : "not measured";
            s.AppendLine(inv, $"| {Name(n.Note)} ({n.Note}) | {n.Heard ?? "not asked"} | {latency} |");
        }

        s.AppendLine();
        s.AppendLine(Soak == TimeSpan.Zero
            ? "Soak: skipped."
            : string.Create(inv, $"Soak: {Name(SoakNote)} ({SoakNote}) at its limit for {Soak.TotalSeconds:0} s: {SoakStruck} strikes, {SoakHeld} held by the duty cycle and interval. Coil afterwards: {Coil ?? "not asked"}."));
        return s.ToString();
    }

    internal static string Name(int note) => Note.Get((SevenBitNumber)(byte)note).ToString();
}

/// <summary>
/// The first power-on, step by step (plan 0006 N6): every note soft and hard with a person
/// listening, a soak at the duty limit, then latency from an onset input if there is one. Every
/// strike goes through the safety governor, as in <c>play</c>.
/// </summary>
internal static class Bench
{
    /// <param name="output">The instrument, behind its <see cref="SafetyGovernor"/>.</param>
    /// <param name="wait">Waits (returns true when stopped).</param>
    /// <param name="onsets">An onset input to measure latency, or null.</param>
    public static BenchReport Run(SafetyGovernor output, int channel, IBenchOperator op, IClock clock, Func<TimeSpan, bool> wait, BenchOptions options, OnsetListener? onsets = null)
    {
        var profile = output.Profile;
        var notes = (profile.Notes ?? new HashSet<int>()).Order().ToArray();
        var results = new List<BenchNote>();
        var inv = CultureInfo.InvariantCulture;
        var soakNote = options.SoakNote ?? notes.FirstOrDefault();
        int struck = 0, held = 0;
        string? coil = null;
        CalibrationResult? calibration = null;

        op.Say(string.Create(inv, $"[bold]1. Every note, soft then hard[/] [grey]({notes.Length} notes, velocity {options.SoftVelocity} then 127)[/]"));
        foreach (var note in notes)
        {
            string? heard;
            do
            {
                Strike(output, channel, note, options.SoftVelocity);
                if (wait(options.Gap))
                {
                    return Report();
                }

                Strike(output, channel, note, 127);
                if (wait(options.Gap))
                {
                    return Report();
                }

                heard = op.Ask(string.Create(inv, $"Heard {BenchReport.Name(note)} ({note}), soft then hard?"), BenchReport.HeardChoices);
            }
            while (heard == "again");
            results.Add(new BenchNote(note, heard, null));
        }

        if (options.Soak > TimeSpan.Zero && notes.Length > 0)
        {
            op.Say(string.Create(inv, $"[bold]2. Soak[/] [grey]{BenchReport.Name(soakNote)} ({soakNote}) as fast as the brain allows for {options.Soak.TotalSeconds:0} s. Ctrl+C stops it.[/]"));
            var interval = profile.IntervalFor(soakNote) > TimeSpan.Zero ? profile.IntervalFor(soakNote) : TimeSpan.FromMilliseconds(60);
            var end = clock.Now + options.Soak;
            while (clock.Now < end)
            {
                var before = output.TotalBlocked;
                Strike(output, channel, soakNote, 127);
                if (output.TotalBlocked == before)
                {
                    struck++;
                }
                else
                {
                    held++;
                }

                if (wait(interval))
                {
                    break;
                }
            }

            coil = op.Ask("Touch the coil (power off first if you like). How warm is it?", BenchReport.CoilChoices);
        }

        if (onsets is not null && notes.Length > 0)
        {
            op.Say(string.Create(inv, $"[bold]3. Latency[/] [grey]{notes.Length} note(s) × {options.Calibration.Repeats}, listening on {Markup.Escape(onsets.Name)}[/]"));
            calibration = Calibrator.Measure(output, channel, notes, options.Calibration, onsets, clock, wait);
            results = [.. results.Select(r => r with { Latency = calibration.Notes.FirstOrDefault(n => n.Note == r.Note)?.Median })];
        }

        return Report();

        BenchReport Report() => new(profile.Name, results, soakNote, options.Soak, struck, held, coil, calibration);
    }

    private static void Strike(SafetyGovernor output, int channel, int note, int velocity)
    {
        output.Send(new NoteOnEvent((SevenBitNumber)note, (SevenBitNumber)velocity) { Channel = (FourBitNumber)channel });
        output.Send(new NoteOffEvent((SevenBitNumber)note, SevenBitNumber.MinValue) { Channel = (FourBitNumber)channel });
    }
}

/// <summary>Walks through an instrument's first power-on and writes up what it found.</summary>
internal sealed class BenchCommand : Command<BenchCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<PORT>")]
        [Description("The instrument's port: index, name, or unique part of a name from [bold]metrodom devices[/]. It must be a MetroDom brain (or [bold]metrodom virtual --body[/]).")]
        public string Port { get; init; } = "";

        [CommandOption("--onsets <PORT>")]
        [Description(OnsetOptions.OnsetsHelp + " Optional: with it, each note's latency is measured and saved for [bold]body write --calibrated[/]; without it, latency stays not measured.")]
        public string? Onsets { get; init; }

        [CommandOption("--onsets-on <TRIGGER>")]
        [Description(OnsetOptions.OnHelp)]
        public string? OnsetsOn { get; init; }

        [CommandOption("--onset-latency <MS>")]
        [Description(OnsetOptions.LatencyHelp)]
        public double? OnsetLatency { get; init; }

        [CommandOption("--soak <SECONDS>")]
        [Description("How long to strike one note at its limit, to feel the coil afterwards (0-600). Default 60; 0 skips it.")]
        [DefaultValue(60)]
        public int Soak { get; init; } = 60;

        [CommandOption("--soak-note <NOTE>")]
        [Description("The note to soak (number or name). Default: the lowest.")]
        public string? SoakNote { get; init; }

        [CommandOption("--out <FILE>")]
        [Description("Also write the bench table as Markdown, for the instrument's README and its build record.")]
        public string? Out { get; init; }

        [CommandOption("--calibrations <FILE>")]
        [Description("Where measured latencies are saved (with [bold]--onsets[/]). Default: $METRODOM_CALIBRATIONS, else the one [bold]calibrate[/] uses.")]
        public string? Calibrations { get; init; }

        public override ValidationResult Validate()
        {
            if (Soak is < 0 or > 600)
            {
                return ValidationResult.Error("--soak must be 0-600 seconds.");
            }

            if (SoakNote is not null && (!NoteList.TryParse(SoakNote, out var n, out _) || n.Count != 1))
            {
                return ValidationResult.Error("--soak-note must be one note (e.g. 38 or D2).");
            }

            return Onsets is not null && OnsetOptions.Validate(Onsets, OnsetsOn, OnsetLatency) is { } error
                ? ValidationResult.Error(error)
                : ValidationResult.Success();
        }
    }

    public override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var backend = MidiDevices.Backend.Value;
        var info = MidiDevices.FindOutput(settings.Port);
        var identified = Identifier.Identify(backend, Identifier.DefaultTimeout, [info])[0];
        if (identified.Kind != PortKind.MetroDom || identified.Profile is not { } profile)
        {
            throw new UsageException($"{info.Name} didn't identify as a MetroDom instrument with a profile{(identified.ProfileError is { } e ? $" ({e})" : "")}. bench drives MetroDom brains; for other devices use metrodom calibrate.");
        }

        if (profile.Notes is not { Count: > 0 })
        {
            throw new UsageException($"{info.Name} has no notes: a silent brain ({profile.Name}). Write it a body with metrodom body write.");
        }

        var soakNote = settings.SoakNote is { } raw && NoteList.TryParse(raw, out var parsed, out _) ? parsed.First() : (int?)null;
        if (soakNote is { } sn && !profile.CanPlay(sn))
        {
            throw new UsageException($"--soak-note {sn} isn't one of {profile.Name}'s notes ({NoteList.Format(profile.Notes)}).");
        }

        var clock = new SystemClock();
        using var stop = new ManualResetEventSlim();
        void OnCancel(object? sender, ConsoleCancelEventArgs e)
        {
            e.Cancel = true;
            stop.Set();
        }

        Console.CancelKeyPress += OnCancel;
        using var registration = cancellationToken.Register(stop.Set);
        using var port = MidiDevices.OpenOutput(info);
        var governor = new SafetyGovernor(port, profile, SafetyLimits.None, clock);
        var channel = profile.Channel ?? 0;
        using var onsets = settings.Onsets is not null ? Calibrator.Listen(OnsetOptions.Source(settings.Onsets, settings.OnsetsOn, settings.OnsetLatency)!, clock) : null;
        var options = new BenchOptions
        {
            Soak = TimeSpan.FromSeconds(settings.Soak),
            SoakNote = soakNote,
            Calibration = new CalibrationOptions { OnsetLatency = TimeSpan.FromMilliseconds(settings.OnsetLatency ?? 0) },
        };

        AnsiConsole.MarkupLine(string.Create(CultureInfo.InvariantCulture,
            $"[bold]Bench: {Markup.Escape(profile.Name)}[/] [grey]on {Markup.Escape(info.Name)}, channel {channel + 1}, notes {NoteList.Format(profile.Notes)}. Every strike goes through the safety governor.[/]"));
        BenchReport report;
        try
        {
            report = Bench.Run(governor, channel, new ConsoleOperator(), clock, stop.Wait, options, onsets);
        }
        finally
        {
            Console.CancelKeyPress -= OnCancel;
            Orchestra.SendPanic(governor);
        }

        var markdown = report.Markdown();
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Panel(Markup.Escape(markdown.TrimEnd())).Header("Bench results").BorderColor(Color.Grey));
        if (settings.Out is { } path)
        {
            File.WriteAllText(path, markdown);
            Ui.Success($"Wrote {path}: paste it into the instrument's README and its build record.");
        }

        if (report.Calibration is { HeardNone: false } result && MeasuredLatency.From(info.Name, result, profile, DateTimeOffset.Now) is { } measured)
        {
            var store = CalibrationStore.Resolve(settings.Calibrations);
            var book = CalibrationStore.Load(store, Ui.Warn);
            book.Set(measured);
            CalibrationStore.Save(book, store);
            Ui.Success($"Saved {info.Name}'s latency ({Calibrator.Describe(result)}). Store it in the body: metrodom body write \"{info.Name}\" --calibrated");
        }
        else if (settings.Onsets is null)
        {
            Ui.Note("Latency not measured: run again with --onsets (a piezo or mic-to-MIDI input), or time a phone recording.");
        }

        var problems = report.Notes.Count(n => n.Heard is "neither" or "soft only" or "hard only");
        if (problems > 0)
        {
            Ui.Warn($"{problems} note(s) didn't sound both soft and hard: check wiring, pulseMs and minPulseMs in body.json.");
        }

        return 0;
    }
}

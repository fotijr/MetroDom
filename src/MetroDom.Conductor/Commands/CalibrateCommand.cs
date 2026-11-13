using System.ComponentModel;
using System.Globalization;
using MetroDom.Conductor.Cli;
using MetroDom.Core;
using MetroDom.Core.Arranging;
using MetroDom.Core.Calibration;
using MetroDom.Core.Timing;
using Spectre.Console;
using Spectre.Console.Cli;

namespace MetroDom.Conductor.Commands;

/// <summary>
/// Measures each instrument's latency, note by note (ADR 0004): strike, listen on the onset
/// input, take the median. Saves the result so <c>play</c>, <c>serve</c> and <c>route</c> use it.
/// </summary>
internal sealed class CalibrateCommand : Command<CalibrateCommand.Settings>
{
    public sealed class Settings : OutputSettings
    {
        [CommandOption("--onsets <PORT>")]
        [Description(OnsetOptions.OnsetsHelp + " Required.")]
        public string? Onsets { get; init; }

        [CommandOption("--onsets-on <TRIGGER>")]
        [Description(OnsetOptions.OnHelp)]
        public string? OnsetsOn { get; init; }

        [CommandOption("--onset-latency <MS>")]
        [Description(OnsetOptions.LatencyHelp)]
        public double? OnsetLatency { get; init; }

        [CommandOption("--repeats <N>")]
        [Description("Strikes per note (1-20); the median counts. Default 5, or 3 with [bold]--quick[/].")]
        public int? Repeats { get; init; }

        [CommandOption("--window <MS>")]
        [Description("How long after a strike its onset may come (50-2000 ms). Default 500. Raise it for slow mechanisms (servos).")]
        public double? Window { get; init; }

        [CommandOption("--quick")]
        [Description("The boot check only: up to three notes each, compared with the stored latencies. Nothing is saved.")]
        public bool Quick { get; init; }

        [CommandOption("--no-save")]
        [Description("Measure and show, but don't keep the result.")]
        public bool NoSave { get; init; }

        public OnsetSource? Source => OnsetOptions.Source(Onsets, OnsetsOn, OnsetLatency);

        public CalibrationOptions Options => (Quick ? CalibrationOptions.Quick : new CalibrationOptions()) with
        {
            Repeats = Repeats ?? (Quick ? CalibrationOptions.Quick.Repeats : new CalibrationOptions().Repeats),
            Window = Window is { } window ? TimeSpan.FromMilliseconds(window) : new CalibrationOptions().Window,
            OnsetLatency = TimeSpan.FromMilliseconds(OnsetLatency ?? 0),
        };

        public override ValidationResult Validate()
        {
            var baseResult = base.Validate();
            if (!baseResult.Successful)
            {
                return baseResult;
            }

            if (Onsets is null)
            {
                return ValidationResult.Error("missing --onsets <PORT>: the input that hears the instruments (try metrodom virtual --onsets).");
            }

            if (OnsetOptions.Validate(Onsets, OnsetsOn, OnsetLatency) is { } error)
            {
                return ValidationResult.Error(error);
            }

            if (Repeats is < 1 or > 20)
            {
                return ValidationResult.Error("--repeats must be 1-20.");
            }

            return Window is < 50 or > 2000 ? ValidationResult.Error("--window must be 50-2000 ms.") : ValidationResult.Success();
        }
    }

    public override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var options = settings.Options;
        var clock = new SystemClock();
        using var session = new Session(settings);
        using var listener = Calibrator.Listen(settings.Source!, clock);
        var storePath = CalibrationStore.Resolve(settings.Calibrations);
        var book = CalibrationStore.Load(storePath, Ui.Warn);
        var outputs = session.Outputs;
        var governors = session.Governors;
        var silent = 0;
        var saved = 0;
        for (var i = 0; i < outputs.Count && !session.IsStopping; i++)
        {
            var output = outputs[i];
            var percussion = output.Channels[0] == GeneralMidi.PercussionChannel;
            var notes = settings.Quick ? LatencyProbe.QuickNotes(output.Profile, percussion) : LatencyProbe.AllNotes(output.Profile, percussion);
            var duration = new LatencyProbe(governors[i].Profile, notes, options, TimeSpan.Zero).Duration;
            Ui.Note(string.Create(CultureInfo.InvariantCulture,
                $"{output.Port.Name}: {notes.Count} note(s) × {options.Repeats} on channel {output.Channels[0] + 1}, listening on {listener.Name}, about {duration.TotalSeconds:0} s."));

            var result = Calibrator.Measure(governors[i], output.Channels[0], notes, options, listener, clock, session.WaitForStop);
            if (session.IsStopping)
            {
                Ui.Warn("Stopped: nothing saved for this instrument.");
                break;
            }

            AnsiConsole.Write(Table(output.Port.Name, result, governors[i].Profile, options));
            if (result.HeardNone)
            {
                silent++;
                Ui.Warn($"{output.Port.Name}: nothing heard. Is it powered, on channel {output.Channels[0] + 1}, and can {listener.Name} hear it?");
                continue;
            }

            if (settings.Quick)
            {
                var check = LatencyCheck.Compare(result, governors[i].Profile, options.DriftThreshold);
                (check.Status == CheckStatus.Ok ? (Action<string>)Ui.Success : Ui.Warn)(Calibrator.Describe(output.Port.Name, check));
                continue;
            }

            Ui.Success($"{output.Port.Name}: {Calibrator.Describe(result)}.");
            if (!result.HeardAll)
            {
                Ui.Warn($"{output.Port.Name}: note(s) {string.Join(", ", result.Notes.Where(n => !n.Heard).Select(n => n.Note))} never heard; they keep their old latency.");
            }

            if (!settings.NoSave && MeasuredLatency.From(output.Port.Name, result, governors[i].Profile, DateTimeOffset.Now) is { } calibration)
            {
                book.Set(calibration);
                saved++;
            }
        }

        if (saved > 0)
        {
            CalibrationStore.Save(book, storePath);
            Ui.Success($"Saved {saved} calibration(s) to {storePath}. play, serve and route use them for these outputs.");
        }

        return silent > 0 ? 1 : 0;
    }

    private static Table Table(string name, CalibrationResult result, InstrumentProfile before, CalibrationOptions options)
    {
        var table = new Table().Border(TableBorder.Rounded).Title(Markup.Escape(name))
            .AddColumn("Note").AddColumn(new TableColumn("Was").RightAligned()).AddColumn(new TableColumn("Measured").RightAligned())
            .AddColumn(new TableColumn("Spread").RightAligned()).AddColumn(new TableColumn("Heard").RightAligned());
        foreach (var note in result.Notes)
        {
            var was = before.LatencyFor(note.Note);
            var measured = note.Median is { } m
                ? (m - was).Duration() > options.DriftThreshold ? $"[yellow]{Ms(m)}[/]" : Ms(m)
                : "[red]–[/]";
            table.AddRow(
                $"{NoteList.Name(note.Note)} [grey]({note.Note})[/]",
                Ms(was),
                measured,
                note.Heard ? Ms(note.Spread) : "",
                $"{note.Latencies.Count}/{note.Latencies.Count + note.Missed}");
        }

        if (result.Strays + result.Echoes > 0)
        {
            table.Caption(string.Create(CultureInfo.InvariantCulture, $"[grey]{result.Strays} stray, {result.Echoes} double onset(s) ignored[/]"));
        }

        return table;
    }

    private static string Ms(TimeSpan t) => string.Create(CultureInfo.InvariantCulture, $"{t.TotalMilliseconds:0.0} ms");
}

using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using Melanchall.DryWetMidi.Core;
using MetroDom.Conductor.Cli;
using MetroDom.Core.Timing;
using Spectre.Console;
using Spectre.Console.Cli;

namespace MetroDom.Conductor.Commands;

/// <summary>
/// Listens to a player (drum triggers, a pad) and shows what the beat tracker hears: tempo, lock,
/// how far each hit landed from its prediction. Optionally records the hits for
/// <c>simulate --follow</c>, so real playing becomes a repeatable test.
/// </summary>
internal sealed class BeatsCommand : Command<BeatsCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<PORT>")]
        [Description("MIDI input to listen to: index, name or part of a name from [bold]metrodom devices[/].")]
        public string Port { get; init; } = "";

        [CommandOption("--on <TRIGGER>")]
        [Description("Which messages are hits, as play's [bold]--follow-on[/] (default [bold]any[/]).")]
        public string? On { get; init; }

        [CommandOption("-t|--tempo <BPM>")]
        [Description("The tempo you mean to play, so the tracker picks beats over eighths or half notes.")]
        public double? Tempo { get; init; }

        [CommandOption("-r|--record <FILE>")]
        [Description("Write the hits to FILE (seconds,note,velocity) when you stop, for [bold]metrodom simulate --follow[/].")]
        public string? Record { get; init; }

        public override ValidationResult Validate()
        {
            if (!TapTrigger.TryParse(On, out _, out var error))
            {
                return ValidationResult.Error($"--on: {error}");
            }

            return Tempo is not (null or (>= TapTempo.MinBpm and <= TapTempo.MaxBpm))
                ? ValidationResult.Error($"--tempo must be {TapTempo.MinBpm:0}-{TapTempo.MaxBpm:0} BPM.")
                : ValidationResult.Success();
        }
    }

    public override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var trigger = TapTrigger.TryParse(settings.On, out var parsed, out _) ? parsed : new TapTrigger();
        var clock = new SystemClock();
        var tracker = new BeatTracker(settings.Tempo);
        var hits = new List<(TimeSpan At, int Note, int Velocity)>();
        var gate = new Lock();
        using var stop = new ManualResetEventSlim();
        void OnCancel(object? sender, ConsoleCancelEventArgs e)
        {
            e.Cancel = true;
            stop.Set();
        }

        Console.CancelKeyPress += OnCancel;
        using var registration = cancellationToken.Register(stop.Set);
        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, e =>
        {
            e.Cancel = true;
            stop.Set();
        });
        try
        {
            using var input = PortPicker.Input(settings.Port, "PORT");
            input.EventReceived += e =>
            {
                lock (gate)
                {
                    if (!trigger.IsTap(e))
                    {
                        return;
                    }

                    var now = clock.Now;
                    var predicted = tracker.NextBeat(now - ((tracker.Period ?? TimeSpan.Zero) / 2));
                    var counted = tracker.Hit(now);
                    hits.Add((now, e is NoteOnEvent on ? on.NoteNumber : -1, e is NoteOnEvent v ? v.Velocity : -1));
                    AnsiConsole.MarkupLine(Line(tracker, counted, predicted is { } p ? now - p : null));
                }
            };
            input.Start();
            AnsiConsole.MarkupLine($"[bold]Listening to {Markup.Escape(input.Name)}[/] [grey](hits: {Markup.Escape(trigger.ToString())}). Play a steady beat; Ctrl+C to stop.[/]");
            stop.Wait(CancellationToken.None);
        }
        finally
        {
            Console.CancelKeyPress -= OnCancel;
        }

        lock (gate)
        {
            if (settings.Record is { } path)
            {
                File.WriteAllText(path, HitLog.Write(hits));
                Ui.Success(string.Create(CultureInfo.InvariantCulture, $"Recorded {hits.Count} hits to {path}. Try: metrodom simulate --follow {path}"));
            }
            else
            {
                Ui.Success(string.Create(CultureInfo.InvariantCulture, $"Heard {hits.Count} hits."));
            }
        }

        return 0;
    }

    private static string Line(BeatTracker tracker, bool counted, TimeSpan? off) => tracker.State switch
    {
        BeatState.Listening => "[grey]·  listening[/]",
        _ when !counted => string.Create(CultureInfo.InvariantCulture, $"[grey]·  between beats ({tracker.Bpm:0.0} BPM)[/]"),
        _ => string.Create(
            CultureInfo.InvariantCulture,
            $"[green]●[/] [bold]{tracker.Bpm:0.0} BPM[/] [grey]{tracker.State.ToString().ToLowerInvariant()}, beat {tracker.Beats}{(off is { } o ? $", {o.TotalMilliseconds:+0;-0;0} ms from prediction" : "")}[/]"),
    };
}

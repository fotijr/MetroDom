using System.ComponentModel;
using Melanchall.DryWetMidi.Core;
using MetroDom.Conductor.Cli;
using MetroDom.Core.Arranging;
using MetroDom.Core.Simulation;
using Spectre.Console;
using Spectre.Console.Cli;

namespace MetroDom.Conductor.Commands;

/// <summary>Shows how a song would be split across the band, without playing it.</summary>
internal sealed class ArrangeCommand : Command<ArrangeCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[SONG]")]
        [Description("Standard MIDI File (.mid). Omit to use the built-in two-piece demo.")]
        public string? Path { get; init; }

        [CommandOption("-i|--instrument <SPEC>")]
        [Description("An instrument, NAME[=CHANNELS]@LATENCY_MS[/NOTES], repeatable (e.g. [bold]-i xylo=1@10/C4-C6 -i drums=10@35/36,38,76[/]). It plays on its first channel; channel 10 means drums. Default: xylo and drums, any notes.")]
        public string[]? Instruments { get; init; }

        [CommandOption("--no-fold")]
        [Description("Drop notes that don't fit instead of moving them by octaves.")]
        public bool NoFold { get; init; }

        public IReadOnlyList<InstrumentSpec> Band =>
            InstrumentSpec.TryParseAll(Instruments, out var specs, out _) && specs.Count > 0 ? specs : InstrumentSpec.DefaultBand;

        public override ValidationResult Validate()
        {
            if (Path is not null && !File.Exists(Path))
            {
                return ValidationResult.Error($"file not found: {Path}");
            }

            return InstrumentSpec.TryParseAll(Instruments, out _, out var error)
                ? ValidationResult.Success()
                : ValidationResult.Error($"--instrument: {error}");
        }
    }

    public override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var midiFile = settings.Path is null ? DemoSong.Create() : MidiFile.Read(settings.Path);
        var band = settings.Band.Select(s => s.ToBandMember()).ToArray();
        var arrangement = Arranger.Arrange(midiFile, band, new ArrangerOptions { FoldOctaves = !settings.NoFold });

        AnsiConsole.MarkupLine($"[grey]Song[/]  [bold]{Markup.Escape(settings.Path is null ? "built-in demo" : System.IO.Path.GetFileName(settings.Path))}[/]");
        ArrangementView.Write(arrangement, band);
        return 0;
    }
}

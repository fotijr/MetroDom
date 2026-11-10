using System.ComponentModel;
using System.Globalization;
using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.MusicTheory;
using MetroDom.Core;
using Spectre.Console;
using Spectre.Console.Cli;

namespace MetroDom.Conductor.Commands;

/// <summary>Successor to the WinForms MIDI Explorer: summarize a Standard MIDI File.</summary>
internal sealed class InspectCommand : Command<InspectCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<SONG>")]
        [Description("Standard MIDI File to summarize (.mid).")]
        public string Path { get; init; } = "";

        public override ValidationResult Validate() =>
            File.Exists(Path) ? ValidationResult.Success() : ValidationResult.Error($"file not found: {Path}");
    }

    public override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var song = SongAnalyzer.Analyze(settings.Path);
        var inv = CultureInfo.InvariantCulture;

        var summary = new Grid().AddColumn(new GridColumn().PadRight(2)).AddColumn();
        summary.AddRow("[grey]File[/]", $"[bold]{Markup.Escape(System.IO.Path.GetFileName(settings.Path))}[/]");
        summary.AddRow("[grey]Format[/]", song.Format?.ToString() ?? "unknown");
        summary.AddRow("[grey]PPQN[/]", song.TicksPerQuarterNote?.ToString(inv) ?? "SMPTE");
        summary.AddRow("[grey]Tempo[/]", string.Create(inv, $"{song.InitialTempoBpm:0.##} BPM [grey](default; humans may override)[/]"));
        summary.AddRow("[grey]Duration[/]", string.Create(inv, $"{song.Duration:m\\:ss\\.f}"));
        AnsiConsole.Write(summary);
        AnsiConsole.WriteLine();

        var tracks = new Table().Title("[bold]Tracks[/]").Border(TableBorder.Rounded).BorderColor(Color.Grey)
            .AddColumn(new TableColumn("[grey]#[/]").RightAligned())
            .AddColumn("Name")
            .AddColumn(new TableColumn("Events").RightAligned())
            .AddColumn(new TableColumn("Notes").RightAligned())
            .AddColumn("Channels");
        foreach (var t in song.Tracks)
        {
            tracks.AddRow(
                t.Index.ToString(inv),
                t.Name is null ? "[grey](unnamed)[/]" : Markup.Escape(t.Name),
                t.EventCount.ToString("N0", inv),
                t.NoteCount.ToString("N0", inv),
                t.Channels.Count == 0 ? "[grey]-[/]" : string.Join(",", t.Channels.Select(c => c + 1)));
        }

        AnsiConsole.Write(tracks);

        var channels = new Table().Title("[bold]Channels[/]").Border(TableBorder.Rounded).BorderColor(Color.Grey)
            .AddColumn(new TableColumn("[grey]Ch[/]").RightAligned())
            .AddColumn(new TableColumn("Notes").RightAligned())
            .AddColumn("Range");
        foreach (var c in song.Channels)
        {
            channels.AddRow(
                (c.Channel + 1).ToString(inv),
                c.NoteCount.ToString("N0", inv),
                Range(c.LowestNote, c.HighestNote));
        }

        AnsiConsole.Write(channels);
        return 0;
    }

    private static string Range(int? low, int? high) =>
        low is { } lo && high is { } hi
            ? string.Create(CultureInfo.InvariantCulture, $"{NoteName(lo)}–{NoteName(hi)} [grey]({lo}–{hi})[/]")
            : "[grey]-[/]";

    private static string NoteName(int number) => Note.Get((SevenBitNumber)(byte)number).ToString();
}

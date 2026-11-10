using System.Globalization;
using MetroDom.Core;

namespace MetroDom.Conductor.Commands;

/// <summary>Successor to the WinForms MIDI Explorer: summarize a Standard MIDI File.</summary>
internal static class InspectCommand
{
    public static int Run(CommandLine cli)
    {
        var path = cli.Positional(0) ?? throw new UsageException("inspect: missing <song.mid>.");
        if (!File.Exists(path))
        {
            throw new UsageException($"inspect: file not found: {path}");
        }

        var song = SongAnalyzer.Analyze(path);
        var inv = CultureInfo.InvariantCulture;
        Console.WriteLine($"File:     {Path.GetFileName(path)}");
        Console.WriteLine($"Format:   {song.Format?.ToString() ?? "unknown"}");
        Console.WriteLine($"PPQN:     {song.TicksPerQuarterNote?.ToString(inv) ?? "SMPTE"}");
        Console.WriteLine(string.Create(inv, $"Tempo:    {song.InitialTempoBpm:0.##} BPM (default; humans may override)"));
        Console.WriteLine(string.Create(inv, $"Duration: {song.Duration:m\\:ss\\.f}"));
        Console.WriteLine();
        Console.WriteLine("Tracks:");
        foreach (var t in song.Tracks)
        {
            var channels = t.Channels.Count == 0 ? "-" : string.Join(",", t.Channels.Select(c => c + 1));
            Console.WriteLine(string.Create(inv, $"  {t.Index,3}  {t.Name ?? "(unnamed)",-28} events {t.EventCount,6}  notes {t.NoteCount,6}  ch {channels}"));
        }

        Console.WriteLine();
        Console.WriteLine("Channels (1-16):");
        foreach (var c in song.Channels)
        {
            Console.WriteLine(string.Create(inv, $"  {c.Channel + 1,3}  notes {c.NoteCount,6}  range {c.LowestNote}-{c.HighestNote}"));
        }

        return 0;
    }
}

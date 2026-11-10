using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;

namespace MetroDom.Core;

/// <summary>Summary of one track in a Standard MIDI File.</summary>
/// <param name="Index">Zero-based track index.</param>
/// <param name="Name">Sequence/track name meta event, if any.</param>
/// <param name="EventCount">Number of events in the track.</param>
/// <param name="NoteCount">Number of complete notes.</param>
/// <param name="Channels">Zero-based MIDI channels used by channel events.</param>
public sealed record TrackSummary(int Index, string? Name, int EventCount, int NoteCount, IReadOnlyList<int> Channels);

/// <summary>Summary of one MIDI channel across the whole file.</summary>
/// <param name="Channel">Zero-based MIDI channel (display as Channel + 1).</param>
/// <param name="NoteCount">Number of complete notes.</param>
/// <param name="LowestNote">Lowest note number, or null if no notes.</param>
/// <param name="HighestNote">Highest note number, or null if no notes.</param>
public sealed record ChannelSummary(int Channel, int NoteCount, int? LowestNote, int? HighestNote);

/// <summary>What the arranger needs to know about a song before assigning it to instruments.</summary>
/// <param name="Format">SMF format (single track / multi track), if known.</param>
/// <param name="TicksPerQuarterNote">Time division, or null for SMPTE-based files.</param>
/// <param name="Duration">Playing time at the file's own tempo map.</param>
/// <param name="InitialTempoBpm">Tempo at time zero, in beats per minute.</param>
/// <param name="Tracks">Per-track summaries.</param>
/// <param name="Channels">Per-channel summaries, ordered by channel.</param>
public sealed record SongAnalysis(
    MidiFileFormat? Format,
    int? TicksPerQuarterNote,
    TimeSpan Duration,
    double InitialTempoBpm,
    IReadOnlyList<TrackSummary> Tracks,
    IReadOnlyList<ChannelSummary> Channels);

/// <summary>Reads Standard MIDI Files into a <see cref="SongAnalysis"/>.</summary>
public static class SongAnalyzer
{
    /// <summary>Analyzes a MIDI file on disk.</summary>
    public static SongAnalysis Analyze(string path) => Analyze(MidiFile.Read(path));

    /// <summary>Analyzes an in-memory MIDI file.</summary>
    public static SongAnalysis Analyze(MidiFile midiFile)
    {
        ArgumentNullException.ThrowIfNull(midiFile);

        var tempoMap = midiFile.GetTempoMap();
        var tracks = midiFile.GetTrackChunks()
            .Select((chunk, index) => new TrackSummary(
                index,
                chunk.Events.OfType<SequenceTrackNameEvent>().FirstOrDefault()?.Text,
                chunk.Events.Count,
                chunk.GetNotes().Count,
                [.. chunk.Events.OfType<ChannelEvent>().Select(e => (int)e.Channel).Distinct().Order()]))
            .ToArray();

        var channels = midiFile.GetNotes()
            .GroupBy(n => (int)n.Channel)
            .OrderBy(g => g.Key)
            .Select(g => new ChannelSummary(g.Key, g.Count(), g.Min(n => (int)n.NoteNumber), g.Max(n => (int)n.NoteNumber)))
            .ToArray();

        MidiFileFormat? format = null;
        try
        {
            format = midiFile.OriginalFormat;
        }
        catch (InvalidOperationException)
        {
            // File was built in memory, not read from disk, so it has no original format.
        }

        return new SongAnalysis(
            format,
            (midiFile.TimeDivision as TicksPerQuarterNoteTimeDivision)?.TicksPerQuarterNote,
            midiFile.GetDuration<MetricTimeSpan>(),
            tempoMap.GetTempoAtTime(new MidiTimeSpan(0)).BeatsPerMinute,
            tracks,
            channels);
    }
}

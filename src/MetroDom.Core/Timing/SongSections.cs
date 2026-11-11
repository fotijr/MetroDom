using System.Text.RegularExpressions;
using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;

namespace MetroDom.Core.Timing;

/// <summary>A named stretch of a song between two markers, in song time.</summary>
public sealed record Section(string Name, TimeSpan Start, TimeSpan End);

/// <summary>
/// Song sections from SMF <b>marker</b> meta events (ADR 0012: verse, chorus, outro). A song
/// without markers has no sections; vamp and jump need at least one.
/// </summary>
public static partial class SongSections
{
    /// <summary>
    /// One section per marker, running to the next marker or the song's end. Music before the first
    /// marker becomes a section called "Start". Markers at the same time keep the last name.
    /// </summary>
    public static IReadOnlyList<Section> From(MidiFile midiFile)
    {
        ArgumentNullException.ThrowIfNull(midiFile);
        var tempoMap = midiFile.GetTempoMap();
        var markers = midiFile.GetTimedEvents()
            .Where(e => e.Event is MarkerEvent m && !string.IsNullOrWhiteSpace(m.Text))
            .Select(e => (At: (TimeSpan)e.TimeAs<MetricTimeSpan>(tempoMap), Name: ((MarkerEvent)e.Event).Text.Trim()))
            .GroupBy(m => m.At)
            .Select(g => g.Last())
            .OrderBy(m => m.At)
            .ToList();
        if (markers.Count == 0)
        {
            return [];
        }

        var end = SongTimeline.From(midiFile).Select(e => e.At).DefaultIfEmpty().Max();
        if (markers[0].At > TimeSpan.Zero)
        {
            markers.Insert(0, (TimeSpan.Zero, "Start"));
        }

        return [.. markers
            .Select((m, i) => new Section(m.Name, m.At, i + 1 < markers.Count ? markers[i + 1].At : Max(end, m.At)))];
    }

    /// <summary>Index of the section playing at <paramref name="songTime"/>, or -1.</summary>
    public static int IndexAt(IReadOnlyList<Section> sections, TimeSpan songTime)
    {
        ArgumentNullException.ThrowIfNull(sections);
        for (var i = sections.Count - 1; i >= 0; i--)
        {
            if (sections[i].Start <= songTime)
            {
                return i;
            }
        }

        return sections.Count > 0 ? 0 : -1;
    }

    /// <summary>The outro: the last section named outro, ending, end, coda or tag, else the last section. -1 if none.</summary>
    public static int OutroIndex(IReadOnlyList<Section> sections)
    {
        ArgumentNullException.ThrowIfNull(sections);
        for (var i = sections.Count - 1; i >= 0; i--)
        {
            if (OutroName().IsMatch(sections[i].Name))
            {
                return i;
            }
        }

        return sections.Count - 1;
    }

    /// <summary>The same sections <paramref name="offset"/> later (after a count-in).</summary>
    public static IReadOnlyList<Section> Shift(IReadOnlyList<Section> sections, TimeSpan offset) =>
        [.. sections.Select(s => s with { Start = s.Start + offset, End = s.End + offset })];

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    [GeneratedRegex(@"(?<![a-z])(outro|ending|end|coda|tag)(?![a-z])", RegexOptions.IgnoreCase)]
    private static partial Regex OutroName();
}

/// <summary>
/// A count-in on a real instrument (ADR 0012: no click track, no synthesized audio): N bars of
/// strikes before the song, first beat of each bar accented, through the scheduler like any note
/// so it's latency-compensated and follows tap tempo.
/// </summary>
public static class CountIn
{
    /// <summary>Strike length. Robots ignore Note Off; synths need one.</summary>
    private static readonly TimeSpan Length = TimeSpan.FromMilliseconds(50);

    /// <summary>One beat (the time signature's denominator) at the file's tempo at the start, and beats per bar.</summary>
    public static (TimeSpan Beat, int BeatsPerBar) Meter(MidiFile midiFile)
    {
        ArgumentNullException.ThrowIfNull(midiFile);
        var tempoMap = midiFile.GetTempoMap();
        var signature = tempoMap.GetTimeSignatureAtTime(new MidiTimeSpan(0));
        var quarter = TimeSpan.FromTicks(tempoMap.GetTempoAtTime(new MidiTimeSpan(0)).MicrosecondsPerQuarterNote * 10);
        return (quarter * 4 / signature.Denominator, signature.Numerator);
    }

    /// <summary>
    /// <paramref name="song"/> moved <paramref name="bars"/> bars later, after count-in strikes on
    /// <paramref name="channel"/> (0-15) and <paramref name="note"/>. Returns the events and how far the song moved.
    /// </summary>
    public static (IReadOnlyList<TimedMidiEvent> Events, TimeSpan Offset) Prepend(
        IReadOnlyList<TimedMidiEvent> song, int bars, TimeSpan beat, int beatsPerBar, int channel, int note)
    {
        ArgumentNullException.ThrowIfNull(song);
        ArgumentOutOfRangeException.ThrowIfNegative(bars);
        ArgumentOutOfRangeException.ThrowIfLessThan(beatsPerBar, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(beat, TimeSpan.Zero);
        if (bars == 0)
        {
            return (song, TimeSpan.Zero);
        }

        var clicks = new List<TimedMidiEvent>();
        var beats = bars * beatsPerBar;
        for (var i = 0; i < beats; i++)
        {
            var velocity = i % beatsPerBar == 0 ? 110 : 80;
            var at = beat * i;
            clicks.Add(new(at, new NoteOnEvent((SevenBitNumber)note, (SevenBitNumber)velocity) { Channel = (FourBitNumber)channel }));
            clicks.Add(new(at + Min(Length, beat / 2), new NoteOffEvent((SevenBitNumber)note, SevenBitNumber.MinValue) { Channel = (FourBitNumber)channel }));
        }

        var offset = beat * beats;
        return ([.. clicks, .. song.Select(e => e with { At = e.At + offset })], offset);
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;
}

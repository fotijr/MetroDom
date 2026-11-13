using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;
using MetroDom.Core.Timing;

namespace MetroDom.Core.Arranging;

/// <summary>An instrument available to the arranger and the MIDI channel it listens on.</summary>
/// <param name="Profile">What it can play: notes, minimum repeat interval.</param>
/// <param name="Channel">Zero-based channel the arranged part is sent on. Channel 9 (MIDI 10) means percussion.</param>
public sealed record BandMember(InstrumentProfile Profile, int Channel)
{
    /// <summary>General MIDI convention: channel 10 is percussion. Its notes are drum sounds, never transposed.</summary>
    public bool IsPercussion => Channel == GeneralMidi.PercussionChannel;
}

/// <summary>One part of a song: the notes of one track on one channel.</summary>
/// <param name="Track">Zero-based track index.</param>
/// <param name="Channel">Zero-based channel.</param>
/// <param name="Name">Track name, or "Track N".</param>
/// <param name="NoteCount">Number of notes.</param>
/// <param name="LowestNote">Lowest note number.</param>
/// <param name="HighestNote">Highest note number.</param>
public sealed record SongPart(int Track, int Channel, string Name, int NoteCount, int LowestNote, int HighestNote)
{
    public bool IsPercussion => Channel == GeneralMidi.PercussionChannel;

    /// <summary>General MIDI program the part starts with, if the file sets one.</summary>
    public int? Program { get; init; }

    /// <summary>What the part is in the band (ADR 0012), see <see cref="RoleDetector"/>.</summary>
    public string Role { get; init; } = Roles.Other;

    /// <summary>Why it has that role.</summary>
    public RoleSource RoleSource { get; init; } = RoleSource.None;
}

/// <summary>A part given to an instrument, and what had to change for it to fit.</summary>
/// <param name="Part">The song part.</param>
/// <param name="Instrument">Instrument name.</param>
/// <param name="Transpose">Semitones the whole part was moved (whole octaves).</param>
/// <param name="Played">Notes the instrument will play.</param>
/// <param name="Folded">Notes still out of range after transposing, moved by octaves to the nearest playable same note name.</param>
/// <param name="Dropped">Notes with no playable equivalent.</param>
/// <param name="Thinned">Notes removed because they repeat faster than the instrument can, or collide after folding.</param>
/// <param name="Substituted">Drum hits the kit lacks, played on a stand-in (a closed hi-hat on the ride).</param>
/// <param name="OverPolyphony">Notes left out of chords bigger than the instrument can strike at once (its <c>polyphony</c>).</param>
public sealed record PartAssignment(SongPart Part, string Instrument, int Transpose, int Played, int Folded, int Dropped, int Thinned, int Substituted = 0, int OverPolyphony = 0);

/// <summary>The arranger's result: who plays what, and the events to send.</summary>
/// <param name="Assignments">One per instrument that got a part, in band order.</param>
/// <param name="Unassigned">Song parts nobody plays.</param>
/// <param name="Idle">Instruments with no part they can play.</param>
/// <param name="Timeline">Note events on each instrument's channel, in time order, ready for the scheduler.</param>
public sealed record Arrangement(
    IReadOnlyList<PartAssignment> Assignments,
    IReadOnlyList<SongPart> Unassigned,
    IReadOnlyList<string> Idle,
    IReadOnlyList<TimedMidiEvent> Timeline);

/// <summary>Options for <see cref="Arranger.Arrange"/>.</summary>
public sealed record ArrangerOptions
{
    /// <summary>Move notes that don't fit by octaves to a playable note of the same name. Pitched parts only.</summary>
    public bool FoldOctaves { get; init; } = true;

    /// <summary>Play a drum sound the kit lacks on a stand-in (<see cref="DrumMap"/>). Percussion parts only.</summary>
    public bool SubstituteDrums { get; init; } = true;

    /// <summary>The song's saved role choices, which override detected roles.</summary>
    public RoleMapping? Roles { get; init; }
}

/// <summary>General MIDI facts the arranger relies on.</summary>
public static class GeneralMidi
{
    /// <summary>Zero-based percussion channel (MIDI channel 10).</summary>
    public const int PercussionChannel = 9;
}

/// <summary>
/// Maps song parts onto the instruments in the band (ARCHITECTURE: Arranger). Each instrument
/// plays at most one part: the one it can cover best. Percussion parts only go to percussion
/// instruments and are never transposed; pitched parts are moved by whole octaves to fit, then
/// leftover notes are folded or dropped, and repeats faster than the instrument's minimum
/// interval are thinned (ADR 0004). Drum sounds the kit lacks go to a stand-in. Chords bigger
/// than the instrument's <c>polyphony</c> keep their outer voices (drums: kick, snare, hats
/// first), so the safety governor, which mirrors the firmware, never has to pick at random. Only notes are kept: robots have no use for program changes,
/// pitch bend or controllers. Everything removed is counted, never silently lost.
/// </summary>
public static class Arranger
{
    private const int MaxOctaveShift = 4;

    /// <summary>Splits a song into parts: one per track and channel that has notes, each tagged with its role.</summary>
    public static IReadOnlyList<SongPart> Parts(MidiFile midiFile, RoleMapping? roles = null)
    {
        ArgumentNullException.ThrowIfNull(midiFile);
        return ReadParts(midiFile, roles).Select(p => p.Part).ToArray();
    }

    public static Arrangement Arrange(MidiFile midiFile, IReadOnlyList<BandMember> band, ArrangerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(midiFile);
        ArgumentNullException.ThrowIfNull(band);
        options ??= new ArrangerOptions();
        if (band.Select(m => m.Profile.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != band.Count)
        {
            throw new ArgumentException("Instrument names must be unique.", nameof(band));
        }

        var parts = ReadParts(midiFile, options.Roles);
        var choices = new List<(int Member, int Part, int Score, int Transpose)>();
        for (var m = 0; m < band.Count; m++)
        {
            for (var p = 0; p < parts.Count; p++)
            {
                if (band[m].IsPercussion != parts[p].Part.IsPercussion)
                {
                    continue;
                }

                var (transpose, score) = BestFit(band[m], parts[p].Notes, options);
                if (score > 0)
                {
                    choices.Add((m, p, score, transpose));
                }
            }
        }

        // Greedy: the best (most notes playable) pairing first, then the next among what's left.
        var memberPart = new Dictionary<int, (int Part, int Transpose)>();
        var takenParts = new HashSet<int>();
        foreach (var c in choices.OrderByDescending(c => c.Score).ThenBy(c => c.Part).ThenBy(c => c.Member))
        {
            if (!memberPart.ContainsKey(c.Member) && takenParts.Add(c.Part))
            {
                memberPart[c.Member] = (c.Part, c.Transpose);
            }
        }

        var assignments = new List<PartAssignment>();
        var timeline = new List<(TimeSpan At, bool IsOn, MidiEvent Event)>();
        for (var m = 0; m < band.Count; m++)
        {
            if (!memberPart.TryGetValue(m, out var pick))
            {
                continue;
            }

            var (assignment, notes) = Fit(band[m], parts[pick.Part], pick.Transpose, options);
            assignments.Add(assignment);
            foreach (var n in notes)
            {
                var channel = (FourBitNumber)band[m].Channel;
                timeline.Add((n.Start, true, new NoteOnEvent((SevenBitNumber)n.Number, (SevenBitNumber)n.Velocity) { Channel = channel }));
                timeline.Add((n.End, false, new NoteOffEvent((SevenBitNumber)n.Number, SevenBitNumber.MinValue) { Channel = channel }));
            }
        }

        return new Arrangement(
            assignments,
            parts.Where((_, p) => !takenParts.Contains(p)).Select(p => p.Part).ToArray(),
            band.Where((_, m) => !memberPart.ContainsKey(m)).Select(b => b.Profile.Name).ToArray(),
            timeline
                .OrderBy(e => e.At)
                .ThenBy(e => e.IsOn) // Note Offs first, so a re-struck note isn't cut by its own release.
                .Select(e => new TimedMidiEvent(e.At, e.Event))
                .ToArray());
    }

    /// <summary>Octave shift that makes the most notes playable as written (smallest shift wins ties).</summary>
    private static (int Transpose, int Score) BestFit(BandMember member, IReadOnlyList<PartNote> notes, ArrangerOptions options)
    {
        var profile = member.Profile;
        if (member.IsPercussion)
        {
            return (0, notes.Count(n => profile.CanPlay(n.Number) || (options.SubstituteDrums && DrumMap.Substitute(profile, n.Number) is not null)));
        }

        if (profile.Notes is null)
        {
            return (0, notes.Count);
        }

        return Enumerable.Range(-MaxOctaveShift, (MaxOctaveShift * 2) + 1)
            .Select(octaves => octaves * 12)
            .Select(shift => (Transpose: shift, Score: notes.Count(n => profile.CanPlay(n.Number + shift))))
            .OrderByDescending(f => f.Score)
            .ThenBy(f => Math.Abs(f.Transpose))
            .ThenBy(f => f.Transpose)
            .First();
    }

    private static (PartAssignment Assignment, List<PartNote> Notes) Fit(BandMember member, ParsedPart part, int transpose, ArrangerOptions options)
    {
        var profile = member.Profile;
        int folded = 0, dropped = 0, thinned = 0, substituted = 0;
        var placed = new List<PartNote>();
        foreach (var note in part.Notes)
        {
            var number = note.Number + transpose;
            if (!profile.CanPlay(number))
            {
                if (!member.IsPercussion && options.FoldOctaves && Fold(profile, number) is { } fold)
                {
                    number = fold;
                    folded++;
                }
                else if (member.IsPercussion && options.SubstituteDrums && DrumMap.Substitute(profile, number) is { } standIn)
                {
                    number = standIn;
                    substituted++;
                }
                else
                {
                    dropped++;
                    continue;
                }
            }

            placed.Add(note with { Number = number });
        }

        var kept = new List<PartNote>();
        var lastStart = new Dictionary<int, TimeSpan>();
        foreach (var note in placed.OrderBy(n => n.Start).ThenBy(n => n.Number))
        {
            var interval = profile.IntervalFor(note.Number);
            var minGap = interval > TimeSpan.Zero ? interval : TimeSpan.FromTicks(1);
            if (lastStart.TryGetValue(note.Number, out var last) && note.Start - last < minGap)
            {
                thinned++; // Too fast to repeat, or the same note twice at once after folding.
                continue;
            }

            lastStart[note.Number] = note.Start;
            kept.Add(note);
        }

        var voiced = Voice(kept, profile, member.IsPercussion);
        return (new PartAssignment(part.Part, profile.Name, transpose, voiced.Count, folded, dropped, thinned, substituted, kept.Count - voiced.Count), voiced);
    }

    /// <summary>
    /// Keeps at most <c>polyphony</c> notes sounding at once, as the firmware counts them: a strike
    /// holds an actuator for its pulse (or, for a software instrument, the note's length). Notes
    /// starting together are chosen by importance: drums by <see cref="DrumMap.Priority"/>,
    /// pitched parts top voice first, then the bass, then the inner voices from the top down.
    /// </summary>
    private static List<PartNote> Voice(List<PartNote> notes, InstrumentProfile profile, bool percussion)
    {
        if (profile.Polyphony is not { } polyphony)
        {
            return notes;
        }

        TimeSpan Busy(PartNote n) =>
            profile.PulseFor(n.Number) is var pulse && pulse > TimeSpan.Zero ? pulse : TimeSpan.FromTicks(Math.Max(1, (n.End - n.Start).Ticks));

        var voiced = new List<PartNote>(notes.Count);
        var sounding = new List<TimeSpan>(); // when each kept note frees its actuator
        foreach (var chord in notes.GroupBy(n => n.Start))
        {
            sounding.RemoveAll(until => until <= chord.Key);
            var ordered = percussion
                ? chord.OrderBy(n => DrumMap.Priority(n.Number)).ThenBy(n => n.Number).ToList()
                : OuterVoicesFirst(chord.ToList());
            foreach (var note in ordered)
            {
                if (sounding.Count < polyphony)
                {
                    voiced.Add(note);
                    sounding.Add(note.Start + Busy(note));
                }
            }
        }

        return voiced;
    }

    private static List<PartNote> OuterVoicesFirst(List<PartNote> chord)
    {
        var byPitch = chord.OrderByDescending(n => n.Number).ToList();
        if (byPitch.Count <= 2)
        {
            return byPitch;
        }

        return [byPitch[0], byPitch[^1], .. byPitch.Skip(1).Take(byPitch.Count - 2)];
    }

    /// <summary>The playable note with the same name (pitch class) nearest to <paramref name="number"/>.</summary>
    private static int? Fold(InstrumentProfile profile, int number) =>
        profile.Notes!
            .Where(n => ((n - number) % 12 + 12) % 12 == 0)
            .OrderBy(n => Math.Abs(n - number))
            .Cast<int?>()
            .FirstOrDefault();

    private static List<ParsedPart> ReadParts(MidiFile midiFile, RoleMapping? roles)
    {
        var tempoMap = midiFile.GetTempoMap();
        var chunks = midiFile.GetTrackChunks().ToArray();

        // A part's program: the first Program Change on its channel in its own track, else in any track.
        int? FirstProgram(IEnumerable<TrackChunk> where, int channel) =>
            where.SelectMany(c => c.Events).OfType<ProgramChangeEvent>().FirstOrDefault(e => e.Channel == channel)?.ProgramNumber;

        return chunks
            .SelectMany((chunk, track) =>
            {
                var name = chunk.Events.OfType<SequenceTrackNameEvent>().FirstOrDefault()?.Text;
                return chunk.GetNotes()
                    .GroupBy(n => (int)n.Channel)
                    .OrderBy(g => g.Key)
                    .Select(g =>
                    {
                        var notes = g
                            .Select(n => new PartNote(
                                n.TimeAs<MetricTimeSpan>(tempoMap),
                                n.EndTimeAs<MetricTimeSpan>(tempoMap),
                                n.NoteNumber,
                                n.Velocity))
                            .ToArray();
                        var partName = string.IsNullOrWhiteSpace(name) ? $"Track {track + 1}" : name.Trim();
                        var part = new SongPart(track, g.Key, partName, notes.Length, notes.Min(n => n.Number), notes.Max(n => n.Number))
                        {
                            Program = FirstProgram([chunk], g.Key) ?? FirstProgram(chunks, g.Key),
                        };
                        var (role, source) = RoleDetector.Detect(part, part.Program, roles);
                        return new ParsedPart(part with { Role = role, RoleSource = source }, notes);
                    });
            })
            .ToList();
    }

    private sealed record ParsedPart(SongPart Part, IReadOnlyList<PartNote> Notes);

    private readonly record struct PartNote(TimeSpan Start, TimeSpan End, int Number, int Velocity);
}

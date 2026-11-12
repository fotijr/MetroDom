using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;

namespace MetroDom.Core.Simulation;

/// <summary>
/// A built-in two-piece song for trying the band without a MIDI file: a xylophone line on
/// channel 1 over a GM drum groove on channel 10 (kick, snare, woodblock), 4 bars at 100 BPM.
/// Melody and drums land together on every beat, which is what an alignment test needs. Markers
/// split it into Verse (bars 1-2), Chorus (bar 3) and Outro (bar 4), to try vamp and jump.
/// </summary>
public static class DemoSong
{
    /// <summary>Zero-based channel of the melody (MIDI channel 1).</summary>
    public const int MelodyChannel = 0;

    /// <summary>Zero-based channel of the drums (MIDI channel 10, GM percussion).</summary>
    public const int DrumChannel = 9;

    private const int Ppq = 480;

    public static MidiFile Create()
    {
        var melody = new TrackChunk(new SequenceTrackNameEvent("Xylophone"));
        using (var events = melody.ManageTimedEvents())
        {
            events.Objects.Add(new TimedEvent(new MarkerEvent("Verse"), 0));
            events.Objects.Add(new TimedEvent(new MarkerEvent("Chorus"), 8 * Ppq));
            events.Objects.Add(new TimedEvent(new MarkerEvent("Outro"), 12 * Ppq));
        }

        int[] line = [60, 64, 67, 72, 71, 67, 64, 62, 60, 64, 67, 69, 67, 64, 62, 60];
        using (var notes = melody.ManageNotes())
        {
            for (var beat = 0; beat < line.Length; beat++)
            {
                notes.Objects.Add(new Note((SevenBitNumber)line[beat], Ppq / 2, beat * Ppq)
                {
                    Channel = (FourBitNumber)MelodyChannel,
                    Velocity = (SevenBitNumber)100,
                });
            }
        }

        const int kick = 36, snare = 38, woodblock = 76;
        var drums = new TrackChunk(new SequenceTrackNameEvent("Drums"));
        using (var notes = drums.ManageNotes())
        {
            for (var beat = 0; beat < line.Length; beat++)
            {
                AddDrum(notes, beat % 2 == 0 ? kick : snare, beat * Ppq);
                AddDrum(notes, woodblock, (beat * Ppq) + (Ppq / 2)); // Off-beats: drums alone.
            }
        }

        var file = new MidiFile(melody, drums) { TimeDivision = new TicksPerQuarterNoteTimeDivision(Ppq) };
        file.ReplaceTempoMap(TempoMap.Create(new TicksPerQuarterNoteTimeDivision(Ppq), Tempo.FromBeatsPerMinute(100)));
        return file;
    }

    private static void AddDrum(TimedObjectsManager<Note> notes, int note, long time) =>
        notes.Objects.Add(new Note((SevenBitNumber)note, Ppq / 8, time)
        {
            Channel = (FourBitNumber)DrumChannel,
            Velocity = (SevenBitNumber)110,
        });
}

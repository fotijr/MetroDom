using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;
using MetroDom.Core.Arranging;
using MetroDom.Core.Simulation;

namespace MetroDom.Core.Tests;

public class ArrangerTests
{
    private const int Ppq = 480; // 120 BPM: one beat = 500 ms.

    private static TimeSpan Ms(double ms) => TimeSpan.FromMilliseconds(ms);

    private static HashSet<int> Notes(params int[] notes) => notes.ToHashSet();

    private static BandMember Xylo(IReadOnlySet<int>? notes = null, int channel = 0, TimeSpan minInterval = default) =>
        new(new InstrumentProfile("xylo", Ms(10)) { Notes = notes, MinInterval = minInterval }, channel);

    private static BandMember Drums(IReadOnlySet<int>? notes = null) =>
        new(new InstrumentProfile("drums", Ms(35)) { Notes = notes }, GeneralMidi.PercussionChannel);

    /// <summary>A song from (track name, channel, notes as (beat, number)) triples. Each note lasts half a beat.</summary>
    private static MidiFile Song(params (string Name, int Channel, (double Beat, int Note)[] Notes)[] tracks)
    {
        var chunks = tracks.Select(t =>
        {
            var chunk = new TrackChunk(new SequenceTrackNameEvent(t.Name));
            using var notes = chunk.ManageNotes();
            foreach (var (beat, number) in t.Notes)
            {
                notes.Objects.Add(new Note((SevenBitNumber)number, Ppq / 2, (long)(beat * Ppq)) { Channel = (FourBitNumber)t.Channel, Velocity = (SevenBitNumber)90 });
            }

            return chunk;
        });
        var file = new MidiFile(chunks) { TimeDivision = new TicksPerQuarterNoteTimeDivision(Ppq) };
        file.ReplaceTempoMap(TempoMap.Create(new TicksPerQuarterNoteTimeDivision(Ppq), Tempo.FromBeatsPerMinute(120)));
        return file;
    }

    private static (double Beat, int Note)[] Line(params int[] notes) => notes.Select((n, i) => ((double)i, n)).ToArray();

    [Fact]
    public void Parts_SplitTracksByChannel()
    {
        var song = Song(("Lead", 0, Line(60, 72)), ("Kit", 9, Line(36)));
        var mixed = new TrackChunk();
        using (var notes = mixed.ManageNotes())
        {
            notes.Objects.Add(new Note((SevenBitNumber)40) { Channel = (FourBitNumber)2 });
            notes.Objects.Add(new Note((SevenBitNumber)41) { Channel = (FourBitNumber)3 });
        }

        song.Chunks.Add(mixed);

        Assert.Equal(
            [
                new SongPart(0, 0, "Lead", 2, 60, 72) { Role = Roles.Melody, RoleSource = RoleSource.TrackName },
                new SongPart(1, 9, "Kit", 1, 36, 36) { Role = Roles.Drums, RoleSource = RoleSource.Channel10 },
                new SongPart(2, 2, "Track 3", 1, 40, 40),
                new SongPart(2, 3, "Track 3", 1, 41, 41),
            ],
            Arranger.Parts(song));
    }

    [Fact]
    public void EachInstrumentGetsThePartItCoversBest_DrumsOnlyGoToDrums()
    {
        var song = Song(("Bass", 1, Line(36, 40, 43, 36)), ("Melody", 0, Line(60, 64, 67, 72, 64)), ("Kit", 9, Line(36, 38)));

        var arrangement = Arranger.Arrange(song, [Xylo(Notes(60, 62, 64, 65, 67, 69, 71, 72)), Drums()]);

        Assert.Equal(["Melody", "Kit"], arrangement.Assignments.Select(a => a.Part.Name));
        Assert.Equal(["xylo", "drums"], arrangement.Assignments.Select(a => a.Instrument));
        Assert.Equal(["Bass"], arrangement.Unassigned.Select(p => p.Name));
        Assert.Empty(arrangement.Idle);
    }

    [Fact]
    public void PitchedPartsMoveByOctavesToFit()
    {
        var song = Song(("Bass", 1, Line(36, 40, 43)));

        var a = Assert.Single(Arranger.Arrange(song, [Xylo(Notes(60, 64, 67))]).Assignments);

        Assert.Equal(24, a.Transpose);
        Assert.Equal((3, 0, 0, 0), (a.Played, a.Folded, a.Dropped, a.Thinned));
    }

    [Fact]
    public void NotesStillOutOfRange_FoldToTheSameNoteName_OrAreDropped()
    {
        var song = Song(("Lead", 0, Line(60, 64, 76, 61)));
        var band = new[] { Xylo(Notes(60, 64, 67)) };

        var folded = Assert.Single(Arranger.Arrange(song, band).Assignments);
        var strict = Assert.Single(Arranger.Arrange(song, band, new ArrangerOptions { FoldOctaves = false }).Assignments);

        Assert.Equal((3, 1, 1), (folded.Played, folded.Folded, folded.Dropped)); // 76 (E5) → 64 (E4); 61 (C#) has no match.
        Assert.Equal((2, 0, 2), (strict.Played, strict.Folded, strict.Dropped));
    }

    [Fact]
    public void RepeatsFasterThanTheInstrumentCan_AreThinned()
    {
        var song = Song(("Lead", 0, [(0, 60), (0.1, 60), (0.2, 64), (1, 60)])); // 50 ms apart at 120 BPM.

        var a = Assert.Single(Arranger.Arrange(song, [Xylo(minInterval: Ms(60))]).Assignments);

        Assert.Equal((3, 1), (a.Played, a.Thinned));
    }

    [Fact]
    public void FoldingTwoNotesOntoOne_KeepsOnlyOne()
    {
        var song = Song(("Lead", 0, [(0, 60), (0, 72)]));

        var a = Assert.Single(Arranger.Arrange(song, [Xylo(Notes(60, 64, 67))]).Assignments);

        Assert.Equal((1, 1, 1), (a.Played, a.Folded, a.Thinned));
    }

    [Fact]
    public void DrumNotesAreNeverTransposedOrFolded()
    {
        var song = Song(("Kit", 9, Line(36, 38, 42, 48)));

        var a = Assert.Single(Arranger.Arrange(song, [Drums(Notes(36, 38, 76))]).Assignments);

        Assert.Equal((0, 2, 0, 2), (a.Transpose, a.Played, a.Folded, a.Dropped));
    }

    [Fact]
    public void Timeline_IsOnEachInstrumentsChannel_InTimeOrder_WithReleasesFirst()
    {
        var song = Song(("Lead", 5, [(0, 60), (0.5, 60)]), ("Kit", 9, Line(36)));

        var arrangement = Arranger.Arrange(song, [Xylo(channel: 2), Drums()]);

        Assert.All(arrangement.Timeline.Take(2), e => Assert.Equal(Ms(0), e.At));
        Assert.Equal(
            [(0, 2, true), (0, 9, true), (250, 2, false), (250, 9, false), (250, 2, true), (500, 2, false)],
            arrangement.Timeline.Select(e => ((int)e.At.TotalMilliseconds, (int)((ChannelEvent)e.Event).Channel, e.Event is NoteOnEvent)));
        Assert.DoesNotContain(arrangement.Timeline, e => e.Event is not NoteEvent);
    }

    [Fact]
    public void AnInstrumentThatCanPlayNothing_IsIdle()
    {
        var song = Song(("Lead", 0, Line(61, 63)));

        var arrangement = Arranger.Arrange(song, [Xylo(Notes(60, 64, 67)), Drums()]);

        Assert.Empty(arrangement.Assignments);
        Assert.Equal(["xylo", "drums"], arrangement.Idle);
        Assert.Single(arrangement.Unassigned);
    }

    [Fact]
    public void DuplicateInstrumentNames_AreRejected() =>
        Assert.Throws<ArgumentException>(() => Arranger.Arrange(Song(), [Xylo(), Xylo()]));

    [Fact]
    public void ArrangedDemo_PlaysWithNothingDroppedOnTheVirtualBand()
    {
        var band = new[] { Xylo(Notes(60, 62, 64, 67, 69, 72)), Drums(Notes(36, 38, 76)) };
        var arrangement = Arranger.Arrange(DemoSong.Create(), band);

        var result = BandSimulation.Run(
            arrangement.Timeline,
            band.Select(b => new SimulatedPart(b.Profile, [b.Channel])).ToArray());

        Assert.Equal(TimeSpan.Zero, result.MaxSpread);
        Assert.All(result.Instruments, i => Assert.Equal((0, 0), (i.Dropped, i.Refused)));
        Assert.Equal(arrangement.Assignments.Select(a => a.Played), result.Instruments.Select(i => i.Played));
    }
}

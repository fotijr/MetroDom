using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;
using MetroDom.Core.Arranging;
using MetroDom.Core.Timing;

namespace MetroDom.Core.Tests;

public class SeatingTests
{
    private const int Ppq = 480;

    /// <summary>Tracks of (name, channel, first note's beat): each plays two notes a beat apart.</summary>
    private static MidiFile Song(params (string Name, int Channel, double Beat)[] tracks)
    {
        var chunks = tracks.Select(t =>
        {
            var chunk = new TrackChunk(new SequenceTrackNameEvent(t.Name));
            using var notes = chunk.ManageNotes();
            notes.Objects.Add(new Note((SevenBitNumber)60, Ppq / 2, (long)(t.Beat * Ppq)) { Channel = (FourBitNumber)t.Channel });
            notes.Objects.Add(new Note((SevenBitNumber)62, Ppq / 2, (long)((t.Beat + 1) * Ppq)) { Channel = (FourBitNumber)t.Channel });
            return chunk;
        });
        var file = new MidiFile(chunks) { TimeDivision = new TicksPerQuarterNoteTimeDivision(Ppq) };
        file.ReplaceTempoMap(TempoMap.Create(new TicksPerQuarterNoteTimeDivision(Ppq), Tempo.FromBeatsPerMinute(120)));
        return file;
    }

    private static List<(TimeSpan At, int Channel, int Note)> NoteOns(MidiFile song) =>
        [.. SongTimeline.From(song).Where(e => e.Event is NoteOnEvent).Select(e => (e.At, (int)((NoteOnEvent)e.Event).Channel, (int)((NoteOnEvent)e.Event).NoteNumber))];

    [Fact]
    public void Seat_RemovesItsRolesNotes_AndLeavesTheRestOnTime()
    {
        var song = Song(("Guitar", 0, 0), ("Bass", 1, 0.5), ("Drums", 9, 0));

        var seated = Seating.Apply(song, [new Seat("guitar", "Dom")]);

        var (part, seat) = Assert.Single(seated.Taken);
        Assert.Equal("Guitar", part.Name);
        Assert.Equal("Dom", seat.Player);
        Assert.Empty(seated.Empty);
        Assert.Equal(NoteOns(song).Where(n => n.Channel != 0), NoteOns(seated.Song));
        Assert.Equal(6, NoteOns(song).Count); // the original is untouched
        Assert.Equal(song.GetTempoMap().GetTempoAtTime(new MidiTimeSpan(0)), seated.Song.GetTempoMap().GetTempoAtTime(new MidiTimeSpan(0)));
    }

    [Fact]
    public void Seat_TakesOnlyItsPart_WhenAnotherPartSharesTheChannel()
    {
        // Two tracks on channel 1: --mute 1 would silence both, a seat takes only the guitar.
        var song = Song(("Guitar", 0, 0), ("Piano", 0, 0.5));

        var left = NoteOns(Seating.Apply(song, [new Seat("guitar")]).Song);

        Assert.Equal([(TimeSpan.FromMilliseconds(250), 0, 60), (TimeSpan.FromMilliseconds(750), 0, 62)], left);
    }

    [Fact]
    public void Seat_WithoutAMatchingPart_IsReportedEmpty_AndNothingChanges()
    {
        var song = Song(("Drums", 9, 0));

        var seated = Seating.Apply(song, [new Seat("bass"), new Seat("drums")]);

        Assert.Equal(new Seat("bass"), Assert.Single(seated.Empty));
        Assert.Empty(NoteOns(seated.Song));
    }

    [Fact]
    public void Seat_UsesTheSavedMapping()
    {
        var song = Song(("Track A", 0, 0), ("Track B", 1, 0));

        var seated = Seating.Apply(song, [new Seat("bass")], new RoleMapping().With("#2", "bass"));

        Assert.Equal("Track B", Assert.Single(seated.Taken).Part.Name);
        Assert.All(NoteOns(seated.Song), n => Assert.Equal(0, n.Channel));
    }

    [Fact]
    public void NoSeats_ReturnsTheSongAsIs()
    {
        var song = Song(("Guitar", 0, 0));
        Assert.Same(song, Seating.Apply(song, []).Song);
    }
    [Fact]
    public void PieceSeats_TakeOnlyTheirNotes_AndShareAKit()
    {
        var song = new MidiFile(new TrackChunk(new SequenceTrackNameEvent("Kit"))) { TimeDivision = new TicksPerQuarterNoteTimeDivision(Ppq) };
        using (var notes = song.GetTrackChunks().First().ManageNotes())
        {
            foreach (var (n, beat) in new[] { (36, 0), (42, 0), (38, 1), (42, 1), (49, 2) })
            {
                notes.Objects.Add(new Note((SevenBitNumber)n, Ppq / 2, beat * Ppq) { Channel = (FourBitNumber)9 });
            }
        }

        var sam = new Seat("drums", "Sam") { Notes = new HashSet<int> { 35, 36, 37, 38 }, Pieces = "kick, snare" };
        var ana = new Seat("drums", "Ana") { Notes = new HashSet<int> { 49 }, Pieces = "crash" };
        var nobody = new Seat("drums", "Lee") { Notes = new HashSet<int> { 56 }, Pieces = "cowbell" };

        var seated = Seating.Apply(song, [sam, ana, nobody]);

        Assert.Equal([42, 42], NoteOns(seated.Song).Select(n => n.Note));
        Assert.Equal(["Sam", "Ana"], seated.Taken.Select(t => t.Seat.Player));
        Assert.Equal("Lee", Assert.Single(seated.Empty).Player);
        Assert.Equal("drums (Sam): kick, snare", sam.ToString());
    }
}

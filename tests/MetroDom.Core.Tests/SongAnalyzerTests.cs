using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;

namespace MetroDom.Core.Tests;

public class SongAnalyzerTests
{
    private static MidiFile TwoTrackSong()
    {
        var tempoMap = TempoMap.Create(new TicksPerQuarterNoteTimeDivision(480), Tempo.FromBeatsPerMinute(120));

        var melody = new TrackChunk(new SequenceTrackNameEvent("Xylophone"));
        using (var notes = melody.ManageNotes())
        {
            notes.Objects.Add(new Note((SevenBitNumber)60, 480, 0) { Channel = (FourBitNumber)0 });
            notes.Objects.Add(new Note((SevenBitNumber)67, 480, 480) { Channel = (FourBitNumber)0 });
        }

        var drums = new TrackChunk();
        using (var notes = drums.ManageNotes())
        {
            notes.Objects.Add(new Note((SevenBitNumber)36, 240, 0) { Channel = (FourBitNumber)9 });
        }

        var file = new MidiFile(melody, drums) { TimeDivision = new TicksPerQuarterNoteTimeDivision(480) };
        file.ReplaceTempoMap(tempoMap);
        return file;
    }

    [Fact]
    public void Analyze_ReportsTracksChannelsAndRanges()
    {
        var analysis = SongAnalyzer.Analyze(TwoTrackSong());

        Assert.Equal(480, analysis.TicksPerQuarterNote);
        Assert.Equal(120, analysis.InitialTempoBpm, precision: 3);
        Assert.Equal(TimeSpan.FromSeconds(1), analysis.Duration);

        Assert.Equal(2, analysis.Tracks.Count);
        Assert.Equal("Xylophone", analysis.Tracks[0].Name);
        Assert.Equal(2, analysis.Tracks[0].NoteCount);
        Assert.Equal([0], analysis.Tracks[0].Channels);
        Assert.Equal([9], analysis.Tracks[1].Channels);

        Assert.Collection(
            analysis.Channels,
            c => Assert.Equal(new ChannelSummary(0, 2, 60, 67), c),
            c => Assert.Equal(new ChannelSummary(9, 1, 36, 36), c));
    }

    [Fact]
    public void Analyze_RoundTripsThroughAFileOnDisk()
    {
        var path = Path.Combine(Path.GetTempPath(), $"metrodom-{Guid.NewGuid():N}.mid");
        try
        {
            TwoTrackSong().Write(path);

            var analysis = SongAnalyzer.Analyze(path);

            Assert.Equal(MidiFileFormat.MultiTrack, analysis.Format);
            Assert.Equal(3, analysis.Channels.Sum(c => c.NoteCount));
        }
        finally
        {
            File.Delete(path);
        }
    }
}

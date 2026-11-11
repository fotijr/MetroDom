using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;
using MetroDom.Core.Arranging;

namespace MetroDom.Core.Tests;

public class RoleTests
{
    private static SongPart Part(string name, int channel = 0, int track = 0) => new(track, channel, name, 1, 60, 60);

    [Theory]
    [InlineData("Drums", "drums")]
    [InlineData("Bass Drum", "drums")]
    [InlineData("Hi-Hat", "drums")]
    [InlineData("Fretless Bass", "bass")]
    [InlineData("Synth Bass", "bass")]
    [InlineData("Bassoon", "winds")]
    [InlineData("Lead Gtr", "guitar")]
    [InlineData("Electric Piano", "keys")]
    [InlineData("Lead Vocal", "vocals")]
    [InlineData("Lead", "melody")]
    [InlineData("Xylophone", "melody")]
    [InlineData("Violins", "strings")]
    [InlineData("Trumpet 2", "brass")]
    [InlineData("Alto Sax", "winds")]
    public void FromName_FindsTheRole(string name, string role) => Assert.Equal(role, RoleDetector.FromName(name));

    [Theory]
    [InlineData("Track 3")]
    [InlineData("Embassy")] // "bass" inside a word doesn't count
    [InlineData("Beat It")]
    [InlineData("")]
    public void FromName_NothingToGoOn_IsNull(string name) => Assert.Null(RoleDetector.FromName(name));

    [Theory]
    [InlineData(0, "keys")]
    [InlineData(13, "melody")] // xylophone
    [InlineData(25, "guitar")]
    [InlineData(33, "bass")]
    [InlineData(48, "strings")]
    [InlineData(52, "vocals")]
    [InlineData(56, "brass")]
    [InlineData(65, "winds")]
    [InlineData(81, "melody")]
    [InlineData(89, "keys")]
    [InlineData(118, "other")]
    public void FromProgram_UsesTheGmFamily(int program, string role) => Assert.Equal(role, RoleDetector.FromProgram(program));

    [Fact]
    public void Detect_Order_IsMappingThenChannel10ThenNameThenProgram()
    {
        var mapping = new RoleMapping().With("#1:10", "cowbell");
        Assert.Equal(("cowbell", RoleSource.Mapping), RoleDetector.Detect(Part("Bass", channel: 9), 33, mapping));
        Assert.Equal((Roles.Drums, RoleSource.Channel10), RoleDetector.Detect(Part("Bass", channel: 9), 33));
        Assert.Equal((Roles.Bass, RoleSource.TrackName), RoleDetector.Detect(Part("Bass"), 0));
        Assert.Equal((Roles.Guitar, RoleSource.Program), RoleDetector.Detect(Part("Track 1"), 27));
        Assert.Equal((Roles.Other, RoleSource.None), RoleDetector.Detect(Part("Track 1"), null));
    }

    [Fact]
    public void Mapping_MostSpecificKeyWins_NamesIgnoreCase()
    {
        var mapping = new RoleMapping()
            .With("lead synth", "melody")
            .With("#2", "keys")
            .With("#2:3", "bass");

        Assert.Equal("melody", mapping.Find(Part("Lead Synth")));
        Assert.Equal("bass", mapping.Find(Part("Track 2", channel: 2, track: 1)));
        Assert.Equal("keys", mapping.Find(Part("Track 2", channel: 0, track: 1)));
        Assert.Null(mapping.Find(Part("Pad")));
        Assert.Null(mapping.With("#2:3", null).With("#2", null).Find(Part("Track 2", channel: 2, track: 1)));
    }

    [Fact]
    public void Mapping_RoundTripsThroughJson()
    {
        var mapping = new RoleMapping().With("Lead", "melody").With("#3", "guitar");
        var json = mapping.ToJson();

        Assert.Contains("metrodom.song-roles/0", json, StringComparison.Ordinal);
        Assert.True(RoleMapping.TryParse(json, out var parsed, out var error), error);
        Assert.Equal(mapping.Parts.OrderBy(p => p.Key), parsed.Parts.OrderBy(p => p.Key));
        Assert.Equal("song.roles.json", Path.GetFileName(RoleMapping.PathFor("/music/song.mid")));
    }

    [Theory]
    [InlineData("nope", "valid")]
    [InlineData("""{"parts":{}}""", "schema")]
    [InlineData("""{"schema":"metrodom.song-roles/0","parts":{"Lead":"lead guitar"}}""", "one-word")]
    [InlineData("""{"schema":"metrodom.song-roles/0","parts":{"Lead":3}}""", "valid")]
    public void Mapping_RejectsBadFiles(string json, string reason)
    {
        Assert.False(RoleMapping.TryParse(json, out _, out var error));
        Assert.Contains(reason, error, StringComparison.Ordinal);
    }

    [Fact]
    public void Parts_ReadTheProgram_FromTheTrackOrAnywhereOnTheChannel()
    {
        // Track 1 sets channel 1 to program 33 (bass) and has no notes; track 2 plays on channel 1 unnamed.
        var setup = new TrackChunk(new ProgramChangeEvent((SevenBitNumber)33) { Channel = (FourBitNumber)0 });
        var notes = new TrackChunk();
        using (var manager = notes.ManageNotes())
        {
            manager.Objects.Add(new Note((SevenBitNumber)40) { Channel = (FourBitNumber)0 });
        }

        var guitar = new TrackChunk(new ProgramChangeEvent((SevenBitNumber)25) { Channel = (FourBitNumber)1 });
        using (var manager = guitar.ManageNotes())
        {
            manager.Objects.Add(new Note((SevenBitNumber)64) { Channel = (FourBitNumber)1 });
        }

        var parts = Arranger.Parts(new MidiFile(setup, notes, guitar));

        Assert.Equal([(Roles.Bass, 33), (Roles.Guitar, 25)], parts.Select(p => (p.Role, p.Program!.Value)));
        Assert.All(parts, p => Assert.Equal(RoleSource.Program, p.RoleSource));
    }

    [Fact]
    public void Arrange_UsesTheMapping()
    {
        var song = new MidiFile(new TrackChunk(new SequenceTrackNameEvent("Track 1")));
        using (var manager = ((TrackChunk)song.Chunks[0]).ManageNotes())
        {
            manager.Objects.Add(new Note((SevenBitNumber)60));
        }

        var band = new[] { new BandMember(new InstrumentProfile("xylo", TimeSpan.Zero), 0) };
        var plain = Assert.Single(Arranger.Arrange(song, band).Assignments);
        var mapped = Assert.Single(Arranger.Arrange(song, band, new ArrangerOptions { Roles = new RoleMapping().With("#1", "melody") }).Assignments);

        Assert.Equal(Roles.Other, plain.Part.Role);
        Assert.Equal(Roles.Melody, mapped.Part.Role);
    }
}

using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;
using MetroDom.Core.Simulation;
using MetroDom.Core.Timing;

namespace MetroDom.Core.Tests;

public class SongStructureTests
{
    private const int Ppq = 480;

    private static TimeSpan Ms(double ms) => TimeSpan.FromMilliseconds(ms);

    private static NoteOnEvent On(int note, int channel = 0) => new((SevenBitNumber)note, (SevenBitNumber)100) { Channel = (FourBitNumber)channel };

    private static NoteOffEvent Off(int note, int channel = 0) => new((SevenBitNumber)note, (SevenBitNumber)0) { Channel = (FourBitNumber)channel };

    /// <summary>A 120 BPM song (500 ms beats) with a note per beat and markers at the given beats.</summary>
    private static MidiFile Song(int beats, params (int Beat, string Name)[] markers)
    {
        var chunk = new TrackChunk();
        using (var events = chunk.ManageTimedEvents())
        {
            foreach (var (beat, name) in markers)
            {
                events.Objects.Add(new TimedEvent(new MarkerEvent(name), beat * Ppq));
            }

            for (var b = 0; b < beats; b++)
            {
                events.Objects.Add(new TimedEvent(On(60 + b), b * Ppq));
                events.Objects.Add(new TimedEvent(Off(60 + b), (b * Ppq) + (Ppq / 2)));
            }
        }

        var file = new MidiFile(chunk) { TimeDivision = new TicksPerQuarterNoteTimeDivision(Ppq) };
        file.ReplaceTempoMap(TempoMap.Create(new TicksPerQuarterNoteTimeDivision(Ppq), Tempo.FromBeatsPerMinute(120)));
        return file;
    }

    [Fact]
    public void Sections_FromMarkers_WithAStartBeforeTheFirst()
    {
        var sections = SongSections.From(Song(8, (2, "Verse"), (4, "Chorus"), (6, "Outro")));

        Assert.Equal(
            [new Section("Start", Ms(0), Ms(1000)), new Section("Verse", Ms(1000), Ms(2000)), new Section("Chorus", Ms(2000), Ms(3000)), new Section("Outro", Ms(3000), Ms(3750))],
            sections);
        Assert.Equal(2, SongSections.IndexAt(sections, Ms(2500)));
        Assert.Equal(3, SongSections.OutroIndex(sections));
        Assert.Empty(SongSections.From(Song(4)));
    }

    [Theory]
    [InlineData("Ending", 1)]
    [InlineData("Coda", 1)]
    [InlineData("Bridge", 2)] // no outro name: the last section
    public void Outro_ByNameElseLast(string name, int expected)
    {
        var sections = new[] { new Section("Verse", Ms(0), Ms(1)), new Section(name, Ms(1), Ms(2)), new Section("Fade", Ms(2), Ms(3)) };
        Assert.Equal(expected == 2 ? 2 : 1, SongSections.OutroIndex(sections));
    }

    [Fact]
    public void Meter_ReadsTempoAndTimeSignature()
    {
        Assert.Equal((Ms(500), 4), CountIn.Meter(Song(1)));

        var waltz = Song(1);
        waltz.ReplaceTempoMap(TempoMap.Create(new TicksPerQuarterNoteTimeDivision(Ppq), Tempo.FromBeatsPerMinute(120), new TimeSignature(6, 8)));
        Assert.Equal((Ms(250), 6), CountIn.Meter(waltz)); // eighth-note beats
    }

    [Fact]
    public void CountIn_StrikesBeforeTheSong_AccentingBarOnes()
    {
        TimedMidiEvent[] song = [new(Ms(0), On(60))];

        var (events, offset) = CountIn.Prepend(song, bars: 2, Ms(500), beatsPerBar: 3, channel: 9, note: 76);

        Assert.Equal(Ms(3000), offset);
        var clicks = events.Where(e => e.Event is NoteOnEvent { NoteNumber: var n } && n == 76).ToList();
        Assert.Equal([0, 500, 1000, 1500, 2000, 2500], clicks.Select(c => c.At.TotalMilliseconds));
        Assert.Equal([110, 80, 80, 110, 80, 80], clicks.Select(c => (int)((NoteOnEvent)c.Event).Velocity));
        Assert.All(clicks, c => Assert.Equal(9, (int)((NoteOnEvent)c.Event).Channel));
        Assert.Equal(Ms(3000), events.Single(e => e.Event is NoteOnEvent { NoteNumber: var n } && n == 60).At);
        Assert.Same(song, CountIn.Prepend(song, 0, Ms(500), 4, 9, 76).Events);
    }

    private readonly VirtualClock _clock = new();

    private (LatencyScheduler Scheduler, FakeOutput Output) Start(MidiFile song, TimeSpan latency = default)
    {
        var output = new FakeOutput();
        var orchestra = new Orchestra();
        orchestra.Assign(0, output);
        var scheduler = new LatencyScheduler(orchestra, _clock, new Dictionary<IMidiOutput, InstrumentProfile> { [output] = new("x", latency) });
        scheduler.Start(SongTimeline.From(song));
        return (scheduler, output);
    }

    /// <summary>Runs until <paramref name="until"/>, recording (clock time, note) of every Note On.</summary>
    private List<(double Ms, int Note)> RunUntil(LatencyScheduler scheduler, FakeOutput output, TimeSpan until, Action<TimeSpan>? at = null)
    {
        var heard = new List<(double, int)>();
        var seen = output.Sent.Count;
        while (scheduler.NextDue is { } due && due <= until)
        {
            _clock.AdvanceTo(due);
            at?.Invoke(_clock.Now);
            scheduler.Pump();
            for (; seen < output.Sent.Count; seen++)
            {
                if (output.Sent[seen] is NoteOnEvent on && on.Velocity > 0)
                {
                    heard.Add((_clock.Now.TotalMilliseconds, on.NoteNumber));
                }
            }
        }

        return heard;
    }

    [Fact]
    public void Vamp_RepeatsTheSection_UntilDisarmed_ThenCarriesOn()
    {
        // Beats 0-1 Verse, 2-3 Chorus, 4-5 Outro; notes 60..65.
        var (scheduler, output) = Start(Song(6, (0, "Verse"), (2, "Chorus"), (4, "Outro")));
        Assert.True(scheduler.ArmJump(Ms(2000), Ms(1000), repeat: true)); // vamp the chorus

        var heard = RunUntil(scheduler, output, Ms(4900)); // before the next pass is committed (50 ms ahead)
        scheduler.Disarm();
        heard.AddRange(RunUntil(scheduler, output, Ms(10_000)));

        Assert.Equal(
            [(0, 60), (500, 61), (1000, 62), (1500, 63), (2000, 62), (2500, 63), (3000, 62), (3500, 63), (4000, 62), (4500, 63), (5000, 64), (5500, 65)],
            heard.Select(h => (h.Ms, h.Note)).Select(h => ((int)h.Item1, h.Item2)));
        Assert.True(scheduler.IsFinished);
    }

    [Fact]
    public void Jump_KeepsLatencyCompensation_AndPositionFollows()
    {
        var (scheduler, output) = Start(Song(6, (0, "Verse"), (2, "Chorus"), (4, "Outro")), latency: Ms(20));
        Assert.True(scheduler.ArmJump(Ms(1000), Ms(2000))); // skip the chorus

        var heard = RunUntil(scheduler, output, Ms(1100));
        _clock.AdvanceTo(Ms(1300));
        Assert.Equal(Ms(2000 + 300 - 20), scheduler.Position); // heard (20 ms behind the song clock) in the outro, not the chorus
        heard.AddRange(RunUntil(scheduler, output, Ms(10_000)));

        // Own latency = lookahead, so sends sit on the song clock; the outro follows the verse directly.
        Assert.Equal([(0, 60), (500, 61), (1000, 64), (1500, 65)], heard.Select(h => ((int)h.Ms, h.Note)));
    }

    [Fact]
    public void Jump_ReleasesNotesHeldAcrossIt()
    {
        // One long note from 0 to 1500 ms; jump at 1000 back to 0.
        var chunk = new TrackChunk();
        using (var events = chunk.ManageTimedEvents())
        {
            events.Objects.Add(new TimedEvent(On(60), 0));
            events.Objects.Add(new TimedEvent(Off(60), 3 * Ppq));
            events.Objects.Add(new TimedEvent(On(72), 4 * Ppq));
        }

        var file = new MidiFile(chunk) { TimeDivision = new TicksPerQuarterNoteTimeDivision(Ppq) };
        file.ReplaceTempoMap(TempoMap.Create(new TicksPerQuarterNoteTimeDivision(Ppq), Tempo.FromBeatsPerMinute(120)));
        var (scheduler, output) = Start(file);
        Assert.True(scheduler.ArmJump(Ms(1000), Ms(2000)));

        RunUntil(scheduler, output, Ms(10_000));

        Assert.Equal(["On 60", "Off 60", "On 72"], output.Sent.OfType<NoteEvent>().Select(e => $"{(e is NoteOnEvent ? "On" : "Off")} {e.NoteNumber}"));
    }

    [Fact]
    public void ArmJump_TooLate_IsRefused_AndPumpingLateDropsIt()
    {
        var (scheduler, output) = Start(Song(6));
        RunUntil(scheduler, output, Ms(1600));
        Assert.False(scheduler.ArmJump(Ms(1500), Ms(0))); // song clock already past it
        Assert.Throws<ArgumentOutOfRangeException>(() => scheduler.ArmJump(Ms(1000), Ms(2000), repeat: true)); // a vamp must go back

        Assert.True(scheduler.ArmJump(Ms(2500), Ms(0)));
        _clock.AdvanceTo(Ms(3000)); // the loop overslept past the jump point
        scheduler.Pump();
        Assert.Null(scheduler.PendingJump);
    }

    [Fact]
    public void Navigator_VampsTheCurrentSection_AndGoesToTheOutro()
    {
        var (scheduler, output) = Start(Song(8, (0, "Verse"), (2, "Chorus"), (4, "Bridge"), (6, "Outro")));
        var navigator = new SectionNavigator(scheduler, SongSections.From(Song(8, (0, "Verse"), (2, "Chorus"), (4, "Bridge"), (6, "Outro"))));
        RunUntil(scheduler, output, Ms(1200));

        Assert.Equal("Chorus (2/4)", navigator.Describe());
        Assert.Equal("Vamp on: repeating Chorus until released.", navigator.ToggleVamp());
        Assert.True(navigator.Vamping);
        Assert.Equal("Chorus (2/4) · vamping", navigator.Describe());
        Assert.Equal("Going to Outro after Chorus.", navigator.JumpToOutro()); // replaces the vamp
        Assert.Equal("Chorus (2/4) · then Outro", navigator.Describe());

        var heard = RunUntil(scheduler, output, Ms(10_000));
        Assert.Equal([63, 66, 67], heard.Select(h => h.Note));
        Assert.Equal("Already in Outro.", navigator.JumpToOutro());
    }

    [Fact]
    public void Navigator_WithoutMarkers_SaysSo()
    {
        var (scheduler, _) = Start(Song(4));
        var navigator = new SectionNavigator(scheduler, []);
        Assert.Contains("no markers", navigator.ToggleVamp(), StringComparison.Ordinal);
        Assert.Contains("no markers", navigator.JumpToOutro(), StringComparison.Ordinal);
        Assert.Null(navigator.Describe());
    }

    [Fact]
    public void DemoSong_HasSections() =>
        Assert.Equal(["Verse", "Chorus", "Outro"], SongSections.From(DemoSong.Create()).Select(s => s.Name));
}

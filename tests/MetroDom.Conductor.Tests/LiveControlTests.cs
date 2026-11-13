using System.Collections.Concurrent;
using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using MetroDom.Conductor.Cli;
using MetroDom.Conductor.Commands;
using MetroDom.Conductor.Midi;
using MetroDom.Core;
using MetroDom.Core.Policy;
using MetroDom.Core.Timing;

namespace MetroDom.Conductor.Tests;

public sealed class LiveControlTests : IDisposable
{
    private readonly VirtualClock _clock = new();
    private readonly Port _port = new();
    private readonly Session _session;
    private readonly LiveControl _live;

    public LiveControlTests()
    {
        _session = new Session([new SessionOutput(_port, [0], TimeSpan.Zero)], []);
        _live = new LiveControl(_session, _clock);
    }

    public void Dispose()
    {
        _live.Dispose();
        _session.Dispose();
    }

    private (ActiveSong Song, List<(double Bpm, string Source)> Tempos, LatencyScheduler Scheduler) Playing()
    {
        var scheduler = new LatencyScheduler(_session.Orchestra, _clock, _session.Profiles);
        var notes = Enumerable.Range(0, 8).Select(i => new TimedMidiEvent(TimeSpan.FromMilliseconds(500 * i), new NoteOnEvent((SevenBitNumber)60, (SevenBitNumber)100)));
        scheduler.Start(notes);
        var tempos = new List<(double, string)>();
        var song = new ActiveSong(scheduler, new SectionNavigator(scheduler, []), (bpm, source) => tempos.Add((bpm, source)), () => { });
        _live.Begin(song);
        return (song, tempos, scheduler);
    }

    [Fact]
    public void StartStop_StartsWhenWaiting_StopsWhenPlaying()
    {
        _live.Begin(null);
        _live.Control(ControlAction.StartStop);
        Assert.True(_live.Done.IsSet);
        Assert.Equal(LiveSignal.Start, _live.Signal);

        Playing();
        Assert.False(_live.Done.IsSet);
        _live.Control(ControlAction.StartStop);
        Assert.Equal(LiveSignal.Stopped, _live.Signal);

        _live.Control(ControlAction.Next); // the first signal wins
        Assert.Equal(LiveSignal.Stopped, _live.Signal);
    }

    [Fact]
    public void TapsWhileWaiting_SetTheNextSongsTempo_TapsWhilePlaying_SetItNow()
    {
        _live.Begin(null);
        _live.Control(ControlAction.Tap);
        _clock.Advance(TimeSpan.FromMilliseconds(500));
        _live.Control(ControlAction.Tap);
        Assert.Equal(120, _live.TakePendingBpm()!.Value, precision: 6);
        Assert.Null(_live.TakePendingBpm()); // taken once

        var (_, tempos, _) = Playing();
        _clock.Advance(TimeSpan.FromMilliseconds(600));
        _live.Tap("tapped");
        // The tap history carries on from the count-off: (500 + 600) / 2 ms per beat.
        Assert.Equal((109.09, "tapped"), (Math.Round(tempos.Single().Bpm, 2), tempos.Single().Source));
    }

    [Fact]
    public void Panic_TellsListeners_AfterTheBandIsSilent()
    {
        var sentBefore = -1;
        _session.Panicked += () => sentBefore = _port.Sent.Count;

        _live.Control(ControlAction.Panic);

        Assert.True(sentBefore > 0, "listeners (lights) must hear of a panic only after All Notes Off went out");
    }

    [Fact]
    public void Panic_StopsTheSongAndSilencesEveryOutput_FromAnyState()
    {
        var (_, _, scheduler) = Playing();

        _live.Control(ControlAction.Panic);

        Assert.True(scheduler.IsFinished);
        Assert.Contains(_port.Sent, e => e is ControlChangeEvent { ControlNumber: var n } && n == ControlNumbers.AllSoundOff);
        Assert.Equal(LiveSignal.Panic, _live.Signal);

        _live.Begin(null);
        _port.Sent.Clear();
        _live.Control(ControlAction.Panic); // waiting: still silences
        Assert.Contains(_port.Sent, e => e is ControlChangeEvent { ControlNumber: var n } && n == ControlNumbers.AllSoundOff);
    }

    [Fact]
    public void VampAndOutro_WhileWaiting_DoNothing()
    {
        _live.Begin(null);
        _live.Control(ControlAction.Vamp);
        _live.Control(ControlAction.Outro);
        Assert.False(_live.Done.IsSet);
        Assert.Empty(_session.Messages.Snapshot());
    }

    [Fact]
    public void Route_TurnsPedalPressesIntoActions()
    {
        var input = new Input();
        var actions = new List<ControlAction>();
        PlayCommand.Route(input, PolicyProfile.LiveBand.NewControlMap(), actions.Add);

        input.Raise(new ControlChangeEvent((SevenBitNumber)81, (SevenBitNumber)127));
        input.Raise(new ControlChangeEvent((SevenBitNumber)81, (SevenBitNumber)0));
        input.Raise(new ControlChangeEvent((SevenBitNumber)82, (SevenBitNumber)127));

        Assert.Equal([ControlAction.Next, ControlAction.Vamp], actions);
    }

    [Theory]
    [InlineData("--control", "pedal", "--tap", "pedal")]
    [InlineData("--policy", "missing.json", "--count-in", "0")]
    public void Play_RejectsBadControlOptions(string option, string value, string option2, string value2)
    {
        var path = Path.GetTempFileName();
        try
        {
            Assert.Equal(MetroDomApp.UsageError, MetroDomApp.Create().Run(["play", path, "-o", "0", option, value, option2, value2], TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Play_RejectsABadPolicyFile()
    {
        var song = Path.GetTempFileName();
        var policy = Path.GetTempFileName();
        try
        {
            File.WriteAllText(policy, """{"schema":"metrodom.policy/0","mode":"x","controls":{"jump":"cc:1"}}""");
            Assert.Equal(MetroDomApp.UsageError, MetroDomApp.Create().Run(["play", song, "-o", "0", "--policy", policy], TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(song);
            File.Delete(policy);
        }
    }

    [Fact]
    public void Following_CountInWhileWaiting_StartsTheSong()
    {
        _live.Begin(null);
        _live.ListenForBeat(120);
        for (var i = 0; i < 3; i++)
        {
            _live.Beat();
            Assert.False(_live.Done.IsSet);
            _clock.Advance(TimeSpan.FromMilliseconds(500));
        }

        _live.Beat();
        Assert.Equal(LiveSignal.Start, _live.Signal);
        Assert.Equal((BeatState.Locked, 120.0), (_live.Following!.Value.State, Math.Round(_live.Following!.Value.Bpm!.Value, 6)));
    }

    [Fact]
    public void Following_StartsTheSongOnThePlayersBeat_AndFollowsThem()
    {
        _live.Begin(null);
        _live.ListenForBeat(120);
        for (var i = 0; i < 4; i++)
        {
            _clock.Advance(TimeSpan.FromMilliseconds(600)); // 100 BPM, song at 120.
            _live.Beat();
        }

        var lastHit = _clock.Now;
        var scheduler = new LatencyScheduler(_session.Orchestra, _clock, _session.Profiles);
        var notes = Enumerable.Range(0, 16).Select(i => new TimedMidiEvent(TimeSpan.FromMilliseconds(500 * i), new NoteOnEvent((SevenBitNumber)60, (SevenBitNumber)100))).ToArray();
        var beats = notes.Select(n => n.At).ToArray();
        _clock.Advance(TimeSpan.FromMilliseconds(100));
        Assert.True(_live.StartSong(scheduler, notes, beats, 1, TimeSpan.FromMilliseconds(250)));
        Assert.Equal(500.0 / 600, scheduler.Speed, 6);
        Assert.Equal(lastHit + TimeSpan.FromMilliseconds(600), scheduler.SoundTime(TimeSpan.Zero));

        _live.Begin(new ActiveSong(scheduler, new SectionNavigator(scheduler, []), (_, _) => { }, () => { }));
        _clock.Advance(TimeSpan.FromMilliseconds(480)); // The player's next beat, 20 ms early: the band is late.
        _live.Beat();
        Assert.True(scheduler.Speed > 500.0 / 600);

        _live.Begin(null);
        var speed = scheduler.Speed;
        _clock.Advance(TimeSpan.FromMilliseconds(600));
        _live.Tick(); // The song ended: no more following.
        Assert.Equal(speed, scheduler.Speed);
    }

    [Fact]
    public void NotFollowing_StartSongStartsAsAsked_AndBeatsAreIgnored()
    {
        var scheduler = new LatencyScheduler(_session.Orchestra, _clock, _session.Profiles);
        Assert.False(_live.StartSong(scheduler, [new(TimeSpan.Zero, new NoteOnEvent())], [], 1.5, TimeSpan.Zero));
        Assert.Equal(1.5, scheduler.Speed);
        _live.Begin(null);
        for (var i = 0; i < 6; i++)
        {
            _live.Beat();
            _clock.Advance(TimeSpan.FromMilliseconds(500));
        }

        Assert.False(_live.Done.IsSet);
        Assert.Null(_live.Following);
    }

    private sealed class Port : IMidiOutputPort
    {
        public string Name => "band";

        public ConcurrentBag<MidiEvent> Sent { get; } = [];

        public void Send(MidiEvent midiEvent) => Sent.Add(midiEvent);

        public void Dispose()
        {
        }
    }

    private sealed class Input : IMidiInputPort
    {
        public event Action<MidiEvent>? EventReceived;

        public string Name => "pedals";

        public void Raise(MidiEvent midiEvent) => EventReceived?.Invoke(midiEvent);

        public void Start()
        {
        }

        public void Stop()
        {
        }

        public void Dispose()
        {
        }
    }

    [Fact]
    public void CurfewNote_SaysWhenTheSetEnds_AndWhenItsOver()
    {
        var now = new DateTimeOffset(2026, 11, 13, 21, 0, 0, TimeSpan.FromHours(-5));
        using var live = new LiveControl(_session, _clock)
        {
            Curfew = new Curfew(new TimeOnly(23, 0), new TimeOnly(10, 0), null, TimeSpan.FromMinutes(5)),
            WallClock = () => now,
        };

        Assert.Equal("Set ends 23:00", live.CurfewNote());
        Assert.Equal("Set ends 23:00", live.Snapshot().Curfew);
        now = now.AddHours(3);
        Assert.Equal("Curfew until 10:00.", live.Snapshot().Curfew);
        Assert.Null(_live.Snapshot().Curfew);
    }
}

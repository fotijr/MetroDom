using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using MetroDom.Conductor.Cli;
using MetroDom.Conductor.Commands;
using MetroDom.Conductor.Midi;
using MetroDom.Core;
using MetroDom.Core.Discovery;

namespace MetroDom.Conductor.Tests;

/// <summary>ADR 0007 step 4: remembered assignments, auto-added instruments, hot-plug.</summary>
public class AutoBandTests
{
    private static readonly DeviceIdentity XyloId = DeviceIdentity.MetroDom(1, new Version(0, 1));
    private static readonly DeviceIdentity DrumsId = DeviceIdentity.MetroDom(2, new Version(0, 1));
    private static readonly DeviceIdentity RolandId = new("41", 0x10, 1, "1.0.0.0");

    private static readonly InstrumentProfile Xylo = new("xylo", TimeSpan.FromMilliseconds(10)) { Notes = new HashSet<int> { 60, 62, 64 } };
    private static readonly InstrumentProfile Drums = new("drums", TimeSpan.FromMilliseconds(35)) { Channel = 9 };

    private static IdentifiedPort Port(string name, DeviceIdentity? identity = null, InstrumentProfile? profile = null, bool hasInput = true) =>
        new(new MidiPortInfo(name, name), identity, HasInput: hasInput) { Profile = profile, ProfileError = identity is { IsMetroDom: true } && profile is null ? "didn't send its profile" : null };

    [Fact]
    public void Plan_AddsMetroDomInstruments_OnTheirChannelOrTheFirstFree()
    {
        var plan = AutoBand.Plan([Port("MetroDom xylo", XyloId, Xylo), Port("MetroDom drums", DrumsId, Drums)], new AssignmentBook(), []);

        Assert.Equal(["MetroDom xylo", "MetroDom drums"], plan.Members.Select(m => m.Port.Name));
        Assert.Equal([0], plan.Members[0].Spec.Channels);
        Assert.Contains("first free channel", plan.Members[0].Why, StringComparison.Ordinal);
        Assert.Equal([9], plan.Members[1].Spec.Channels);
        Assert.Contains("from its profile", plan.Members[1].Why, StringComparison.Ordinal);

        // The profile came along: latency and notes for the scheduler and arranger, named after the port.
        Assert.Equal(TimeSpan.FromMilliseconds(35), plan.Members[1].Profile.Latency);
        Assert.Equal([60, 62, 64], plan.Members[0].Profile.Notes!.Order());
        Assert.Equal("MetroDom xylo", plan.Members[0].Profile.Name);
        Assert.Empty(plan.Available);
    }

    [Fact]
    public void Plan_AutoAddedInstrumentsAvoidTakenChannels()
    {
        var plan = AutoBand.Plan([Port("drums", DrumsId, Drums), Port("xylo", XyloId, Xylo)], new AssignmentBook(), usedChannels: [0, 9]);

        Assert.Equal([1], plan.Members[0].Spec.Channels);
        Assert.Contains("channel 10 was taken", plan.Members[0].Why, StringComparison.Ordinal);
        Assert.Equal([2], plan.Members[1].Spec.Channels);
    }

    [Fact]
    public void Plan_WithEveryChannelTaken_OffersTheInstrumentInstead()
    {
        var plan = AutoBand.Plan([Port("xylo", XyloId, Xylo)], new AssignmentBook(), Enumerable.Range(0, 16));

        Assert.Empty(plan.Members);
        Assert.Contains("every channel is taken", Assert.Single(plan.Available).Why, StringComparison.Ordinal);
    }

    [Fact]
    public void Plan_OnlyOffersDevicesItCantVouchFor()
    {
        var plan = AutoBand.Plan(
            [Port("Roland", RolandId), Port("quiet"), Port("synth", hasInput: false), Port("old xylo", XyloId), new IdentifiedPort(new MidiPortInfo("busy", "busy"), null, "in use")],
            new AssignmentBook(),
            []);

        Assert.Empty(plan.Members);
        Assert.Equal(["Roland", "quiet", "synth", "old xylo", "busy"], plan.Available.Select(a => a.Port.Output.Name));
        Assert.Contains("Roland", plan.Available[0].Why, StringComparison.Ordinal);
        Assert.Contains("didn't say", plan.Available[1].Why, StringComparison.Ordinal);
        Assert.Contains("without a profile", plan.Available[3].Why, StringComparison.Ordinal);
        Assert.Contains("in use", plan.Available[4].Why, StringComparison.Ordinal);
    }

    [Fact]
    public void Plan_AssignmentsWin_AndClaimTheirChannelsFirst()
    {
        var book = new AssignmentBook();
        book.Set(new Assignment(AssignmentBook.KeyFor("Roland", RolandId), "Roland", [0, 1], TimeSpan.FromMilliseconds(5)));
        book.Set(new Assignment(AssignmentBook.KeyFor("drums", DrumsId), "drums", [9], Notes: new HashSet<int> { 36, 38 }));

        // The xylophone comes first in the list, but the Roland's channel 1 is the owner's.
        var plan = AutoBand.Plan([Port("xylo", XyloId, Xylo), Port("Roland", RolandId), Port("drums", DrumsId, Drums)], book, []);

        var byName = plan.Members.ToDictionary(m => m.Port.Name);
        Assert.Equal([0, 1], byName["Roland"].Spec.Channels);
        Assert.Equal(TimeSpan.FromMilliseconds(5), byName["Roland"].Profile.Latency);
        Assert.StartsWith("remembered: ch 1,2 · 5 ms", byName["Roland"].Why, StringComparison.Ordinal);
        Assert.Equal([2], byName["xylo"].Spec.Channels);

        // The owner set notes but not latency: the latency still comes from the instrument.
        Assert.Equal([36, 38], byName["drums"].Profile.Notes!.Order());
        Assert.Equal(TimeSpan.FromMilliseconds(35), byName["drums"].Profile.Latency);
    }

    [Fact]
    public void Plan_SkipsPortsAlreadyInTheBand()
    {
        var plan = AutoBand.Plan([Port("xylo", XyloId, Xylo), Port("Roland", RolandId)], new AssignmentBook(), [], new HashSet<string> { "xylo", "Roland" });

        Assert.Empty(plan.Members);
        Assert.Empty(plan.Available);
    }

    [Fact]
    public void Store_MissingFileIsEmpty_BrokenFileWarns_SaveRoundTrips()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"metrodom-assign-{Guid.NewGuid():N}");
        var path = Path.Combine(folder, "nested", "assignments.json");
        try
        {
            Assert.Empty(AssignmentStore.Load(path).All);

            var book = new AssignmentBook();
            book.Set(new Assignment("k", "Roland", [0]));
            AssignmentStore.Save(book, path);
            Assert.Equal("Roland", Assert.Single(AssignmentStore.Load(path).All).Port);
            Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!)); // no temp file left behind

            File.WriteAllText(path, "{ broken");
            var warnings = new List<string>();
            Assert.Empty(AssignmentStore.Load(path, warnings.Add).All);
            Assert.Contains("Ignoring", Assert.Single(warnings), StringComparison.Ordinal);

            Assert.Equal("x.json", AssignmentStore.Resolve("x.json"));
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    [Fact]
    public void Watcher_IdentifiesNewPortsOnce_AndReportsTheOnesThatLeave()
    {
        var backend = new DeviceBackend();
        backend.Add("already here", answer: XyloId);
        var inBand = new HashSet<string>();
        using var watcher = new DeviceWatcher(backend, ["already here"], inBand.Contains, TimeSpan.FromMilliseconds(100), every: TimeSpan.FromHours(1));
        var arrived = new List<IdentifiedPort>();
        var left = new List<string>();
        watcher.Arrived += arrived.Add;
        watcher.Left += left.Add;

        watcher.Poll();
        Assert.Empty(arrived); // present at start: already handled
        Assert.Equal(0, backend.RequestsTo("already here"));

        backend.Add("xylo", answer: XyloId, profile: InstrumentProfileJson.Write(Xylo));
        watcher.Poll();
        watcher.Poll();
        Assert.Equal("xylo", Assert.Single(arrived).Output.Name);
        Assert.Equal("xylo", arrived[0].Profile?.Name);
        Assert.Equal(1, backend.RequestsTo("xylo")); // asked once, not every second

        backend.Remove("xylo");
        watcher.Poll();
        Assert.Equal(["xylo"], left);

        // A band member coming back is the reconnecting output's job: not asked again.
        inBand.Add("xylo");
        backend.Add("xylo", answer: XyloId);
        watcher.Poll();
        Assert.Single(arrived);
        Assert.Equal(1, backend.RequestsTo("xylo"));
        Assert.All(backend.Opened, p => Assert.True(p.Disposed, $"{p.Name} left open"));
    }

    [Fact]
    public void Watcher_ReportsRepeatedFailuresOnce_AndKeepsGoing()
    {
        var backend = new FailingBackend();
        using var watcher = new DeviceWatcher(backend, [], _ => false, every: TimeSpan.FromHours(1));
        var failures = new List<Exception>();
        watcher.Failed += failures.Add;

        watcher.Poll();
        Assert.Empty(failures); // one hiccup while a device unplugs is normal

        watcher.Poll();
        watcher.Poll();
        Assert.Single(failures);
    }

    [Fact]
    public void Session_HotPluggedInstrument_JoinsAtTheNextSong_OnAFreeChannel()
    {
        var first = new RecordingPort("MetroDom xylo");
        using var session = new Session([new SessionOutput(first, [0], Xylo)], []) { Book = new AssignmentBook() };
        var opened = new List<RecordingPort>();

        session.Arrived(Port("MetroDom drums", DrumsId, Drums with { Channel = 0 })); // wants channel 1: taken
        session.Arrived(Port("Roland", RolandId));

        Assert.Single(session.Outputs); // never mid-song
        Assert.Equal([1], Assert.Single(session.Pending).Spec.Channels);
        Assert.Contains(session.Messages.Snapshot(), m => m.Contains("Joins at the next song", StringComparison.Ordinal));
        Assert.Contains(session.Messages.Snapshot(), m => m.Contains("Roland plugged in", StringComparison.Ordinal) && m.Contains("--assign", StringComparison.Ordinal));

        var joined = session.AdmitPending(info =>
        {
            var port = new RecordingPort(info.Name);
            opened.Add(port);
            return port;
        });

        Assert.Equal(["MetroDom drums"], joined);
        Assert.Equal(2, session.Outputs.Count);
        Assert.Equal(TimeSpan.FromMilliseconds(35), session.Profiles.Values.Max(p => p.MaxLatency)); // the scheduler will plan for it
        Assert.Empty(session.Pending);

        // Channel 2 now reaches the new instrument, through its own safety governor.
        session.Orchestra.Route(new NoteOnEvent((SevenBitNumber)38, (SevenBitNumber)90) { Channel = (FourBitNumber)1 });
        Assert.Single(opened[0].Sent);
        Assert.Empty(first.Sent);
        Assert.Equal(2, session.Governors.Count);
    }

    [Fact]
    public void Session_DeviceThatLeavesBeforeItsSong_DoesntJoin()
    {
        using var session = new Session([new SessionOutput(new RecordingPort("xylo"), [0], Xylo)], []) { Book = new AssignmentBook() };

        session.Arrived(Port("drums", DrumsId, Drums));
        session.Withdraw("drums");

        Assert.Empty(session.Pending);
        Assert.Empty(session.AdmitPending(_ => throw new InvalidOperationException("must not open")));
        Assert.Contains(session.Messages.Snapshot(), m => m.Contains("unplugged before it joined", StringComparison.Ordinal));
    }

    [Fact]
    public void Session_ADeviceThatWontOpen_DoesntStopTheSet()
    {
        using var session = new Session([new SessionOutput(new RecordingPort("xylo"), [0], Xylo)], []) { Book = new AssignmentBook() };
        session.Arrived(Port("drums", DrumsId, Drums));

        Assert.Empty(session.AdmitPending(_ => throw new MidiDeviceException("gone")));

        Assert.Single(session.Outputs);
        Assert.Contains(session.Messages.Snapshot(), m => m.Contains("drums couldn't join: gone", StringComparison.Ordinal));
    }

    [Fact]
    public void Session_NamedOnTheCommandLine_IgnoresHotPlug()
    {
        using var session = new Session([new SessionOutput(new RecordingPort("xylo"), [0], Xylo)], []);

        session.Arrived(Port("drums", DrumsId, Drums));

        Assert.Empty(session.Pending);
    }

    [Fact]
    public void Devices_ForgetAndAssign_UseTheGivenFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"metrodom-assign-{Guid.NewGuid():N}.json");
        try
        {
            var book = new AssignmentBook();
            book.Set(new Assignment("k", "Roland", [0]));
            AssignmentStore.Save(book, path);

            Assert.Equal(0, MetroDomApp.Create().Run(["devices", "--forget", "roland", "--assignments", path], TestContext.Current.CancellationToken));
            Assert.Empty(AssignmentStore.Load(path).All);

            Assert.Equal(MetroDomApp.UsageError, MetroDomApp.Create().Run(["devices", "--assign", "Roland=17", "--assignments", path], TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private sealed class RecordingPort(string name) : IMidiOutputPort
    {
        public string Name { get; } = name;

        public List<MidiEvent> Sent { get; } = [];

        public void Send(MidiEvent midiEvent) => Sent.Add(midiEvent);

        public void Dispose()
        {
        }
    }

    private sealed class FailingBackend : IMidiBackend
    {
        public IReadOnlyList<MidiPortInfo> Outputs() => throw new MidiDeviceException("driver hiccup");

        public IReadOnlyList<MidiPortInfo> Inputs() => [];

        public IMidiOutputPort OpenOutput(MidiPortInfo port) => throw new NotSupportedException();

        public IMidiInputPort OpenInput(MidiPortInfo port) => throw new NotSupportedException();

        public IVirtualPort CreateVirtualInput(string name) => throw new NotSupportedException();
    }
}

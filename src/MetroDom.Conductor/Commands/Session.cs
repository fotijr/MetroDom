using System.Runtime.InteropServices;
using MetroDom.Conductor.Cli;
using MetroDom.Conductor.Midi;
using MetroDom.Core;
using MetroDom.Core.Discovery;
using MetroDom.Core.Safety;
using MetroDom.Core.Timing;

namespace MetroDom.Conductor.Commands;

/// <summary>An open output and what it plays: its channels and profile (latency, notes, min interval).</summary>
internal sealed record SessionOutput(IMidiOutputPort Port, IReadOnlyList<int> Channels, InstrumentProfile Profile)
{
    /// <summary>An output described only by the command line.</summary>
    public SessionOutput(IMidiOutputPort port, IReadOnlyList<int> channels, TimeSpan latency, IReadOnlySet<int>? notes = null)
        : this(port, channels, new InstrumentProfile(port.Name, latency) { Notes = notes })
    {
    }

    /// <summary>
    /// The profile for <paramref name="name"/>: what the command line says, with anything it left
    /// out taken from the profile the instrument sent. <c>@LATENCY</c> replaces the instrument's
    /// latencies (per note too); <c>/NOTES</c> replaces its notes. The minimum interval always
    /// comes from the instrument, since the command line can't set one.
    /// </summary>
    public static InstrumentProfile Merge(string name, OutputSpec spec, InstrumentProfile? sent)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (sent is null)
        {
            return new InstrumentProfile(name, spec.Latency ?? TimeSpan.Zero) { Notes = spec.Notes };
        }

        return sent with
        {
            Name = name,
            Latency = spec.Latency ?? sent.Latency,
            NoteLatency = spec.Latency is null ? sent.NoteLatency : new Dictionary<int, TimeSpan>(),
            Notes = spec.Notes ?? sent.Notes,
        };
    }

    public bool IsConnected => Port is not ResilientOutput resilient || resilient.IsConnected;
}

/// <summary>
/// Shared setup for commands that drive outputs: a safety governor per output, orchestra, mutes,
/// Ctrl+C → panic. Everything sent goes through the governors (docs/RELIABILITY.md, layer 2).
/// </summary>
internal sealed class Session : IDisposable
{
    private readonly PosixSignalRegistration _sigterm;
    private readonly ManualResetEventSlim _stop = new();
    private readonly Lock _gate = new();
    private readonly List<SessionOutput> _outputs = [];
    private readonly List<SafetyGovernor> _governors = [];
    private readonly List<PlannedOutput> _pending = [];
    private readonly SafetyLimits _limits;
    private readonly IClock _clock;
    private DeviceWatcher? _watcher;
    private Action<string> _log = Ui.Warn;

    public Session(OutputSettings settings)
        : this(Open(settings, out var book), settings.MutedChannels, settings.Limits)
    {
        Book = book;
    }

    /// <summary>One output on every channel, no latency.</summary>
    internal Session(IMidiOutputPort output, IReadOnlyList<int> mutedChannels)
        : this([new SessionOutput(output, InstrumentSpec.AllChannels, TimeSpan.Zero)], mutedChannels)
    {
    }

    internal Session(IReadOnlyList<SessionOutput> outputs, IReadOnlyList<int> mutedChannels, SafetyLimits? limits = null, IClock? clock = null)
    {
        _clock = clock ?? new SystemClock();
        _limits = limits ?? SafetyLimits.None;
        foreach (var output in outputs)
        {
            Add(output);
        }

        Orchestra.SendFailed += (port, ex) => _log($"MIDI {port.Name}: send failed ({ex.Message}).");
        foreach (var channel in mutedChannels)
        {
            Orchestra.SetMuted(channel, true);
        }

        Console.CancelKeyPress += OnCancel;

        // Headless stops (systemd, docker stop) mean the same as Ctrl+C: panic, then exit cleanly.
        _sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, e =>
        {
            e.Cancel = true;
            RequestStop();
        });
    }

    public Orchestra Orchestra { get; } = new();

    /// <summary>The band right now. Grows when a hot-plugged device is admitted between songs.</summary>
    public IReadOnlyList<SessionOutput> Outputs
    {
        get
        {
            lock (_gate)
            {
                return [.. _outputs];
            }
        }
    }

    /// <summary>The governor in front of each output, in the same order as <see cref="Outputs"/>.</summary>
    public IReadOnlyList<SafetyGovernor> Governors
    {
        get
        {
            lock (_gate)
            {
                return [.. _governors];
            }
        }
    }

    /// <summary>
    /// The remembered assignments, when the band was put together from discovery (no <c>-o</c>).
    /// Null when the command line named the band, which then stays as named.
    /// </summary>
    public AssignmentBook? Book { get; internal set; }

    /// <summary>Devices waiting to join at the next song.</summary>
    public IReadOnlyList<PlannedOutput> Pending
    {
        get
        {
            lock (_gate)
            {
                return [.. _pending];
            }
        }
    }

    public ChannelActivity Activity { get; } = new();

    public MessageLog Messages { get; } = new();

    public string OutputName => string.Join(", ", Outputs.Select(o => o.Port.Name));

    public bool IsOutputConnected => Outputs.All(o => o.IsConnected);

    /// <summary>Declared latency per output (keyed by its governor, which is what the orchestra routes to), for the scheduler.</summary>
    public IReadOnlyDictionary<IMidiOutput, InstrumentProfile> Profiles =>
        Governors.ToDictionary(g => (IMidiOutput)g, g => g.Profile);

    /// <summary>One line per output that had notes held back or turned down, for the end of a run.</summary>
    public IEnumerable<string> SafetySummary() =>
        Governors.Where(g => g.Summary() is not null).Select(g => $"{g.Name}: {g.Summary()}");

    public bool IsStopping => _stop.IsSet;

    /// <summary>Routes warnings (send failures, unplug/replug notices) to <paramref name="log"/>.</summary>
    public void UseLog(Action<string> log)
    {
        _log = log;
        MidiDevices.LogSink = log;
    }

    public void RequestStop() => _stop.Set();

    public void WaitForStop() => _stop.Wait();

    public bool WaitForStop(TimeSpan timeout) => _stop.Wait(timeout);

    /// <summary>
    /// Watches for devices plugged in during the set (ADR 0007 step 4): an assigned device or a
    /// MetroDom instrument is queued to join at the next song (<see cref="AdmitPending"/>), never
    /// mid-song; anything else is announced as available. Only for a band put together from
    /// discovery. Unplug/replug of band members stays with <see cref="ResilientOutput"/>.
    /// </summary>
    public void WatchForDevices(IMidiBackend backend)
    {
        ArgumentNullException.ThrowIfNull(backend);
        if (Book is null || _watcher is not null)
        {
            return;
        }

        _watcher = new DeviceWatcher(backend, backend.Outputs().Select(o => o.Name), IsInBand);
        _watcher.Arrived += Arrived;
        _watcher.Left += Withdraw;
        _watcher.Failed += ex => _log($"Device watch: {ex.Message}");
    }

    /// <summary>What a newly seen device means for the band.</summary>
    internal void Arrived(IdentifiedPort port)
    {
        if (Book is null)
        {
            return;
        }

        BandPlan plan;
        lock (_gate)
        {
            var names = _outputs.Select(o => o.Port.Name).Concat(_pending.Select(p => p.Port.Name)).ToHashSet(StringComparer.Ordinal);
            var used = _outputs.SelectMany(o => o.Channels).Concat(_pending.SelectMany(p => p.Spec.Channels));
            plan = AutoBand.Plan([port], Book, used, names);
            _pending.AddRange(plan.Members);
        }

        foreach (var member in plan.Members)
        {
            Messages.Add($"{member.Port.Name} plugged in: {member.Why}. Joins at the next song.");
        }

        foreach (var device in plan.Available)
        {
            Messages.Add($"{device.Port.Output.Name} plugged in ({device.Why}). To use it: metrodom devices --assign \"{device.Port.Output.Name}=CHANNELS\".");
        }
    }

    /// <summary>A device that left before its song came: it no longer joins.</summary>
    internal void Withdraw(string port)
    {
        int removed;
        lock (_gate)
        {
            removed = _pending.RemoveAll(p => p.Port.Name == port);
        }

        if (removed > 0)
        {
            Messages.Add($"{port} unplugged before it joined.");
        }
    }

    /// <summary>
    /// Opens and adds the devices waiting to join. Call between songs, before the scheduler is made,
    /// so a new member's latency is planned for from its first note. Returns who joined.
    /// </summary>
    public IReadOnlyList<string> AdmitPending(Func<MidiPortInfo, IMidiOutputPort>? open = null)
    {
        open ??= MidiDevices.OpenOutput;
        List<PlannedOutput> joining;
        lock (_gate)
        {
            joining = [.. _pending];
            _pending.Clear();
        }

        var joined = new List<string>();
        foreach (var member in joining)
        {
            try
            {
                var port = open(member.Port);
                Add(new SessionOutput(port, member.Spec.Channels, member.Profile));
                joined.Add(port.Name);
                Messages.Add($"{port.Name} joined the band on channel {ChannelList.Format(member.Spec.Channels)}.");
            }
#pragma warning disable CA1031 // One device that can't be opened must not stop the set.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                Messages.Add($"{member.Port.Name} couldn't join: {ex.Message}");
            }
        }

        return joined;
    }

    private bool IsInBand(string port)
    {
        lock (_gate)
        {
            return _outputs.Any(o => o.Port.Name == port) || _pending.Any(p => p.Port.Name == port);
        }
    }

    /// <summary>Puts a governor in front of <paramref name="output"/> and routes its channels to it.</summary>
    private void Add(SessionOutput output)
    {
        var governor = new SafetyGovernor(output.Port, output.Profile, _limits, _clock);
        lock (_gate)
        {
            _outputs.Add(output);
            _governors.Add(governor);
        }

        foreach (var channel in output.Channels)
        {
            Orchestra.Assign(channel, governor);
        }
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        Console.CancelKeyPress -= OnCancel;
        _sigterm.Dispose();
        Orchestra.Panic();
        foreach (var output in Outputs)
        {
            output.Port.Dispose();
        }

        _stop.Dispose();
    }

    /// <summary>
    /// Opens the band. With <c>-o</c>: every named output, asking those the command line didn't
    /// fully describe for their profile. Without: the band discovery finds (assigned devices and
    /// MetroDom instruments, <paramref name="book"/> set), else a prompt for one output.
    /// </summary>
    private static List<SessionOutput> Open(OutputSettings settings, out AssignmentBook? book)
    {
        book = null;
        var specs = settings.OutputSpecs;
        var identify = !settings.NoIdentify;
        if (specs.Count == 0 && identify && Discover(settings) is { } found)
        {
            (book, var plan) = found;
            return OpenAll(plan.Select(m => (m.Port, m.Spec, m.Profile)).ToArray());
        }

        if (specs.Count == 0)
        {
            specs = [new OutputSpec(Port: null!, InstrumentSpec.AllChannels)];
        }

        var ports = specs.Select(s => PortPicker.FindOutput(s.Port)).ToArray();
        var sent = identify ? FetchProfiles(ports.Where((_, i) => specs[i].WantsProfile).Distinct().ToArray()) : [];
        return OpenAll(ports.Select((p, i) => (p, specs[i], SessionOutput.Merge(p.Name, specs[i], sent.GetValueOrDefault(p.Name)))).ToArray());
    }

    /// <summary>
    /// Asks every output who it is and plans a band from the answers and the remembered
    /// assignments. Null when nothing can join on its own (the caller then prompts).
    /// </summary>
    private static (AssignmentBook Book, IReadOnlyList<PlannedOutput> Plan)? Discover(OutputSettings settings)
    {
        var book = AssignmentStore.Load(AssignmentStore.Resolve(settings.Assignments), Ui.Warn);
        var plan = AutoBand.Plan(Identifier.Identify(MidiDevices.Backend.Value, Identifier.DefaultTimeout), book, []);
        foreach (var device in plan.Available)
        {
            Ui.Note($"Available: {device.Port.Output.Name} ({device.Why}). Give it a part with metrodom devices --assign \"{device.Port.Output.Name}=CHANNELS\".");
        }

        if (plan.Members.Count == 0)
        {
            Ui.Note("No assigned device or MetroDom instrument found.");
            return null;
        }

        foreach (var member in plan.Members)
        {
            Ui.Note($"Band: {member.Port.Name}, {member.Why} ({DevicesCommand.Summary(member.Profile)}).");
        }

        if (plan.Members.Any(m => m.Why.StartsWith("MetroDom", StringComparison.Ordinal)))
        {
            Ui.Note("Instruments found on their own play their channel as written; add --arrange to give each the part it plays best, or keep a choice with metrodom devices --assign.");
        }

        return (book, plan.Members);
    }

    /// <summary>Opens every output. Closes what it opened if one fails.</summary>
    private static List<SessionOutput> OpenAll(IReadOnlyList<(MidiPortInfo Port, OutputSpec Spec, InstrumentProfile Profile)> band)
    {
        var opened = new List<SessionOutput>();
        try
        {
            foreach (var (info, spec, profile) in band)
            {
                var port = MidiDevices.OpenOutput(info);
                opened.Add(new SessionOutput(port, spec.Channels, profile with { Name = port.Name }));
            }

            return opened;
        }
        catch
        {
            foreach (var output in opened)
            {
                output.Port.Dispose();
            }

            throw;
        }
    }

    /// <summary>Asks <paramref name="ports"/> who they are and reports what each MetroDom instrument sent.</summary>
    private static Dictionary<string, InstrumentProfile> FetchProfiles(MidiPortInfo[] ports)
    {
        if (ports.Length == 0)
        {
            return [];
        }

        var found = new Dictionary<string, InstrumentProfile>(StringComparer.Ordinal);
        foreach (var port in Identifier.Identify(MidiDevices.Backend.Value, Identifier.DefaultTimeout, ports))
        {
            if (port.Profile is { } profile)
            {
                found[port.Output.Name] = profile;
                Ui.Note($"{port.Output.Name}: using the profile it sent ({DevicesCommand.Summary(profile)}).");
            }
            else if (port.Kind == PortKind.MetroDom)
            {
                Ui.Warn($"{port.Output.Name}: MetroDom instrument, but {port.ProfileError}. Using the command line only.");
            }
        }

        return found;
    }

    private void OnCancel(object? sender, ConsoleCancelEventArgs e)
    {
        e.Cancel = true;
        RequestStop();
    }
}

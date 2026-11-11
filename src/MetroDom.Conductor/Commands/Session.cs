using MetroDom.Conductor.Cli;
using MetroDom.Conductor.Midi;
using MetroDom.Core;
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
    private readonly ManualResetEventSlim _stop = new();
    private Action<string> _log = Ui.Warn;

    public Session(OutputSettings settings)
        : this(Open(settings.OutputSpecs, !settings.NoIdentify), settings.MutedChannels, settings.Limits)
    {
    }

    /// <summary>One output on every channel, no latency.</summary>
    internal Session(IMidiOutputPort output, IReadOnlyList<int> mutedChannels)
        : this([new SessionOutput(output, InstrumentSpec.AllChannels, TimeSpan.Zero)], mutedChannels)
    {
    }

    internal Session(IReadOnlyList<SessionOutput> outputs, IReadOnlyList<int> mutedChannels, SafetyLimits? limits = null, IClock? clock = null)
    {
        Outputs = outputs;
        clock ??= new SystemClock();
        Governors = outputs.Select(o => new SafetyGovernor(o.Port, o.Profile, limits ?? SafetyLimits.None, clock)).ToArray();
        for (var i = 0; i < outputs.Count; i++)
        {
            foreach (var channel in outputs[i].Channels)
            {
                Orchestra.Assign(channel, Governors[i]);
            }
        }

        Orchestra.SendFailed += (port, ex) => _log($"MIDI {port.Name}: send failed ({ex.Message}).");
        foreach (var channel in mutedChannels)
        {
            Orchestra.SetMuted(channel, true);
        }

        Console.CancelKeyPress += OnCancel;
    }

    public Orchestra Orchestra { get; } = new();

    public IReadOnlyList<SessionOutput> Outputs { get; }

    /// <summary>The governor in front of each output, in the same order as <see cref="Outputs"/>.</summary>
    public IReadOnlyList<SafetyGovernor> Governors { get; }

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

    public void Dispose()
    {
        Console.CancelKeyPress -= OnCancel;
        Orchestra.Panic();
        foreach (var output in Outputs)
        {
            output.Port.Dispose();
        }

        _stop.Dispose();
    }

    /// <summary>
    /// Opens every requested output (or prompts for one). Outputs the command line didn't fully
    /// describe are asked for their profile first (<paramref name="identify"/>). Closes what it
    /// opened if one fails.
    /// </summary>
    private static List<SessionOutput> Open(IReadOnlyList<OutputSpec> specs, bool identify)
    {
        if (specs.Count == 0)
        {
            specs = [new OutputSpec(Port: null!, InstrumentSpec.AllChannels)];
        }

        var ports = specs.Select(s => PortPicker.FindOutput(s.Port)).ToArray();
        var sent = identify ? FetchProfiles(ports.Where((_, i) => specs[i].WantsProfile).Distinct().ToArray()) : [];
        var opened = new List<SessionOutput>();
        try
        {
            for (var i = 0; i < specs.Count; i++)
            {
                var port = MidiDevices.OpenOutput(ports[i]);
                opened.Add(new SessionOutput(port, specs[i].Channels, SessionOutput.Merge(port.Name, specs[i], sent.GetValueOrDefault(ports[i].Name))));
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

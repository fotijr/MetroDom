using MetroDom.Conductor.Cli;
using MetroDom.Conductor.Midi;
using MetroDom.Core;
using MetroDom.Core.Safety;
using MetroDom.Core.Timing;

namespace MetroDom.Conductor.Commands;

/// <summary>An open output and what it plays: its channels and declared latency.</summary>
internal sealed record SessionOutput(IMidiOutputPort Port, IReadOnlyList<int> Channels, TimeSpan Latency, IReadOnlySet<int>? Notes = null)
{
    public InstrumentProfile Profile => new(Port.Name, Latency) { Notes = Notes };

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
        : this(Open(settings.OutputSpecs), settings.MutedChannels, settings.Limits)
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

    /// <summary>Opens every requested output (or prompts for one). Closes what it opened if one fails.</summary>
    private static List<SessionOutput> Open(IReadOnlyList<OutputSpec> specs)
    {
        if (specs.Count == 0)
        {
            return [new SessionOutput(PortPicker.Output(null), InstrumentSpec.AllChannels, TimeSpan.Zero)];
        }

        var opened = new List<SessionOutput>();
        try
        {
            foreach (var spec in specs)
            {
                opened.Add(new SessionOutput(PortPicker.Output(spec.Port), spec.Channels, spec.Latency, spec.Notes));
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

    private void OnCancel(object? sender, ConsoleCancelEventArgs e)
    {
        e.Cancel = true;
        RequestStop();
    }
}

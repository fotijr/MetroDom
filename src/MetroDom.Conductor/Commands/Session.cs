using MetroDom.Conductor.Cli;
using MetroDom.Conductor.Midi;
using MetroDom.Core;

namespace MetroDom.Conductor.Commands;

/// <summary>An open output and what it plays: its channels and declared latency.</summary>
internal sealed record SessionOutput(IMidiOutputPort Port, IReadOnlyList<int> Channels, TimeSpan Latency)
{
    public InstrumentProfile Profile => new(Port.Name, Latency);

    public bool IsConnected => Port is not ResilientOutput resilient || resilient.IsConnected;
}

/// <summary>Shared setup for commands that drive outputs: orchestra, mutes, Ctrl+C → panic.</summary>
internal sealed class Session : IDisposable
{
    private readonly ManualResetEventSlim _stop = new();
    private Action<string> _log = Ui.Warn;

    public Session(OutputSettings settings)
        : this(Open(settings.OutputSpecs), settings.MutedChannels)
    {
    }

    /// <summary>One output on every channel, no latency.</summary>
    internal Session(IMidiOutputPort output, IReadOnlyList<int> mutedChannels)
        : this([new SessionOutput(output, InstrumentSpec.AllChannels, TimeSpan.Zero)], mutedChannels)
    {
    }

    internal Session(IReadOnlyList<SessionOutput> outputs, IReadOnlyList<int> mutedChannels)
    {
        Outputs = outputs;
        foreach (var output in outputs)
        {
            foreach (var channel in output.Channels)
            {
                Orchestra.Assign(channel, output.Port);
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

    public ChannelActivity Activity { get; } = new();

    public MessageLog Messages { get; } = new();

    public string OutputName => string.Join(", ", Outputs.Select(o => o.Port.Name));

    public bool IsOutputConnected => Outputs.All(o => o.IsConnected);

    /// <summary>Declared latency per output, for the scheduler.</summary>
    public IReadOnlyDictionary<IMidiOutput, InstrumentProfile> Profiles =>
        Outputs.ToDictionary(o => (IMidiOutput)o.Port, o => o.Profile);

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
                opened.Add(new SessionOutput(PortPicker.Output(spec.Port), spec.Channels, spec.Latency));
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

using MetroDom.Conductor.Cli;
using MetroDom.Conductor.Midi;
using MetroDom.Core;

namespace MetroDom.Conductor.Commands;

/// <summary>Shared setup for commands that drive an output: orchestra, mutes, Ctrl+C → panic.</summary>
internal sealed class Session : IDisposable
{
    private readonly IMidiOutputPort _output;
    private readonly ManualResetEventSlim _stop = new();
    private Action<string> _log = Ui.Warn;

    public Session(OutputSettings settings)
        : this(PortPicker.Output(settings.Output), settings.MutedChannels)
    {
    }

    internal Session(IMidiOutputPort output, IReadOnlyList<int> mutedChannels)
    {
        _output = output;
        Orchestra.AssignAll(_output);
        Orchestra.SendFailed += (port, ex) => _log($"MIDI {port.Name}: send failed ({ex.Message}).");
        foreach (var channel in mutedChannels)
        {
            Orchestra.SetMuted(channel, true);
        }

        Console.CancelKeyPress += OnCancel;
    }

    public Orchestra Orchestra { get; } = new();

    public ChannelActivity Activity { get; } = new();

    public MessageLog Messages { get; } = new();

    public string OutputName => _output.Name;

    public bool IsOutputConnected => _output is not ResilientOutput resilient || resilient.IsConnected;

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
        _output.Dispose();
        _stop.Dispose();
    }

    private void OnCancel(object? sender, ConsoleCancelEventArgs e)
    {
        e.Cancel = true;
        RequestStop();
    }
}

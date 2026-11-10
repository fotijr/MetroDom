using MetroDom.Conductor.Midi;
using MetroDom.Core;

namespace MetroDom.Conductor.Commands;

/// <summary>Shared setup for commands that drive an output: orchestra, mutes, Ctrl+C → panic.</summary>
internal sealed class Session : IDisposable
{
    private readonly IMidiOutputPort _output;
    private readonly ManualResetEventSlim _stop = new();

    public Session(CommandLine cli)
    {
        var port = cli.Option("output") ?? throw new UsageException($"{cli.Command}: missing --output <port>.");
        var mutes = cli.Channels("mute");
        _output = MidiDevices.OpenOutput(port);
        Orchestra.AssignAll(_output);
        Orchestra.SendFailed += (output, ex) => Console.Error.WriteLine($"MIDI {output.Name}: send failed ({ex.Message}).");
        foreach (var channel in mutes)
        {
            Orchestra.SetMuted(channel, true);
        }

        Console.CancelKeyPress += OnCancel;
        Console.WriteLine($"Output: {_output.Name}" + (mutes.Count > 0 ? $"  muted: {string.Join(",", mutes.Select(c => c + 1))}" : ""));
    }

    public Orchestra Orchestra { get; } = new();

    public void RequestStop() => _stop.Set();

    public void WaitForStop() => _stop.Wait();

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
        Console.WriteLine("Stopping (panic).");
        RequestStop();
    }
}

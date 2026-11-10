using MetroDom.Conductor.Midi;
using MetroDom.Core;

namespace MetroDom.Conductor.Commands;

internal static class PanicCommand
{
    public static int Run(CommandLine cli)
    {
        var port = cli.Option("output") ?? throw new UsageException("panic: missing --output <port>.");
        using var output = new DeviceOutput(MidiDevices.OpenOutput(port));
        Orchestra.SendPanic(output);
        Console.WriteLine($"Panic sent to {output.Name}.");
        return 0;
    }
}

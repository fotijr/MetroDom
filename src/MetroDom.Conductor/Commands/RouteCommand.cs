using MetroDom.Conductor.Midi;

namespace MetroDom.Conductor.Commands;

/// <summary>Successor to the WinForms Live Control form: a live controller plays the band.</summary>
internal static class RouteCommand
{
    public static int Run(CommandLine cli)
    {
        var port = cli.Option("input") ?? throw new UsageException("route: missing --input <port>.");
        using var session = new Session(cli);
        using var input = MidiDevices.OpenInput(port);
        input.EventReceived += e => session.Orchestra.Route(e);
        input.Start();

        Console.WriteLine($"Routing from {input.Name}. Ctrl+C to stop.");
        session.WaitForStop();
        input.Stop();
        return 0;
    }
}

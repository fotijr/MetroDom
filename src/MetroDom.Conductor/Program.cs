using MetroDom.Conductor;
using MetroDom.Conductor.Commands;

const string usage = """
    metrodom: MetroDom Conductor (prototype CLI)

    Usage:
      metrodom inspect <song.mid>                          Summarize tracks, channels, tempo
      metrodom devices                                     List MIDI inputs and outputs
      metrodom play <song.mid> --output <port> [--mute 10] Play a song to an output
      metrodom route --input <port> --output <port> [--mute 10]
                                                           Route a live controller to an output
      metrodom panic --output <port>                       All sound/notes off on every channel

    Ports are names or indexes from 'metrodom devices'. Channels are 1-16, comma separated.
    Ctrl+C during play/route stops and sends panic.
    """;

var cli = CommandLine.Parse(args);
try
{
    return cli.Command switch
    {
        "inspect" => InspectCommand.Run(cli),
        "devices" => DevicesCommand.Run(),
        "play" => PlayCommand.Run(cli),
        "route" => RouteCommand.Run(cli),
        "panic" => PanicCommand.Run(cli),
        null or "help" or "--help" or "-h" => Help(),
        _ => throw new UsageException($"Unknown command '{cli.Command}'."),
    };
}
catch (UsageException ex)
{
    Console.Error.WriteLine(ex.Message);
    Console.Error.WriteLine();
    Console.Error.WriteLine(usage);
    return 2;
}
catch (PlatformNotSupportedException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 3;
}

int Help()
{
    Console.WriteLine(usage);
    return 0;
}

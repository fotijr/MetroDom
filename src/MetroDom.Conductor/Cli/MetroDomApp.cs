using MetroDom.Conductor.Commands;
using MetroDom.Conductor.Midi;
using Spectre.Console;
using Spectre.Console.Cli;

namespace MetroDom.Conductor.Cli;

/// <summary>Composition of the <c>metrodom</c> CLI (Spectre.Console.Cli): commands, help, exit codes.</summary>
internal static class MetroDomApp
{
    public const int UsageError = 2;
    public const int PlatformError = 3;
    public const int DeviceError = 4;

    public static CommandApp Create()
    {
        MidiDevices.LogSink = Ui.Warn;
        var app = new CommandApp();
        app.Configure(config =>
        {
            config.SetApplicationName("metrodom");
            config.SetApplicationVersion(typeof(MetroDomApp).Assembly.GetName().Version?.ToString(3) ?? "0.0.0");

            config.AddCommand<InspectCommand>("inspect")
                .WithDescription("Summarize a MIDI file: tracks, channels, tempo, duration.")
                .WithExample("inspect", "song.mid");
            config.AddCommand<DevicesCommand>("devices")
                .WithDescription("List MIDI outputs (instruments) and inputs (controllers).")
                .WithExample("devices");
            config.AddCommand<PlayCommand>("play")
                .WithDescription("Play a song to an output, with a live status view. Ctrl+C stops and sends panic.")
                .WithExample("play", "song.mid", "--output", "0")
                .WithExample("play", "song.mid", "-o", "fluid", "--mute", "10");
            config.AddCommand<RouteCommand>("route")
                .WithDescription("Route a live controller to an output. Ctrl+C stops and sends panic.")
                .WithExample("route", "--input", "keystation", "--output", "1");
            config.AddCommand<SimulateCommand>("simulate")
                .WithDescription("Play a song to virtual instruments on a virtual clock and report how well they line up. No devices needed.")
                .WithExample("simulate")
                .WithExample("simulate", "song.mid", "-i", "xylo=1@10", "-i", "drums=10@35", "--jitter", "2")
                .WithExample("simulate", "--no-compensation");
            config.AddCommand<VirtualCommand>("virtual")
                .WithDescription("Create virtual instruments as OS MIDI ports (Linux, macOS) that play, a DAW or a keyboard can send to.")
                .WithExample("virtual")
                .WithExample("virtual", "-i", "xylo=1@10", "-i", "drums=10@35");
            config.AddCommand<DemoSongCommand>("demo-song")
                .WithDescription("Write the built-in two-piece demo song (xylophone + drums) to a .mid file.")
                .WithExample("demo-song", "demo.mid");
            config.AddCommand<PanicCommand>("panic")
                .WithDescription("All sound and notes off on every channel of an output.")
                .WithExample("panic", "--output", "0");

            config.SetExceptionHandler((ex, _) => Report(ex));
        });
        return app;
    }

    /// <summary>Prints an error (to stderr) and maps it to the CLI's documented exit code.</summary>
    internal static int Report(Exception ex)
    {
        switch (ex)
        {
            case CommandAppException { Pretty: { } pretty }:
                Ui.Error.Write(pretty);
                return UsageError;
            case CommandAppException or UsageException:
                Ui.Fail(ex.Message);
                Ui.Error.MarkupLine("[grey]Run [bold]metrodom --help[/] (or [bold]metrodom <command> --help[/]) for usage.[/]");
                return UsageError;
            case PlatformNotSupportedException:
                Ui.Fail(ex.Message);
                return PlatformError;
            case MidiDeviceException:
                Ui.Fail(ex.Message);
                return DeviceError;
            default:
                Ui.Error.WriteException(ex, ExceptionFormats.ShortenEverything);
                return 1;
        }
    }
}

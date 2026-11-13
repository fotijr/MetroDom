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
                .WithExample("devices")
                .WithExample("devices", "--identify");
            config.AddCommand<PlayCommand>("play")
                .WithDescription("Play a song to one or more outputs, each sent early by its latency. Tap or nudge the tempo live. Ctrl+C stops and sends panic.")
                .WithExample("play", "song.mid", "--output", "0")
                .WithExample("play", "song.mid", "-o", "fluid", "--mute", "10")
                .WithExample("play", "song.mid", "-o", "\"MetroDom xylo=1-9,11-16@10\"", "-o", "\"MetroDom drums=10@35\"", "--tempo", "92")
                .WithExample("play", "song.mid", "-o", "0", "--follow", "\"e-kit\"", "--follow-on", "note:38");
            config.AddCommand<ServeCommand>("serve")
                .WithDescription("Run the Conductor as a service: a song library, a queue people edit from their phones, the remote always on, tokens and queue kept across restarts, mDNS on the LAN.")
                .WithExample("serve", "~/songs")
                .WithExample("serve", "~/songs", "--policy", "policies/crowd-jukebox.json", "-o", "\"MetroDom xylo\"");
            config.AddCommand<CalibrateCommand>("calibrate")
                .WithDescription("Measure each instrument's latency note by note from an onset input (a piezo trigger, a mic-to-MIDI box) and keep it for play, serve and route.")
                .WithExample("calibrate", "-o", "\"MetroDom xylo\"", "--onsets", "\"trigger\"")
                .WithExample("calibrate", "--onsets", "\"MetroDom onsets\"", "--quick");
            config.AddCommand<RouteCommand>("route")
                .WithDescription("Route a live controller to an output. Ctrl+C stops and sends panic.")
                .WithExample("route", "--input", "keystation", "--output", "1");
            config.AddCommand<ArrangeCommand>("arrange")
                .WithDescription("Show which part of a song each instrument would play, and what had to change to fit.")
                .WithExample("arrange", "song.mid", "-i", "xylo=1@10/C4-C6", "-i", "drums=10@35/36,38,76");
            config.AddCommand<RolesCommand>("roles")
                .WithDescription("Show each part's role (drums, bass, melody…) and where it came from. --set saves corrections next to the song.")
                .WithExample("roles", "song.mid")
                .WithExample("roles", "song.mid", "--set", "\"Lead Synth=melody\"", "--set", "#3=guitar");
            config.AddCommand<BeatsCommand>("beats")
                .WithDescription("Listen to a player (drum triggers, a pad) and show the tempo and beats MetroDom hears. --record saves the hits for simulate --follow.")
                .WithExample("beats", "\"e-kit\"", "--on", "note:38", "--record", "groove.csv");
            config.AddCommand<SimulateCommand>("simulate")
                .WithDescription("Play a song to virtual instruments on a virtual clock and report how well they line up. No devices needed.")
                .WithExample("simulate")
                .WithExample("simulate", "song.mid", "-i", "xylo=1@10", "-i", "drums=10@35", "--jitter", "2")
                .WithExample("simulate", "--no-compensation")
                .WithExample("simulate", "song.mid", "--follow", "groove.csv")
                .WithExample("simulate", "song.mid", "--arrange", "-i", "xylo=1@10/60,64,67", "-i", "drums=10@35/36,38");
            config.AddCommand<VirtualCommand>("virtual")
                .WithDescription("Create virtual instruments as OS MIDI ports (Linux, macOS) that play, a DAW or a keyboard can send to.")
                .WithExample("virtual")
                .WithExample("virtual", "-i", "xylo=1@10", "-i", "drums=10@35");
            config.AddCommand<DemoSongCommand>("demo-song")
                .WithDescription("Write the built-in two-piece demo song (xylophone + drums) to a .mid file.")
                .WithExample("demo-song", "demo.mid");
            config.AddCommand<LightsCommand>("lights")
                .WithDescription("Check a light profile and show its patch, or hold a scene on the rig over sACN (DMX over the network).")
                .WithExample("lights", "rig.json")
                .WithExample("lights", "rig.json", "--scene", "36");
            config.AddCommand<BenchCommand>("bench")
                .WithDescription("Walk through an instrument's first power-on: every note soft and hard, a soak at the duty limit, latency from an onset input. Writes up the results.")
                .WithExample("bench", "\"MetroDom snare\"")
                .WithExample("bench", "\"MetroDom snare\"", "--onsets", "trigger", "--out", "bench.md");
            config.AddBranch("instrument", instrument =>
            {
                instrument.SetDescription("Start a new MetroDom instrument: its folder, module list, body file and README.");
                instrument.AddCommand<InstrumentNewCommand>("new")
                    .WithDescription("Make Instruments/<ID>/ with an instrument.toml, a body.json a brain will accept, and a README to fill in.")
                    .WithExample("instrument", "new", "snare", "--notes", "38", "--plays", "drum-kit")
                    .WithExample("instrument", "new", "xylophone-alto", "--notes", "C5-C6");
            });
            config.AddBranch("body", body =>
            {
                body.SetDescription("Check body files, or write one into a brain's body EEPROM so the brain becomes that instrument.");
                body.AddCommand<BodyCheckCommand>("check")
                    .WithDescription("Check a body file the way a brain will, and show what the instrument becomes.")
                    .WithExample("body", "check", "Instruments/xylophone_solenoid/body.json")
                    .WithExample("body", "check");
                body.AddCommand<BodyReadCommand>("read")
                    .WithDescription("Show the body record a brain booted from; --out saves it as a body file.")
                    .WithExample("body", "read", "\"MetroDom xylophone\"", "--out", "xylophone-backup.json");
                body.AddCommand<BodyWriteCommand>("write")
                    .WithDescription("Write a body file into a MetroDom brain over MIDI. The brain checks it, stores it, reads it back and restarts as that instrument.")
                    .WithExample("body", "write", "\"MetroDom brain\"", "Instruments/xylophone_solenoid/body.json")
                    .WithExample("body", "write", "\"MetroDom xylophone\"", "--calibrated");
            });
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

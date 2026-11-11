using System.ComponentModel;
using MetroDom.Conductor.Cli;
using MetroDom.Core.Simulation;
using Spectre.Console;
using Spectre.Console.Cli;

namespace MetroDom.Conductor.Commands;

/// <summary>Writes the built-in two-piece demo song, so play/virtual/DAWs can use it.</summary>
internal sealed class DemoSongCommand : Command<DemoSongCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<PATH>")]
        [Description("Where to write the .mid file.")]
        public string Path { get; init; } = "";

        [CommandOption("-f|--force")]
        [Description("Overwrite an existing file.")]
        public bool Force { get; init; }

        public override ValidationResult Validate() =>
            File.Exists(Path) && !Force
                ? ValidationResult.Error($"{Path} exists; pass --force to overwrite.")
                : ValidationResult.Success();
    }

    public override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        DemoSong.Create().Write(settings.Path, overwriteFile: true);
        Ui.Success($"Wrote {settings.Path}: xylophone on channel 1, drums on channel 10, 4 bars at 100 BPM.");
        return 0;
    }
}

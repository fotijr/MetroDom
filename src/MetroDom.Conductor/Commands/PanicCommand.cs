using System.ComponentModel;
using MetroDom.Conductor.Cli;
using MetroDom.Core;
using Spectre.Console.Cli;

namespace MetroDom.Conductor.Commands;

internal sealed class PanicCommand : Command<PanicCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("-o|--output <PORT>")]
        [Description("Output port: index, name, or unique part of a name from [bold]metrodom devices[/]. Prompted for when omitted in a terminal.")]
        public string? Output { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        using var output = PortPicker.Output(settings.Output);
        Orchestra.SendPanic(output);
        Ui.Success($"Panic sent to {output.Name}.");
        return 0;
    }
}

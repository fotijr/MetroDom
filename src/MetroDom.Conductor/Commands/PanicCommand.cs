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
        [Description("Output port: index, name, or unique part of a name from [bold]metrodom devices[/]. Repeat for several (play's PORT=CHANNELS@LATENCY form is accepted). Prompted for when omitted in a terminal.")]
        public string[]? Outputs { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        string?[] ports = settings.Outputs is { Length: > 0 } outputs
            ? outputs.Select(o => (string?)(OutputSpec.TryParse(o, out var spec, out _) ? spec.Port : o)).ToArray()
            : [null];
        foreach (var port in ports)
        {
            using var output = PortPicker.Output(port);
            Orchestra.SendPanic(output);
            Ui.Success($"Panic sent to {output.Name}.");
        }

        return 0;
    }
}

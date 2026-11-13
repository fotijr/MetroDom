using System.ComponentModel;
using MetroDom.Conductor.Cli;
using MetroDom.Conductor.Midi;
using Spectre.Console;
using Spectre.Console.Cli;

namespace MetroDom.Conductor.Commands;

internal sealed class PanicCommand : Command<PanicCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("-o|--output <PORT>")]
        [Description("Output port: index, name, or unique part of a name from [bold]metrodom devices[/]. Repeat for several (play's PORT=CHANNELS@LATENCY form is accepted). Prompted for when omitted in a terminal.")]
        public string[]? Outputs { get; init; }

        [CommandOption("-a|--all")]
        [Description("Every output port there is.")]
        public bool All { get; init; }

        public override ValidationResult Validate() =>
            All && Outputs is { Length: > 0 }
                ? ValidationResult.Error("--all and --output can't be used together.")
                : ValidationResult.Success();
    }

    public override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var backend = MidiDevices.Backend.Value;
        IReadOnlyList<MidiPortInfo> ports = settings.All
            ? backend.Outputs()
            : settings.Outputs is { Length: > 0 } outputs
                ? outputs.Select(o => PortPicker.FindOutput(OutputSpec.TryParse(o, out var spec, out _) ? spec.Port : o)).ToArray()
                : [PortPicker.FindOutput(null)];
        if (ports.Count == 0)
        {
            Ui.Warn("No MIDI outputs to panic.");
            return 0;
        }

        var failed = false;
        foreach (var result in Panicker.Send(backend, ports, Identifier.DefaultTimeout))
        {
            var name = result.Port.DisplayName;
            if (result.Error is not null)
            {
                failed = true;
                Ui.Fail($"Panic didn't reach {name}: {result.Error}");
            }
            else
            {
                Ui.Success(result.AllOff ? $"Panic sent to {name} (and the MetroDom all-off)." : $"Panic sent to {name}.");
            }
        }

        return failed ? 1 : 0;
    }
}

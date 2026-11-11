using System.ComponentModel;
using MetroDom.Conductor.Cli;
using Spectre.Console;
using Spectre.Console.Cli;

namespace MetroDom.Conductor.Commands;

/// <summary>Successor to the WinForms Live Control form: a live controller plays the band.</summary>
internal sealed class RouteCommand : Command<RouteCommand.Settings>
{
    public sealed class Settings : OutputSettings
    {
        [CommandOption("-i|--input <PORT>")]
        [Description("Controller input port: index, name, or unique part of a name from [bold]metrodom devices[/]. Prompted for when omitted in a terminal.")]
        public string? Input { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        string output;
        using (var session = new Session(settings))
        {
            output = session.OutputName;
            using var input = PortPicker.Input(settings.Input);
            input.EventReceived += e =>
            {
                if (session.Orchestra.Route(e) > 0)
                {
                    session.Activity.Record(e);
                }
            };
            input.Start();

            if (!Ui.IsInteractive)
            {
                Console.WriteLine($"Routing {input.Name} to {output}. Ctrl+C to stop.");
            }

            LiveStatus.Run(session, () => LiveStatus.Panel(
                $"⇄ {Markup.Escape(input.Name)}",
                session,
                progress: null));
            input.Stop();
        }

        Ui.Success($"Stopped. Panic sent to {output}.");
        return 0;
    }
}

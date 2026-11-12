using System.Globalization;
using MetroDom.Conductor.Midi;
using Spectre.Console;

namespace MetroDom.Conductor.Cli;

/// <summary>Turns a <c>--output</c>/<c>--input</c> value into an open port, prompting when it was omitted.</summary>
internal static class PortPicker
{
    public static IMidiOutputPort Output(string? query, string option = "--output") =>
        MidiDevices.OpenOutput(FindOutput(query, option));

    /// <summary>The output <paramref name="query"/> names (prompting when null), not yet opened.</summary>
    public static MidiPortInfo FindOutput(string? query, string option = "--output") =>
        MidiDevices.FindOutput(query ?? Pick(MidiDevices.OutputNames, "output", "instrument", option));

    public static IMidiInputPort Input(string? query, string option = "--input") =>
        MidiDevices.OpenInput(query ?? Pick(MidiDevices.InputNames, "input", "controller", option));

    private static string Pick(Func<IReadOnlyList<string>> list, string kind, string noun, string option)
    {
        if (!Ui.IsInteractive)
        {
            throw new UsageException($"missing {option} <PORT>. Run 'metrodom devices' to list {kind}s.");
        }

        var names = list();
        if (names.Count == 0)
        {
            throw new UsageException($"no MIDI {kind}s found. Plug one in, or start a software synth.");
        }

        var index = AnsiConsole.Prompt(
            new SelectionPrompt<int>()
                .Title($"Which MIDI [bold]{kind}[/] ({noun})?")
                .AddChoices(Enumerable.Range(0, names.Count))
                .UseConverter(i => $"{i,3}  {Markup.Escape(names[i])}"));
        return index.ToString(CultureInfo.InvariantCulture);
    }
}

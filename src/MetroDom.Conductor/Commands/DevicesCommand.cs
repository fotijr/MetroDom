using MetroDom.Conductor.Midi;

namespace MetroDom.Conductor.Commands;

internal static class DevicesCommand
{
    public static int Run()
    {
        Print("Outputs (instruments)", MidiDevices.OutputNames());
        Console.WriteLine();
        Print("Inputs (controllers)", MidiDevices.InputNames());
        return 0;
    }

    private static void Print(string title, IReadOnlyList<string> names)
    {
        Console.WriteLine($"{title}:");
        if (names.Count == 0)
        {
            Console.WriteLine("  (none)");
        }

        for (var i = 0; i < names.Count; i++)
        {
            Console.WriteLine($"  {i,3}  {names[i]}");
        }
    }
}

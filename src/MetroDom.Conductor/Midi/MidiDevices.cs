namespace MetroDom.Conductor.Midi;

/// <summary>
/// Picks the OS MIDI adapter (ADR 0002: Core never touches devices). Linux uses the ALSA
/// sequencer directly. Windows and macOS use DryWetMIDI's device API. Opened ports reconnect
/// themselves after an unplug (<see cref="ResilientOutput"/>, <see cref="ResilientInput"/>).
/// </summary>
internal static class MidiDevices
{
    private static readonly Lazy<IMidiBackend> Backend = new(Create);

    public static IReadOnlyList<string> OutputNames() => Backend.Value.Outputs().Select(p => p.DisplayName).ToArray();

    public static IReadOnlyList<string> InputNames() => Backend.Value.Inputs().Select(p => p.DisplayName).ToArray();

    /// <summary>Opens an output by index, exact name, or unique part of a name (e.g. ALSA "20:0").</summary>
    public static IMidiOutputPort OpenOutput(string query)
    {
        var ports = Backend.Value.Outputs();
        var port = ports[Resolve(query, ports.Select(p => p.DisplayName).ToArray(), "output")];
        return new ResilientOutput(Backend.Value, port, Log);
    }

    /// <summary>Opens an input by index, exact name, or unique part of a name (e.g. ALSA "20:0").</summary>
    public static IMidiInputPort OpenInput(string query)
    {
        var ports = Backend.Value.Inputs();
        var port = ports[Resolve(query, ports.Select(p => p.DisplayName).ToArray(), "input")];
        return new ResilientInput(Backend.Value, port, Log);
    }

    internal static int Resolve(string query, IReadOnlyList<string> names, string kind)
    {
        if (int.TryParse(query, out var index))
        {
            // A number is always an index, never a substring ("3" must not match "(1234:0)").
            return index >= 0 && index < names.Count
                ? index
                : throw new UsageException($"No MIDI {kind} number {index}. Run 'metrodom devices'.");
        }

        var exact = Matches(names, n => string.Equals(n, query, StringComparison.OrdinalIgnoreCase));
        if (exact.Count == 1)
        {
            return exact[0];
        }

        var partial = Matches(names, n => n.Contains(query, StringComparison.OrdinalIgnoreCase));
        return partial.Count switch
        {
            1 => partial[0],
            0 => throw new UsageException($"No MIDI {kind} matches '{query}'. Run 'metrodom devices'."),
            _ => throw new UsageException(
                $"'{query}' matches {partial.Count} MIDI {kind}s; use the index from 'metrodom devices'."),
        };
    }

    private static void Log(string message) => Console.Error.WriteLine(message);

    private static List<int> Matches(IReadOnlyList<string> names, Func<string, bool> predicate) =>
        Enumerable.Range(0, names.Count).Where(i => predicate(names[i])).ToList();

    private static IMidiBackend Create()
    {
        if (OperatingSystem.IsLinux())
        {
            return new Alsa.AlsaBackend();
        }

        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
        {
            return new DryWetMidiBackend();
        }

        throw new PlatformNotSupportedException("MIDI devices are supported on Linux, macOS and Windows.");
    }
}

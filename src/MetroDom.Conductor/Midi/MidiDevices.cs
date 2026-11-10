using Melanchall.DryWetMidi.Multimedia;

namespace MetroDom.Conductor.Midi;

/// <summary>
/// OS MIDI device access via DryWetMIDI. This is the platform adapter from ADR 0002: Core never
/// touches devices. DryWetMIDI's device API supports Windows and macOS. Linux needs its own
/// adapter (ALSA), tracked as TD-14 in docs/exec-plans/tech-debt-tracker.md.
/// </summary>
internal static class MidiDevices
{
    public static bool IsSupported => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

    public static void EnsureSupported()
    {
        if (!IsSupported)
        {
            throw new PlatformNotSupportedException(
                "MIDI device I/O isn't implemented on this OS yet (Linux ALSA adapter is planned, TD-14). " +
                "File commands like 'inspect' work everywhere.");
        }
    }

    public static IReadOnlyList<string> OutputNames()
    {
        EnsureSupported();
        return OutputDevice.GetAll().Select(Name).ToArray();
    }

    public static IReadOnlyList<string> InputNames()
    {
        EnsureSupported();
        return InputDevice.GetAll().Select(Name).ToArray();
    }

    /// <summary>Opens an output by exact name, or by index as listed by <c>metrodom devices</c>.</summary>
    public static OutputDevice OpenOutput(string nameOrIndex)
    {
        EnsureSupported();
        var names = OutputNames();
        return OutputDevice.GetByIndex(Resolve(nameOrIndex, names, "output"));
    }

    /// <summary>Opens an input by exact name, or by index as listed by <c>metrodom devices</c>.</summary>
    public static InputDevice OpenInput(string nameOrIndex)
    {
        EnsureSupported();
        var names = InputNames();
        return InputDevice.GetByIndex(Resolve(nameOrIndex, names, "input"));
    }

    private static int Resolve(string nameOrIndex, IReadOnlyList<string> names, string kind)
    {
        if (int.TryParse(nameOrIndex, out var index) && index >= 0 && index < names.Count)
        {
            return index;
        }

        for (var i = 0; i < names.Count; i++)
        {
            if (string.Equals(names[i], nameOrIndex, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        throw new UsageException($"No MIDI {kind} named or numbered '{nameOrIndex}'. Run 'metrodom devices'.");
    }

    private static string Name(MidiDevice device)
    {
        using (device)
        {
            return device.Name;
        }
    }
}

using MetroDom.Core;
using MetroDom.Core.Discovery;

namespace MetroDom.Conductor.Midi;

/// <summary>What <see cref="Panicker.Send"/> did for one port.</summary>
/// <param name="Port">The output.</param>
/// <param name="AllOff">It identified as MetroDom and also got the SysEx all-off.</param>
/// <param name="Error">Why the CCs couldn't be sent, or null.</param>
internal sealed record PanicResult(MidiPortInfo Port, bool AllOff, string? Error = null);

/// <summary>
/// <c>metrodom panic</c> for ports the Conductor knows nothing about. First CC 120/123 on every
/// channel to every port, at once, so nothing waits on a reply. Then one identify round for those
/// ports, and the MetroDom all-off (<c>F0 7D 4D 44 03 F7</c>) to each brain that answered, which
/// also reaches a brain listening on one channel only. MetroDom SysEx still goes only to ports
/// that identified as MetroDom (ADR 0007).
/// </summary>
internal static class Panicker
{
    public static IReadOnlyList<PanicResult> Send(IMidiBackend backend, IReadOnlyList<MidiPortInfo> outputs, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(outputs);
        var errors = outputs.Select(port => Try(backend, port, Orchestra.SendPanic)).ToArray();
        var reached = outputs.Where((_, i) => errors[i] is null).ToArray();
        var brains = reached.Length == 0
            ? []
            : Identifier.Identify(backend, timeout, reached)
                .Where(p => p.Kind == PortKind.MetroDom)
                .Select(p => p.Output.Name)
                .ToHashSet(StringComparer.Ordinal);

        return outputs.Select((port, i) => new PanicResult(
            port,
            errors[i] is null && brains.Contains(port.Name) && Try(backend, port, o => o.Send(ProfileExchange.AllOff())) is null,
            errors[i])).ToArray();
    }

    /// <returns>null if it was sent, else why not.</returns>
    private static string? Try(IMidiBackend backend, MidiPortInfo port, Action<IMidiOutputPort> send)
    {
        try
        {
            using var output = backend.OpenOutput(port);
            send(output);
            return null;
        }
#pragma warning disable CA1031 // One unreachable port must not stop the panic reaching the others.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return ex.Message;
        }
    }
}

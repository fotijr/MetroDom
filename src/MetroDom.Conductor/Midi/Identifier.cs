using System.Collections.Concurrent;
using MetroDom.Core.Discovery;

namespace MetroDom.Conductor.Midi;

/// <summary>What discovery learned about one output (ADR 0007, step 2).</summary>
internal enum PortKind
{
    /// <summary>Answered as a MetroDom instrument.</summary>
    MetroDom,

    /// <summary>Answered with another manufacturer's identity: a known general MIDI device.</summary>
    MidiDevice,

    /// <summary>Has a matching input but didn't answer. Many devices don't; still usable.</summary>
    NoReply,

    /// <summary>No input with the same name, so it can't answer (synths, virtual sinks).</summary>
    OutputOnly,

    /// <summary>Couldn't be opened to ask.</summary>
    Unavailable,
}

/// <summary>One output port and what it said it is.</summary>
internal sealed record IdentifiedPort(MidiPortInfo Output, DeviceIdentity? Identity, string? Error = null, bool HasInput = true)
{
    public PortKind Kind => Error is not null ? PortKind.Unavailable
        : Identity is { IsMetroDom: true } ? PortKind.MetroDom
        : Identity is not null ? PortKind.MidiDevice
        : HasInput ? PortKind.NoReply
        : PortKind.OutputOnly;
}

/// <summary>
/// Asks every output "who are you?" with the Universal SysEx Identity Request and listens on the
/// input of the same name. All ports are asked at once, so the whole scan takes one
/// <c>timeout</c> (ADR 0007 suggests ~250 ms), not one per port. Ports are opened directly, not
/// through the reconnecting wrappers, and closed again before returning.
/// </summary>
internal static class Identifier
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMilliseconds(250);

    public static IReadOnlyList<IdentifiedPort> Identify(IMidiBackend backend, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(backend);
        var outputs = backend.Outputs();
        var inputs = backend.Inputs().GroupBy(i => i.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var results = new IdentifiedPort?[outputs.Count];
        var replies = new ConcurrentDictionary<int, DeviceIdentity>();
        var opened = new List<IDisposable>();
        using var allAnswered = new ManualResetEventSlim();
        var expected = int.MaxValue;
        try
        {
            var asking = new List<(int Index, IMidiOutputPort Port)>();
            for (var i = 0; i < outputs.Count; i++)
            {
                var output = outputs[i];
                if (!inputs.TryGetValue(output.Name, out var inputInfo))
                {
                    results[i] = new IdentifiedPort(output, null, HasInput: false);
                    continue;
                }

                try
                {
                    var input = backend.OpenInput(inputInfo);
                    opened.Add(input);
                    var index = i;
                    input.EventReceived += e =>
                    {
                        // Loopback ports echo our own request back; only a reply counts.
                        if (DeviceIdentity.TryParseReply(e, out var identity) && replies.TryAdd(index, identity)
                            && replies.Count >= Volatile.Read(ref expected))
                        {
                            allAnswered.Set();
                        }
                    };
                    input.Start();
                    var port = backend.OpenOutput(output);
                    opened.Add(port);
                    asking.Add((i, port));
                }
#pragma warning disable CA1031 // One busy or vanished port must not stop the scan.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    results[i] = new IdentifiedPort(output, null, ex.Message);
                }
            }

            foreach (var (index, port) in asking)
            {
                try
                {
                    port.Send(DeviceIdentity.Request());
                }
#pragma warning disable CA1031
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    results[index] = new IdentifiedPort(outputs[index], null, ex.Message);
                }
            }

            Volatile.Write(ref expected, asking.Count(a => results[a.Index] is null));
            if (replies.Count < expected)
            {
                allAnswered.Wait(timeout);
            }
            for (var i = 0; i < outputs.Count; i++)
            {
                results[i] ??= new IdentifiedPort(outputs[i], replies.GetValueOrDefault(i));
            }

            return results!;
        }
        finally
        {
            foreach (var port in opened)
            {
                port.Dispose();
            }
        }
    }
}

using MetroDom.Core.Bodies;

namespace MetroDom.Conductor.Midi;

/// <summary>Sends a body record to a brain and waits for its Body Write Reply.</summary>
internal static class BodyWriter
{
    /// <summary>
    /// Long enough for a brain to write and read back a full record (9 EEPROM pages at ~5 ms each,
    /// plus USB), short enough that a brain without Body Write doesn't leave the command hanging.
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(2);

    /// <returns>The brain's reply, or <c>null</c> if it said nothing within <paramref name="timeout"/>.</returns>
    public static BodyWriteReply? Write(IMidiBackend backend, MidiPortInfo output, byte[] record, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(output);
        var inputInfo = backend.Inputs().FirstOrDefault(i => i.Name == output.Name)
            ?? throw new MidiDeviceException($"{output.Name} has no input port, so its reply can't be heard.");

        BodyWriteReply? reply = null;
        using var answered = new ManualResetEventSlim();
        using var input = backend.OpenInput(inputInfo);
        input.EventReceived += e =>
        {
            if (BodyWrite.TryParseReply(e, out var r))
            {
                reply = r;
                answered.Set();
            }
        };
        input.Start();
        using var port = backend.OpenOutput(output);
        foreach (var chunk in BodyWrite.Chunks(record))
        {
            port.Send(chunk);
        }

        answered.Wait(timeout);
        return reply;
    }

    /// <summary>
    /// Asks a brain for the body record it booted from.
    /// </summary>
    /// <returns>
    /// <c>Answered</c> false if it said nothing within <paramref name="timeout"/>; else the record,
    /// or <c>null</c> when it has none (the bench body or a silent brain).
    /// </returns>
    public static (bool Answered, byte[]? Record) Read(IMidiBackend backend, MidiPortInfo output, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(output);
        var inputInfo = backend.Inputs().FirstOrDefault(i => i.Name == output.Name)
            ?? throw new MidiDeviceException($"{output.Name} has no input port, so its answer can't be heard.");

        var assembler = new BodyReadAssembler();
        using var done = new ManualResetEventSlim();
        using var input = backend.OpenInput(inputInfo);
        input.EventReceived += e =>
        {
            lock (assembler)
            {
                if (assembler.Add(e))
                {
                    done.Set();
                }
            }
        };
        input.Start();
        using (var port = backend.OpenOutput(output))
        {
            port.Send(BodyWrite.ReadRequest());
        }

        done.Wait(timeout);
        lock (assembler)
        {
            return (assembler.Done, assembler.Record);
        }
    }
}

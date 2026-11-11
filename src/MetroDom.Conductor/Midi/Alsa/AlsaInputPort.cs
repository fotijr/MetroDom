using System.Runtime.Versioning;
using Melanchall.DryWetMidi.Core;
using static MetroDom.Conductor.Midi.Alsa.AlsaNative;

namespace MetroDom.Conductor.Midi.Alsa;

/// <summary>
/// Receives from one ALSA sequencer port on a dedicated thread (poll, no sleeps), decoding
/// sequencer events back to MIDI bytes and then to DryWetMIDI events.
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed unsafe class AlsaInputPort : IMidiInputPort
{
    private const int PollTimeoutMs = 100;
    private readonly nint _seq;
    private readonly nint _decoder;
    private Thread? _thread;
    private volatile bool _running;
    private bool _disposed;

    public AlsaInputPort(AlsaPort source)
        : this(source.DisplayName, "in", source)
    {
    }

    private AlsaInputPort(string name, string portName, AlsaPort? source)
    {
        Name = name;
        _seq = AlsaBackend.OpenSequencer(SND_SEQ_OPEN_INPUT);
        try
        {
            var port = Check(snd_seq_create_simple_port(
                _seq,
                portName,
                SND_SEQ_PORT_CAP_WRITE | SND_SEQ_PORT_CAP_SUBS_WRITE,
                SND_SEQ_PORT_TYPE_MIDI_GENERIC | SND_SEQ_PORT_TYPE_APPLICATION), "create_simple_port");
            if (source is not null)
            {
                Check(snd_seq_connect_from(_seq, port, source.Client, source.Port), $"connect from {source.DisplayName}");
            }

            Check(snd_seq_nonblock(_seq, 1), "nonblock");
            Check(snd_midi_event_new(4096, out _decoder), "midi_event_new");
            snd_midi_event_no_status(_decoder, 1); // Always emit status bytes (no running status).
        }
        catch
        {
            _ = snd_seq_close(_seq);
            throw;
        }
    }

    public event Action<MidiEvent>? EventReceived;

    /// <summary>
    /// A port named "MetroDom:<paramref name="portName"/>" that any ALSA client can connect
    /// to and send to (e.g. <c>metrodom play -o "MetroDom:xylo"</c> or <c>aconnect</c>).
    /// </summary>
    public static AlsaInputPort CreateVirtual(string portName) => new($"MetroDom:{portName}", portName, null);

    public string Name { get; }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_running)
        {
            return;
        }

        _running = true;
        _thread = new Thread(Listen) { IsBackground = true, Name = "ALSA MIDI in", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    public void Stop()
    {
        _running = false;
        _thread?.Join();
        _thread = null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Stop();
        _disposed = true;
        snd_midi_event_free(_decoder);
        _ = snd_seq_close(_seq);
    }

    private void Listen()
    {
        var count = Math.Max(1, snd_seq_poll_descriptors_count(_seq, POLLIN));
        var fds = stackalloc PollFd[count];
        count = snd_seq_poll_descriptors(_seq, fds, (uint)count, POLLIN);
        var buffer = new byte[4096];
        using var toEvent = new BytesToMidiEventConverter();

        while (_running)
        {
            if (poll(fds, (nuint)count, PollTimeoutMs) <= 0)
            {
                continue;
            }

            while (_running)
            {
                var result = snd_seq_event_input(_seq, out var ev);
                if (result == -ENOSPC)
                {
                    continue; // Kernel buffer overran; events were dropped, keep going.
                }

                if (result < 0)
                {
                    break; // -EAGAIN: drained.
                }

                Dispatch(ev, buffer, toEvent);
            }
        }
    }

    private void Dispatch(byte* ev, byte[] buffer, BytesToMidiEventConverter toEvent)
    {
        nint length;
        fixed (byte* data = buffer)
        {
            length = snd_midi_event_decode(_decoder, data, buffer.Length, ev);
        }

        if (length <= 0)
        {
            return; // Not a MIDI message (e.g. port subscribed notice) or too large.
        }

        try
        {
            EventReceived?.Invoke(toEvent.Convert(buffer, 0, (int)length));
        }
#pragma warning disable CA1031 // A bad message or handler must not kill the input thread.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Console.Error.WriteLine($"MIDI in ({Name}): {ex.Message}");
        }
    }
}

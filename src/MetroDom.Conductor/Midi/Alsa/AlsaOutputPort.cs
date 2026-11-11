using System.Runtime.Versioning;
using Melanchall.DryWetMidi.Core;
using static MetroDom.Conductor.Midi.Alsa.AlsaNative;

namespace MetroDom.Conductor.Midi.Alsa;

/// <summary>
/// Sends to one ALSA sequencer port. Each MIDI event goes out immediately (direct, unqueued);
/// timing belongs to the Conductor's scheduler, not ALSA queues.
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed unsafe class AlsaOutputPort : IMidiOutputPort
{
    private readonly Lock _lock = new();
    private readonly MidiEventToBytesConverter _toBytes = new();
    private readonly nint _seq;
    private readonly nint _encoder;
    private readonly int _port;
    private bool _disposed;

    public AlsaOutputPort(AlsaPort target)
    {
        Name = target.DisplayName;
        _seq = AlsaBackend.OpenSequencer(SND_SEQ_OPEN_OUTPUT);
        try
        {
            _port = Check(snd_seq_create_simple_port(
                _seq,
                "out",
                SND_SEQ_PORT_CAP_READ | SND_SEQ_PORT_CAP_SUBS_READ,
                SND_SEQ_PORT_TYPE_MIDI_GENERIC | SND_SEQ_PORT_TYPE_APPLICATION), "create_simple_port");
            Check(snd_seq_connect_to(_seq, _port, target.Client, target.Port), $"connect to {target.DisplayName}");
            Check(snd_midi_event_new(4096, out _encoder), "midi_event_new");
        }
        catch
        {
            _ = snd_seq_close(_seq);
            throw;
        }
    }

    public string Name { get; }

    public void Send(MidiEvent midiEvent)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            SendToSubscribers(_seq, _encoder, _port, _toBytes.Convert(midiEvent), Name, midiEvent.EventType.ToString());
        }
    }

    /// <summary>Encodes MIDI bytes to sequencer events and sends them, unqueued, to the port's subscribers.</summary>
    internal static void SendToSubscribers(nint seq, nint encoder, int port, byte[] bytes, string name, string what)
    {
        var ev = stackalloc byte[EventSize];
        snd_midi_event_reset_encode(encoder);
        fixed (byte* data = bytes)
        {
            nint offset = 0;
            while (offset < bytes.Length)
            {
                new Span<byte>(ev, EventSize).Clear();
                var used = snd_midi_event_encode(encoder, data + offset, bytes.Length - offset, ev);
                if (used <= 0)
                {
                    throw new MidiDeviceException($"ALSA couldn't encode {what} for {name}.");
                }

                offset += used;
                if (ev[EventTypeOffset] == SND_SEQ_EVENT_NONE)
                {
                    continue; // Message not complete yet (long SysEx spans calls).
                }

                // snd_seq_ev_set_source / set_subs / set_direct (header-only macros in C).
                ev[EventQueueOffset] = SND_SEQ_QUEUE_DIRECT;
                ev[EventSourcePortOffset] = (byte)port;
                ev[EventDestClientOffset] = SND_SEQ_ADDRESS_SUBSCRIBERS;
                ev[EventDestPortOffset] = SND_SEQ_ADDRESS_UNKNOWN;
                Check(snd_seq_event_output_direct(seq, ev), $"send to {name}");
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            snd_midi_event_free(_encoder);
            _ = snd_seq_close(_seq);
            _toBytes.Dispose();
        }
    }
}

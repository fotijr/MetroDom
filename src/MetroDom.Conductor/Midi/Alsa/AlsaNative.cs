using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace MetroDom.Conductor.Midi.Alsa;

#pragma warning disable CA1707, SA1300 // Keep ALSA's C names so the API docs map 1:1.

/// <summary>
/// P/Invoke into alsa-lib (libasound.so.2), sequencer API. Present on essentially every Linux
/// desktop and Raspberry Pi OS; on minimal images install <c>libasound2</c>.
/// </summary>
[SupportedOSPlatform("linux")]
internal static unsafe partial class AlsaNative
{
    private const string Lib = "libasound.so.2";

    public const int SND_SEQ_OPEN_OUTPUT = 1;
    public const int SND_SEQ_OPEN_INPUT = 2;
    public const int SND_SEQ_OPEN_DUPLEX = 3;
    public const int SND_SEQ_NONBLOCK = 1;

    public const uint SND_SEQ_PORT_CAP_READ = 1 << 0;
    public const uint SND_SEQ_PORT_CAP_WRITE = 1 << 1;
    public const uint SND_SEQ_PORT_CAP_SUBS_READ = 1 << 5;
    public const uint SND_SEQ_PORT_CAP_SUBS_WRITE = 1 << 6;
    public const uint SND_SEQ_PORT_CAP_NO_EXPORT = 1 << 7;
    public const uint SND_SEQ_PORT_TYPE_MIDI_GENERIC = 1 << 1;
    public const uint SND_SEQ_PORT_TYPE_APPLICATION = 1 << 20;

    // snd_seq_event_t is a fixed 28-byte struct (kernel ABI, same on 32/64-bit).
    public const int EventSize = 28;
    public const int EventTypeOffset = 0;
    public const int EventQueueOffset = 3;
    public const int EventSourcePortOffset = 13;
    public const int EventDestClientOffset = 14;
    public const int EventDestPortOffset = 15;
    public const byte SND_SEQ_EVENT_NONE = 0;
    public const byte SND_SEQ_QUEUE_DIRECT = 253;
    public const byte SND_SEQ_ADDRESS_SUBSCRIBERS = 254;
    public const byte SND_SEQ_ADDRESS_UNKNOWN = 253;

    public const short POLLIN = 1;
    public const int EAGAIN = 11;
    public const int ENOSPC = 28;

    [StructLayout(LayoutKind.Sequential)]
    public struct PollFd
    {
        public int Fd;
        public short Events;
        public short Revents;
    }

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int snd_seq_open(out nint handle, string name, int streams, int mode);

    [LibraryImport(Lib)]
    public static partial int snd_seq_close(nint handle);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int snd_seq_set_client_name(nint handle, string name);

    [LibraryImport(Lib)]
    public static partial int snd_seq_client_id(nint handle);

    [LibraryImport(Lib)]
    public static partial int snd_seq_nonblock(nint handle, int nonblock);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int snd_seq_create_simple_port(nint handle, string name, uint caps, uint type);

    [LibraryImport(Lib)]
    public static partial int snd_seq_connect_to(nint handle, int myPort, int destClient, int destPort);

    [LibraryImport(Lib)]
    public static partial int snd_seq_connect_from(nint handle, int myPort, int srcClient, int srcPort);

    [LibraryImport(Lib)]
    public static partial int snd_seq_event_output_direct(nint handle, byte* ev);

    [LibraryImport(Lib)]
    public static partial int snd_seq_event_input(nint handle, out byte* ev);

    [LibraryImport(Lib)]
    public static partial int snd_seq_poll_descriptors_count(nint handle, short events);

    [LibraryImport(Lib)]
    public static partial int snd_seq_poll_descriptors(nint handle, PollFd* pfds, uint space, short events);

    [LibraryImport(Lib)]
    public static partial int snd_seq_client_info_malloc(out nint info);

    [LibraryImport(Lib)]
    public static partial void snd_seq_client_info_free(nint info);

    [LibraryImport(Lib)]
    public static partial void snd_seq_client_info_set_client(nint info, int client);

    [LibraryImport(Lib)]
    public static partial int snd_seq_client_info_get_client(nint info);

    [LibraryImport(Lib)]
    public static partial nint snd_seq_client_info_get_name(nint info);

    [LibraryImport(Lib)]
    public static partial int snd_seq_query_next_client(nint handle, nint info);

    [LibraryImport(Lib)]
    public static partial int snd_seq_port_info_malloc(out nint info);

    [LibraryImport(Lib)]
    public static partial void snd_seq_port_info_free(nint info);

    [LibraryImport(Lib)]
    public static partial void snd_seq_port_info_set_client(nint info, int client);

    [LibraryImport(Lib)]
    public static partial void snd_seq_port_info_set_port(nint info, int port);

    [LibraryImport(Lib)]
    public static partial int snd_seq_port_info_get_port(nint info);

    [LibraryImport(Lib)]
    public static partial uint snd_seq_port_info_get_capability(nint info);

    [LibraryImport(Lib)]
    public static partial nint snd_seq_port_info_get_name(nint info);

    [LibraryImport(Lib)]
    public static partial int snd_seq_query_next_port(nint handle, nint info);

    [LibraryImport(Lib)]
    public static partial int snd_midi_event_new(nuint bufferSize, out nint parser);

    [LibraryImport(Lib)]
    public static partial void snd_midi_event_free(nint parser);

    [LibraryImport(Lib)]
    public static partial void snd_midi_event_no_status(nint parser, int on);

    [LibraryImport(Lib)]
    public static partial void snd_midi_event_reset_encode(nint parser);

    [LibraryImport(Lib)]
    public static partial nint snd_midi_event_encode(nint parser, byte* buffer, nint count, byte* ev);

    [LibraryImport(Lib)]
    public static partial nint snd_midi_event_decode(nint parser, byte* buffer, nint count, byte* ev);

    [LibraryImport(Lib)]
    public static partial nint snd_strerror(int error);

    [LibraryImport("libc")]
    public static partial int poll(PollFd* fds, nuint nfds, int timeoutMs);

    public static string Utf8(nint text) => Marshal.PtrToStringUTF8(text) ?? "";

    /// <summary>Throws <see cref="MidiDeviceException"/> for a negative ALSA return code.</summary>
    public static int Check(int result, string what) =>
        result >= 0 ? result : throw new MidiDeviceException($"ALSA {what} failed: {Utf8(snd_strerror(result))}");
}

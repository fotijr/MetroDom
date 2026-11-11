using System.Runtime.Versioning;
using static MetroDom.Conductor.Midi.Alsa.AlsaNative;

namespace MetroDom.Conductor.Midi.Alsa;

/// <summary>
/// Linux MIDI via the ALSA sequencer: USB-MIDI devices, MIDI interfaces, software synths and
/// virtual ports all appear here. Port names are "Client:Port (client:port)".
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed class AlsaBackend : IMidiBackend
{
    private const uint Writable = SND_SEQ_PORT_CAP_WRITE | SND_SEQ_PORT_CAP_SUBS_WRITE;
    private const uint Readable = SND_SEQ_PORT_CAP_READ | SND_SEQ_PORT_CAP_SUBS_READ;

    public IReadOnlyList<MidiPortInfo> Outputs() => Ports(Writable);

    public IReadOnlyList<MidiPortInfo> Inputs() => Ports(Readable);

    public IMidiOutputPort OpenOutput(MidiPortInfo port) => new AlsaOutputPort((AlsaPort)port);

    public IMidiInputPort OpenInput(MidiPortInfo port) => new AlsaInputPort((AlsaPort)port);

    public IVirtualPort CreateVirtualInput(string name) => AlsaInputPort.CreateVirtual(name);

    /// <summary>Opens a sequencer client named "MetroDom", translating "no ALSA" into a clear error.</summary>
    internal static nint OpenSequencer(int streams)
    {
        int result;
        nint handle;
        try
        {
            result = snd_seq_open(out handle, "default", streams, 0);
        }
        catch (DllNotFoundException)
        {
            throw new PlatformNotSupportedException(
                "libasound.so.2 not found. Install ALSA (e.g. 'sudo apt install libasound2t64' or 'libasound2').");
        }

        if (result < 0)
        {
            throw new MidiDeviceException(
                $"Can't open the ALSA sequencer ({Utf8(snd_strerror(result))}). " +
                "Check /dev/snd/seq exists (modprobe snd-seq) and you're in the 'audio' group.");
        }

        _ = snd_seq_set_client_name(handle, "MetroDom");
        return handle;
    }

    private static List<AlsaPort> Ports(uint caps)
    {
        var seq = OpenSequencer(SND_SEQ_OPEN_OUTPUT);
        Check(snd_seq_client_info_malloc(out var client), "client_info_malloc");
        Check(snd_seq_port_info_malloc(out var port), "port_info_malloc");
        try
        {
            var self = snd_seq_client_id(seq);
            var ports = new List<AlsaPort>();
            snd_seq_client_info_set_client(client, -1);
            while (snd_seq_query_next_client(seq, client) >= 0)
            {
                var clientId = snd_seq_client_info_get_client(client);
                if (clientId == 0 || clientId == self)
                {
                    continue; // 0 is the System client (timer/announce), not music.
                }

                var clientName = Utf8(snd_seq_client_info_get_name(client));
                snd_seq_port_info_set_client(port, clientId);
                snd_seq_port_info_set_port(port, -1);
                while (snd_seq_query_next_port(seq, port) >= 0)
                {
                    var portCaps = snd_seq_port_info_get_capability(port);
                    if ((portCaps & caps) == caps && (portCaps & SND_SEQ_PORT_CAP_NO_EXPORT) == 0)
                    {
                        var portName = Utf8(snd_seq_port_info_get_name(port));
                        ports.Add(new AlsaPort(clientId, snd_seq_port_info_get_port(port), $"{clientName}:{portName}"));
                    }
                }
            }

            return ports;
        }
        finally
        {
            snd_seq_port_info_free(port);
            snd_seq_client_info_free(client);
            _ = snd_seq_close(seq);
        }
    }
}

/// <summary>
/// An ALSA sequencer port. The address can change when a device is replugged; the
/// "Client:Port" name is what identifies it across replugs.
/// </summary>
internal sealed record AlsaPort(int Client, int Port, string Name)
    : MidiPortInfo(Name, $"{Name} ({Client}:{Port})");

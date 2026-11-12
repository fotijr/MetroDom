using System.Net;
using System.Net.Sockets;
using MetroDom.Core.Lighting;

namespace MetroDom.Conductor.Lighting;

/// <summary>Sends UDP datagrams. A seam so the sender is tested without a network.</summary>
internal interface ISacnSocket : IDisposable
{
    void Send(byte[] packet, string host, int port);
}

/// <summary>The real socket: one UDP client for every universe, multicast or unicast.</summary>
internal sealed class UdpSacnSocket : ISacnSocket
{
    private readonly UdpClient _client = new(AddressFamily.InterNetwork);
    private readonly Dictionary<string, IPEndPoint> _endpoints = new(StringComparer.Ordinal);

    public UdpSacnSocket()
    {
        // Stay on the local network: a rig is one switch, not the internet.
        _client.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 1);
        _client.MulticastLoopback = true; // so a receiver on this machine (QLC+, a test) hears it too
    }

    public void Send(byte[] packet, string host, int port)
    {
        if (!_endpoints.TryGetValue(host, out var endpoint))
        {
            var address = IPAddress.TryParse(host, out var parsed)
                ? parsed
                : Dns.GetHostAddresses(host).First(a => a.AddressFamily == AddressFamily.InterNetwork);
            endpoint = _endpoints[host] = new IPEndPoint(address, port);
        }

        _client.Send(packet, packet.Length, endpoint);
    }

    public void Dispose() => _client.Dispose();
}

/// <summary>
/// Streams DMX frames as sACN (E1.31). A changed frame goes out on the next tick (25 ms, under
/// DMX's 44 Hz) and twice more for lost packets; an unchanged one is repeated every second so
/// receivers don't time out (they wait 2.5 s). Disposing ends each stream with terminated packets.
/// A send that fails is logged once per run of failures and never thrown: lights never block music
/// (ADR 0011 §5).
/// </summary>
internal sealed class SacnOutput : IDisposable
{
    public static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(25);
    public static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(1);
    public const int Repeats = 3;

    private readonly ISacnSocket _socket;
    private readonly LightOutput _output;
    private readonly string _sourceName;
    private readonly TimeProvider _time;
    private readonly Action<string> _log;
    private readonly Lock _gate = new();
    private readonly Dictionary<int, Stream> _streams = [];
    private readonly ITimer? _timer;
    private bool _failing;
    private bool _disposed;

    /// <param name="socket">Where packets go.</param>
    /// <param name="output">Destination and priority from the light profile.</param>
    /// <param name="sourceName">Shown by receivers (QLC+, WLED) as who is sending.</param>
    /// <param name="cid">This source's id; random per run by default.</param>
    /// <param name="time">Clock for ticks and keep-alives.</param>
    /// <param name="log">Where send failures are reported.</param>
    /// <param name="start">False leaves ticking to the caller (<see cref="Pump"/>), for tests.</param>
    public SacnOutput(ISacnSocket socket, LightOutput output, string sourceName, Guid? cid = null, TimeProvider? time = null, Action<string>? log = null, bool start = true)
    {
        _socket = socket;
        _output = output;
        _sourceName = sourceName;
        _time = time ?? TimeProvider.System;
        _log = log ?? (_ => { });
        Cid = cid ?? Guid.NewGuid();
        if (start)
        {
            _timer = _time.CreateTimer(_ => Pump(), null, Tick, Tick);
        }
    }

    public Guid Cid { get; }

    /// <summary>Packets sent so far (all universes).</summary>
    public long Sent { get; private set; }

    /// <summary>Where <paramref name="universe"/> goes: the profile's host, else its multicast group.</summary>
    public string DestinationFor(int universe) => _output.Destination ?? E131.MulticastAddress(universe);

    /// <summary>Shows these frames (universe → 512 slots). Unchanged universes aren't re-sent early.</summary>
    public void Show(IReadOnlyDictionary<int, byte[]> frames)
    {
        ArgumentNullException.ThrowIfNull(frames);
        lock (_gate)
        {
            foreach (var (universe, slots) in frames)
            {
                if (!_streams.TryGetValue(universe, out var stream))
                {
                    stream = _streams[universe] = new Stream();
                }
                else if (stream.Slots.AsSpan().SequenceEqual(slots))
                {
                    continue;
                }

                stream.Slots = [.. slots];
                stream.Pending = Repeats;
            }
        }
    }

    /// <summary>One tick: sends what changed and what is due a keep-alive.</summary>
    internal void Pump()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            var now = _time.GetTimestamp();
            foreach (var (universe, stream) in _streams)
            {
                if (stream.Pending > 0)
                {
                    stream.Pending--;
                }
                else if (_time.GetElapsedTime(stream.LastSent, now) < KeepAlive)
                {
                    continue;
                }

                stream.LastSent = now;
                Send(universe, stream, terminated: false);
            }
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            // E1.31 §6.7.1: three packets with Stream_Terminated, so receivers let go at once.
            for (var i = 0; i < Repeats; i++)
            {
                foreach (var (universe, stream) in _streams)
                {
                    Send(universe, stream, terminated: true);
                }
            }
        }

        _socket.Dispose();
    }

    private void Send(int universe, Stream stream, bool terminated)
    {
        var packet = E131.DataPacket(Cid, _sourceName, universe, stream.Sequence++, stream.Slots, _output.Priority, terminated);
        try
        {
            _socket.Send(packet, DestinationFor(universe), E131.Port);
            Sent++;
            if (_failing)
            {
                _failing = false;
                _log("sACN: sending again.");
            }
        }
#pragma warning disable CA1031 // No network, no route, a full buffer: the band plays on.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            if (!_failing)
            {
                _failing = true;
                _log($"sACN: can't send to {DestinationFor(universe)} ({ex.Message}). Lights hold; the music doesn't stop.");
            }
        }
    }

    private sealed class Stream
    {
        public byte[] Slots { get; set; } = new byte[LightRenderer.SlotsPerUniverse];

        public byte Sequence { get; set; }

        public int Pending { get; set; }

        public long LastSent { get; set; }
    }
}

using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace MetroDom.Conductor.Service;

/// <summary>One DNS resource record, its data already encoded.</summary>
internal sealed record DnsRecord(string Name, ushort Type, uint Ttl, byte[] Data, bool CacheFlush = false)
{
    /// <summary>Same name, type and data (records hold arrays, so record equality won't do).</summary>
    public (string, ushort, string) Key => (Name.ToUpperInvariant(), Type, Convert.ToHexString(Data));
}

/// <summary>A question from a query: name, type and whether it asked for a unicast answer (QU).</summary>
internal readonly record struct DnsQuestion(string Name, ushort Type, bool Unicast);

/// <summary>Just enough of the DNS wire format (RFC 1035) for multicast DNS (RFC 6762) and DNS-SD (RFC 6763).</summary>
internal static class DnsWire
{
    public const ushort A = 1;
    public const ushort Ptr = 12;
    public const ushort Txt = 16;
    public const ushort Aaaa = 28;
    public const ushort Srv = 33;
    public const ushort Nsec = 47;
    public const ushort Any = 255;

    private const ushort ClassIn = 1;
    private const ushort CacheFlushBit = 0x8000;

    /// <summary>
    /// The questions of a query, or none if <paramref name="packet"/> is a response, truncated or
    /// malformed. Follows compression pointers, refusing loops.
    /// </summary>
    public static IReadOnlyList<DnsQuestion> Questions(ReadOnlySpan<byte> packet, out ushort id)
    {
        id = 0;
        if (packet.Length < 12)
        {
            return [];
        }

        id = BinaryPrimitives.ReadUInt16BigEndian(packet);
        var flags = BinaryPrimitives.ReadUInt16BigEndian(packet[2..]);
        var count = BinaryPrimitives.ReadUInt16BigEndian(packet[4..]);
        if ((flags & 0x8000) != 0 || (flags & 0x7800) != 0 || count > 64)
        {
            return []; // A response, or not a standard query.
        }

        var questions = new List<DnsQuestion>(count);
        var at = 12;
        for (var i = 0; i < count; i++)
        {
            if (ReadName(packet, ref at) is not { } name || at + 4 > packet.Length)
            {
                return [];
            }

            var type = BinaryPrimitives.ReadUInt16BigEndian(packet[at..]);
            var klass = BinaryPrimitives.ReadUInt16BigEndian(packet[(at + 2)..]);
            at += 4;
            if ((klass & 0x7FFF) is ClassIn or Any)
            {
                questions.Add(new DnsQuestion(name, type, (klass & 0x8000) != 0));
            }
        }

        return questions;
    }

    /// <summary>
    /// An authoritative response carrying <paramref name="answers"/>, plus <paramref name="additional"/>.
    /// With <paramref name="echo"/> (legacy unicast), the question is repeated and the id kept.
    /// </summary>
    public static byte[] Response(ushort id, IReadOnlyList<DnsRecord> answers, IReadOnlyList<DnsRecord> additional, DnsQuestion? echo = null)
    {
        using var stream = new MemoryStream();
        Span<byte> header = stackalloc byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(header, id);
        BinaryPrimitives.WriteUInt16BigEndian(header[2..], 0x8400); // Response, authoritative.
        BinaryPrimitives.WriteUInt16BigEndian(header[4..], (ushort)(echo is null ? 0 : 1));
        BinaryPrimitives.WriteUInt16BigEndian(header[6..], (ushort)answers.Count);
        BinaryPrimitives.WriteUInt16BigEndian(header[10..], (ushort)additional.Count);
        stream.Write(header);
        if (echo is { } question)
        {
            WriteName(stream, question.Name);
            WriteUInt16(stream, question.Type);
            WriteUInt16(stream, ClassIn);
        }

        Span<byte> ttl = stackalloc byte[4];
        foreach (var record in answers.Concat(additional))
        {
            WriteName(stream, record.Name);
            WriteUInt16(stream, record.Type);
            WriteUInt16(stream, (ushort)(ClassIn | (record.CacheFlush ? CacheFlushBit : 0)));
            BinaryPrimitives.WriteUInt32BigEndian(ttl, record.Ttl);
            stream.Write(ttl);
            WriteUInt16(stream, (ushort)record.Data.Length);
            stream.Write(record.Data);
        }

        return stream.ToArray();
    }

    /// <summary>A name as labels (no compression).</summary>
    public static byte[] Name(string name)
    {
        using var stream = new MemoryStream();
        WriteName(stream, name);
        return stream.ToArray();
    }

    public static byte[] SrvData(ushort port, string target)
    {
        var name = Name(target);
        var data = new byte[6 + name.Length];
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(4), port); // Priority 0, weight 0.
        name.CopyTo(data, 6);
        return data;
    }

    /// <summary>
    /// NSEC data saying <paramref name="name"/> has only A records (RFC 6762 §6.1), so a resolver
    /// asking for AAAA gets "none" at once instead of waiting for an answer that never comes.
    /// </summary>
    public static byte[] OnlyAData(string name) => [.. Name(name), 0, 1, 0x40];

    /// <summary>TXT strings, each at most 255 bytes.</summary>
    public static byte[] TxtData(IEnumerable<string> strings)
    {
        using var stream = new MemoryStream();
        foreach (var text in strings)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            if (bytes.Length > 255)
            {
                throw new ArgumentException($"TXT string too long: {text}", nameof(strings));
            }

            stream.WriteByte((byte)bytes.Length);
            stream.Write(bytes);
        }

        return stream.Length == 0 ? [0] : stream.ToArray();
    }

    /// <summary>Reads a possibly compressed name at <paramref name="at"/>, moving past it.</summary>
    public static string? ReadName(ReadOnlySpan<byte> packet, ref int at)
    {
        var labels = new List<string>();
        var position = at;
        var jumped = false;
        for (var hops = 0; hops < 32; hops++)
        {
            if (position >= packet.Length)
            {
                return null;
            }

            var length = packet[position];
            if (length == 0)
            {
                if (!jumped)
                {
                    at = position + 1;
                }

                return string.Join('.', labels);
            }

            if ((length & 0xC0) == 0xC0)
            {
                if (position + 1 >= packet.Length)
                {
                    return null;
                }

                if (!jumped)
                {
                    at = position + 2;
                }

                jumped = true;
                position = ((length & 0x3F) << 8) | packet[position + 1];
                continue;
            }

            if ((length & 0xC0) != 0 || position + 1 + length > packet.Length)
            {
                return null;
            }

            labels.Add(Encoding.UTF8.GetString(packet.Slice(position + 1, length)));
            position += 1 + length;
        }

        return null; // A pointer loop.
    }

    private static void WriteName(Stream stream, string name)
    {
        foreach (var label in name.TrimEnd('.').Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var bytes = Encoding.UTF8.GetBytes(label);
            if (bytes.Length > 63)
            {
                throw new ArgumentException($"DNS label too long: {label}", nameof(name));
            }

            stream.WriteByte((byte)bytes.Length);
            stream.Write(bytes);
        }

        stream.WriteByte(0);
    }

    private static void WriteUInt16(Stream stream, ushort value)
    {
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, value);
        stream.Write(bytes);
    }
}

/// <summary>
/// What <c>serve</c> says about itself on the LAN: the instance name, the host name phones can
/// type (<c>metrodom.local</c>), the port, and TXT facts. No tokens, ever.
/// </summary>
internal sealed record MdnsService(string Instance, string Host, ushort Port, IReadOnlyList<string> Txt)
{
    public const string ServiceType = "_metrodom._tcp.local";
    public const string HttpType = "_http._tcp.local";
    public const string Browse = "_services._dns-sd._udp.local";
    public const uint HostTtl = 120;
    public const uint ServiceTtl = 4500;

    /// <summary>The records that answer <paramref name="question"/>, or none.</summary>
    public IReadOnlyList<DnsRecord> Answer(DnsQuestion question, IReadOnlyList<IPAddress> addresses)
    {
        bool Asks(string name, ushort type) =>
            string.Equals(question.Name, name, StringComparison.OrdinalIgnoreCase) && (question.Type == type || question.Type == DnsWire.Any);

        var answers = new List<DnsRecord>();
        foreach (var type in new[] { ServiceType, HttpType })
        {
            var instance = $"{Instance}.{type}";
            if (Asks(type, DnsWire.Ptr))
            {
                answers.Add(Pointer(type));
            }

            if (Asks(Browse, DnsWire.Ptr))
            {
                answers.Add(new DnsRecord(Browse, DnsWire.Ptr, ServiceTtl, DnsWire.Name(type)));
            }

            if (Asks(instance, DnsWire.Srv))
            {
                answers.Add(Service(instance));
            }

            if (Asks(instance, DnsWire.Txt))
            {
                answers.Add(Text(instance));
            }
        }

        if (Asks(Host, DnsWire.A))
        {
            answers.AddRange(HostRecords(addresses));
        }
        else if (string.Equals(question.Name, Host, StringComparison.OrdinalIgnoreCase))
        {
            answers.Add(NoOtherAddresses()); // AAAA and the rest: there are none.
        }

        return [.. answers.DistinctBy(r => r.Key)];
    }

    /// <summary>What a browser needs next after <paramref name="answers"/> (SRV, TXT, A), so it needn't ask.</summary>
    public IReadOnlyList<DnsRecord> Additional(IReadOnlyList<DnsRecord> answers, IReadOnlyList<IPAddress> addresses)
    {
        var extra = new List<DnsRecord>();
        foreach (var answer in answers.Where(a => a.Type == DnsWire.Ptr && a.Name != Browse))
        {
            var instance = $"{Instance}.{answer.Name}";
            extra.Add(Service(instance));
            extra.Add(Text(instance));
        }

        if (answers.Any(a => a.Type is DnsWire.Ptr or DnsWire.Srv && a.Name != Browse) && !answers.Any(a => a.Type == DnsWire.A))
        {
            extra.AddRange(HostRecords(addresses));
        }

        var given = answers.Select(a => a.Key).ToHashSet();
        return [.. extra.Where(e => !given.Contains(e.Key)).DistinctBy(r => r.Key)];
    }

    /// <summary>Everything, for announcing at start (or saying goodbye with <paramref name="ttl"/> 0).</summary>
    public IReadOnlyList<DnsRecord> All(IReadOnlyList<IPAddress> addresses, uint? ttl = null)
    {
        DnsRecord[] all =
        [
            Pointer(ServiceType), Service($"{Instance}.{ServiceType}"), Text($"{Instance}.{ServiceType}"),
            Pointer(HttpType), Service($"{Instance}.{HttpType}"), Text($"{Instance}.{HttpType}"),
            .. HostRecords(addresses),
        ];
        return ttl is { } t ? [.. all.Select(r => r with { Ttl = t })] : all;
    }

    private DnsRecord NoOtherAddresses() => new(Host, DnsWire.Nsec, HostTtl, DnsWire.OnlyAData(Host), CacheFlush: true);

    private DnsRecord Pointer(string type) => new(type, DnsWire.Ptr, ServiceTtl, DnsWire.Name($"{Instance}.{type}"));

    private DnsRecord Service(string instance) => new(instance, DnsWire.Srv, HostTtl, DnsWire.SrvData(Port, Host), CacheFlush: true);

    private DnsRecord Text(string instance) => new(instance, DnsWire.Txt, ServiceTtl, DnsWire.TxtData(Txt), CacheFlush: true);

    private IEnumerable<DnsRecord> HostRecords(IReadOnlyList<IPAddress> addresses) =>
        addresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork)
            .Select(a => new DnsRecord(Host, DnsWire.A, HostTtl, a.GetAddressBytes(), CacheFlush: true));
}

/// <summary>
/// A small mDNS responder (RFC 6762) for <see cref="MdnsService"/>: answers on the IPv4 multicast
/// group, announces at start and says goodbye at stop. It shares port 5353 with the OS's own
/// responder (Bonjour, Avahi). Best effort: when it can't bind, the service still runs and
/// people use the printed link. IPv4 only; no name-conflict probing (docs tech-debt TD-27).
/// </summary>
internal sealed class MdnsAdvertiser : IDisposable
{
    public const int Port = 5353;

    private static readonly IPAddress Group = IPAddress.Parse("224.0.0.251");
    private static readonly IPEndPoint GroupEndpoint = new(Group, Port);

    private readonly MdnsService _service;
    private readonly Func<IReadOnlyList<IPAddress>> _addresses;
    private readonly UdpClient _udp;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private int _disposed;

    private MdnsAdvertiser(MdnsService service, Func<IReadOnlyList<IPAddress>> addresses, UdpClient udp)
    {
        _service = service;
        _addresses = addresses;
        _udp = udp;
        _loop = Task.Run(Run);
    }

    /// <summary>Starts advertising, or returns null and why when the port can't be shared.</summary>
    public static MdnsAdvertiser? TryStart(MdnsService service, out string? error, Func<IReadOnlyList<IPAddress>>? addresses = null)
    {
        ArgumentNullException.ThrowIfNull(service);
        UdpClient? udp = null;
        try
        {
            udp = new UdpClient(AddressFamily.InterNetwork);
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.ExclusiveAddressUse = false;
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, Port));
            udp.JoinMulticastGroup(Group);
            udp.MulticastLoopback = true;
            udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
            error = null;
            return new MdnsAdvertiser(service, addresses ?? LanAddresses, udp);
        }
        catch (SocketException ex)
        {
            udp?.Dispose();
            error = ex.Message;
            return null;
        }
    }

    /// <summary>This computer's LAN IPv4 addresses (no loopback, no link-local).</summary>
    public static IReadOnlyList<IPAddress> LanAddresses() =>
        [.. NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback && n.SupportsMulticast)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a) && !a.ToString().StartsWith("169.254.", StringComparison.Ordinal))
            .Distinct()];

    private async Task Run()
    {
        try
        {
            // RFC 6762 §8.3: announce at least twice, a second apart.
            await Send(DnsWire.Response(0, _service.All(_addresses()), []), GroupEndpoint).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromSeconds(1), _stop.Token).ConfigureAwait(false);
            await Send(DnsWire.Response(0, _service.All(_addresses()), []), GroupEndpoint).ConfigureAwait(false);

            while (!_stop.IsCancellationRequested)
            {
                var received = await _udp.ReceiveAsync(_stop.Token).ConfigureAwait(false);
                Reply(received.Buffer, received.RemoteEndPoint);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
        {
        }
    }

    private void Reply(byte[] packet, IPEndPoint from)
    {
        var questions = DnsWire.Questions(packet, out var id);
        if (questions.Count == 0)
        {
            return;
        }

        var addresses = _addresses();
        var answers = questions.SelectMany(q => _service.Answer(q, addresses)).DistinctBy(r => r.Key).ToArray();
        if (answers.Length == 0)
        {
            return;
        }

        var additional = _service.Additional(answers, addresses);

        // A one-shot resolver (source port isn't 5353) gets a classic unicast reply (RFC 6762 §6.7).
        var legacy = from.Port != Port;
        var reply = DnsWire.Response(legacy ? id : (ushort)0, answers, additional, legacy ? questions[0] : null);
        _ = Send(reply, legacy || questions.All(q => q.Unicast) ? from : GroupEndpoint);
    }

    private async Task Send(byte[] packet, IPEndPoint to)
    {
        try
        {
            await _udp.SendAsync(packet, to, _stop.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
        {
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        try
        {
            // Goodbye: TTL 0 tells caches to forget us now, not in two minutes.
            _udp.Send(DnsWire.Response(0, _service.All(_addresses(), ttl: 0), []), GroupEndpoint);
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
        }

        _stop.Cancel();
        _udp.Dispose();
        try
        {
            _loop.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException)
        {
        }

        _stop.Dispose();
    }
}

using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using MetroDom.Conductor.Api;
using MetroDom.Conductor.Cli;
using MetroDom.Conductor.Commands;
using MetroDom.Conductor.Service;
using MetroDom.Core.Policy;
using MetroDom.Core.Setlist;
using MetroDom.Core.Simulation;

namespace MetroDom.Conductor.Tests;

/// <summary>The <c>serve</c> pieces that need no network: library, saved state, mDNS packets, QR codes (plan 0004 S3).</summary>
public sealed class ServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"metrodom-serve-{Guid.NewGuid():N}");

    public ServiceTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Song(string relative)
    {
        var path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        DemoSong.Create().Write(path, overwriteFile: true);
        return path;
    }

    [Fact]
    public void Library_FindsMidiFilesInSubfolders_ByRelativeId()
    {
        Song("Opener.mid");
        Song("covers/Closer.MIDI");
        File.WriteAllText(Path.Combine(_dir, "notes.txt"), "not a song");
        File.WriteAllText(Path.Combine(_dir, "broken.mid"), "MThd but not really");

        var library = new SongLibrary(_dir);

        Assert.Equal(["Closer", "Opener"], library.Songs.Select(s => s.Title));
        Assert.Equal(["covers/Closer.MIDI", "Opener.mid"], library.Songs.Select(s => s.Id));
        Assert.Equal(["broken.mid"], library.Unreadable);
        var opener = library.Find("Opener.mid")!;
        Assert.Equal(100, opener.Bpm, precision: 1);
        Assert.True(opener.Duration > TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Library_OnlyResolvesIdsItListed()
    {
        Song("a.mid");
        var library = new SongLibrary(_dir);
        Assert.Null(library.Find("../a.mid"));
        Assert.Null(library.Find(Path.Combine(_dir, "a.mid")));
        Assert.Null(library.Find(null));
    }

    [Fact]
    public void Library_Rescan_SeesNewAndRemovedFiles()
    {
        var first = Song("a.mid");
        var library = new SongLibrary(_dir);
        Song("b.mid");
        File.Delete(first);

        Assert.Equal(["b"], library.Rescan().Select(s => s.Title));
        Assert.Null(library.Find("a.mid"));
    }

    [Fact]
    public void State_FirstRun_MakesAnOwnerToken_AndKeepsItAcrossRestarts()
    {
        Assert.True(ServiceState.TryLoad(_dir, out var first, out var error), error);
        Assert.True(first.OwnerToken.Length >= 16);
        Assert.False(File.Exists(first.Path)); // nothing written until the service saves

        first.Save([], ["a.mid", "covers/b.mid"]);
        Assert.True(ServiceState.TryLoad(_dir, out var again, out error), error);
        Assert.Equal(first.OwnerToken, again.OwnerToken);
        Assert.Equal(["a.mid", "covers/b.mid"], again.Queue);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(again.Path));
        }
    }

    [Fact]
    public void State_KeepsHandedOutTokens_AsHashesOnly()
    {
        Assert.True(ServiceState.TryLoad(_dir, out var state, out _));
        var before = new TokenStore(state.OwnerToken);
        Assert.True(before.TryIssue(Role.Guest, TimeSpan.FromHours(2), "table 4", SessionRules.Default, out var holder, out var secret, out _));
        state.Save(before.Export(), []);

        Assert.DoesNotContain(secret, File.ReadAllText(state.Path), StringComparison.Ordinal);
        Assert.Contains(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret))), File.ReadAllText(state.Path), StringComparison.Ordinal);

        Assert.True(ServiceState.TryLoad(_dir, out var restarted, out _));
        var after = new TokenStore(restarted.OwnerToken);
        after.Import(restarted.Tokens);
        Assert.Equal(holder, after.Find(secret));
        Assert.Equal(Role.Owner, after.Find(state.OwnerToken)?.Role);
    }

    [Fact]
    public void State_ThatCantBeRead_IsAnError_NotSilentlyReplaced()
    {
        File.WriteAllText(Path.Combine(_dir, ServiceState.FileName), "{ not json");
        Assert.False(ServiceState.TryLoad(_dir, out _, out var error));
        Assert.Contains("can't read", error, StringComparison.Ordinal);

        File.WriteAllText(Path.Combine(_dir, ServiceState.FileName), """{"schema":"metrodom.service-state/0"}""");
        Assert.False(ServiceState.TryLoad(_dir, out _, out error));
        Assert.Contains("no owner token", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Tokens_ExpiredWhileTheBoxWasOff_DontComeBack()
    {
        var time = new StepTime();
        var before = new TokenStore("owner-token-123456", time);
        before.TryIssue(Role.Guest, TimeSpan.FromHours(1), null, SessionRules.Default, out _, out var secret, out _);
        var saved = before.Export();

        time.Now += TimeSpan.FromHours(2);
        var after = new TokenStore("owner-token-123456", time);
        after.Import(saved);
        Assert.Null(after.Find(secret));
        Assert.Empty(after.Issued());
    }

    [Fact]
    public void Upcoming_IsTheCurrentSongAndWhatsComing()
    {
        var queue = new SetQueue();
        queue.Replace([("a.mid", "a"), ("b.mid", "b"), ("c.mid", "c")]);
        Assert.Equal(["a.mid", "b.mid", "c.mid"], ServeCommand.Upcoming(queue.Snapshot()));
        queue.Cue(0);
        queue.Advance();
        Assert.Equal(["b.mid", "c.mid"], ServeCommand.Upcoming(queue.Snapshot()));
    }

    [Fact]
    public void Serve_WithoutALibraryFolder_IsAUsageError()
    {
        var missing = Path.Combine(_dir, "nope");
        Assert.Equal(MetroDomApp.UsageError, MetroDomApp.Create().Run(["serve", missing], TestContext.Current.CancellationToken));
        Assert.Contains("library folder not found", new ServeCommand.Settings { Library = missing }.Validate().Message, StringComparison.Ordinal);
    }

    // ---- mDNS ----

    private static readonly MdnsService Service = new("MetroDom on box", "metrodom.local", 7890, ["path=/", "api=/api/v0"]);
    private static readonly IReadOnlyList<IPAddress> Lan = [IPAddress.Parse("192.168.4.1")];

    /// <summary>A query as a browser sends it: the second question's name compressed against the first.</summary>
    private static byte[] Query(ushort id, params (string Name, ushort Type)[] questions)
    {
        var packet = new List<byte>(new byte[12]);
        BinaryPrimitives.WriteUInt16BigEndian(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(packet), id);
        packet[5] = (byte)questions.Length;
        foreach (var (name, type) in questions)
        {
            packet.AddRange(DnsWire.Name(name));
            packet.AddRange([(byte)(type >> 8), (byte)type, 0, 1]);
        }

        return [.. packet];
    }

    [Fact]
    public void Mdns_ReadsQuestions_FollowingCompression()
    {
        // "_metrodom._tcp.local" then "metrodom.local" as a label plus a pointer to "local" (offset 12 + 10 + 5).
        var packet = Query(7, ("_metrodom._tcp.local", DnsWire.Ptr)).ToList();
        packet[5] = 2;
        packet.AddRange([8, .. "metrodom"u8.ToArray(), 0xC0, 12 + 10 + 5, 0, 1, 0x80, 1]);

        var questions = DnsWire.Questions(packet.ToArray(), out var id);

        Assert.Equal(7, id);
        Assert.Equal([new DnsQuestion("_metrodom._tcp.local", DnsWire.Ptr, false), new DnsQuestion("metrodom.local", DnsWire.A, true)], questions);
    }

    [Fact]
    public void Mdns_IgnoresResponsesAndJunk()
    {
        var response = DnsWire.Response(0, Service.All(Lan), []);
        Assert.Empty(DnsWire.Questions(response, out _));
        Assert.Empty(DnsWire.Questions([1, 2, 3], out _));

        // A pointer that points at itself must not hang.
        byte[] loop = [0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0xC0, 12, 0, 1, 0, 1];
        Assert.Empty(DnsWire.Questions(loop, out _));
    }

    [Fact]
    public void Mdns_BrowsingForMetroDom_GetsEverythingInOneReply()
    {
        var answers = Service.Answer(new DnsQuestion(MdnsService.ServiceType, DnsWire.Ptr, false), Lan);
        var additional = Service.Additional(answers, Lan);

        var ptr = Assert.Single(answers);
        Assert.Equal((MdnsService.ServiceType, DnsWire.Ptr), (ptr.Name, ptr.Type));
        Assert.Equal(DnsWire.Name("MetroDom on box._metrodom._tcp.local"), ptr.Data);
        Assert.Equal([DnsWire.Srv, DnsWire.Txt, DnsWire.A], additional.Select(r => r.Type));
        var srv = additional[0];
        Assert.Equal(7890, BinaryPrimitives.ReadUInt16BigEndian(srv.Data.AsSpan(4)));
        var at = 6;
        Assert.Equal("metrodom.local", DnsWire.ReadName(srv.Data, ref at));
        Assert.Equal([192, 168, 4, 1], additional[2].Data);
        Assert.Equal("\u0006path=/\u000bapi=/api/v0", Encoding.UTF8.GetString(additional[1].Data));
    }

    [Fact]
    public void Mdns_AnswersTheHostName_AndNothingElse()
    {
        Assert.Equal([DnsWire.A], Service.Answer(new DnsQuestion("METRODOM.local", DnsWire.A, false), Lan).Select(r => r.Type));
        Assert.Empty(Service.Answer(new DnsQuestion("printer.local", DnsWire.A, false), Lan));

        // No IPv6: say so (NSEC with only A), or resolvers wait seconds for an AAAA that never comes.
        var none = Assert.Single(Service.Answer(new DnsQuestion("metrodom.local", DnsWire.Aaaa, false), Lan));
        Assert.Equal(DnsWire.Nsec, none.Type);
        Assert.Equal([.. DnsWire.Name("metrodom.local"), 0, 1, 0x40], none.Data);
        Assert.Empty(Service.Answer(new DnsQuestion("_ipp._tcp.local", DnsWire.Ptr, false), Lan));
        Assert.NotEmpty(Service.Answer(new DnsQuestion(MdnsService.Browse, DnsWire.Ptr, false), Lan));
    }

    [Fact]
    public void Mdns_Response_IsWellFormed()
    {
        var question = new DnsQuestion(MdnsService.ServiceType, DnsWire.Ptr, false);
        var packet = DnsWire.Response(42, Service.Answer(question, Lan), [], echo: question);

        Assert.Equal(42, BinaryPrimitives.ReadUInt16BigEndian(packet));
        Assert.Equal(0x8400, BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(2)));
        Assert.Equal((1, 1), (BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(4)), BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(6))));
        var at = 12;
        Assert.Equal(MdnsService.ServiceType, DnsWire.ReadName(packet, ref at));
        at += 4;
        Assert.Equal(MdnsService.ServiceType, DnsWire.ReadName(packet, ref at));
        Assert.Equal(DnsWire.Ptr, BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(at)));
        Assert.Equal(MdnsService.ServiceTtl, BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(at + 4)));
    }

    [Fact]
    public void Mdns_Goodbye_HasZeroTtls()
    {
        Assert.All(Service.All(Lan, ttl: 0), r => Assert.Equal(0u, r.Ttl));
    }

    // ---- QR ----

    [Fact]
    public void Qr_SvgAndText_DrawTheSameCode()
    {
        const string link = "http://192.168.4.1:7890/#token=abcdefghijklmnopqrstuv";
        var modules = PairingQr.Modules(link);
        Assert.True(modules.Count >= 21 + 8); // version 1 plus the quiet zone
        Assert.Equal(0, (modules.Count - 8 - 21) % 4);

        var svg = PairingQr.Svg(link);
        Assert.StartsWith("<svg xmlns=\"http://www.w3.org/2000/svg\"", svg, StringComparison.Ordinal);
        Assert.Contains($"viewBox=\"0 0 {modules.Count} {modules.Count}\"", svg, StringComparison.Ordinal);
        Assert.DoesNotContain("token", svg, StringComparison.Ordinal); // the code, not the text

        var lines = PairingQr.Text(link).TrimEnd('\n').Split('\n');
        Assert.Equal((modules.Count + 1) / 2, lines.Length);
        Assert.All(lines, l => Assert.Equal(modules.Count, l.Length));
    }

    private sealed class StepTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 12, 20, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
